using System.Globalization;
using System.IO;
using System.Windows;
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

    [STAThread]
    internal static int Main(string[] args)
    {
        int pages = args.Length > 0 && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? Math.Clamp(n, 1, 20_000)
            : 600;

        var pdf = Path.Combine(Path.GetTempPath(), $"hangprobe-{pages}p.pdf");
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
                _exitCode = await RunAsync(pages, pdf);
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
        return _exitCode;
    }

    private static async Task<int> RunAsync(int pages, string pdf)
    {
        using var watch = new UiWatchdog(Dispatcher.CurrentDispatcher);
        var results = new List<Stall>();

        var window = (MainWindow)Application.Current.MainWindow;
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
        results.Add(await watch.MeasureAsync("open print preview", Ms(300), () =>
        {
            preview = new PrintPreviewWindow(doc, 0) { Owner = window };
            preview.Show();
        }));
        // Printer discovery moved off the UI thread, so verify it still lands in the UI —
        // a silent failure here would leave the Print button dead.
        var printerChecks = PrintPreviewChecks(preview!);
        results.Add(await watch.MeasureAsync("close print preview", Ms(400), () => preview!.Close()));

        // The job renders every selected page at 300 DPI. Same paginator and XPS path a
        // real print takes, written to a file so the probe needs no printer.
        var xps = Path.Combine(Path.GetTempPath(), "hangprobe-print.xps");
        var printPages = Enumerable.Range(0, Math.Min(25, pages)).ToList();
        results.Add(await watch.MeasureAsync($"print {printPages.Count} pages at 300 dpi", Ms(400),
            () => PrintJob.WriteXpsAsync(doc, printPages, PrintJob.FallbackPaper, xps)));

        var checks = await LayoutChecks.RunAsync(window, pages, watch.SettleAsync);
        checks.AddRange(printerChecks);
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

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    private static int Report(List<Stall> results, List<Check> checks)
    {
        Console.WriteLine();
        Console.WriteLine($"{"scenario",-38} {"elapsed",10} {"worst stall",13} {"budget",9}  result");
        Console.WriteLine(new string('-', 88));

        int failed = 0;
        foreach (var r in results)
        {
            if (r.Failed) failed++;
            Console.WriteLine($"{r.Scenario,-38} {r.Duration.TotalMilliseconds,8:F0}ms " +
                              $"{r.WorstStall.TotalMilliseconds,11:F0}ms {r.Budget.TotalMilliseconds,7:F0}ms  " +
                              (r.Failed ? "HANG" : "ok"));
        }

        Console.WriteLine();
        Console.WriteLine("layout checks");
        Console.WriteLine(new string('-', 88));
        foreach (var c in checks)
        {
            if (!c.Ok) failed++;
            Console.WriteLine($"{(c.Ok ? "ok  " : "FAIL")} {c.Name,-38} {c.Detail}");
        }

        int total = results.Count + checks.Count;
        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? $"PASS — {total} checks, no UI-thread stall over budget."
            : $"FAIL — {failed} of {total} checks failed.");
        return failed == 0 ? 0 : 1;
    }
}
