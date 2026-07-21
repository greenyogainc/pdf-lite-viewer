using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PdfLiteViewer;

/// <summary>
/// Shows pages exactly as the print paginator will place them on paper
/// (same scale-to-fit math), with page-range selection. "Print…" then opens
/// the system dialog for printer/copies and prints the previewed range.
/// </summary>
public partial class PrintPreviewWindow : Window
{
    private readonly PdfDoc _doc;
    private readonly int _currentDocPage;
    private Size _paper;
    private List<int> _pages = new();
    private int _previewIndex;
    private CancellationTokenSource _cts = new();

    public PrintPreviewWindow(PdfDoc doc, int currentDocPage)
    {
        InitializeComponent();
        _doc = doc;
        _currentDocPage = currentDocPage;
        Title = $"Print preview — {System.IO.Path.GetFileName(doc.FilePath)}";

        // Paper size of the default printer; letter fallback when there is none.
        try
        {
            var probe = new PrintDialog();
            _paper = new Size(probe.PrintableAreaWidth, probe.PrintableAreaHeight);
            if (_paper.Width < 50 || _paper.Height < 50) throw new InvalidOperationException();
        }
        catch
        {
            _paper = new Size(816, 1056);
        }

        Paper.Width = _paper.Width;
        Paper.Height = _paper.Height;
        PaperCanvas.Width = _paper.Width;
        PaperCanvas.Height = _paper.Height;

        RebuildPages();
    }

    private void RebuildPages()
    {
        _pages = RangeMode.SelectedIndex switch
        {
            1 => new List<int> { _currentDocPage },
            2 => ParseRange(RangeBox.Text),
            _ => Enumerable.Range(0, _doc.PageCount).ToList(),
        };

        bool empty = _pages.Count == 0;
        EmptyRangeHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Paper.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        _previewIndex = 0;
        _ = ShowPageAsync();
    }

    /// <summary>Parses "1-5, 8, 11-13" into 0-based page indices (clamped, deduplicated, ordered).</summary>
    private List<int> ParseRange(string text)
    {
        var result = new SortedSet<int>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = part.Split('-', StringSplitOptions.TrimEntries);
            if (bounds.Length == 1 && int.TryParse(bounds[0], out int single))
            {
                if (single >= 1 && single <= _doc.PageCount) result.Add(single - 1);
            }
            else if (bounds.Length == 2 && int.TryParse(bounds[0], out int from) && int.TryParse(bounds[1], out int to))
            {
                from = Math.Max(1, from);
                to = Math.Min(_doc.PageCount, to);
                for (int p = from; p <= to; p++) result.Add(p - 1);
            }
        }
        return result.ToList();
    }

    private async Task ShowPageAsync()
    {
        if (_pages.Count == 0)
        {
            PageLabel.Text = "0 / 0";
            return;
        }

        _previewIndex = Math.Clamp(_previewIndex, 0, _pages.Count - 1);
        int pdfIndex = _pages[_previewIndex];
        PageLabel.Text = $"{_previewIndex + 1} / {_pages.Count}";

        var (ptW, ptH) = _doc.PageSizes[pdfIndex];
        var rect = PdfPrintPaginator.PlacePage(ptW, ptH, _paper);
        Canvas.SetLeft(PageImage, rect.X);
        Canvas.SetTop(PageImage, rect.Y);
        PageImage.Width = rect.Width;
        PageImage.Height = rect.Height;

        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        try
        {
            // ~1300px is plenty for an on-screen preview at any window size.
            var bmp = await _doc.RenderPageAsync(pdfIndex, 1300, _cts.Token);
            PageImage.Source = bmp;
        }
        catch (OperationCanceledException) { }
    }

    private void Prev_Click(object sender, RoutedEventArgs e) { _previewIndex--; _ = ShowPageAsync(); }
    private void Next_Click(object sender, RoutedEventArgs e) { _previewIndex++; _ = ShowPageAsync(); }

    private void RangeMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        RangeBox.Visibility = RangeMode.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        RebuildPages();
    }

    private void RangeBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded && RangeMode.SelectedIndex == 2) RebuildPages();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Left:
            case Key.PageUp: _previewIndex--; _ = ShowPageAsync(); break;
            case Key.Right:
            case Key.PageDown: _previewIndex++; _ = ShowPageAsync(); break;
            case Key.P when Keyboard.Modifiers.HasFlag(ModifierKeys.Control): Print_Click(sender, e); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_pages.Count == 0) return;

        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return;

        // Printing happens at the printer's actual paper size, which can
        // differ from the preview's default-printer guess.
        var paper = new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight);
        var paginator = new PdfPrintPaginator(_doc, _pages, paper);

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            dlg.PrintDocument(paginator, System.IO.Path.GetFileName(_doc.FilePath));
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Printing failed.\n\n{ex.Message}",
                "PDF Lite Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }
}
