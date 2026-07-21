using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PdfLiteViewer;

/// <summary>
/// The app's one print surface: live preview (same scale-to-fit math as the
/// paginator), page-range selection, printer picker and copies — Print sends
/// the job directly, no second OS dialog. (The Win11 print dialog's built-in
/// preview pane only serves the UWP print pipeline, so it can't be used here.)
/// </summary>
public partial class PrintPreviewWindow : Window
{
    private readonly PdfDoc _doc;
    private readonly int _currentDocPage;
    private Size _paper = new(816, 1056);   // letter fallback
    private List<int> _pages = new();
    private int _previewIndex;
    private CancellationTokenSource _cts = new();

    public PrintPreviewWindow(PdfDoc doc, int currentDocPage)
    {
        InitializeComponent();
        _doc = doc;
        _currentDocPage = currentDocPage;
        Title = $"Print — {System.IO.Path.GetFileName(doc.FilePath)}";

        LoadPrinters();
        ApplyPaperSize();
        RebuildPages();
    }

    private void LoadPrinters()
    {
        try
        {
            using var server = new LocalPrintServer();
            var queues = server.GetPrintQueues(new[]
            {
                EnumeratedPrintQueueTypes.Local,
                EnumeratedPrintQueueTypes.Connections,
            }).Select(q => q.FullName).ToList();

            string? defaultName = null;
            try { defaultName = LocalPrintServer.GetDefaultPrintQueue().FullName; } catch { }

            foreach (var name in queues)
                PrinterBox.Items.Add(name);

            PrinterBox.SelectedItem = defaultName is not null && queues.Contains(defaultName)
                ? defaultName
                : queues.FirstOrDefault();
        }
        catch
        {
            // No print system available; leave the list empty.
        }

        PrintBtn.IsEnabled = PrinterBox.SelectedItem is not null;
    }

    private PrintQueue? SelectedQueue()
    {
        if (PrinterBox.SelectedItem is not string name) return null;
        try
        {
            using var server = new LocalPrintServer();
            return server.GetPrintQueues(new[]
            {
                EnumeratedPrintQueueTypes.Local,
                EnumeratedPrintQueueTypes.Connections,
            }).FirstOrDefault(q => q.FullName == name);
        }
        catch
        {
            return null;
        }
    }

    private void ApplyPaperSize()
    {
        try
        {
            var queue = SelectedQueue();
            if (queue is not null)
            {
                var dlg = new PrintDialog { PrintQueue = queue };
                var size = new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight);
                if (size.Width >= 50 && size.Height >= 50)
                    _paper = size;
            }
        }
        catch
        {
            // keep current paper size
        }

        Paper.Width = _paper.Width;
        Paper.Height = _paper.Height;
        PaperCanvas.Width = _paper.Width;
        PaperCanvas.Height = _paper.Height;
    }

    private void Printer_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        PrintBtn.IsEnabled = PrinterBox.SelectedItem is not null;
        ApplyPaperSize();
        _ = ShowPageAsync();
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
            if (BwCheck.IsChecked == true)
            {
                var gray = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                    bmp, System.Windows.Media.PixelFormats.Gray8, null, 0);
                gray.Freeze();
                PageImage.Source = gray;
            }
            else
            {
                PageImage.Source = bmp;
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Bw_Toggled(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) _ = ShowPageAsync();
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
        if (e.OriginalSource is TextBox && e.Key is Key.Left or Key.Right) return;

        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Left:
            case Key.PageUp: _previewIndex--; _ = ShowPageAsync(); break;
            case Key.Right:
            case Key.PageDown: _previewIndex++; _ = ShowPageAsync(); break;
            case Key.Enter:
            case Key.P when Keyboard.Modifiers.HasFlag(ModifierKeys.Control): Print_Click(sender, e); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_pages.Count == 0) return;
        var queue = SelectedQueue();
        if (queue is null) return;

        int copies = int.TryParse(CopiesBox.Text, out int c) ? Math.Clamp(c, 1, 99) : 1;

        var dlg = new PrintDialog { PrintQueue = queue };
        if (dlg.PrintTicket is not null)
        {
            dlg.PrintTicket.CopyCount = copies;
            if (BwCheck.IsChecked == true)
                dlg.PrintTicket.OutputColor = OutputColor.Grayscale;
            if (DraftCheck.IsChecked == true)
                dlg.PrintTicket.OutputQuality = OutputQuality.Draft;
        }

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
