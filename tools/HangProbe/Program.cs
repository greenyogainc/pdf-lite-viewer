using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PdfLiteViewer;

namespace HangProbe;

/// <summary>
/// Regression probe for UI-thread hangs.
///
/// It drives the real MainWindow through the operations that can block the message
/// pump — opening a large document, switching to continuous mode, zooming, rotating,
/// jumping to the end, opening the print preview (which enumerates printers) — while
/// a watchdog thread measures how long the UI thread takes to service queued input.
/// Any scenario whose worst stall exceeds its budget fails the run.
///
///   dotnet run --project tools\HangProbe -- [pageCount]
///
/// Needs an interactive desktop session (it really shows windows).
/// </summary>
internal static class Program
{
    private static int _exitCode;
    private static bool _coverage;

    /// <summary>Answers the process's native dialogs; checks register what they expect through it.</summary>
    internal static DialogDismisser Dialogs { get; private set; } = null!;

    [STAThread]
    internal static int Main(string[] args)
    {
        // First statement, before any window exists: this machine's default printer is a
        // physical one, and the preview window selects it on its own.
        PrintJob.Submit = (_, _, _) =>
            throw new InvalidOperationException("HangProbe must never reach a real print queue.");

        // Before the first window too: a modal dialog nothing answers would hang the run.
        Dialogs = new DialogDismisser();

        // Instrumented code is slower, so stall budgets are reported but do not fail the run.
        _coverage = args.Contains("--coverage");

        int pages = args.Length > 0 && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? Math.Clamp(n, 1, 20_000)
            : 600;

        var pdf = Path.Combine(Path.GetTempPath(), $"hangprobe-{pages}p-{Guid.NewGuid():N}.pdf");
        StressPdf.Create(pages, pdf);
        Console.WriteLine($"stress document: {pdf} ({pages} pages, {new FileInfo(pdf).Length / 1024} KB)");

        // The real App — production application resources, production startup path
        // (its StartupUri puts up the MainWindow the probe then drives).
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ApplicationIdle: runs once the app has finished starting and its window is up.
        Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (args.Contains("--about-check"))
                    _exitCode = ReportChecks(await AboutChecks.RunAsync());
                else if (args.Contains("--startup-check"))
                    _exitCode = ReportChecks(await StartupChecksAsync(args));
                else _exitCode = await RunAsync(pages, pdf);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"probe failed: {ex}");
                _exitCode = 2;
            }
            finally
            {
                app.Shutdown();
            }
        }, DispatcherPriority.ApplicationIdle);

        app.Run();
        Dialogs.Dispose();

        // Guid-named per run, so nothing overwrites it next time: remove it here or every
        // run leaves another copy (up to ~19 MB at the 20k-page cap) behind in %TEMP%.
        // PdfDoc reads the whole file into memory, so no handle is held on it by now.
        TryDelete(pdf);
        return _exitCode;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException ex) { Console.Error.WriteLine($"could not delete {path}: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { Console.Error.WriteLine($"could not delete {path}: {ex.Message}"); }
    }

    private static async Task<int> RunAsync(int pages, string pdf)
    {
        using var watch = new UiWatchdog(Dispatcher.CurrentDispatcher);
        var results = new List<Stall>();

        var window = (MainWindow)Application.Current.MainWindow;

        // Mirror StoreShots's gate: the probe assumes a screen large enough to hold its
        // 1280x900 window unclipped. On a smaller display the layouts centring and scrollbar
        // checks would silently measure against a cropped viewport.
        if (SystemParameters.PrimaryScreenWidth < 1280 || SystemParameters.PrimaryScreenHeight < 900)
        {
            Console.Error.WriteLine(
                $"Primary screen is smaller than 1280x900; the probe would measure against a clipped viewport. Aborting.");
            return 2;
        }

        window.Width = 1280;
        window.Height = 900;

        // Control: with the app idle, the watchdog must see essentially no stall.
        // If this one is dirty, the numbers below mean nothing.
        results.Add(await watch.MeasureAsync("idle (control)", Ms(150), () => Task.Delay(1500)));

        results.Add(await watch.MeasureAsync($"open {pages}-page document", Ms(800),
            () => OpenAsync(window, pdf)));

        results.Add(await watch.MeasureAsync("switch to continuous (scroll) mode", Ms(400),
            () => window.SetMode(ViewMode.Continuous)));

        results.Add(await watch.MeasureAsync("zoom in x6 (continuous)", Ms(400), () =>
        {
            for (int i = 0; i < 6; i++) window.SetZoom(1.0 * Math.Pow(1.2, i + 1));
        }));

        results.Add(await watch.MeasureAsync("rotate (continuous)", Ms(400),
            () => window.RotateClockwise()));

        results.Add(await watch.MeasureAsync("jump to last page (continuous)", Ms(400),
            () => window.GoToPage(pages - 1)));

        results.Add(await watch.MeasureAsync("10 jumps, no chapter pane", Ms(400),
            () => JumpAroundAsync(window, pages)));

        // The sidebar holds one chapter per page, and every page change re-syncs the
        // highlighted chapter and scrolls the tree to it.
        results.Add(await watch.MeasureAsync($"open chapter pane ({pages} chapters)", Ms(400),
            () => window.SetChapterPaneVisible(true)));

        results.Add(await watch.MeasureAsync("10 jumps with chapters open", Ms(400),
            () => JumpAroundAsync(window, pages)));

        // Eviction must walk the retained window, never the whole document — the old
        // full scan cost O(pages) on the UI thread at every settled render update.
        await Task.Delay(300);                       // let the deferred render pass run
        await watch.SettleAsync();
        var evictionChecks = new List<Check>
        {
            new("eviction scan bounded (continuous)",
                window.LastEvictionScanLength > 0 && window.LastEvictionScanLength <= 200,
                $"last eviction pass visited {window.LastEvictionScanLength} slot(s) of {pages} pages"),
        };

        // Sidebar scrolling recycles chapter containers; a re-realized container for the
        // active chapter re-applies the TwoWay IsSelected binding and re-raises
        // SelectedItemChanged. Those echoes must never move the document (they used to
        // GoToPage the reader back to the chapter's first page). The check is
        // self-validating: it counts the echoes the guard absorbed, so a run that never
        // exercised the guarded path fails instead of passing vacuously.
        // The jumps above leave the viewport on the last pages, so "chapter + 5" falls
        // off a short outline. Park where the tree still has neighbours below.
        window.GoToPage(Math.Clamp(pages / 4, 0, Math.Max(0, pages - 6)));
        await watch.SettleAsync();
        window.Scroller.ScrollToVerticalOffset(window.Scroller.VerticalOffset + 137);
        await watch.SettleAsync();
        double offsetBefore = window.Scroller.VerticalOffset;
        // A non-numeric PageBox (e.g. "Page 1/600" added for clarity, or any future
        // localisation of the value) must fail the check loudly, not silently pivot to
        // pages / 2 and pass against an unrelated chapter.
        if (!int.TryParse(window.PageBox.Text, out int pageBoxPage))
        {
            evictionChecks.Add(new Check("sidebar scrolling never moves the document", false,
                $"PageBox text '{window.PageBox.Text}' is not a pure integer; the sidebar-echo target would have been silently re-pivoted."));
            evictionChecks.Add(new Check("selecting a different chapter navigates", false,
                $"PageBox text '{window.PageBox.Text}' is not a pure integer; cannot derive a target page."));
            return Report(results, evictionChecks);
        }
        int selectedIndex = pageBoxPage - 1;
        if (AboutChecks.FindChildren<ScrollViewer>(window.ChapterTree).FirstOrDefault() is { } treeScroller)
        {
            int echoBefore = window.ChapterEchoCount;
            // The default cache is a full viewport. Forty chapters fit in that cache, so
            // nothing is recycled and the echo guard never runs. Zero the cache for this
            // scroll only, then put it back.
            var cacheLength = VirtualizingPanel.GetCacheLength(window.ChapterTree);
            var cacheUnit = VirtualizingPanel.GetCacheLengthUnit(window.ChapterTree);
            VirtualizingPanel.SetCacheLengthUnit(window.ChapterTree, VirtualizationCacheLengthUnit.Item);
            VirtualizingPanel.SetCacheLength(window.ChapterTree, new VirtualizationCacheLength(0));
            try
            {
                window.ChapterTree.UpdateLayout();
                for (int round = 0; round < 3; round++)
                {
                    // Park far away, then bring the active chapter's container back into
                    // view: that recycle/re-realize cycle is what raises the echo.
                    treeScroller.ScrollToHome();
                    await watch.SettleAsync();
                    treeScroller.ScrollToEnd();
                    await watch.SettleAsync();
                    treeScroller.ScrollToVerticalOffset(Math.Max(0, selectedIndex - 5));   // item-scrolling: offset == index
                    await watch.SettleAsync();
                    window.ChapterTree.UpdateLayout();
                }
            }
            finally
            {
                VirtualizingPanel.SetCacheLengthUnit(window.ChapterTree, cacheUnit);
                VirtualizingPanel.SetCacheLength(window.ChapterTree, cacheLength);
            }
            int echoes = window.ChapterEchoCount - echoBefore;
            double offsetAfter = window.Scroller.VerticalOffset;
            evictionChecks.Add(new Check("sidebar scrolling never moves the document",
                echoes > 0 && Math.Abs(offsetAfter - offsetBefore) < 1.0,
                $"{echoes} echo(es) absorbed; document offset {offsetBefore:F1} -> {offsetAfter:F1}"));

            // The inverse must keep working: picking a *different* chapter navigates.
            // selectedIndex + 5 sits inside the viewport (its top is at
            // selectedIndex - 5), so the target's container is realized regardless
            // of the panel's cache configuration.
            var roots = window.ChapterTree.ItemsSource as List<ChapterItem>;
            var target = roots is not null && selectedIndex + 5 < roots.Count
                ? roots[selectedIndex + 5] : null;
            if (target?.PageIndex is int targetPage)
            {
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                target.IsSelected = true;
                await watch.SettleAsync();
                evictionChecks.Add(new Check("selecting a different chapter navigates",
                    int.TryParse(window.PageBox.Text, out int landed) && landed == targetPage + 1,
                    $"picked the chapter at page {targetPage + 1}, page box reads '{window.PageBox.Text}'"));
            }
            else
            {
                evictionChecks.Add(new Check("selecting a different chapter navigates", false,
                    "no realized target chapter available for the check"));
            }
        }
        else
        {
            // Both checks must appear in the tally — a missing scroller fails them
            // explicitly instead of silently shrinking the check count.
            evictionChecks.Add(new Check("sidebar scrolling never moves the document", false,
                "chapter tree scroller not found - the check could not run"));
            evictionChecks.Add(new Check("selecting a different chapter navigates", false,
                "chapter tree scroller not found - the check could not run"));
        }

        results.Add(await watch.MeasureAsync("switch to facing mode", Ms(400),
            () => window.SetMode(ViewMode.Facing)));

        results.Add(await watch.MeasureAsync("20 page turns (facing)", Ms(400), () =>
        {
            for (int i = 0; i < 20; i++) window.GoToPage(i * 2 + 1);
        }));

        // Print preview: printer enumeration and paper-size lookup hit the spooler,
        // which is where a machine with an offline network printer stalls hardest.
        var doc = await Task.Run(() => new PdfDoc(pdf));
        PrintPreviewWindow? preview = null;
        var printerChecks = new List<Check>();
        // The show and the checks share one try/finally, so a throw from the preview's
        // constructor, Show() or a check cannot leave a parented preview window open.
        // (MeasureAsync itself never throws on a budget overrun; it records a stall.)
        try
        {
            results.Add(await watch.MeasureAsync("open print preview", Ms(300), () =>
            {
                preview = new PrintPreviewWindow(doc, 0) { Owner = window };
                preview.Show();
            }));
            // Printer discovery moved off the UI thread, so verify it still lands in the UI —
            // a silent failure here would leave the Print button dead.
            printerChecks.AddRange(PrintPreviewChecks(preview!));
            printerChecks.AddRange(await PrintRangeUiAsync(preview!, doc.PageCount, watch.SettleAsync));
            // Measure the close path on its own row. A regression in Window.Close() (a
            // future OnClosing handler doing I/O, for example) would otherwise show up
            // only as a leaked window — not a per-scenario hang against this budget.
            results.Add(await watch.MeasureAsync("close print preview", Ms(400),
                () => preview!.Close()));
        }
        finally
        {
            // No-op after the measured close above; closes the window if anything before it threw.
            try { preview?.Close(); } catch { /* the open itself threw — nothing to close */ }
        }

        // The job renders every selected page at 300 DPI. Same paginator and XPS path a
        // real print takes, written to a file so the probe needs no printer.
        // Per-run unique suffix: the previous fixed name was shared across parallel CI shards
        // and would either race the print spooler or hand the second probe a stale file.
        var xps = Path.Combine(Path.GetTempPath(), $"hangprobe-print-{Guid.NewGuid():N}.xps");
        var printPages = Enumerable.Range(0, Math.Min(25, pages)).ToList();
        try
        {
            results.Add(await watch.MeasureAsync($"print {printPages.Count} pages at 300 dpi", Ms(400),
                () => PrintJob.WriteXpsAsync(doc, printPages, PrintJob.FallbackPaper, xps)));
        }
        finally
        {
            TryDelete(xps);     // Guid-named per run: nothing else would ever clean it up
        }

        var checks = await LayoutChecks.RunAsync(window, pages, watch.SettleAsync);
        checks.AddRange(printerChecks);
        checks.AddRange(evictionChecks);
        checks.AddRange(await PreviewRaceChecks.RunAsync(doc));
        checks.AddRange(await ContractChecks.RunAsync(window, doc, watch.SettleAsync));
        checks.AddRange(await AboutChecks.RunAsync());
        checks.AddRange(await InputChecks.RunAsync(window, pdf, watch.SettleAsync));
        checks.AddRange(await SupportClientChecks.RunAsync());
        checks.AddRange(await AppChecks.RunAsync());
        checks.AddRange(await DocumentChecks.RunAsync());
        await CaptureModesAsync(window, pages, watch.SettleAsync);

        window.Close();
        return Report(results, checks);
    }

    /// <summary>Opening is asynchronous: the parse/IO must not run on the UI thread.</summary>
    private static Task OpenAsync(MainWindow window, string path) => window.OpenFileAsync(path);

    /// <summary>
    /// Ten jumps across the document, letting the message pump run between them the way it
    /// does for a real user — so the number this reports is the stall of a single jump.
    /// </summary>
    private static async Task JumpAroundAsync(MainWindow window, int pages)
    {
        for (int i = 1; i <= 10; i++)
        {
            window.GoToPage(pages * i / 11);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

    private static List<Check> PrintPreviewChecks(PrintPreviewWindow preview)
    {
        int printers = preview.PrinterBox.Items.Count;
        if (printers == 0)
        {
            return new List<Check>
            {
                new("printer list populated", true, "no printers installed on this machine — skipped"),
            };
        }

        return new List<Check>
        {
            new("printer list populated", true, $"{printers} printer(s), selected '{preview.PrinterBox.SelectedItem}'"),
            new("print button enabled", preview.PrintBtn.IsEnabled, "Print is clickable once a printer is known"),
        };
    }

    /// <summary>
    /// Range controls on the preview that is already on screen. ParseRange itself is
    /// table-tested in ContractChecks; this covers the combo and text box wiring.
    /// </summary>
    private static async Task<List<Check>> PrintRangeUiAsync(PrintPreviewWindow preview, int pageCount, Func<Task> settle)
    {
        var checks = new List<Check>();
        checks.Add(new Check("print range: all pages",
            preview.PageLabel.Text == $"1 / {pageCount}",
            $"label '{preview.PageLabel.Text}'"));

        preview.RangeMode.SelectedIndex = 1;
        await settle();
        checks.Add(new Check("print range: current page",
            preview.PageLabel.Text == "1 / 1",
            $"label '{preview.PageLabel.Text}'"));

        preview.RangeMode.SelectedIndex = 2;
        preview.RangeBox.Text = "2-3";
        await settle();
        bool printerKnown = preview.PrinterBox.SelectedItem is not null;
        bool custom = preview.PageLabel.Text == "1 / 2"
                      && preview.RangeBox.Visibility == Visibility.Visible
                      && (!printerKnown || preview.PrintBtn.IsEnabled);
        checks.Add(new Check("print range: 2-3 selects those pages", custom,
            $"label '{preview.PageLabel.Text}', print enabled={preview.PrintBtn.IsEnabled}"));

        preview.BwCheck.IsChecked = true;
        await settle();
        await settle();
        bool gray = preview.PageImage.Source is System.Windows.Media.Imaging.FormatConvertedBitmap converted
                    && converted.Format == System.Windows.Media.PixelFormats.Gray8;
        checks.Add(new Check("print preview: grayscale renders gray", gray,
            preview.PageImage.Source is null ? "no bitmap" : preview.PageImage.Source.GetType().Name));

        var next = AboutChecks.FindChildren<System.Windows.Controls.Button>(preview)
            .FirstOrDefault(b => System.Windows.Automation.AutomationProperties.GetName(b) == Strings.Get("NextPageTooltip"));
        var prev = AboutChecks.FindChildren<System.Windows.Controls.Button>(preview)
            .FirstOrDefault(b => System.Windows.Automation.AutomationProperties.GetName(b) == Strings.Get("PrevPageTooltip"));
        next?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await settle();
        bool stepped = preview.PageLabel.Text == "2 / 2";
        prev?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await settle();
        bool right = RaiseKey(preview, Key.Right, Keyboard.KeyDownEvent);
        bool down = RaiseKey(preview, Key.PageDown, Keyboard.KeyDownEvent);
        bool left = RaiseKey(preview, Key.Left, Keyboard.KeyDownEvent);
        bool up = RaiseKey(preview, Key.PageUp, Keyboard.KeyDownEvent);
        checks.Add(new Check("print preview: buttons and keys step the page",
            next is not null && prev is not null && stepped && right && down && left && up,
            $"after next label moved={stepped}, keys handled right={right} down={down} left={left} up={up}, now '{preview.PageLabel.Text}'"));
        preview.BwCheck.IsChecked = false;

        preview.RangeBox.Text = "nope";
        await settle();
        checks.Add(new Check("print range: invalid range disables print",
            !preview.PrintBtn.IsEnabled && preview.EmptyRangeHint.Visibility == Visibility.Visible,
            $"print enabled={preview.PrintBtn.IsEnabled}, empty hint={preview.EmptyRangeHint.Visibility}"));
        return checks;
    }

    /// <summary>PNGs of each view mode, for eyeballing what the numeric checks cannot describe.</summary>
    private static async Task CaptureModesAsync(MainWindow window, int pages, Func<Task> settle)
    {
        Console.WriteLine();
        window.SetChapterPaneVisible(true);

        window.SetMode(ViewMode.Continuous);
        window.GoToPage(pages / 2);
        await settle();
        await Task.Delay(700);
        Console.WriteLine($"screenshot: {Shots.Capture(window, "continuous")}");

        window.SetMode(ViewMode.Facing);
        window.GoToPage(6);
        await settle();
        await Task.Delay(700);
        Console.WriteLine($"screenshot: {Shots.Capture(window, "facing")}");

        window.SetMode(ViewMode.Single);
        await settle();
        window.SetZoom(2.5);            // wider than the viewport: panning must be possible
        await settle();
        await Task.Delay(700);
        Console.WriteLine($"screenshot: {Shots.Capture(window, "single-zoomed")}");
        Console.WriteLine($"           zoomed panning: scrollable {window.Scroller.ScrollableWidth:F0}x" +
                          $"{window.Scroller.ScrollableHeight:F0}px");
    }

    private static bool RaiseKey(IInputElement source, Key key, RoutedEvent routed)
    {
        if (source is not Visual visual)
            throw new InvalidOperationException("key source is not a visual");
        var presentation = PresentationSource.FromVisual(visual)
            ?? throw new InvalidOperationException("key source has no presentation source");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, presentation, Environment.TickCount, key)
        {
            RoutedEvent = routed,
        };
        source.RaiseEvent(args);
        return args.Handled;
    }

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>Checks every mode ends with: the print guard is still in place, and no dialog went unplanned.</summary>
    private static void AddFinalChecks(List<Check> checks)
    {
        checks.Add(PrintGuardCheck());
        checks.AddRange(Dialogs.FinalChecks());
    }

    private static Check PrintGuardCheck()
    {
        const string name = "print guard blocks the spooler call";
        try
        {
            // The guard ignores its arguments, so nothing here can reach a queue.
            PrintJob.Submit(null!, null!, null!);
            return new Check(name, false, "PrintJob.Submit returned instead of throwing");
        }
        catch (InvalidOperationException ex)
        {
            return new Check(name, true, ex.Message);
        }
    }

    /// <summary>
    /// The production startup path: the first document on the command line is picked up by
    /// App.ApplyStartupArguments and opened by MainWindow_Loaded, with nothing from the probe.
    /// </summary>
    private static async Task<List<Check>> StartupChecksAsync(string[] args)
    {
        int flag = Array.IndexOf(args, "--startup-check");
        if (flag + 1 >= args.Length)
            return new List<Check> { new("startup: document argument opens the document", false,
                "--startup-check needs the PDF path as the next argument") };

        var expected = args[flag + 1];
        var window = (MainWindow)Application.Current.MainWindow;
        var timeout = TimeSpan.FromSeconds(30);
        bool opened = await Poll.UntilAsync(() => window.Document is not null, timeout);
        var document = window.Document;

        var checks = new List<Check>
        {
            new("startup: App picks the document argument",
                ((App)Application.Current).StartupFile == expected,
                $"StartupFile '{((App)Application.Current).StartupFile}', argument '{expected}'"),
            new("startup: document argument opens the document",
                opened && Path.GetFullPath(document!.FilePath) == Path.GetFullPath(expected),
                opened ? $"opened '{document!.FilePath}', {document.PageCount} page(s)"
                       : $"window.Document was not set within {timeout.TotalSeconds:F0} s"),
            new("startup: title names the file",
                opened && window.Title.Contains(Path.GetFileName(expected)),
                $"title '{window.Title}'"),
        };
        return checks;
    }

    /// <summary>Prints a plain list of checks (the modes that run no stall scenarios) and returns the exit code.</summary>
    private static int ReportChecks(List<Check> checks)
    {
        AddFinalChecks(checks);
        Console.WriteLine();
        Console.WriteLine("checks");
        Console.WriteLine(new string('-', 88));
        int failed = PrintChecks(checks);

        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? $"PASS — {checks.Count} checks."
            : $"FAIL — {failed} of {checks.Count} checks failed.");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Prints one line per check and returns how many failed.</summary>
    private static int PrintChecks(IEnumerable<Check> checks)
    {
        int failed = 0;
        foreach (var c in checks)
        {
            if (!c.Ok) failed++;
            Console.WriteLine($"{(c.Ok ? "ok  " : "FAIL")} {c.Name,-38} {c.Detail}");
        }
        return failed;
    }

    private static int Report(List<Stall> results, List<Check> checks)
    {
        AddFinalChecks(checks);

        Console.WriteLine();
        Console.WriteLine($"{"scenario",-38} {"elapsed",10} {"worst stall",13} {"budget",9}  result");
        Console.WriteLine(new string('-', 88));

        int failed = 0, ignored = 0;
        foreach (var r in results)
        {
            if (r.Failed)
            {
                if (_coverage) ignored++;
                else failed++;
            }
            Console.WriteLine($"{r.Scenario,-38} {r.Duration.TotalMilliseconds,8:F0}ms " +
                              $"{r.WorstStall.TotalMilliseconds,11:F0}ms {r.Budget.TotalMilliseconds,7:F0}ms  " +
                              (r.Failed ? (_coverage ? "HANG (ignored: --coverage)" : "HANG") : "ok"));
        }

        Console.WriteLine();
        Console.WriteLine("layout checks");
        Console.WriteLine(new string('-', 88));
        failed += PrintChecks(checks);

        int total = results.Count + checks.Count;
        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? ignored == 0
                ? $"PASS — {total} checks, no UI-thread stall over budget."
                : $"PASS — {total} checks; {ignored} stall(s) over budget ignored (--coverage)."
            : $"FAIL — {failed} of {total} checks failed.");
        return failed == 0 ? 0 : 1;
    }
}
