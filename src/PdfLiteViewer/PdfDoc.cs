using System.IO;
using System.Runtime.InteropServices;
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
    // First-completed parse wins; every other caller (concurrent probe, late sidebar
    // open, etc.) blocks on .Value and gets the same list. ExecutionAndPublication keeps
    // a duplicate parse from ever racing the first one.
    private Lazy<List<ChapterItem>>? _chaptersLazy;

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
            throw new UnreadablePagesException("The document contains no pages.");
        var sizes = PDFtoImage.Conversion.GetPageSizes(_bytes);
        PageSizes = sizes.Select(s => ((double)s.Width, (double)s.Height)).ToList();
        // Every layout indexes PageSizes by page number up to PageCount - 1, and the two come
        // from separate PDFium calls: pin the invariant here rather than in a layout pass.
        if (PageSizes.Count != PageCount)
            throw new UnreadablePagesException($"The document reports {PageCount} pages but {PageSizes.Count} page sizes.");
    }

    /// <summary>
    /// Page size in PDF points after the current <see cref="Rotation"/> —
    /// width/height swap for 90° and 270°.
    /// </summary>
    public (double Width, double Height) GetDisplaySize(int pageIndex) => GetDisplaySize(pageIndex, Rotation);

    /// <summary>
    /// The same under an explicit rotation. The print paginator snapshots the rotation when
    /// the job starts, so rotating the view while pages are still being produced on the print
    /// thread can neither change the remaining sheets nor tear one between size and bitmap.
    /// </summary>
    public (double Width, double Height) GetDisplaySize(int pageIndex, PDFtoImage.PdfRotation rotation)
    {
        var (w, h) = PageSizes[pageIndex];
        return rotation is PDFtoImage.PdfRotation.Rotate90 or PDFtoImage.PdfRotation.Rotate270
            ? (h, w)
            : (w, h);
    }

    public async Task<BitmapSource> RenderPageAsync(int pageIndex, int targetPixelWidth, CancellationToken ct)
    {
        // Track acquisition explicitly. SemaphoreSlim.WaitAsync(ct) throws
        // OperationCanceledException on token cancellation WITHOUT decrementing the semaphore,
        // so an unconditional RenderLock.Release() in the finally block would call Release on
        // a lock whose count is still at its maximum — SemaphoreFullException — and the real
        // cancellation would be hidden behind an unrelated, unobservable fault in the caller.
        bool acquired = false;
        try
        {
            await RenderLock.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
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
            if (acquired) RenderLock.Release();
        }
    }

    /// <summary>
    /// Synchronous render, used by the print paginator (one page at a time) with the rotation
    /// it snapshotted at job start rather than the live <see cref="Rotation"/>.
    /// </summary>
    public BitmapSource RenderPageSync(int pageIndex, int targetPixelWidth, PDFtoImage.PdfRotation rotation)
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
                    Rotation: rotation,
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
        // Fast path: the parse has already been completed; even a torn read here is fine,
        // because every later reader of the field will see at least this lazy.
        var lazy = _chaptersLazy;
        if (lazy is null)
        {
            // Double-check inside the lock; the field is assigned under the gate so a
            // racing reader either sees our new Lazy or our predecessor's Lazy — never a
            // freshly-created one that has yet to do work. The whole point of the lazy is
            // to ensure the parse runs at most once even with concurrent first callers.
            lock (_chaptersGate)
            {
                lazy = _chaptersLazy ??= new Lazy<List<ChapterItem>>(
                    () => BuildChapters(untitledFallback),
                    LazyThreadSafetyMode.ExecutionAndPublication);
            }
        }

        // Lazy.Value ignores CancellationToken, so the only way to honor ct here is to
        // check it before accessing Value. The parse itself runs to completion once the
        // first caller wins the lazy; later callers just observe the cached list. The
        // caller's Task.Run already gates the entire call on ct, so the practical
        // cancellation surface is what the user already expects for a chapter load.
        ct.ThrowIfCancellationRequested();
        return lazy.Value;
    }

    private List<ChapterItem> BuildChapters(string untitledFallback)
    {
        // SkipMissingFonts: outline extraction never needs glyph data; avoids font-parse
        // work PdfPig would otherwise do while opening large documents.
        using var pdf = PdfDocument.Open(_bytes, new ParsingOptions { SkipMissingFonts = true });

        var roots = new List<ChapterItem>();
        if (!pdf.TryGetBookmarks(out var bookmarks, allowContainerNode: true))
            return roots;

        int order = 0;
        MapBookmarks(bookmarks.Roots, parent: null, depth: 0, output: roots, order: ref order, default, untitledFallback);
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

    /// <summary>
    /// Copies the Skia pixel storage into a GC-owned buffer and returns a frozen WPF
    /// <see cref="BitmapSource"/> built over that managed copy. The native buffer is
    /// read once, via <see cref="Marshal.Copy(int, byte[], int, int)"/>, so disposing
    /// the source <see cref="SKBitmap"/> after this returns is safe — WIC will not be
    /// asked to read freed memory when the bitmap is realized by the virtualizing panel.
    /// </summary>
    private static BitmapSource ToBitmapSource(SKBitmap bmp)
    {
        // PDFtoImage hands back whatever format the converter chose; normalize to BGRA so
        // the byte order always matches WPF's Pbgra32. The temp bitmap owns its own pixels,
        // so disposing it before Marshal.Copy below is safe.
        SKBitmap src = bmp;
        SKBitmap? converted = null;
        try
        {
            if (bmp.ColorType != SKColorType.Bgra8888)
            {
                converted = new SKBitmap(bmp.Width, bmp.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                bmp.CopyTo(converted, SKColorType.Bgra8888);
                src = converted;
            }

            int stride = src.RowBytes;
            int length = stride * src.Height;
            var managed = new byte[length];
            Marshal.Copy(src.GetPixels(), managed, 0, length);

            // The Array overload copies the input buffer into a WIC-owned allocation, so
            // the managed array is only needed during this call. Stride is derived from
            // width * bytesPerPixel for tightly-packed pixel formats like Pbgra32, which
            // always matches the row length this method hands in.
            var bs = BitmapSource.Create(
                src.Width, src.Height, 96, 96,
                PixelFormats.Pbgra32, null,
                managed, stride);
            bs.Freeze();
            return bs;
        }
        finally
        {
            converted?.Dispose();
        }
    }
}

/// <summary>
/// Thrown by <see cref="PdfDoc"/> when PDFium opens a file but reports no usable pages. The
/// main window maps it to a localized message; tools see the English detail.
/// </summary>
public sealed class UnreadablePagesException : IOException
{
    public UnreadablePagesException(string message) : base(message) { }
}
