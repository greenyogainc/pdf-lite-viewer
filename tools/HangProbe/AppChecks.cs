using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using PdfLiteViewer;

namespace HangProbe;

/// <summary>
/// Guards what keeps the viewer alive and readable when something goes wrong: the three
/// unhandled-exception paths and the log they share, the localization helpers behind every
/// message box, and the chapter tree's own bookkeeping. The failures are real where a real
/// one is safe to cause - a throw on the dispatcher, a task nobody observes - and injected
/// where it would end the probe (an unhandled exception on another thread). Every dialog
/// the app raises here is answered through <see cref="Program.Dialogs"/>.
/// </summary>
internal static class AppChecks
{
    public static async Task<List<Check>> RunAsync()
    {
        var checks = new List<Check>
        {
            await CheckKit.RunAsync("app: a dispatcher exception is logged, reported and survived", DispatcherExceptionAsync),
            await CheckKit.RunAsync("app: unhandled background failures reach the log", BackgroundFailuresAsync),
            await CheckKit.RunAsync("app: an unobserved task failure is logged", UnobservedTaskAsync),
            CheckKit.Run("app: a locked log never takes the app down", LockedLog),
            CheckKit.Run("app: the first document argument wins", FirstDocumentWins),
            CheckKit.Run("strings: a missing key comes back as the key", MissingKey),
            CheckKit.Run("strings: right-to-left follows the shipped translations", RightToLeft),
            CheckKit.Run("strings: ApplyFlowDirection mirrors only shipped RTL and sets the language", FlowDirectionApplied),
            await CheckKit.RunAsync("strings: ShowError reaches the user with and without an owner", ShowErrorAsync),
            CheckKit.Run("strings: the Loc markup extension resolves its key", LocExtensionResolves),
            CheckKit.Run("chapters: expanding and selecting raise PropertyChanged for subscribers only", ChapterNotifications),
            CheckKit.Run("chapters: title and navigability", ChapterBasics),
            CheckKit.Run("chapters: flattening orders by page, depth, then source order", ChapterFlattening),
        };
        return checks;
    }

    // ---------- App: failure paths ----------

    private static async Task<Check> DispatcherExceptionAsync()
    {
        const string name = "app: a dispatcher exception is logged, reported and survived";
        var marker = ProbeLog.NewMarker("probe-dispatcher");
        var dispatcher = Application.Current.Dispatcher;
        using var dialog = Program.Dialogs.Expect(Strings.Get("AppTitle"), DialogAction.PressOk, textContains: marker);

        _ = dispatcher.BeginInvoke(new Action(() => throw new InvalidOperationException(marker)));
        bool shown = await dialog.WaitAsync(TimeSpan.FromSeconds(15));
        bool logged = await ProbeLog.UntilContainsAsync(marker, TimeSpan.FromSeconds(5));
        int later = await dispatcher.InvokeAsync(() => 42);
        bool alive = later == 42 && !dispatcher.HasShutdownStarted;

        var verdict = dialog.Verify(name);
        return new Check(name, verdict.Ok && logged && alive,
            $"{verdict.Detail}; logged={logged}; dispatcher still runs work={alive}");
    }

    private static async Task<Check> BackgroundFailuresAsync()
    {
        const string name = "app: unhandled background failures reach the log";
        var exceptionMarker = ProbeLog.NewMarker("probe-background");
        var objectMarker = ProbeLog.NewMarker("probe-thrown-object");

        // The handler App registers on AppDomain.UnhandledException, called directly: raising
        // a real one would terminate the probe and lose its coverage data.
        App.OnUnhandledException(null, new UnhandledExceptionEventArgs(new InvalidOperationException(exceptionMarker), isTerminating: false));
        App.OnUnhandledException(null, new UnhandledExceptionEventArgs(objectMarker, isTerminating: true));

        bool exceptionLogged = await ProbeLog.UntilContainsAsync(exceptionMarker, TimeSpan.FromSeconds(5));
        bool objectLogged = await ProbeLog.UntilContainsAsync(objectMarker, TimeSpan.FromSeconds(5))
                            && ProbeLog.Contains("Non-Exception throw on background thread");
        return new Check(name, exceptionLogged && objectLogged,
            $"exception logged={exceptionLogged}; a non-Exception object logged with its description={objectLogged}");
    }

    private static async Task<Check> UnobservedTaskAsync()
    {
        const string name = "app: an unobserved task failure is logged";
        var marker = ProbeLog.NewMarker("probe-unobserved");
        AbandonFaultedTask(marker);

        // The event fires from the finalizer of a task nobody awaited: force collections until it has.
        var clock = Stopwatch.StartNew();
        bool logged = false;
        int rounds = 0;
        while (!logged && clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            rounds++;
            logged = ProbeLog.Contains(marker);
            if (!logged) await Task.Delay(50);
        }
        return new Check(name, logged, logged
            ? $"the finalizer reported it after {rounds} collection(s) and the handler logged it"
            : $"'{marker}' never reached the log in {clock.Elapsed.TotalSeconds:F0} s");
    }

    /// <summary>
    /// Its own method, never inlined, so no local of the caller keeps the task reachable and
    /// the collector is free to finalize it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AbandonFaultedTask(string marker)
    {
        var source = new TaskCompletionSource();
        source.SetException(new InvalidOperationException(marker));
    }

    private static Check LockedLog()
    {
        const string name = "app: a locked log never takes the app down";
        var dropped = ProbeLog.NewMarker("probe-locked-log");
        var afterwards = ProbeLog.NewMarker("probe-unlocked-log");

        bool threw = false;
        var held = new FileStream(ProbeLog.Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            try { App.LogError(new InvalidOperationException(dropped)); }
            catch { threw = true; }
        }
        finally
        {
            held.Dispose();
        }

        bool lost = !ProbeLog.Contains(dropped);
        App.LogError(new InvalidOperationException(afterwards));
        bool recovered = ProbeLog.Contains(afterwards);
        return new Check(name, !threw && lost && recovered,
            $"LogError threw={threw}; the entry written while the file was held was dropped={lost}; logging works again once it is released={recovered}");
    }

    private static Check FirstDocumentWins()
    {
        const string name = "app: the first document argument wins";
        var cases = new (string[] Args, string? Expected)[]
        {
            (new[] { "first.pdf", "second.pdf" }, "first.pdf"),
            (new[] { "3000", "first.pdf", "second.pdf", "third.pdf" }, "first.pdf"),
            (new[] { "-x", "report.txt", "only.pdf" }, "only.pdf"),
            (new[] { "-x", "report.txt" }, null),
            (Array.Empty<string>(), null),
        };
        var wrong = new List<string>();
        foreach (var (args, expected) in cases)
        {
            var got = App.ApplyStartupArguments(args);
            if (got != expected) wrong.Add($"[{string.Join(", ", args)}] -> {got ?? "null"}, expected {expected ?? "null"}");
        }
        return CheckKit.Tally(name, cases.Length, wrong);
    }

    // ---------- Strings and LocExtension ----------

    private static Check MissingKey()
    {
        const string name = "strings: a missing key comes back as the key";
        var key = "no-such-key-" + Guid.NewGuid().ToString("N");
        var missing = Strings.Get(key);
        var known = Strings.Get("AppTitle");
        bool ok = missing == key && known.Length > 0 && known != "AppTitle";
        return new Check(name, ok, $"missing -> '{missing}'; AppTitle -> '{known}'");
    }

    private static Check RightToLeft()
    {
        const string name = "strings: right-to-left follows the shipped translations";
        // Only Arabic ships a translation. Hebrew, Persian and Urdu read right to left but
        // would get a mirrored layout around English text, so they stay left to right.
        var cases = new (string Culture, bool Rtl)[]
        {
            ("en", false), ("en-US", false), ("de", false), ("ja-JP", false),
            ("ar", true), ("ar-SA", true), ("ar-EG", true),
            ("he", false), ("he-IL", false), ("fa-IR", false), ("ur-PK", false),
        };
        var wrong = new List<string>();
        var previous = Thread.CurrentThread.CurrentUICulture;
        try
        {
            foreach (var (culture, rtl) in cases)
            {
                Thread.CurrentThread.CurrentUICulture = new CultureInfo(culture);
                var options = Strings.MessageBoxOptions;
                var expectedOptions = rtl ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None;
                if (Strings.IsRightToLeft != rtl) wrong.Add($"{culture}: IsRightToLeft={Strings.IsRightToLeft}");
                if (options != expectedOptions) wrong.Add($"{culture}: MessageBoxOptions={options}, expected {expectedOptions}");
            }
        }
        finally
        {
            Thread.CurrentThread.CurrentUICulture = previous;
        }
        return CheckKit.Tally(name, cases.Length, wrong);
    }

    private static Check FlowDirectionApplied()
    {
        const string name = "strings: ApplyFlowDirection mirrors only shipped RTL and sets the language";
        var cases = new (string Culture, FlowDirection Expected)[]
        {
            ("ar", FlowDirection.RightToLeft),
            ("ar-SA", FlowDirection.RightToLeft),
            ("en-US", FlowDirection.LeftToRight),
            ("he-IL", FlowDirection.LeftToRight),
            ("zh-Hant", FlowDirection.LeftToRight),
        };
        var wrong = new List<string>();
        var previous = Thread.CurrentThread.CurrentUICulture;
        try
        {
            foreach (var (culture, expected) in cases)
            {
                Thread.CurrentThread.CurrentUICulture = new CultureInfo(culture);
                var window = new Window();     // never shown: only its two properties are read
                Strings.ApplyFlowDirection(window);
                if (window.FlowDirection != expected) wrong.Add($"{culture}: FlowDirection={window.FlowDirection}");
                var tag = window.Language.IetfLanguageTag;
                var wanted = new CultureInfo(culture).IetfLanguageTag;
                if (!string.Equals(tag, wanted, StringComparison.OrdinalIgnoreCase)) wrong.Add($"{culture}: Language '{tag}', expected '{wanted}'");
            }
        }
        finally
        {
            Thread.CurrentThread.CurrentUICulture = previous;
        }
        return CheckKit.Tally(name, cases.Length, wrong);
    }

    private static async Task<Check> ShowErrorAsync()
    {
        const string name = "strings: ShowError reaches the user with and without an owner";
        var wrong = new List<string>();
        var caption = Strings.Get("AppTitle");

        // MessageBox.Show blocks this thread inside its own loop; the dismisser answers it from another.
        var noOwner = ProbeLog.NewMarker("probe-no-owner");
        using (var dialog = Program.Dialogs.Expect(caption, DialogAction.PressOk, textContains: noOwner))
        {
            Strings.ShowError(null, "Probe message " + noOwner);
            if (!await dialog.WaitAsync(TimeSpan.FromSeconds(10))) wrong.Add("the ownerless message box never appeared");
            else if (dialog.Text != "Probe message " + noOwner) wrong.Add($"ownerless text was '{dialog.Text}'");
        }

        var withOwner = ProbeLog.NewMarker("probe-owned");
        var owner = new Window { Title = "probe owner", Width = 320, Height = 120, ShowInTaskbar = false };
        owner.Show();
        try
        {
            using var dialog = Program.Dialogs.Expect(caption, DialogAction.PressOk, textContains: withOwner);
            Strings.ShowError(owner, "Probe message " + withOwner);
            if (!await dialog.WaitAsync(TimeSpan.FromSeconds(10))) wrong.Add("the owned message box never appeared");
            else if (dialog.Text != "Probe message " + withOwner) wrong.Add($"owned text was '{dialog.Text}'");
        }
        finally
        {
            owner.Close();
        }
        return CheckKit.Tally(name, 2, wrong, "each box carried the app title and the message, and was answered");
    }

    private static Check LocExtensionResolves()
    {
        const string name = "strings: the Loc markup extension resolves its key";
        var wrong = new List<string>();
        var known = new LocExtension("AppTitle");
        if (known.Key != "AppTitle") wrong.Add($"Key is '{known.Key}'");
        if (known.ProvideValue(null!) as string != Strings.Get("AppTitle")) wrong.Add("a known key did not resolve to its text");
        var missing = new LocExtension("no-such-key");
        if (missing.ProvideValue(null!) as string != "no-such-key") wrong.Add("a missing key did not fall back to itself");
        return CheckKit.Tally(name, 3, wrong);
    }

    // ---------- ChapterItem ----------

    private static Check ChapterNotifications()
    {
        const string name = "chapters: expanding and selecting raise PropertyChanged for subscribers only";
        var wrong = new List<string>();
        var item = new ChapterItem { Title = "Chapter" };

        // Nobody listening: the setters must still work.
        item.IsExpanded = true;
        item.IsSelected = true;
        if (!item.IsExpanded || !item.IsSelected) wrong.Add("a setter without a subscriber lost its value");

        var raised = new List<(object? Sender, string? Name)>();
        item.PropertyChanged += (sender, args) => raised.Add((sender, args.PropertyName));
        item.IsExpanded = false;
        item.IsSelected = false;
        if (raised.Select(r => r.Name).SequenceEqual(new[] { nameof(ChapterItem.IsExpanded), nameof(ChapterItem.IsSelected) }) is false)
            wrong.Add($"raised [{string.Join(", ", raised.Select(r => r.Name))}]");
        if (raised.Any(r => !ReferenceEquals(r.Sender, item))) wrong.Add("the sender was not the item");
        if (item.IsExpanded || item.IsSelected) wrong.Add("the new values were not stored");
        return CheckKit.Tally(name, 4, wrong);
    }

    private static Check ChapterBasics()
    {
        const string name = "chapters: title and navigability";
        var wrong = new List<string>();
        if (new ChapterItem { Title = "Intro" }.ToString() != "Intro") wrong.Add("ToString is not the title");
        if (new ChapterItem().ToString() != "") wrong.Add("an untitled item does not print as empty");
        foreach (var (page, navigable) in new (int? Page, bool Navigable)[] { (null, false), (0, true), (7, true) })
        {
            var item = new ChapterItem { PageIndex = page };
            if (item.IsNavigable != navigable) wrong.Add($"PageIndex {page?.ToString() ?? "null"}: IsNavigable={item.IsNavigable}");
        }
        return CheckKit.Tally(name, 5, wrong);
    }

    private static Check ChapterFlattening()
    {
        const string name = "chapters: flattening orders by page, depth, then source order";
        // Source orders are deliberately out of walk order, so the sort - not the traversal -
        // has to put B before C before A.
        var a = new ChapterItem { Title = "A", PageIndex = 3, Depth = 0, SourceOrder = 7 };
        var skipped = new ChapterItem { Title = "skipped", PageIndex = null, Depth = 1, SourceOrder = 8, Parent = a };
        var deep = new ChapterItem { Title = "deep", PageIndex = 1, Depth = 2, SourceOrder = 9, Parent = skipped };
        var child = new ChapterItem { Title = "child", PageIndex = 3, Depth = 1, SourceOrder = 1, Parent = a };
        var b = new ChapterItem { Title = "B", PageIndex = 3, Depth = 0, SourceOrder = 2 };
        var c = new ChapterItem { Title = "C", PageIndex = 3, Depth = 0, SourceOrder = 5 };
        a.Children.Add(skipped);
        skipped.Children.Add(deep);
        a.Children.Add(child);

        var wrong = new List<string>();
        var flat = ChapterItem.FlattenNavigable(new List<ChapterItem> { a, b, c });
        var expected = new[] { deep, b, c, a, child };
        if (!flat.SequenceEqual(expected))
            wrong.Add($"got [{string.Join(", ", flat.Select(i => i.Title))}], expected [{string.Join(", ", expected.Select(i => i.Title))}]");
        if (flat.Contains(skipped)) wrong.Add("a node without a destination was included");
        if (!flat.Contains(deep)) wrong.Add("the child of a node without a destination was dropped");
        if (ChapterItem.FlattenNavigable(new List<ChapterItem>()).Count != 0) wrong.Add("an empty outline did not flatten to nothing");
        return CheckKit.Tally(name, 4, wrong);
    }
}
