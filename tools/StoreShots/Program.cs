using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PdfLiteViewer;

namespace StoreShots;

/// <summary>
/// Rebuilds the tracked Microsoft Store screenshot set from the real, current build.
///
///   dotnet run --project tools\StoreShots -c Release [outputDir]
///
/// Drives the production windows (same InternalsVisibleTo route as tools/HangProbe)
/// through nine scenes, captures the composited screen at a consistent resolution,
/// verifies every PNG against the Store's desktop minimum (1366x768), and writes
/// captions.md next to the images. Needs an interactive desktop session, a screen of
/// at least the target resolution, with native support scenes available offline.
/// </summary>
internal static class Program
{
    private const int TargetW = 2560;
    private const int TargetH = 1440;

    private sealed record Shot(string File, string Caption);

    private static readonly List<Shot> Captured = new();
    private static int _exitCode;

    [STAThread]
    internal static int Main(string[] args)
    {
        ScreenCapture.TrySetPerMonitorV2();

        var repoRoot = FindRepoRoot();
        // First non-flag argument is the output directory; "--fill-check" must not
        // become a directory name.
        var dirArg = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        var outDir = dirArg is not null ? Path.GetFullPath(dirArg)
            : Path.Combine(repoRoot, "packaging", "store-screenshots");
        Directory.CreateDirectory(outDir);

        if (!args.Contains("--fill-check") && !args.Contains("--submit-check") &&
            (SystemParameters.PrimaryScreenWidth < TargetW || SystemParameters.PrimaryScreenHeight < TargetH))
        {
            Console.Error.WriteLine(
                $"Primary screen is smaller than {TargetW}x{TargetH}; captures would be clipped. Aborting.");
            return 2;
        }

        // The filename shows in window titles, so it must look like a document, not tooling.
        var demo = DemoPdf.Create(Path.Combine(Path.GetTempPath(), "User Guide.pdf"));
        Console.WriteLine($"demo document: {demo}");

        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // --submit-check really sends one test message; --fill-check stops short of the
        // click and only proves the native form accepts input and validates.
        bool submitCheck = args.Contains("--submit-check");
        bool fillCheck = args.Contains("--fill-check");
        Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try
            {
                _exitCode = submitCheck || fillCheck
                    ? await RunSubmitCheckAsync(clickSend: submitCheck)
                    : await RunScenesAsync(demo, outDir);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"storeshots failed: {ex}");
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

    private static async Task<int> RunScenesAsync(string demoPdf, string outDir)
    {
        // Fresh set: the old captures must not survive next to the new ones - nor the
        // captions that described them, or a run that fails midway leaves a captions.md
        // listing nine images beside a partial set of new PNGs.
        foreach (var stale in Directory.GetFiles(outDir, "*.png"))
            File.Delete(stale);
        File.Delete(Path.Combine(outDir, "captions.md"));

        var window = (MainWindow)Application.Current.MainWindow;
        Prepare(window);
        await window.OpenFileAsync(demoPdf);
        await SettleAsync();

        // --- 1: facing + chapter sidebar -----------------------------------
        window.SetMode(ViewMode.Facing);
        window.SetChapterPaneVisible(true);
        window.GoToPage(3);
        await RenderPauseAsync();
        Capture(window, outDir, "1-facing-chapters.png",
            "Facing-page reading with the chapter sidebar: the document's outline as a tree, with the current chapter highlighted as you read.");

        // --- 2: continuous scroll ------------------------------------------
        window.SetMode(ViewMode.Continuous);
        window.GoToPage(2);
        await RenderPauseAsync();
        Capture(window, outDir, "2-continuous-scroll.png",
            "Continuous scrolling through the whole document, with lazy rendering that keeps even huge PDFs fast.");

        // --- 3: print preview (generic printer target only) ----------------
        var doc = window.Document!;
        var preview = new PrintPreviewWindow(doc, 2) { Owner = window };
        preview.Show();
        await WaitUntilAsync(() => preview.PrinterBox.Items.Count > 0, TimeSpan.FromSeconds(15));
        // A generic, non-personal queue name for the screenshot. Substring match so
        // Windows variants ("Microsoft Print to PDF", "Microsoft Print to PDF (redirected)")
        // all qualify; fail the run if no PDF printer is present so the scene can't capture
        // an arbitrary local printer under the "generic" caption.
        string? pdfPrinter = null;
        foreach (var item in preview.PrinterBox.Items)
            if (item is string s && s.Contains("Print to PDF", StringComparison.OrdinalIgnoreCase))
            { pdfPrinter = s; preview.PrinterBox.SelectedItem = s; break; }
        if (pdfPrinter is null)
        {
            Console.Error.WriteLine(
                $"scene 3: no printer containing 'Print to PDF' found in the combobox (saw: " +
                $"[{string.Join(", ", preview.PrinterBox.Items.Cast<object>())}]). Aborting.");
            preview.Close();
            return 1;
        }
        await RenderPauseAsync(1400);
        Capture(window, outDir, "3-print-preview.png",
            "Built-in print preview: pick a printer, page range and copies, black & white or draft — and see exactly how each page lands on paper.");
        preview.Close();
        await SettleAsync();

        // --- 4: single page, full screen -----------------------------------
        window.SetMode(ViewMode.Single);
        window.GoToPage(4);
        window.ToggleFullscreen();
        await RenderPauseAsync();
        Capture(window, outDir, "4-single-fullscreen.png",
            "Distraction-free full screen (F11) for single-page reading.");
        window.ToggleFullscreen();
        await SettleAsync();

        // --- 5: rotation ---------------------------------------------------
        window.RotateClockwise();
        await RenderPauseAsync();
        Capture(window, outDir, "5-rotation.png",
            "Rotate the view 90 degrees at a time (Ctrl+R) — the file on disk is never changed.");
        for (int i = 0; i < 3; i++) window.RotateClockwise();   // back to upright
        await SettleAsync();

        // --- 6: About ------------------------------------------------------
        var about = new AboutWindow { Owner = window };
        about.Show();
        await RenderPauseAsync(700);
        Capture(window, outDir, "6-about.png",
            "The About window: version, MIT license and Green Yoga Inc links — plus native support.");

        // --- 7: native support form (offline) ------------------------------
        about.Height = 920;
        about.OpenSupportPane();
        await RenderPauseAsync(700);
        Capture(window, outDir, "7-support-form.png",
            "Contact support inside the app. Write your message in the native form; only Send contacts Green Yoga Inc.");
        about.Close();
        await SettleAsync();
        window.Close();
        await SettleAsync();

        // --- 8: Arabic (RTL) -----------------------------------------------
        SetCulture("ar");
        var arWindow = new MainWindow();
        Prepare(arWindow);
        arWindow.Show();
        await arWindow.OpenFileAsync(demoPdf);
        arWindow.SetMode(ViewMode.Facing);
        arWindow.SetChapterPaneVisible(true);
        arWindow.GoToPage(3);
        await RenderPauseAsync();
        Capture(arWindow, outDir, "8-lang-ar.png",
            "Fourteen interface languages, including full right-to-left layout in Arabic.");
        arWindow.Close();
        await SettleAsync();

        // --- 9: German -----------------------------------------------------
        SetCulture("de");
        var deWindow = new MainWindow();
        Prepare(deWindow);
        deWindow.Show();
        await deWindow.OpenFileAsync(demoPdf);
        deWindow.SetMode(ViewMode.Continuous);
        deWindow.SetChapterPaneVisible(true);
        deWindow.GoToPage(2);
        await RenderPauseAsync();
        Capture(deWindow, outDir, "9-lang-de.png",
            "The German interface — the viewer follows your Windows display language.");
        deWindow.Close();

        // --- verify + captions --------------------------------------------
        int bad = VerifyAndWriteCaptions(outDir);
        // Keep the demo document with the set so the captures are reproducible byte-for-byte.
        Directory.CreateDirectory(Path.Combine(outDir, "source"));
        File.Copy(demoPdf, Path.Combine(outDir, "source", "PdfLiteViewer-demo.pdf"), overwrite: true);

        Console.WriteLine(bad == 0
            ? $"PASS — {Captured.Count} screenshots at {TargetW}x{TargetH} in {outDir}"
            : $"FAIL — {bad} screenshot(s) failed verification.");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>Native form smoke check; --submit-check explicitly sends one marked support request.</summary>
    private static async Task<int> RunSubmitCheckAsync(bool clickSend)
    {
        var about = new AboutWindow();
        try
        {
            about.Height = 920;
            about.Show();
            about.OpenSupportPane();
            about.SupportNameInput.Text = "PDF Lite Viewer release check";
            about.SupportEmailInput.Text = "noreply@greenyogainc.com";
            about.SupportSubjectInput.Text = "Automated native support check - please ignore";
            about.SupportMessageInput.Text = "Automated verification of native app support. No action needed.";
            bool valid = GreenYoga.Support.AppSupportClient.Validate(about.CurrentSupportRequest()) is null;
            if (!clickSend)
            {
                await RenderPauseAsync(700);
                var snapshot = new RenderTargetBitmap((int)about.ActualWidth, (int)about.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                snapshot.Render(about);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(snapshot));
                var shotPath = Path.Combine(Path.GetTempPath(), "pdf-native-support.png");
                using (var output = File.Create(shotPath)) encoder.Save(output);
                Console.WriteLine("Support screenshot: " + shotPath);
                Console.WriteLine(valid ? "fill-check PASS: native fields validate; nothing sent." : "fill-check FAIL");
                return valid ? 0 : 1;
            }
            await about.SendSupportAsync();
            Console.WriteLine(about.SupportStatusText.Text);
            return about.SupportSubmitted ? 0 : 1;
        }
        finally { about.Close(); }
    }

    private static void Prepare(Window window)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        window.Topmost = true;
        window.Left = 0;
        window.Top = 0;
        window.Width = TargetW / dpi.DpiScaleX;
        window.Height = TargetH / dpi.DpiScaleY;
    }

    /// <summary>
    /// Captures the fixed target rectangle. Its size is what the PNG's size will be, by
    /// construction - so what has to be checked here is that the app window really fills
    /// that rectangle in device pixels (a DPI-virtualized or displaced window would leave
    /// desktop in the capture and still produce a 2560x1440 file).
    /// </summary>
    private static void Capture(Window window, string outDir, string file, string caption)
    {
        var toDevice = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice
                       ?? throw new InvalidOperationException($"{file}: window has no presentation source");
        var origin = toDevice.Transform(new Point(window.Left, window.Top));
        var extent = toDevice.Transform(new Vector(window.ActualWidth, window.ActualHeight));
        bool covers = origin.X <= 0.5 && origin.Y <= 0.5
                      && origin.X + extent.X >= TargetW - 0.5 && origin.Y + extent.Y >= TargetH - 0.5;
        if (!covers)
            throw new InvalidOperationException(
                $"{file}: window covers ({origin.X:F0},{origin.Y:F0}) {extent.X:F0}x{extent.Y:F0} device px, " +
                $"which does not contain the (0,0) {TargetW}x{TargetH} capture rectangle");

        var path = Path.Combine(outDir, file);
        // Every app surface is now WPF. Render the app itself so an unrelated
        // foreground window can never contaminate a Store listing screenshot.
        var composite = new DrawingVisual();
        using (var drawing = composite.RenderOpen())
        {
            drawing.DrawImage(RenderWindow(window), new Rect(0, 0, TargetW, TargetH));
            foreach (Window dialog in window.OwnedWindows)
            {
                if (!dialog.IsVisible) continue;
                var dpi = VisualTreeHelper.GetDpi(dialog);
                var width = dialog.ActualWidth * dpi.DpiScaleX;
                var height = dialog.ActualHeight * dpi.DpiScaleY;
                drawing.DrawImage(RenderWindow(dialog),
                    new Rect((TargetW - width) / 2, (TargetH - height) / 2, width, height));
            }
        }
        var image = new RenderTargetBitmap(TargetW, TargetH, 96, 96, PixelFormats.Pbgra32);
        image.Render(composite);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var output = File.Create(path)) encoder.Save(output);
        Captured.Add(new Shot(file, caption));
        Console.WriteLine($"captured: {path}");
    }

    private static BitmapSource RenderWindow(Window window)
    {
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var image = new RenderTargetBitmap(
            (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        image.Render(window);
        image.Freeze();
        return image;
    }

    private static int VerifyAndWriteCaptions(string outDir)
    {
        int bad = 0;
        var captions = new List<string>
        {
            "# Store screenshot captions",
            "",
            $"Captured from the current Release build by `dotnet run --project tools\\StoreShots -c Release` at {TargetW}x{TargetH}.",
            "Regenerate the whole set for every release; never reuse or upscale old captures.",
            "",
        };
        foreach (var shot in Captured)
        {
            var path = Path.Combine(outDir, shot.File);
            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            bool sizeOk = frame.PixelWidth == TargetW && frame.PixelHeight == TargetH
                          && frame.PixelWidth >= 1366 && frame.PixelHeight >= 768;
            // The size above can only disagree if the encoder did; the failure a capture
            // really has is content - a black or blank frame where BitBlt copied nothing
            // useful. Measured on the shipped set with this sampler: the sparsest scene (a
            // single page full screen) has 59 distinct colours, the busiest 200; a uniform
            // frame has 1 and a three-level noise frame about 9.
            int colours = DistinctSampleColours(frame);
            bool contentOk = colours >= 16;
            bool captionOk = shot.Caption.Length <= 200;
            if (!sizeOk) { bad++; Console.Error.WriteLine($"BAD SIZE {shot.File}: {frame.PixelWidth}x{frame.PixelHeight}"); }
            if (!contentOk) { bad++; Console.Error.WriteLine($"BLANK CAPTURE {shot.File}: only {colours} distinct colour(s) sampled"); }
            if (!captionOk) { bad++; Console.Error.WriteLine($"CAPTION TOO LONG {shot.File}: {shot.Caption.Length} chars"); }
            captions.Add($"- **{shot.File}** ({frame.PixelWidth}x{frame.PixelHeight}): {shot.Caption}");
        }
        captions.Add("");
        File.WriteAllLines(Path.Combine(outDir, "captions.md"), captions);
        return bad;
    }

    /// <summary>Distinct colours on a 16-pixel grid across the frame: a blank capture has one.</summary>
    private static int DistinctSampleColours(BitmapSource frame)
    {
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        var colours = new HashSet<int>();
        for (int y = 0; y < bgra.PixelHeight; y += 16)
            for (int x = 0; x < bgra.PixelWidth; x += 16)
                colours.Add(BitConverter.ToInt32(pixels, y * stride + x * 4));
        return colours.Count;
    }

    private static void SetCulture(string name)
    {
        var culture = new CultureInfo(name);
        Thread.CurrentThread.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    private static async Task SettleAsync()
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.SystemIdle);
        await Task.Delay(150);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.SystemIdle);
    }

    private static async Task RenderPauseAsync(int extraMs = 900)
    {
        await SettleAsync();
        await Task.Delay(extraMs);   // async page bitmaps arriving
        await SettleAsync();
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(200);
        }
        return condition();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(Environment.CurrentDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PdfLiteViewer.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException(
            "Run from inside the repository (PdfLiteViewer.slnx not found in any parent directory).");
    }
}
