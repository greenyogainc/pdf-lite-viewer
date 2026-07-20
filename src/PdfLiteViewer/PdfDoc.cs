using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace PdfLiteViewer;

/// <summary>
/// Wraps a loaded PDF and renders pages to WPF bitmaps via PDFium (PDFtoImage).
/// PDFium is not thread-safe, so all renders are serialized through one lock.
/// </summary>
public sealed class PdfDoc
{
    private static readonly SemaphoreSlim RenderLock = new(1, 1);

    private readonly byte[] _bytes;

    public string FilePath { get; }
    public int PageCount { get; }

    /// <summary>Page sizes in PDF points (1/72 inch).</summary>
    public IReadOnlyList<(double Width, double Height)> PageSizes { get; }

    public PdfDoc(string path)
    {
        FilePath = path;
        _bytes = File.ReadAllBytes(path);
        PageCount = PDFtoImage.Conversion.GetPageCount(_bytes);
        var sizes = PDFtoImage.Conversion.GetPageSizes(_bytes);
        PageSizes = sizes.Select(s => ((double)s.Width, (double)s.Height)).ToList();
    }

    public async Task<BitmapSource> RenderPageAsync(int pageIndex, int targetPixelWidth, CancellationToken ct)
    {
        await RenderLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
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
