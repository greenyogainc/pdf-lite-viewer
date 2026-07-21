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
    private readonly int _firstPage;   // 0-based, inclusive
    private readonly int _lastPage;    // 0-based, inclusive
    private readonly Size _pageSize;   // device-independent pixels (1/96")

    public PdfPrintPaginator(PdfDoc doc, int firstPage, int lastPage, Size pageSize)
    {
        _doc = doc;
        _firstPage = firstPage;
        _lastPage = lastPage;
        _pageSize = pageSize;
    }

    public override bool IsPageCountValid => true;
    public override int PageCount => _lastPage - _firstPage + 1;
    public override Size PageSize { get => _pageSize; set { } }
    public override IDocumentPaginatorSource? Source => null;

    public override DocumentPage GetPage(int pageNumber)
    {
        int pdfIndex = _firstPage + pageNumber;
        var (ptW, ptH) = _doc.PageSizes[pdfIndex];
        double pageW = ptW * 96.0 / 72.0;   // DIPs
        double pageH = ptH * 96.0 / 72.0;

        // Scale to fit the printable area, never upscale past 100%.
        double margin = 0;
        double scale = Math.Min(1.0, Math.Min(
            (_pageSize.Width - margin) / pageW,
            (_pageSize.Height - margin) / pageH));
        double drawW = pageW * scale;
        double drawH = pageH * scale;
        double x = (_pageSize.Width - drawW) / 2;
        double y = (_pageSize.Height - drawH) / 2;

        int pixelWidth = Math.Min(6000, (int)Math.Round(drawW / 96.0 * PrintDpi));
        var bmp = _doc.RenderPageSync(pdfIndex, pixelWidth);

        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
            ctx.DrawImage(bmp, new Rect(x, y, drawW, drawH));

        return new DocumentPage(visual, _pageSize,
            new Rect(_pageSize), new Rect(x, y, drawW, drawH));
    }
}
