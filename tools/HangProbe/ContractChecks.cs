using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfLiteViewer;

namespace HangProbe;

/// <summary>
/// Regression checks for contracts the 2026-09 full-codebase review found unguarded:
/// a zero-page document must be refused at open (PDFium accepts it, the layouts cannot
/// show it); a non-PDF startup argument must not become a startup file (this very probe
/// passes a page count); a facing-mode jump to the other page of the visible spread must
/// not rebuild the spread; the print preview must commit to a job once Print is clicked;
/// a print job must keep the rotation it started with, and render a quarter-turned page at
/// print resolution; and the print-range parser and
/// scale-to-fit placement must hold their documented edge cases.
/// </summary>
internal static class ContractChecks
{
    public static async Task<List<Check>> RunAsync(MainWindow window, PdfDoc doc, Func<Task> settle)
    {
        var checks = new List<Check>();
        checks.Add(ZeroPageDocumentRejected());
        checks.AddRange(StartupArgumentRule());
        checks.AddRange(await FacingSpreadAsync(window, settle));
        checks.AddRange(await PrintCommitsAsync(doc));
        checks.Add(await PrintRotationSnapshotAsync(doc));
        checks.Add(await PrintQuarterTurnResolutionAsync(doc));
        checks.Add(PrintRangeParsing());
        checks.Add(PlacePageFits());
        checks.Add(await CancelledPrintWritesNothingAsync(doc));
        checks.Add(await PaginatorHonorsCancelledTokenAsync(doc));
        checks.Add(await PixelCapAsync());
        checks.Add(await MissingPrinterPaperAsync());
        checks.Add(StartupLanguage());
        checks.Add(ErrorLogAppends());
        return checks;
    }

    // ---------- PdfDoc: zero pages ----------

    private static Check ZeroPageDocumentRejected()
    {
        const string name = "zero-page document is rejected at open";
        // Per-run Guid suffix on the temp fixture: the file is owned by this function
        // (write+read+delete), but parallel CI shards running the same probe would
        // otherwise race on the same fixed path.
        var path = Path.Combine(Path.GetTempPath(), $"hangprobe-zero-pages-{Guid.NewGuid():N}.pdf");
        try
        {
            WriteZeroPagePdf(path);
            try
            {
                var doc = new PdfDoc(path);
                return new Check(name, false,
                    $"PdfDoc opened it with PageCount={doc.PageCount}; RebuildItems would index an empty PageSizes");
            }
            catch (Exception ex)
            {
                // Any rejection is the contract: MainWindow.OpenFileAsync catches Exception and
                // reports it. PdfDoc's own guard is the expected source, PDFium refusing the
                // file outright would be just as acceptable.
                return new Check(name, true, $"rejected with {ex.GetType().Name}: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            return new Check(name, false, $"could not run: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>A well-formed PDF whose page tree is empty, xref offsets and all.</summary>
    private static void WriteZeroPagePdf(string path)
    {
        var body = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        void Obj(string content)
        {
            offsets.Add(body.Length);
            body.Append(offsets.Count).Append(" 0 obj\n").Append(content).Append("\nendobj\n");
        }
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj("<< /Type /Pages /Kids [] /Count 0 >>");

        int xref = body.Length;
        body.Append("xref\n0 ").Append(offsets.Count + 1).Append('\n');
        body.Append("0000000000 65535 f \n");
        foreach (int off in offsets) body.Append(off.ToString("D10")).Append(" 00000 n \n");
        body.Append("trailer\n<< /Size ").Append(offsets.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xref).Append("\n%%EOF\n");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(body.ToString()));
    }

    // ---------- App: startup arguments ----------

    /// <summary>
    /// The probe runs the production App with its page count as the first argument (and
    /// tools/StoreShots with an output directory). Neither is a PDF, so neither may become
    /// the startup file - a regression here puts a modal "could not open" dialog over every
    /// window the probe measures. The rule is table-tested directly so the check means the
    /// same whether or not this run was given a page count; the live StartupFile is reported
    /// alongside.
    /// </summary>
    private static IEnumerable<Check> StartupArgumentRule()
    {
        var existingFile = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
        var cases = new (string Arg, bool Expected)[]
        {
            ("3000", false),                                    // HangProbe's own argument
            (Path.GetTempPath().TrimEnd('\\'), false),          // an existing directory (StoreShots)
            (existingFile, true),                               // an existing file
            (@"C:\gone\moved-away.pdf", true),                  // a PDF that no longer exists: reported, not dropped
            (@"C:\gone\Moved Away.PDF", true),
            ("--lang=de", false),
            ("-x", false),
            ("", false),
            ("report.txt", false),
        };
        var wrong = cases.Where(c => App.IsDocumentArgument(c.Arg) != c.Expected)
            .Select(c => $"'{c.Arg}' -> {!c.Expected}").ToList();
        yield return new Check("startup argument rule", wrong.Count == 0,
            wrong.Count == 0 ? $"{cases.Length} cases verified" : string.Join("; ", wrong));

        var args = Environment.GetCommandLineArgs().Skip(1).ToList();
        var startupFile = ((App)Application.Current).StartupFile;
        bool anyDocument = args.Any(App.IsDocumentArgument);
        yield return new Check("non-PDF startup arguments are ignored",
            anyDocument || startupFile is null,
            $"args [{string.Join(", ", args)}] -> StartupFile = {(startupFile is null ? "null" : $"'{startupFile}'")}"
            + (args.Count == 0 ? " (no arguments this run; the rule itself is covered above)" : ""));
    }

    // ---------- MainWindow: facing spread ----------

    private static async Task<IEnumerable<Check>> FacingSpreadAsync(MainWindow window, Func<Task> settle)
    {
        var checks = new List<Check>();
        if (window.Document is null || window.Document.PageCount < 5)
        {
            checks.Add(new Check("facing: jump within the spread keeps the pages", false, "needs an open document of 5+ pages"));
            return checks;
        }

        window.SetMode(ViewMode.Facing);
        window.GoToPage(1);                 // spread [1,2] (0-based), page box reads 2
        await settle();
        var spread = window.Items;

        window.GoToPage(2);                 // the right-hand page of the same spread
        await settle();
        checks.Add(new Check("facing: jump within the spread keeps the pages",
            ReferenceEquals(spread, window.Items) && window.PageBox.Text == "3",
            ReferenceEquals(spread, window.Items)
                ? $"same page slots kept; page box reads '{window.PageBox.Text}' (expected '3')"
                : "the spread was rebuilt for a page that was already on screen"));

        // The inverse keeps the check honest: a jump to another spread must rebuild.
        window.GoToPage(3);
        await settle();
        checks.Add(new Check("facing: jump to another spread rebuilds",
            !ReferenceEquals(spread, window.Items) && window.Items.Count == 2 && window.Items[0].PageIndex == 3,
            $"slots now start at page index {(window.Items.Count > 0 ? window.Items[0].PageIndex : -1)} ({window.Items.Count} slot(s))"));
        return checks;
    }

    // ---------- PrintPreviewWindow: the job is committed ----------

    private static async Task<IEnumerable<Check>> PrintCommitsAsync(PdfDoc doc)
    {
        var checks = new List<Check>();
        PrintPreviewWindow? window = null;
        try
        {
            // Never shown, like PreviewRaceChecks: the constructor builds the whole window,
            // and printer discovery bails out on an unshown window instead of racing this.
            window = new PrintPreviewWindow(doc, 0);
            window.PrinterBox.Items.Add("probe queue");
            window.PrinterBox.SelectedItem = "probe queue";

            var job = new TaskCompletionSource();
            window.PrintOverride = () => job.Task;

            // Assert transitions, not resting states. Print is still disabled here: the unshown
            // window's Printer_Changed bails before UpdatePrintEnabled, so its enabled flag
            // only becomes informative once SetPrintingState(false) recomputes it below.
            bool cancelBefore = window.CancelBtn.IsEnabled, printerBefore = window.PrinterBox.IsEnabled;
            var printing = window.PrintAsync();
            bool locked = cancelBefore && printerBefore && !window.CancelBtn.IsEnabled && !window.PrinterBox.IsEnabled;
            checks.Add(new Check("print: settings and Cancel lock while the job spools", locked,
                $"Cancel {cancelBefore}->{window.CancelBtn.IsEnabled}, printer box {printerBefore}->{window.PrinterBox.IsEnabled}"));

            job.SetResult();
            await printing;
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            checks.Add(new Check("print: controls return once the job is spooled",
                window.CancelBtn.IsEnabled && window.PrinterBox.IsEnabled && window.PrintBtn.IsEnabled,
                $"Cancel enabled={window.CancelBtn.IsEnabled}, printer box enabled={window.PrinterBox.IsEnabled}, " +
                $"Print enabled={window.PrintBtn.IsEnabled} (was disabled before the job; recomputed for the selected queue)"));
        }
        catch (Exception ex)
        {
            checks.Add(new Check("print commit checks ran", false, $"{ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
            try { window?.Close(); } catch { }
        }
        return checks;
    }

    // ---------- PdfPrintPaginator: rotation is fixed when the job starts ----------

    /// <summary>
    /// The preview can be closed and the view rotated while a job is still producing pages
    /// on the print thread. The paginator must keep the rotation it was created with, both
    /// for the sheet geometry and for the bitmap drawn into it. The page is produced on a
    /// dedicated STA thread, as PrintJob does - a synchronous PDFium render never belongs on
    /// the UI thread this probe is guarding.
    /// </summary>
    private static async Task<Check> PrintRotationSnapshotAsync(PdfDoc doc)
    {
        const string name = "print: pages keep the rotation the job started with";
        var before = doc.Rotation;
        try
        {
            doc.Rotation = PDFtoImage.PdfRotation.Rotate0;
            var (w, h) = doc.GetDisplaySize(0, PDFtoImage.PdfRotation.Rotate0);
            bool portrait = w < h;   // the stress pages are portrait letter
            var paginator = new PdfPrintPaginator(doc, new[] { 0 }, PrintJob.FallbackPaper, PDFtoImage.PdfRotation.Rotate0);

            doc.Rotation = PDFtoImage.PdfRotation.Rotate90;   // the user rotates mid-job

            // Measured on the print thread too: the DocumentPage's visual belongs to it.
            var (boxW, boxH, bmpW, bmpH) = await OnStaThreadAsync(() =>
            {
                var page = paginator.GetPage(0);
                var image = VisualTreeHelper.GetDrawing(page.Visual)?.Children.OfType<ImageDrawing>()
                    .Select(d => d.ImageSource as BitmapSource).FirstOrDefault(b => b is not null);
                return (page.ContentBox.Width, page.ContentBox.Height, image?.PixelWidth ?? 0, image?.PixelHeight ?? 0);
            });

            bool boxOk = (boxW < boxH) == portrait;
            bool bitmapOk = bmpW > 0 && bmpH > 0 && (bmpW < bmpH) == portrait;
            return new Check(name, boxOk && bitmapOk,
                $"content box {boxW:F0}x{boxH:F0}, bitmap {bmpW}x{bmpH}; page is " +
                $"{(portrait ? "portrait" : "landscape")} and the view was rotated after the job started");
        }
        catch (Exception ex)
        {
            return new Check(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            doc.Rotation = before;
        }
    }

    /// <summary>
    /// PDFtoImage sizes a render from the *unrotated* page and swaps the sides afterwards, so
    /// asking a 90° render for a width used to hand back a bitmap whose height was that width:
    /// a rotated portrait page came out oversampled by its aspect ratio (past the paginator's
    /// pixel budget on tall pages), a rotated landscape page undersampled. The bitmap drawn
    /// into the sheet must match the content box at the paginator's 300 DPI.
    /// </summary>
    private static async Task<Check> PrintQuarterTurnResolutionAsync(PdfDoc doc)
    {
        const string name = "print: a 90° page renders at 300 dpi across its content box";
        try
        {
            var paginator = new PdfPrintPaginator(doc, new[] { 0 }, PrintJob.FallbackPaper, PDFtoImage.PdfRotation.Rotate90);
            var (boxW, boxH, bmpW, bmpH) = await OnStaThreadAsync(() =>
            {
                var page = paginator.GetPage(0);
                var image = VisualTreeHelper.GetDrawing(page.Visual)?.Children.OfType<ImageDrawing>()
                    .Select(d => d.ImageSource as BitmapSource).FirstOrDefault(b => b is not null);
                return (page.ContentBox.Width, page.ContentBox.Height, image?.PixelWidth ?? 0, image?.PixelHeight ?? 0);
            });

            double expectedW = boxW / 96.0 * 300.0;
            double expectedH = boxH / 96.0 * 300.0;
            // Floor in the paginator and truncation in PDFium: allow a couple of pixels.
            bool ok = bmpW > 0 && Math.Abs(bmpW - expectedW) <= 2 && Math.Abs(bmpH - expectedH) <= 2;
            return new Check(name, ok,
                $"content box {boxW:F0}x{boxH:F0} DIP wants ~{expectedW:F0}x{expectedH:F0} px, bitmap {bmpW}x{bmpH}");
        }
        catch (Exception ex)
        {
            return new Check(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Runs work on a fresh STA thread and shuts down the dispatcher it may create, like PrintJob.</summary>
    private static Task<T> OnStaThreadAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
            finally { System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown(); }
        })
        {
            Name = "probe-print",
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    // ---------- Pure print math ----------

    private static Check PrintRangeParsing()
    {
        var cases = new (string Text, int Pages, int[] Expected)[]
        {
            ("1-3,5", 10, new[] { 0, 1, 2, 4 }),
            ("5-1", 10, Array.Empty<int>()),          // reversed range selects nothing
            ("0", 10, Array.Empty<int>()),            // pages are 1-based
            ("9999", 10, Array.Empty<int>()),
            ("", 10, Array.Empty<int>()),
            ("  ", 10, Array.Empty<int>()),
            ("3, 3, 2", 10, new[] { 1, 2 }),          // deduplicated and ordered
            ("2-99", 5, new[] { 1, 2, 3, 4 }),        // clamped to the document
            ("a-b", 10, Array.Empty<int>()),
            ("-3", 10, Array.Empty<int>()),
            ("1-2-3", 10, Array.Empty<int>()),
        };

        var wrong = new List<string>();
        foreach (var (text, pages, expected) in cases)
        {
            var got = PrintPreviewWindow.ParseRange(text, pages);
            if (!got.SequenceEqual(expected))
                wrong.Add($"'{text}' -> [{string.Join(",", got)}], expected [{string.Join(",", expected)}]");
        }
        return new Check("print range parser", wrong.Count == 0,
            wrong.Count == 0 ? $"{cases.Length} cases verified" : string.Join("; ", wrong));
    }

    /// <summary>
    /// An already-cancelled job must unwind on the print thread before it creates an XPS
    /// package. A file left behind would be a job that ignored the token.
    /// </summary>
    private static async Task<Check> CancelledPrintWritesNothingAsync(PdfDoc doc)
    {
        const string name = "print: cancelled job writes no XPS";
        var path = Path.Combine(Path.GetTempPath(), $"hangprobe-cancel-{Guid.NewGuid():N}.xps");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            await PrintJob.WriteXpsAsync(doc, new[] { 0 }, PrintJob.FallbackPaper, path, cts.Token);
            return new Check(name, false, "WriteXpsAsync completed instead of cancelling");
        }
        catch (OperationCanceledException)
        {
            bool leftNothing = !File.Exists(path);
            return new Check(name, leftNothing,
                leftNothing ? "cancelled before the package was created" : $"cancelled but left {path}");
        }
        catch (Exception ex)
        {
            return new Check(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>The paginator's own token check, independent of the XPS writer.</summary>
    private static async Task<Check> PaginatorHonorsCancelledTokenAsync(PdfDoc doc)
    {
        const string name = "print: paginator stops when the job is cancelled";
        try
        {
            var paginator = new PdfPrintPaginator(doc, new[] { 0 }, PrintJob.FallbackPaper, doc.Rotation)
            {
                CancellationToken = new CancellationToken(canceled: true),
            };
            paginator.PageSize = paginator.PageSize;   // the empty setter is part of DocumentPaginator
            await OnStaThreadAsync(() => paginator.GetPage(0));
            return new Check(name, false, "GetPage returned a page for a cancelled job");
        }
        catch (OperationCanceledException)
        {
            return new Check(name, true, "GetPage threw OperationCanceledException");
        }
        catch (Exception ex)
        {
            return new Check(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// A page large enough that 300 DPI would exceed the paginator's pixel budget must come
    /// back scaled down. Width is pre-capped at 6000 and height is not, so the guarantee is
    /// the area (36e6), not both sides being ≤ 6000.
    /// </summary>
    private static async Task<Check> PixelCapAsync()
    {
        const string name = "print: extreme page stays inside the pixel budget";
        const int maxArea = 36_000_000;
        var path = Path.Combine(Path.GetTempPath(), $"hangprobe-huge-{Guid.NewGuid():N}.pdf");
        try
        {
            // 1440×1575 pt → 1920×2100 DIP. On a paper larger than that, placement does not
            // shrink the page, and 300 DPI asks for 6000×6562 px (area ~39e6) before the cap.
            WriteSinglePagePdf(path, 1440, 1575);
            var doc = new PdfDoc(path);
            var paginator = new PdfPrintPaginator(doc, new[] { 0 }, new Size(4000, 4000), PDFtoImage.PdfRotation.Rotate0);
            var (bmpW, bmpH) = await OnStaThreadAsync(() =>
            {
                var page = paginator.GetPage(0);
                var image = VisualTreeHelper.GetDrawing(page.Visual)?.Children.OfType<ImageDrawing>()
                    .Select(d => d.ImageSource as BitmapSource).FirstOrDefault(b => b is not null);
                return (image?.PixelWidth ?? 0, image?.PixelHeight ?? 0);
            });
            long area = (long)bmpW * bmpH;
            // Uncapped height is ~6562. Under 6400 means the budget scale ran; over 5000 means
            // we did not accidentally render a thumbnail.
            bool ok = bmpW > 0 && bmpH > 5000 && bmpH < 6400 && area <= maxArea + maxArea / 100;
            return new Check(name, ok, $"bitmap {bmpW}x{bmpH}, area {area}");
        }
        catch (Exception ex)
        {
            return new Check(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static async Task<Check> MissingPrinterPaperAsync()
    {
        const string name = "print: missing queue falls back to letter";
        try
        {
            var size = await Task.Run(() => PrintJob.PaperFor("no-such-printer-" + Guid.NewGuid().ToString("N")));
            bool ok = size.Width == PrintJob.FallbackPaper.Width && size.Height == PrintJob.FallbackPaper.Height;
            return new Check(name, ok, $"{size.Width:F0}x{size.Height:F0}");
        }
        catch (Exception ex)
        {
            return new Check(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Check StartupLanguage()
    {
        const string name = "startup: --lang= applies, unknown cultures are ignored";
        var previousUi = CultureInfo.CurrentUICulture;
        var previousDefault = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            // "not-a-culture" is accepted (culture name "not"). A name with no hyphen
            // is what actually throws CultureNotFoundException, which is the branch
            // OnStartup swallows.
            App.ApplyStartupArguments(new[] { "--lang=zzzz" });
            bool ignored = CultureInfo.CurrentUICulture.Name == "en-US"
                           && CultureInfo.DefaultThreadCurrentUICulture?.Name == "en-US";

            var file = App.ApplyStartupArguments(new[] { "--lang=de", @"C:\gone\moved-away.pdf" });
            bool applied = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de"
                           && CultureInfo.DefaultThreadCurrentUICulture?.TwoLetterISOLanguageName == "de"
                           && file == @"C:\gone\moved-away.pdf";
            return new Check(name, ignored && applied,
                $"unknown ignored={ignored}, de applied={applied}, file='{file}'");
        }
        catch (Exception ex)
        {
            return new Check(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousUi;
            CultureInfo.DefaultThreadCurrentUICulture = previousDefault;
        }
    }

    private static Check ErrorLogAppends()
    {
        const string name = "startup: errors append to the log";
        var path = Path.Combine(Path.GetTempPath(), "PdfLiteViewer.log");
        var first = "probe-log-" + Guid.NewGuid().ToString("N");
        var second = "probe-log-" + Guid.NewGuid().ToString("N");
        try
        {
            App.LogError(new InvalidOperationException(first));
            App.LogError(new InvalidOperationException(second));
            var text = File.ReadAllText(path);
            int atFirst = text.LastIndexOf(first, StringComparison.Ordinal);
            int atSecond = text.LastIndexOf(second, StringComparison.Ordinal);
            bool ok = atFirst >= 0 && atSecond > atFirst;
            return new Check(name, ok,
                ok ? "two entries appended in order" : $"markers at {atFirst}, {atSecond}");
        }
        catch (Exception ex)
        {
            return new Check(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>One-page PDF whose MediaBox is exactly the given size in points.</summary>
    private static void WriteSinglePagePdf(string path, int widthPt, int heightPt)
    {
        var stream = $"BT /F1 24 Tf 72 72 Td (Big) Tj ET\n";
        var body = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        void Obj(string content)
        {
            offsets.Add(body.Length);
            body.Append(offsets.Count).Append(" 0 obj\n").Append(content).Append("\nendobj\n");
        }
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {widthPt} {heightPt}] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> >>");
        Obj($"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        int xref = body.Length;
        body.Append("xref\n0 ").Append(offsets.Count + 1).Append('\n');
        body.Append("0000000000 65535 f \n");
        foreach (int off in offsets) body.Append(off.ToString("D10")).Append(" 00000 n \n");
        body.Append("trailer\n<< /Size ").Append(offsets.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xref).Append("\n%%EOF\n");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(body.ToString()));
    }

    private static Check PlacePageFits()
    {
        var paper = PrintJob.FallbackPaper;                       // Letter portrait, 816x1056 DIPs
        var landscape = PdfPrintPaginator.PlacePage(792, 612, paper);
        var small = PdfPrintPaginator.PlacePage(72, 72, paper);   // 1 inch square: never upscaled
        bool landscapeOk = Math.Abs(landscape.Width - paper.Width) < 0.01
                           && landscape.Height < paper.Height
                           && Math.Abs(landscape.Y * 2 + landscape.Height - paper.Height) < 0.01;
        bool smallOk = Math.Abs(small.Width - 96) < 0.01 && Math.Abs(small.Height - 96) < 0.01
                       && Math.Abs(small.X * 2 + small.Width - paper.Width) < 0.01;
        return new Check("print placement scales to fit, centred, never up", landscapeOk && smallOk,
            $"landscape page -> {landscape.Width:F0}x{landscape.Height:F0} at y={landscape.Y:F1}; " +
            $"1in square -> {small.Width:F0}x{small.Height:F0} at x={small.X:F1}");
    }
}
