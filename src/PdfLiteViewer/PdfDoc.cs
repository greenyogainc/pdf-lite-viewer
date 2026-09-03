using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Outline;

namespace PdfLiteViewer;

/// <summary>
/// Wraps a loaded PDF and renders pages to WPF bitmaps via PDFium (PDFtoImage).
/// PDFium is not thread-safe, so all renders are serialized through one lock.
/// </summary>
public sealed class PdfDoc
{
    private static readonly SemaphoreSlim RenderLock = new(1, 1);

    private readonly byte[] _bytes;
    private readonly object _chaptersGate = new();
    private List<ChapterItem>? _chaptersCache;
    private bool _chaptersCacheEmpty;

    public string FilePath { get; }
    public int PageCount { get; }

    /// <summary>Page sizes in PDF points (1/72 inch), unrotated.</summary>
    public IReadOnlyList<(double Width, double Height)> PageSizes { get; }

    /// <summary>View/print rotation applied when rendering. Does not rewrite the file on disk.</summary>
    public PDFtoImage.PdfRotation Rotation { get; set; } = PDFtoImage.PdfRotation.Rotate0;

    public PdfDoc(string path)
    {
        FilePath = path;
        _bytes = File.ReadAllBytes(path);
        PageCount = PDFtoImage.Conversion.GetPageCount(_bytes);
        // PDFium opens a well-formed file whose page tree is empty (/Count 0) without
        // complaint, but nothing here can show or print zero pages: the facing/single
        // layouts index PageSizes[0] and GoToPage clamps to [0, -1]. Refuse it up front so
        // every caller's existing "could not open" path reports it instead.
        if (PageCount <= 0)
            throw new InvalidDataException("The document contains no pages.");
        var sizes = PDFtoImage.Conversion.GetPageSizes(_bytes);
        PageSizes = sizes.Select(s => ((double)s.Width, (double)s.Height)).ToList();
    }

    /// <summary>
    /// Page size in PDF points after the current <see cref="Rotation"/> —
    /// width/height swap for 90° and 270°.
    /// </summary>
    public (double Width, double Height) GetDisplaySize(int pageIndex)
    {
        var (w, h) = PageSizes[pageIndex];
        return Rotation is PDFtoImage.PdfRotation.Rotate90 or PDFtoImage.PdfRotation.Rotate270
            ? (h, w)
            : (w, h);
    }

    public async Task<BitmapSource> RenderPageAsync(int pageIndex, int targetPixelWidth, CancellationToken ct)
    {
        await RenderLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            var rotation = Rotation;
            return await Task.Run(() =>
            {
                using var sk = PDFtoImage.Conversion.ToImage(
                    _bytes,
                    page: pageIndex,
                    options: new PDFtoImage.RenderOptions(
                        Width: targetPixelWidth,
                        WithAspectRatio: true,
                        WithAnnotations: true,
                        WithFormFill: true,
                        Rotation: rotation,
                        AntiAliasing: PDFtoImage.PdfAntiAliasing.All,
                        BackgroundColor: SKColors.White));
                return ToBitmapSource(sk);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            RenderLock.Release();
        }
    }

    /// <summary>Synchronous render, used by the print paginator (one page at a time).</summary>
    public BitmapSource RenderPageSync(int pageIndex, int targetPixelWidth)
    {
        RenderLock.Wait();
        try
        {
            using var sk = PDFtoImage.Conversion.ToImage(
                _bytes,
                page: pageIndex,
                options: new PDFtoImage.RenderOptions(
                    Width: targetPixelWidth,
                    WithAspectRatio: true,
                    WithAnnotations: true,
                    WithFormFill: true,
                    Rotation: Rotation,
                    AntiAliasing: PDFtoImage.PdfAntiAliasing.All,
                    BackgroundColor: SKColors.White));
            return ToBitmapSource(sk);
        }
        finally
        {
            RenderLock.Release();
        }
    }

    /// <summary>
    /// Extracts the embedded outline/bookmark hierarchy via PdfPig (read-only; PDFium rendering
    /// is untouched). Returns an empty list when the document has no outline. Throws on parse
    /// failure — the caller turns that into a localized "could not load" state, so a broken
    /// outline never blocks opening or rendering the PDF.
    /// </summary>
    /// <param name="untitledFallback">
    /// Localized label for blank bookmark titles, resolved by the caller on the UI thread
    /// (pool threads do not inherit CurrentUICulture).
    /// </param>
    public List<ChapterItem> GetChapters(CancellationToken ct, string untitledFallback)
    {
        lock (_chaptersGate)
        {
            if (_chaptersCache is not null)
                return _chaptersCache;
            if (_chaptersCacheEmpty)
                return new List<ChapterItem>();
        }

        ct.ThrowIfCancellationRequested();

        // SkipMissingFonts: outline extraction never needs glyph data; avoids font-parse
        // work PdfPig would otherwise do while opening large documents.
        using var pdf = PdfDocument.Open(_bytes, new ParsingOptions { SkipMissingFonts = true });

        ct.ThrowIfCancellationRequested();

        var roots = new List<ChapterItem>();
        if (!pdf.TryGetBookmarks(out var bookmarks, allowContainerNode: true))
        {
            lock (_chaptersGate) { _chaptersCacheEmpty = true; }
            return roots;
        }

        ct.ThrowIfCancellationRequested();

        int order = 0;
        MapBookmarks(bookmarks.Roots, parent: null, depth: 0, output: roots, order: ref order, ct, untitledFallback);

        ct.ThrowIfCancellationRequested();

        lock (_chaptersGate) { _chaptersCache = roots; }
        return roots;
    }

    private void MapBookmarks(IReadOnlyList<BookmarkNode> nodes, ChapterItem? parent, int depth,
        List<ChapterItem> output, ref int order, CancellationToken ct, string untitledFallback)
    {
        foreach (var node in nodes)
        {
            ct.ThrowIfCancellationRequested();

            var title = node.Title?.Trim();
            if (string.IsNullOrEmpty(title))
                title = untitledFallback;

            // Only a genuine in-document destination navigates. External- and embedded-file
            // nodes derive from DocumentBookmarkNode, so they must be excluded explicitly;
            // container and URI nodes never are DocumentBookmarkNodes at all.
            int? pageIndex = null;
            if (node is DocumentBookmarkNode docNode
                and not ExternalBookmarkNode
                and not EmbeddedBookmarkNode)
            {
                int page = docNode.PageNumber;  // PdfPig: 1-based, 0 = invalid destination
                if (page >= 1 && page <= PageCount)
                    pageIndex = page - 1;
            }

            var item = new ChapterItem
            {
                Title = title,
                PageIndex = pageIndex,
                Parent = parent,
                Depth = depth,
                SourceOrder = order++
            };
            output.Add(item);

            if (node.Children is { Count: > 0 } children)
                MapBookmarks(children, item, depth + 1, item.Children, ref order, ct, untitledFallback);
        }
    }

    private static BitmapSource ToBitmapSource(SKBitmap bmp)
    {
        SKBitmap src = bmp;
        if (bmp.ColorType != SKColorType.Bgra8888)
        {
            src = new SKBitmap(bmp.Width, bmp.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            bmp.CopyTo(src, SKColorType.Bgra8888);
        }

        try
        {
            var bs = BitmapSource.Create(
                src.Width, src.Height, 96, 96,
                PixelFormats.Pbgra32, null,
                src.GetPixels(), src.RowBytes * src.Height, src.RowBytes);
            bs.Freeze();
            return bs;
        }
        finally
        {
            if (!ReferenceEquals(src, bmp))
                src.Dispose();
        }
    }
}
