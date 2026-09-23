using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace PdfLiteViewer;

/// <summary>
/// Renders PDF pages one at a time for printing, so large documents
/// never hold more than a single page bitmap in memory.
/// </summary>
public sealed class PdfPrintPaginator : DocumentPaginator
{
    private const double PrintDpi = 300;
    // WIC's largest reliable texture dimension is well under 32768 on most drivers; capping
    // the total pixel budget keeps extreme aspect-ratio pages from blowing up the
    // document writer with a single page they cannot encode.
    private const int MaxPixelArea = 36_000_000;        // ~6000 x 6000 budget
    private const int MaxPixelDimension = 6000;         // cap that mirrors the width ceiling

    private readonly PdfDoc _doc;
    private readonly IReadOnlyList<int> _pages;   // 0-based PDF page indices to print
    private readonly Size _pageSize;              // device-independent pixels (1/96")
    private readonly PDFtoImage.PdfRotation _rotation;

    /// <summary>
    /// Set by <see cref="PrintJob"/> before <c>writer.Write</c> returns control to the
    /// spooler. The paginator checks this between renders so closing the print window
    /// (or the app) can interrupt a long job. Default is <see cref="CancellationToken.None"/>
    /// for callers that don't care.
    /// </summary>
    internal CancellationToken CancellationToken { get; set; }

    /// <param name="rotation">
    /// Snapshot of the view rotation when the job was started. The paginator runs on the
    /// print thread after the preview has closed, so it must not read the live
    /// <see cref="PdfDoc.Rotation"/> the user may change meanwhile.
    /// </param>
    public PdfPrintPaginator(PdfDoc doc, IReadOnlyList<int> pages, Size pageSize, PDFtoImage.PdfRotation rotation)
    {
        _doc = doc;
        _pages = pages;
        _pageSize = pageSize;
        _rotation = rotation;
    }

    public override bool IsPageCountValid => true;
    public override int PageCount => _pages.Count;
    public override Size PageSize { get => _pageSize; set { } }
    public override IDocumentPaginatorSource? Source => null;

    /// <summary>Scale-to-fit placement of a PDF page on the paper, shared with the preview window.</summary>
    public static Rect PlacePage(double ptW, double ptH, Size paper)
    {
        double pageW = ptW * 96.0 / 72.0;
        double pageH = ptH * 96.0 / 72.0;
        double scale = Math.Min(1.0, Math.Min(paper.Width / pageW, paper.Height / pageH));
        double drawW = pageW * scale;
        double drawH = pageH * scale;
        return new Rect((paper.Width - drawW) / 2, (paper.Height - drawH) / 2, drawW, drawH);
    }

    public override DocumentPage GetPage(int pageNumber)
    {
        CancellationToken.ThrowIfCancellationRequested();

        int pdfIndex = _pages[pageNumber];
        var (ptW, ptH) = _doc.GetDisplaySize(pdfIndex, _rotation);
        var rect = PlacePage(ptW, ptH, _pageSize);

        // Cap the rectangle from the page's *display* dimensions (not the aspect ratio the
        // library gives back), so a long foldout still fits inside WIC's max texture size.
        // The existing width ceiling drives the longest side; the matching budget keeps
        // the other side from running away on extreme aspect ratios.
        int pixelWidth = Math.Min(MaxPixelDimension, (int)Math.Round(rect.Width / 96.0 * PrintDpi));
        int pixelHeight = (int)Math.Round(rect.Height / 96.0 * PrintDpi);
        if (pixelWidth * (long)pixelHeight > MaxPixelArea)
        {
            double scale = Math.Sqrt((double)MaxPixelArea / (pixelWidth * (long)pixelHeight));
            pixelWidth = Math.Max(1, (int)Math.Round(pixelWidth * scale));
            pixelHeight = Math.Max(1, (int)Math.Round(pixelHeight * scale));
        }
        var bmp = _doc.RenderPageSync(pdfIndex, pixelWidth, _rotation);

        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
            ctx.DrawImage(bmp, rect);

        return new DocumentPage(visual, _pageSize, new Rect(_pageSize), rect);
    }
}
