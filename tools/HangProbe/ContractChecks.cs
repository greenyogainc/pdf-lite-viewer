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
/// a print job must keep the rotation it started with; and the print-range parser and
/// scale-to-fit placement must hold their documented edge cases.
/// </summary>
internal static class ContractChecks
{
    public static async Task<List<Check>> RunAsync(MainWindow window, PdfDoc doc, Func<Task> settle)
    {
        var checks = new List<Check>();
        checks.Add(ZeroPageDocumentRejected());
        checks.Add(StartupArgumentIgnored());
        checks.AddRange(await FacingSpreadAsync(window, settle));
        checks.AddRange(await PrintCommitsAsync(doc));
        checks.Add(PrintRotationSnapshot(doc));
        checks.Add(PrintRangeParsing());
        checks.Add(PlacePageFits());
        return checks;
    }

    // ---------- PdfDoc: zero pages ----------

    private static Check ZeroPageDocumentRejected()
    {
        const string name = "zero-page document is rejected at open";
        var path = Path.Combine(Path.GetTempPath(), "hangprobe-zero-pages.pdf");
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
    /// window the probe measures.
    /// </summary>
    private static Check StartupArgumentIgnored()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToList();
        var startupFile = ((App)Application.Current).StartupFile;
        bool anyPdf = args.Any(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || File.Exists(a));
        return new Check("non-PDF startup arguments are ignored",
            anyPdf || startupFile is null,
            $"args [{string.Join(", ", args)}] -> StartupFile = {(startupFile is null ? "null" : $"'{startupFile}'")}");
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

            var printing = window.PrintAsync();
            bool committed = !window.CancelBtn.IsEnabled && !window.PrintBtn.IsEnabled && !window.PrinterBox.IsEnabled;
            checks.Add(new Check("print: settings and Cancel lock while the job spools", committed,
                $"Cancel enabled={window.CancelBtn.IsEnabled}, Print enabled={window.PrintBtn.IsEnabled}, printer box enabled={window.PrinterBox.IsEnabled}"));

            job.SetResult();
            await printing;
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            checks.Add(new Check("print: controls return once the job is spooled",
                window.CancelBtn.IsEnabled && window.PrinterBox.IsEnabled,
                $"Cancel enabled={window.CancelBtn.IsEnabled}, printer box enabled={window.PrinterBox.IsEnabled}"));
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
    /// for the sheet geometry and for the bitmap drawn into it.
    /// </summary>
    private static Check PrintRotationSnapshot(PdfDoc doc)
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
            var page = paginator.GetPage(0);

            var box = page.ContentBox;
            bool boxOk = (box.Width < box.Height) == portrait;
            var image = VisualTreeHelper.GetDrawing(page.Visual)?.Children.OfType<ImageDrawing>()
                .Select(d => d.ImageSource as BitmapSource).FirstOrDefault(b => b is not null);
            bool bitmapOk = image is not null && (image.PixelWidth < image.PixelHeight) == portrait;

            return new Check(name, boxOk && bitmapOk,
                $"content box {box.Width:F0}x{box.Height:F0}, bitmap " +
                (image is null ? "missing" : $"{image.PixelWidth}x{image.PixelHeight}") +
                $"; page is {(portrait ? "portrait" : "landscape")} and the view was rotated after the job started");
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
