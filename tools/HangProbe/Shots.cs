using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HangProbe;

/// <summary>
/// Renders the live window to a PNG so a human can eyeball the layout the numeric checks
/// can only describe — page centring, the sidebar, zoomed panning.
/// </summary>
internal static class Shots
{
    public static string Capture(FrameworkElement element, string name)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        var path = Path.Combine(Path.GetTempPath(), $"hangprobe-{name}.png");
        using var file = File.Create(path);
        encoder.Save(file);
        return path;
    }
}
