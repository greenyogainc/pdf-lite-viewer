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
/// at least the target resolution, and — for the support-form scene — internet access.
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

        if (SystemParameters.PrimaryScreenWidth < TargetW || SystemParameters.PrimaryScreenHeight < TargetH)
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
        // click and only proves the form accepts input and validates inside the embed.
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
        // A generic, non-personal queue name for the screenshot.
        foreach (var item in preview.PrinterBox.Items)
            if (item is string s && s == "Microsoft Print to PDF") { preview.PrinterBox.SelectedItem = s; break; }
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
            "The About window: version, MIT license and Green Yoga Inc links — plus built-in web support.");

        // --- 7: embedded support form (requires internet) ------------------
        about.OpenSupportPane();
        await about.LoadSupportAsync();
        bool loaded = await WaitUntilAsync(() => about.WebViewHost.Visibility == Visibility.Visible,
            TimeSpan.FromSeconds(45));
        if (!loaded)
        {
            Console.Error.WriteLine("support form did not load — scene 7 failed (is the network up?)");
            about.Close();
            return 1;
        }
        await RenderPauseAsync(5000);   // let the page paint fully
        Capture(window, outDir, "7-support-form.png",
            "Contact support without leaving the app: the Green Yoga Inc support form, loaded only when you choose. PDF viewing itself never goes online.");
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

    /// <summary>
    /// `--submit-check`: proves the embedded support form can really submit from this app.
    /// Loads the live form, fills it with clearly-marked automated test values (no real
    /// personal data; the honeypot field is left alone), clicks Send, and watches the DOM
    /// for the site's success state. Sends exactly one test message to Green Yoga's own
    /// support inbox.
    /// </summary>
    private static async Task<int> RunSubmitCheckAsync(bool clickSend)
    {
        var about = new PdfLiteViewer.AboutWindow();
        about.Show();
        about.OpenSupportPane();
        await about.LoadSupportAsync();

        bool loaded = await WaitUntilAsync(() => about.WebViewHost.Visibility == Visibility.Visible,
            TimeSpan.FromSeconds(45));
        if (!loaded || about.WebViewForTest?.CoreWebView2 is not { } core)
        {
            Console.Error.WriteLine("submit-check: support form did not load");
            return 1;
        }
        await Task.Delay(3000);   // let the page hydrate

        // React-controlled inputs: assign through the prototype setter and raise `input`,
        // or the framework never sees the value.
        const string fill = """
            (() => {
              const set = (el, v, proto) => {
                Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, v);
                el.dispatchEvent(new Event('input', { bubbles: true }));
              };
              const name = document.getElementById('name');
              const email = document.getElementById('email');
              const subject = document.getElementById('subject');
              const message = document.getElementById('message');
              if (!name || !email || !subject || !message) return 'missing-fields';
              set(name, 'PDF Lite Viewer release check', HTMLInputElement.prototype);
              set(email, 'noreply@greenyogainc.com', HTMLInputElement.prototype);
              set(subject, 'Automated embedded-form check - please ignore', HTMLInputElement.prototype);
              set(message, 'Automated verification that the embedded support form in PDF Lite Viewer can submit. No action needed.', HTMLTextAreaElement.prototype);
              const btn = document.querySelector('form button[type=submit]');
              if (!btn) return 'missing-button';
              const form = btn.closest('form');
              const valid = form ? form.checkValidity() : false;
              if (!window.__plvClick) return valid && !btn.disabled ? 'fill-ok' : 'fill-invalid';
              btn.click();
              return 'submitted';
            })()
            """;
        if (clickSend)
            await core.ExecuteScriptAsync("window.__plvClick = true");
        var fillResult = await core.ExecuteScriptAsync(fill);
        Console.WriteLine($"{(clickSend ? "submit" : "fill")}-check: fill result -> {fillResult}");
        if (!clickSend)
        {
            about.Close();
            bool ok = fillResult.Contains("fill-ok");
            Console.WriteLine(ok
                ? "fill-check PASS: fields accept input, the form validates, Send is enabled. Nothing was sent."
                : $"fill-check FAIL: {fillResult}");
            return ok ? 0 : 1;
        }
        if (!fillResult.Contains("submitted"))
            return 1;

        const string probe = """
            (() => {
              const txt = document.body.innerText.toLowerCase();
              if (txt.includes('thank') || txt.includes('sent') || txt.includes('received') || txt.includes('talk soon')) return 'success';
              if (txt.includes('something went wrong') || txt.includes('failed')) return 'error';
              const form = document.querySelector('form.space-y-6');
              if (!form) return 'success';
              const btn = form.querySelector('button[type=submit]');
              return btn && btn.disabled ? 'sending' : 'pending';
            })()
            """;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        string state = "pending";
        while (DateTime.UtcNow < deadline)
        {
            state = (await core.ExecuteScriptAsync(probe)).Trim('"');
            Console.WriteLine($"submit-check: state = {state}");
            if (state is "success" or "error") break;
            await Task.Delay(1500);
        }
        about.Close();

        Console.WriteLine(state == "success"
            ? "submit-check PASS: the embedded form submitted and the site confirmed it."
            : $"submit-check FAIL: final state '{state}'.");
        return state == "success" ? 0 : 1;
    }

    /// <summary>Borderless, topmost, exactly the capture rectangle: the capture then
    /// contains only the app — no taskbar, no desktop, no terminal.</summary>
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
        ScreenCapture.CaptureRect(0, 0, TargetW, TargetH, path);
        Captured.Add(new Shot(file, caption));
        Console.WriteLine($"captured: {path}");
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
