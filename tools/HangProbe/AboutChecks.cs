using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using GreenYoga.Support;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using PdfLiteViewer;

namespace HangProbe;

/// <summary>
/// Regression checks for native support, accessibility, RTL, validation and delivery states.
/// </summary>
internal static class AboutChecks
{
    public static async Task<List<Check>> RunAsync()
    {
        var checks = new List<Check>();
        checks.AddRange(PolicyChecks());

        try
        {
            checks.AddRange(await WindowChecksAsync());
        }
        catch (Exception ex)
        {
            checks.Add(new Check("about window checks ran", false, ex.ToString()));
        }

        checks.AddRange(await CheckKit.RunManyAsync("about window extended checks ran", ExtendedChecksAsync));
        checks.Add(RtlCheck());
        return checks;
    }

    private static IEnumerable<Check> PolicyChecks()
    {
        yield return new Check("external-shell gate takes only web URLs",
            SupportNavigationPolicy.IsSafeExternalUrl("https://example.com") &&
            SupportNavigationPolicy.IsSafeExternalUrl("http://example.com") &&
            !SupportNavigationPolicy.IsSafeExternalUrl(null) &&
            !SupportNavigationPolicy.IsSafeExternalUrl("file:///C:/x") &&
            !SupportNavigationPolicy.IsSafeExternalUrl("javascript:alert(1)"), "About links remain gated");
    }

    // ---------- AboutWindow (UI) ----------

    private static async Task<List<Check>> WindowChecksAsync()
    {
        var checks = new List<Check>();
        int posts = 0;
        var pending = new TaskCompletionSource<HttpResponseMessage>();
        var handler = new SupportReply(async (request, ct) => {
            posts++;
            if (posts == 1) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
            return await pending.Task.WaitAsync(ct);
        });
        var about = new AboutWindow(new AppSupportClient(new HttpClient(handler)));
        try
        {
            about.Show();
            await PumpAsync();

            var version = AboutWindow.AppVersion();
            checks.Add(new Check("about: runtime version derived", Regex.IsMatch(version, @"^\d+\.\d+\.\d+$"),
                $"AppVersion() = '{version}'"));
            checks.Add(new Check("about: version shown", about.VersionText.Text.Contains(version),
                $"version line reads '{about.VersionText.Text}'"));

            checks.Add(new Check("about: brand mark has an accessible name",
                !string.IsNullOrWhiteSpace(AutomationProperties.GetName(about.BrandMark)),
                $"name = '{AutomationProperties.GetName(about.BrandMark)}'"));

            // The mark must actually decode — a bad pack URI renders silently blank
            // (that is how the entry-assembly-relative URI bug slipped out).
            try
            {
                var mark = about.BrandMark.Source as System.Windows.Media.Imaging.BitmapSource;
                checks.Add(new Check("about: brand mark bitmap decodes",
                    mark is not null && mark.PixelWidth > 100,
                    mark is null ? "Image.Source is null" : $"{mark.PixelWidth}x{mark.PixelHeight}"));
            }
            catch (Exception ex)
            {
                checks.Add(new Check("about: brand mark bitmap decodes", false, ex.Message));
            }

            var unnamed = new List<string>();
            foreach (var button in FindChildren<Button>(about))
            {
                bool named = !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)) ||
                             button.Content is string { Length: > 0 };
                if (!named) unnamed.Add(button.Name is { Length: > 0 } n ? n : "(anonymous)");
            }
            checks.Add(new Check("about: every button announces a name", unnamed.Count == 0,
                unnamed.Count == 0 ? "all buttons carry text content or an automation name"
                                   : string.Join(", ", unnamed)));

            checks.Add(new Check("about: initial keyboard focus", about.ContactSupportBtn.IsFocused,
                "Contact support is the focused element on open"));

            // License panel: loads the embedded MIT text on first toggle.
            about.LicenseToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await PumpAsync();
            checks.Add(new Check("about: license text loads offline",
                about.LicenseText.Visibility == Visibility.Visible && about.LicenseText.Text.Contains("MIT License"),
                $"{about.LicenseText.Text.Length} chars shown"));

            about.LicenseToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await PumpAsync();
            checks.Add(new Check("about: license hides again",
                about.LicenseText.Visibility == Visibility.Collapsed
                && about.LicenseToggle.Content as string == Strings.Get("AboutViewLicense"),
                $"visibility={about.LicenseText.Visibility}, toggle='{about.LicenseToggle.Content}'"));

            about.OpenSupportPane();
            await PumpAsync();
            checks.Add(new Check("support: native form opens offline", posts == 0 &&
                about.SupportForm.Visibility == Visibility.Visible && about.SupportNameInput.IsFocused,
                "opening support performs no HTTP request"));
            checks.Add(new Check("support: Send visible at default window size",
                IsInSupportViewport(about, about.SupportSendBtn),
                $"window {about.ActualWidth}x{about.ActualHeight}, viewport {about.SupportPanel.ViewportHeight:F1}"));
            var defaultHeight = about.Height;
            about.Height = 620;
            about.SupportPanel.ScrollToBottom();
            await PumpAsync();
            await Foreground.TakeAsync(about);
            await about.SendSupportAsync();
            await PumpAsync();
            checks.Add(new Check("support: invalid field visible after bottom submission",
                about.SupportNameInput.IsKeyboardFocused && IsInSupportViewport(about, about.SupportNameInput),
                $"focused={about.SupportNameInput.IsKeyboardFocused}, offset={about.SupportPanel.VerticalOffset:F1}"));
            about.Height = defaultHeight;
            await PumpAsync();
            checks.Add(new Check("support: required fields prevent submission", posts == 0 &&
                about.SupportStatusText.Text == Strings.Get("SupportRequired"), "invalid form stays local"));
            about.SupportNameInput.Text = "Ada";
            about.SupportEmailInput.Text = "ada@example.com";
            about.SupportSubjectInput.Text = "Question";
            about.SupportMessageInput.Text = "Please help.";
            about.BackBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            about.OpenSupportPane();
            checks.Add(new Check("support: back preserves draft", about.SupportMessageInput.Text == "Please help.", "draft retained"));
            var id = about.CurrentSupportRequest().RequestId;
            await about.SendSupportAsync();
            checks.Add(new Check("support: failure retains fields and request id",
                !about.SupportSubmitted && about.SupportMessageInput.Text == "Please help." &&
                about.CurrentSupportRequest().RequestId == id && about.SupportSendBtn.IsEnabled,
                "manual retry uses the original id"));
            var sending = about.SendSupportAsync();
            await about.SendSupportAsync();
            checks.Add(new Check("support: double sends blocked", posts == 2 && !about.SupportForm.IsEnabled,
                "one HTTP request is in flight"));
            pending.SetResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(JsonSerializer.Serialize(new { success = true, requestId = id })) });
            await sending;
            checks.Add(new Check("support: matching acknowledgment shows reference", about.SupportSubmitted &&
                about.SupportStatusText.Text.Contains(id) && about.SupportAnotherBtn.Visibility == Visibility.Visible,
                "success requires the request reference"));
            about.SupportAnotherBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            checks.Add(new Check("support: send another starts a new draft", !about.SupportSubmitted &&
                about.SupportMessageInput.Text.Length == 0 && about.CurrentSupportRequest().RequestId != id, "fresh draft"));

        }
        finally
        {
            about.Close();
        }
        bool cancelledOnClose = false;
        var closing = new AboutWindow(new AppSupportClient(new HttpClient(new SupportReply(async (_, token) => {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelledOnClose = true; throw; }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }))));
        closing.Show();
        closing.OpenSupportPane();
        closing.SupportNameInput.Text = "Ada";
        closing.SupportEmailInput.Text = "ada@example.com";
        closing.SupportSubjectInput.Text = "Question";
        closing.SupportMessageInput.Text = "Help please.";
        var closingSend = closing.SendSupportAsync();
        closing.Close();
        await closingSend;
        checks.Add(new Check("support: close cancels safely", cancelledOnClose, "in-flight send cancelled without touching closed controls"));
        return checks;
    }

    /// <summary>Under an RTL culture the window must mirror; the check restores the culture.</summary>
    private static Check RtlCheck()
    {
        var previous = Thread.CurrentThread.CurrentUICulture;
        try
        {
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("ar");
            var rtl = new AboutWindow();
            var direction = rtl.FlowDirection;
            rtl.Close();
            return new Check("about: mirrors under Arabic", direction == FlowDirection.RightToLeft,
                $"FlowDirection = {direction}");
        }
        catch (Exception ex)
        {
            return new Check("about: mirrors under Arabic", false, ex.Message);
        }
        finally
        {
            Thread.CurrentThread.CurrentUICulture = previous;
        }
    }

    // ---------- AboutWindow: launches, version, license, wiring and the support states ----------

    private static async Task<IEnumerable<Check>> ExtendedChecksAsync()
    {
        var checks = new List<Check>
        {
            CheckKit.Run("about: Website, Software and Privacy launch their pages", LinkButtons),
            CheckKit.Run("about: only web URLs reach the shell", OnlyWebUrlsLaunch),
        };
        checks.Add(await CheckKit.RunAsync("about: a failing launch is logged, not fatal", FailingLaunchLoggedAsync));
        checks.Add(CheckKit.Run("about: version comes from the informational version", VersionFromAssembly));
        checks.Add(await CheckKit.RunAsync("about: license falls back when the resource is unusable", LicenseFallbackAsync));
        checks.Add(await CheckKit.RunAsync("about: Close, Contact support and Send buttons are wired", ButtonWiringAsync));
        checks.Add(await CheckKit.RunAsync("support: invalid form focuses the first missing field", InvalidFocusCascadeAsync));
        checks.Add(await CheckKit.RunAsync("support: each server answer shows its own message", ServerAnswersAsync));
        checks.Add(await CheckKit.RunAsync("support: after success the pane offers Send another and refuses a resend", AfterSuccessAsync));
        checks.Add(await CheckKit.RunAsync("support: closing during a send that ignores cancellation is safe", ClosedDuringUncancellableSendAsync));
        checks.Add(await CheckKit.RunAsync("support: invariant culture reports locale en", LocaleAsync));
        return checks;
    }

    private static void Click(System.Windows.Controls.Primitives.ButtonBase button) =>
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    /// <summary>Focus moved to <paramref name="element"/>: keyboard focus when the window is active, logical focus when something else holds the foreground.</summary>
    private static bool HasFocus(Window window, UIElement element) =>
        element.IsKeyboardFocused || ReferenceEquals(System.Windows.Input.FocusManager.GetFocusedElement(window), element);

    private static void CloseQuietly(Window window)
    {
        try { window.Close(); } catch { /* already closed */ }
    }

    private static AppSupportClient ClientFor(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) =>
        new(new HttpClient(new SupportReply(reply)));

    /// <summary>A window on screen, active, with the support pane open.</summary>
    private static async Task<AboutWindow> ShowSupportAsync(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply)
    {
        var about = new AboutWindow(ClientFor(reply));
        about.Show();
        about.Activate();
        await PumpAsync();
        about.OpenSupportPane();
        await PumpAsync();
        return about;
    }

    private static void Fill(AboutWindow about, string name = "Ada", string email = "ada@example.com",
        string subject = "Question", string message = "Please help.")
    {
        about.SupportNameInput.Text = name;
        about.SupportEmailInput.Text = email;
        about.SupportSubjectInput.Text = subject;
        about.SupportMessageInput.Text = message;
    }

    private static Check LinkButtons()
    {
        const string name = "about: Website, Software and Privacy launch their pages";
        var launched = new List<ProcessStartInfo>();
        var original = AboutWindow.Launch;
        var about = new AboutWindow(ClientFor((_, _) => throw new InvalidOperationException("this check never sends")));
        try
        {
            AboutWindow.Launch = start => { launched.Add(start); return null; };
            Click(about.WebsiteLink);
            Click(about.SoftwareLink);
            Click(about.PrivacyLink);
        }
        finally
        {
            AboutWindow.Launch = original;
            CloseQuietly(about);
        }

        var expected = new[] { SupportNavigationPolicy.WebsiteUrl, SupportNavigationPolicy.SoftwareUrl, SupportNavigationPolicy.PrivacyUrl };
        var wrong = new List<string>();
        if (launched.Count != expected.Length) wrong.Add($"{launched.Count} launches, expected {expected.Length}");
        for (int i = 0; i < Math.Min(launched.Count, expected.Length); i++)
        {
            if (launched[i].FileName != expected[i]) wrong.Add($"button {i + 1} launched '{launched[i].FileName}', expected '{expected[i]}'");
            if (!launched[i].UseShellExecute) wrong.Add($"button {i + 1} did not ask for the shell");
        }
        if (!ReferenceEquals(AboutWindow.Launch, original)) wrong.Add("the Launch seam was not restored");
        return CheckKit.Tally(name, expected.Length, wrong);
    }

    private static Check OnlyWebUrlsLaunch()
    {
        const string name = "about: only web URLs reach the shell";
        var launched = new List<string>();
        var original = AboutWindow.Launch;
        var cases = new (string Url, bool Launches)[]
        {
            ("https://example.com/", true),
            ("http://example.com/page?q=1", true),
            ("file:///C:/Windows/notepad.exe", false),
            (@"C:\Windows\notepad.exe", false),
            (@"\\server\share\tool.exe", false),
            ("javascript:alert(1)", false),
            ("ftp://example.com/file", false),
            ("mailto:ada@example.com", false),
            ("", false),
            ("not a url", false),
        };
        var wrong = new List<string>();
        try
        {
            AboutWindow.Launch = start => { launched.Add(start.FileName); return null; };
            foreach (var (url, launches) in cases)
            {
                int before = launched.Count;
                AboutWindow.OpenExternal(url);
                bool did = launched.Count == before + 1;
                if (did != launches) wrong.Add($"'{url}' {(did ? "launched" : "was refused")}, expected {(launches ? "a launch" : "a refusal")}");
            }
        }
        finally
        {
            AboutWindow.Launch = original;
        }
        return CheckKit.Tally(name, cases.Length, wrong);
    }

    private static async Task<Check> FailingLaunchLoggedAsync()
    {
        const string name = "about: a failing launch is logged, not fatal";
        var marker = ProbeLog.NewMarker("probe-launch");
        var original = AboutWindow.Launch;
        try
        {
            AboutWindow.Launch = _ => throw new InvalidOperationException(marker);
            AboutWindow.OpenExternal("https://example.com/");
        }
        finally
        {
            AboutWindow.Launch = original;
        }
        bool logged = await ProbeLog.UntilContainsAsync(marker, TimeSpan.FromSeconds(5));
        return new Check(name, logged, logged ? "OpenExternal returned and the exception is in the log" : $"'{marker}' never reached {ProbeLog.Path}");
    }

    private static Check VersionFromAssembly()
    {
        const string name = "about: version comes from the informational version";
        var cases = new (string Label, Assembly Assembly, string Expected)[]
        {
            ("informational with +metadata", new FakeAssembly("1.2.3+abc123", null), "1.2.3"),
            ("prerelease with +metadata, ignoring the assembly version", new FakeAssembly("2.0.0-beta.1+sha.5114f85", new Version(9, 9, 9, 9)), "2.0.0-beta.1"),
            ("informational without metadata", new FakeAssembly("4.5.6", null), "4.5.6"),
            ("empty informational falls back to the assembly version", new FakeAssembly("", new Version(3, 4, 5, 6)), "3.4.5"),
            ("only an assembly version", new FakeAssembly(null, new Version(2, 3, 4, 5)), "2.3.4"),
            ("a two-part assembly version has no build number", new FakeAssembly(null, new Version(2, 3)), "2.3.0"),
            ("no version at all", new FakeAssembly(null, null), "?"),
        };
        var wrong = new List<string>();
        foreach (var (label, assembly, expected) in cases)
        {
            var got = AboutWindow.AppVersion(assembly);
            if (got != expected) wrong.Add($"{label}: '{got}', expected '{expected}'");
        }
        return CheckKit.Tally(name, cases.Length, wrong);
    }

    private static async Task<Check> LicenseFallbackAsync()
    {
        const string name = "about: license falls back when the resource is unusable";
        var wrong = new List<string>();
        static bool IsFallback(string text) => text.StartsWith("MIT License", StringComparison.Ordinal) && text.Contains("opensource.org");

        var served = new FakeAssembly(null, null, _ => new MemoryStream("Custom license\nsecond line"u8.ToArray()));
        var text = AboutWindow.ReadLicense(served, "PdfLiteViewer.LICENSE");
        if (text != "Custom license\nsecond line") wrong.Add($"a readable resource came back as '{text}'");
        if (served.RequestedResource != "PdfLiteViewer.LICENSE") wrong.Add($"asked for '{served.RequestedResource}'");

        var missing = AboutWindow.ReadLicense(new FakeAssembly(null, null, _ => null), "PdfLiteViewer.LICENSE");
        if (!IsFallback(missing)) wrong.Add($"a missing resource gave '{missing}'");

        var openMarker = ProbeLog.NewMarker("probe-license-open");
        var opening = AboutWindow.ReadLicense(
            new FakeAssembly(null, null, _ => throw new IOException(openMarker)), "PdfLiteViewer.LICENSE");
        if (!IsFallback(opening)) wrong.Add($"a failing open gave '{opening}'");

        var readMarker = ProbeLog.NewMarker("probe-license-read");
        var reading = AboutWindow.ReadLicense(
            new FakeAssembly(null, null, _ => new SupportClientChecks.ReadScript((_, _) => throw new IOException(readMarker))),
            "PdfLiteViewer.LICENSE");
        if (!IsFallback(reading)) wrong.Add($"a failing read gave '{reading}'");

        if (!await ProbeLog.UntilContainsAsync(openMarker, TimeSpan.FromSeconds(5))) wrong.Add("the open failure was not logged");
        if (!await ProbeLog.UntilContainsAsync(readMarker, TimeSpan.FromSeconds(5))) wrong.Add("the read failure was not logged");
        return CheckKit.Tally(name, 4, wrong, "both failures are in the log");
    }

    private static async Task<Check> ButtonWiringAsync()
    {
        const string name = "about: Close, Contact support and Send buttons are wired";
        int posts = 0;
        bool closed = false;
        var about = await ShowSupportAsync((_, _) => { posts++; throw new HttpRequestException("offline"); });
        about.Closed += (_, _) => closed = true;
        try
        {
            // The pane was opened for the helper; go back so the Contact support button is the way in.
            Click(about.BackBtn);
            await PumpAsync();
            bool onAbout = about.AboutPanel.Visibility == Visibility.Visible && about.SupportPanel.Visibility == Visibility.Collapsed;

            Click(about.ContactSupportBtn);
            await PumpAsync();
            bool contact = about.SupportPanel.Visibility == Visibility.Visible
                           && about.AboutPanel.Visibility == Visibility.Collapsed
                           && about.BackBtn.Visibility == Visibility.Visible
                           && HasFocus(about, about.SupportNameInput);

            // An empty form: the click reaches SendSupportAsync, which refuses it locally.
            Click(about.SupportSendBtn);
            bool shown = await Poll.UntilAsync(() => about.SupportStatusText.Visibility == Visibility.Visible, TimeSpan.FromSeconds(5));
            bool send = shown && about.SupportStatusText.Text == Strings.Get("SupportRequired") && posts == 0;

            Click(about.CloseBtn);
            bool closedByButton = await Poll.UntilAsync(() => closed, TimeSpan.FromSeconds(5));
            bool ok = onAbout && contact && send && closedByButton;
            return new Check(name, ok,
                $"back to About={onAbout}, Contact support opens the form={contact}, Send click validated locally={send} (posts {posts}), Close closed the window={closedByButton}");
        }
        finally
        {
            CloseQuietly(about);
        }
    }

    private static async Task<Check> InvalidFocusCascadeAsync()
    {
        const string name = "support: invalid form focuses the first missing field";
        int posts = 0;
        var about = await ShowSupportAsync((_, _) => { posts++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); });
        try
        {
            var cases = new (string Label, string Name, string Email, string Subject, string Message, Func<AboutWindow, TextBox> Target, string Key)[]
            {
                ("name blank", "", "ada@example.com", "Q", "M", w => w.SupportNameInput, "SupportRequired"),
                ("name only spaces", "   ", "ada@example.com", "Q", "M", w => w.SupportNameInput, "SupportRequired"),
                ("email blank", "Ada", "", "Q", "M", w => w.SupportEmailInput, "SupportRequired"),
                ("email malformed", "Ada", "not-an-email", "Q", "M", w => w.SupportEmailInput, "SupportInvalidEmail"),
                ("email without a dot in the domain", "Ada", "ada@localhost", "Q", "M", w => w.SupportEmailInput, "SupportInvalidEmail"),
                ("subject blank", "Ada", "ada@example.com", "", "M", w => w.SupportSubjectInput, "SupportRequired"),
                ("message blank", "Ada", "ada@example.com", "Q", "", w => w.SupportMessageInput, "SupportRequired"),
                ("name wins over a bad email", "", "bad", "", "", w => w.SupportNameInput, "SupportRequired"),
                ("a bad email waits behind a blank subject", "Ada", "bad", "", "M", w => w.SupportSubjectInput, "SupportRequired"),
            };
            var wrong = new List<string>();
            foreach (var (label, who, email, subject, message, target, key) in cases)
            {
                Fill(about, who, email, subject, message);
                await about.SendSupportAsync();
                var expectedField = target(about);
                if (!HasFocus(about, expectedField)) wrong.Add($"{label}: focus is not on {expectedField.Name}");
                if (about.SupportStatusText.Text != Strings.Get(key)) wrong.Add($"{label}: status '{about.SupportStatusText.Text}', expected the {key} text");
                if (about.SupportStatusText.Visibility != Visibility.Visible) wrong.Add($"{label}: status is not shown");
            }
            if (posts != 0) wrong.Add($"{posts} post(s) left the window");
            return CheckKit.Tally(name, cases.Length, wrong);
        }
        finally
        {
            CloseQuietly(about);
        }
    }

    private static async Task<Check> ServerAnswersAsync()
    {
        const string name = "support: each server answer shows its own message";
        var table = new (HttpStatusCode? Status, string? Error, string Key)[]
        {
            (HttpStatusCode.BadRequest, "invalid-email", "SupportInvalidEmail"),
            (HttpStatusCode.BadRequest, "invalid-request", "SupportRequired"),
            (HttpStatusCode.Conflict, "request-in-progress", "SupportInProgress"),
            (HttpStatusCode.Conflict, "request-conflict", "SupportConflict"),
            (HttpStatusCode.TooManyRequests, "rate-limited", "SupportRateLimited"),
            (HttpStatusCode.ServiceUnavailable, "delivery-failed", "SupportSendFailed"),
            (HttpStatusCode.BadRequest, "something-new", "SupportSendFailed"),
            (null, null, "SupportSendFailed"),     // the connection fails outright
        };
        int row = 0;
        var about = await ShowSupportAsync((_, _) =>
        {
            var (status, error, _) = table[row];
            if (status is null) throw new HttpRequestException("offline");
            return Task.FromResult(new HttpResponseMessage(status.Value) { Content = new StringContent(JsonSerializer.Serialize(new { error })) });
        });
        try
        {
            Fill(about);
            var wrong = new List<string>();
            var shown = new List<string>();
            for (row = 0; row < table.Length; row++)
            {
                await about.SendSupportAsync();
                var (status, error, key) = table[row];
                string label = status is null ? "connection failure" : $"({(int)status}, {error})";
                if (about.SupportStatusText.Text != Strings.Get(key)) wrong.Add($"{label}: '{about.SupportStatusText.Text}', expected the {key} text");
                if (about.SupportSubmitted) wrong.Add($"{label}: marked as submitted");
                if (!about.SupportForm.IsEnabled || !about.BackBtn.IsEnabled || (string)about.SupportSendBtn.Content != Strings.Get("SupportSend"))
                    wrong.Add($"{label}: the form was not handed back");
                shown.Add(about.SupportStatusText.Text);
            }
            // Different failures must not read the same, apart from the ones that share a cause.
            var distinctKeys = table.Select(t => t.Key).Distinct().Count();
            if (shown.Distinct().Count() != distinctKeys)
                wrong.Add($"{shown.Distinct().Count()} distinct messages for {distinctKeys} distinct causes");
            return CheckKit.Tally(name, table.Length, wrong);
        }
        finally
        {
            CloseQuietly(about);
        }
    }

    private static async Task<Check> AfterSuccessAsync()
    {
        const string name = "support: after success the pane offers Send another and refuses a resend";
        int posts = 0;
        string id = "";
        var about = await ShowSupportAsync((_, _) =>
        {
            posts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { success = true, requestId = id })),
            });
        });
        try
        {
            Fill(about);
            id = about.CurrentSupportRequest().RequestId;
            await about.SendSupportAsync();
            bool sent = about.SupportSubmitted && posts == 1;

            // Back to About and in again: the form is gone, Send another is the way forward.
            Click(about.BackBtn);
            await PumpAsync();
            about.OpenSupportPane();
            await PumpAsync();
            bool offers = HasFocus(about, about.SupportAnotherBtn) && about.SupportForm.Visibility == Visibility.Collapsed;

            string statusBefore = about.SupportStatusText.Text;
            await about.SendSupportAsync();
            bool refused = posts == 1 && about.SupportStatusText.Text == statusBefore;
            return new Check(name, sent && offers && refused,
                $"sent={sent} (posts {posts}), Send another focused and the form hidden={offers}, second send refused without a post={refused}");
        }
        finally
        {
            CloseQuietly(about);
        }
    }

    private static async Task<Check> ClosedDuringUncancellableSendAsync()
    {
        const string name = "support: closing during a send that ignores cancellation is safe";
        int posts = 0;
        string id = "";
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var about = await ShowSupportAsync(async (_, _) =>
        {
            posts++;
            entered.TrySetResult();
            await release.Task;     // deliberately not the token: the window is already closed when this returns
            var body = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { success = true, requestId = id }));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new SupportClientChecks.ScriptedContent(SupportClientChecks.Chunked(body, 1024, honorCancellation: false)),
            };
        });
        try
        {
            Fill(about);
            id = about.CurrentSupportRequest().RequestId;
            var send = about.SendSupportAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            about.Close();
            release.SetResult();

            bool finished = await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(10))) == send;
            if (finished) await send;     // a throw here is the failure this check exists for
            bool notSubmitted = !about.SupportSubmitted;

            int before = posts;
            await about.SendSupportAsync();
            bool refusedAfterClose = posts == before;
            return new Check(name, finished && notSubmitted && refusedAfterClose,
                $"send finished after close={finished}, success ignored (not submitted)={notSubmitted}, send after close posted nothing={refusedAfterClose}");
        }
        finally
        {
            release.TrySetResult();
            CloseQuietly(about);
        }
    }

    private static async Task<Check> LocaleAsync()
    {
        const string name = "support: invariant culture reports locale en";
        var about = await ShowSupportAsync((_, _) => throw new InvalidOperationException("this check never sends"));
        var previous = Thread.CurrentThread.CurrentUICulture;
        try
        {
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;
            var invariant = about.CurrentSupportRequest().Locale;
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("de-DE");
            var german = about.CurrentSupportRequest().Locale;
            bool ok = invariant == "en" && german == "de-DE";
            return new Check(name, ok, $"invariant -> '{invariant}', de-DE -> '{german}'");
        }
        finally
        {
            Thread.CurrentThread.CurrentUICulture = previous;
            CloseQuietly(about);
        }
    }

    /// <summary>An assembly the About window can be pointed at, with a chosen version, attributes and manifest resource.</summary>
    private sealed class FakeAssembly(string? informational, Version? version, Func<string, Stream?>? resource = null) : Assembly
    {
        public string? RequestedResource { get; private set; }

        public override object[] GetCustomAttributes(Type attributeType, bool inherit)
        {
            // Attribute.GetCustomAttributes casts the result to Attribute[], so it must be the right array type.
            int count = attributeType == typeof(AssemblyInformationalVersionAttribute) && informational is not null ? 1 : 0;
            var found = (object[])Array.CreateInstance(attributeType, count);
            if (count == 1) found[0] = new AssemblyInformationalVersionAttribute(informational!);
            return found;
        }

        public override AssemblyName GetName() => new("FakeAssembly") { Version = version };

        public override Stream? GetManifestResourceStream(string name)
        {
            RequestedResource = name;
            return resource?.Invoke(name);
        }
    }

    internal sealed class SupportReply(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => reply(request, token);
    }

    private static bool IsInSupportViewport(AboutWindow window, FrameworkElement element)
    {
        var bounds = element.TransformToAncestor(window.SupportPanel)
            .TransformBounds(new Rect(element.RenderSize));
        return new Rect(0, 0, window.SupportPanel.ViewportWidth, window.SupportPanel.ViewportHeight).Contains(bounds);
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(120);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    internal static IEnumerable<T> FindChildren<T>(DependencyObject root) where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindChildren<T>(child)) yield return nested;
        }
    }
}
