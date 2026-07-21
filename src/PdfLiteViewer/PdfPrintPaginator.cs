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

    private readonly PdfDoc _doc;
    private readonly IReadOnlyList<int> _pages;   // 0-based PDF page indices to print
    private readonly Size _pageSize;              // device-independent pixels (1/96")

    public PdfPrintPaginator(PdfDoc doc, IReadOnlyList<int> pages, Size pageSize)
    {
        _doc = doc;
        _pages = pages;
        _pageSize = pageSize;
    }

    public PdfPrintPaginator(PdfDoc doc, int firstPage, int lastPage, Size pageSize)
        : this(doc, Enumerable.Range(firstPage, lastPage - firstPage + 1).ToList(), pageSize)
    {
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
        int pdfIndex = _pages[pageNumber];
        var (ptW, ptH) = _doc.PageSizes[pdfIndex];
        var rect = PlacePage(ptW, ptH, _pageSize);

        int pixelWidth = Math.Min(6000, (int)Math.Round(rect.Width / 96.0 * PrintDpi));
        var bmp = _doc.RenderPageSync(pdfIndex, pixelWidth);

        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
            ctx.DrawImage(bmp, rect);

        return new DocumentPage(visual, _pageSize, new Rect(_pageSize), rect);
    }
}
