using System.ComponentModel;
using System.Windows.Media.Imaging;

namespace PdfLiteViewer;

/// <summary>One page slot in the current view. Bitmap is filled in lazily as it scrolls into view.</summary>
public sealed class PageItem : INotifyPropertyChanged
{
    private BitmapSource? _image;
    private double _displayWidth;
    private double _displayHeight;

    public int PageIndex { get; init; }

    public BitmapSource? Image
    {
        get => _image;
        set { _image = value; OnChanged(nameof(Image)); }
    }

    public double DisplayWidth
    {
        get => _displayWidth;
        set { _displayWidth = value; OnChanged(nameof(DisplayWidth)); }
    }

    public double DisplayHeight
    {
        get => _displayHeight;
        set { _displayHeight = value; OnChanged(nameof(DisplayHeight)); }
    }

    /// <summary>Pixel width of the bitmap currently in <see cref="Image"/>, 0 if none.</summary>
    public int RenderedPixelWidth { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
