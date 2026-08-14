using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PdfLiteViewer;

/// <summary>
/// The app's one print surface: live preview (same scale-to-fit math as the
/// paginator), page-range selection, printer picker and copies — Print sends
/// the job directly, no second OS dialog. (The Win11 print dialog's built-in
/// preview pane only serves the UWP print pipeline, so it can't be used here.)
///
/// Every call into the print spooler — enumerating queues, reading a paper size,
/// sending the job — goes through <see cref="PrintJob"/> on a worker thread, so a
/// slow or offline printer can never freeze this window.
/// </summary>
public partial class PrintPreviewWindow : Window
{
    private readonly PdfDoc _doc;
    private readonly int _currentDocPage;
    private Size _paper = PrintJob.FallbackPaper;
    private List<int> _pages = new();
    private int _previewIndex;
    private CancellationTokenSource _cts = new();
    private bool _printing;

    public PrintPreviewWindow(PdfDoc doc, int currentDocPage)
    {
        InitializeComponent();
        Strings.ApplyFlowDirection(this);
        _doc = doc;
        _currentDocPage = currentDocPage;
        Title = string.Format(Strings.Get("PrintWindowTitleFormat"), System.IO.Path.GetFileName(doc.FilePath));

        PrintBtn.IsEnabled = false;         // until a printer is known
        ApplyPaperSize(_paper);             // letter placeholder; the real size lands below
        RebuildPages();
        _ = LoadPrintersAsync();
    }

    private async Task LoadPrintersAsync()
    {
        var (names, defaultName) = await Task.Run(PrintJob.EnumerateQueues);
        if (!IsLoaded && !IsVisible) return;

        foreach (var name in names)
            PrinterBox.Items.Add(name);

        PrinterBox.SelectedItem = defaultName is not null && names.Contains(defaultName)
            ? defaultName
            : names.FirstOrDefault();

        UpdatePrintEnabled();
        await UpdatePaperSizeAsync();
    }

    private async Task UpdatePaperSizeAsync()
    {
        if (PrinterBox.SelectedItem is not string name) return;

        var paper = await Task.Run(() => PrintJob.PaperFor(name));

        // A second printer change while this one was in flight owns the UI instead.
        if (PrinterBox.SelectedItem as string != name) return;

        _paper = paper;
        ApplyPaperSize(paper);
        await ShowPageAsync();
    }

    private void ApplyPaperSize(Size paper)
    {
        Paper.Width = paper.Width;
        Paper.Height = paper.Height;
        PaperCanvas.Width = paper.Width;
        PaperCanvas.Height = paper.Height;
    }

    private void UpdatePrintEnabled() =>
        PrintBtn.IsEnabled = !_printing && PrinterBox.SelectedItem is not null && _pages.Count > 0;

    private void Printer_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdatePrintEnabled();
        _ = UpdatePaperSizeAsync();
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
        UpdatePrintEnabled();

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
            PageLabel.Text = string.Format(Strings.Get("PageLabelFormat"), 0, 0);
            return;
        }

        _previewIndex = Math.Clamp(_previewIndex, 0, _pages.Count - 1);
        int pdfIndex = _pages[_previewIndex];
        PageLabel.Text = string.Format(Strings.Get("PageLabelFormat"), _previewIndex + 1, _pages.Count);

        var (ptW, ptH) = _doc.GetDisplaySize(pdfIndex);
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

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_printing || _pages.Count == 0 || PrinterBox.SelectedItem is not string queueName) return;

        // Snapshot the settings: the job runs on its own thread from here on.
        var pages = _pages.ToList();
        int copies = int.TryParse(CopiesBox.Text, out int c) ? Math.Clamp(c, 1, 99) : 1;
        bool grayscale = BwCheck.IsChecked == true;
        bool draft = DraftCheck.IsChecked == true;
        var jobName = System.IO.Path.GetFileName(_doc.FilePath);

        SetPrintingState(true);
        try
        {
            // Rendering every page at 300 DPI used to happen here, on the UI thread.
            await PrintJob.RunAsync(_doc, pages, queueName, copies, grayscale, draft, jobName);
            if (IsLoaded) Close();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            Strings.ShowError(this, string.Format(Strings.Get("PrintingFailedMessage"), ex.Message));
        }
        finally
        {
            SetPrintingState(false);
        }
    }

    /// <summary>The window stays responsive while a job spools; it just stops taking new input.</summary>
    private void SetPrintingState(bool printing)
    {
        _printing = printing;
        PrinterBox.IsEnabled = !printing;
        RangeMode.IsEnabled = !printing;
        RangeBox.IsEnabled = !printing;
        CopiesBox.IsEnabled = !printing;
        BwCheck.IsEnabled = !printing;
        DraftCheck.IsEnabled = !printing;
        Mouse.OverrideCursor = printing ? Cursors.Wait : null;
        UpdatePrintEnabled();
    }
}
