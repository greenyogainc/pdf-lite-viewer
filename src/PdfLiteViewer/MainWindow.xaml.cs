using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PdfLiteViewer;

public enum ViewMode { Facing, Single, Continuous }

public partial class MainWindow : Window
{
    private const double PageMargin = 16;   // 8 on each side, from the item template
    private const int MaxRenderPixelWidth = 3500;
    private const int RenderBuffer = 2;     // extra pages rendered above/below the viewport
    private const int KeepBuffer = 5;       // pages kept in memory beyond the viewport

    private PdfDoc? _doc;
    private ViewMode _mode = ViewMode.Facing;
    private int _currentPage;               // 0-based
    private double _zoom = 1.0;
    private bool _fitToView = true;

    private readonly ObservableCollection<PageItem> _items = new();
    private readonly DispatcherTimer _renderTimer;
    private CancellationTokenSource _renderCts = new();

    private bool _fullscreen;
    private WindowState _preFsState;
    private WindowStyle _preFsStyle;

    private readonly ItemsPanelTemplate _verticalPanel;
    private readonly ItemsPanelTemplate _horizontalPanel;

    public MainWindow()
    {
        InitializeComponent();
        PagesHost.ItemsSource = _items;

        _verticalPanel = MakePanelTemplate(Orientation.Vertical);
        _horizontalPanel = MakePanelTemplate(Orientation.Horizontal);
        PagesHost.ItemsPanel = _verticalPanel;

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _renderTimer.Tick += (_, _) => { _renderTimer.Stop(); _ = UpdateRenderedPagesAsync(); };

        Loaded += MainWindow_Loaded;
        SizeChanged += (_, _) =>
        {
            if (!_fitToView) return;
            // The layout pass for this resize (e.g. maximize/fullscreen) hasn't
            // completed yet, so Scroller.ViewportWidth/Height are still stale here.
            // Defer until after layout settles so FitZoom() sees the real size.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ApplyLayout(scrollToCurrent: false));
        };
    }

    private static ItemsPanelTemplate MakePanelTemplate(Orientation orientation)
    {
        var factory = new FrameworkElementFactory(typeof(StackPanel));
        factory.SetValue(StackPanel.OrientationProperty, orientation);
        return new ItemsPanelTemplate(factory);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var startupFile = ((App)Application.Current).StartupFile;
        if (startupFile is not null)
            OpenFile(startupFile);
    }

    // ---------- File handling ----------

    private void Open_Click(object sender, RoutedEventArgs e) => ShowOpenDialog();

    private void ShowOpenDialog()
    {
        var dlg = new OpenFileDialog { Filter = "PDF files (*.pdf)|*.pdf", Title = "Open PDF" };
        if (dlg.ShowDialog() == true)
            OpenFile(dlg.FileName);
    }

    private void OpenFile(string path)
    {
        try
        {
            _doc = new PdfDoc(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open \"{path}\".\n\n{ex.Message}",
                "PDF Lite Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Title = $"{System.IO.Path.GetFileName(path)} — PDF Lite Viewer";
        EmptyHint.Visibility = Visibility.Collapsed;
        _currentPage = 0;
        _fitToView = true;
        PageCountText.Text = $"/ {_doc.PageCount}";
        RebuildItems();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasPdf(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var pdf = files.FirstOrDefault(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
            if (pdf is not null) OpenFile(pdf);
        }
    }

    private static bool HasPdf(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files &&
        files.Any(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

    // ---------- View modes ----------

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _mode = ReferenceEquals(sender, ModeFacing) ? ViewMode.Facing
              : ReferenceEquals(sender, ModeSingle) ? ViewMode.Single
              : ViewMode.Continuous;
        _fitToView = true;
        RebuildItems();
    }

    private void SetMode(ViewMode mode)
    {
        // Setting IsChecked triggers Mode_Checked, which rebuilds.
        switch (mode)
        {
            case ViewMode.Facing: ModeFacing.IsChecked = true; break;
            case ViewMode.Single: ModeSingle.IsChecked = true; break;
            default: ModeContinuous.IsChecked = true; break;
        }
    }

    /// <summary>Pages shown together in facing mode: [0], [1,2], [3,4], … (book layout).</summary>
    private static int FacingGroupStart(int page) => page == 0 ? 0 : (page % 2 == 1 ? page : page - 1);

    private void RebuildItems()
    {
        if (_doc is null) return;

        CancelPendingRenders();
        _items.Clear();

        switch (_mode)
        {
            case ViewMode.Continuous:
                PagesHost.ItemsPanel = _verticalPanel;
                for (int i = 0; i < _doc.PageCount; i++)
                    _items.Add(new PageItem { PageIndex = i });
                break;

            case ViewMode.Single:
                PagesHost.ItemsPanel = _verticalPanel;
                _items.Add(new PageItem { PageIndex = _currentPage });
                break;

            case ViewMode.Facing:
                PagesHost.ItemsPanel = _horizontalPanel;
                int start = FacingGroupStart(_currentPage);
                _items.Add(new PageItem { PageIndex = start });
                if (start != 0 && start + 1 < _doc.PageCount)
                    _items.Add(new PageItem { PageIndex = start + 1 });
                break;
        }

        ApplyLayout(scrollToCurrent: true);
    }

    // ---------- Zoom & layout ----------

    private double FitZoom()
    {
        if (_doc is null) return 1.0;

        double viewW = Math.Max(100, Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : Scroller.ActualWidth);
        double viewH = Math.Max(100, Scroller.ViewportHeight > 0 ? Scroller.ViewportHeight : Scroller.ActualHeight);

        // Page sizes are in points; 100% zoom maps 72pt -> 96px.
        double maxW = _items.Count > 0
            ? _items.Max(it => _doc.PageSizes[it.PageIndex].Width) * 96.0 / 72.0
            : 100;

        if (_mode == ViewMode.Continuous)
            return Math.Max(0.05, (viewW - PageMargin - 24) / maxW);

        // Single / facing: fit the whole page group inside the viewport.
        double groupW = _items.Sum(it => _doc.PageSizes[it.PageIndex].Width * 96.0 / 72.0 + PageMargin);
        double maxH = _items.Max(it => _doc.PageSizes[it.PageIndex].Height) * 96.0 / 72.0;
        double zw = (viewW - 24) / groupW;
        double zh = (viewH - PageMargin - 4) / maxH;
        return Math.Max(0.05, Math.Min(zw, zh));
    }

    private void ApplyLayout(bool scrollToCurrent)
    {
        if (_doc is null) return;

        if (_fitToView)
            _zoom = FitZoom();

        foreach (var it in _items)
        {
            var (w, h) = _doc.PageSizes[it.PageIndex];
            it.DisplayWidth = w * 96.0 / 72.0 * _zoom;
            it.DisplayHeight = h * 96.0 / 72.0 * _zoom;
        }

        ZoomText.Text = $"{Math.Round(_zoom * 100)}%";
        PageBox.Text = (_currentPage + 1).ToString();

        if (scrollToCurrent && _mode == ViewMode.Continuous)
        {
            Scroller.UpdateLayout();
            Scroller.ScrollToVerticalOffset(OffsetOfPage(_currentPage));
        }

        ScheduleRender();
    }

    private double OffsetOfPage(int page)
    {
        double off = 0;
        for (int i = 0; i < page && i < _items.Count; i++)
            off += _items[i].DisplayHeight + PageMargin;
        return off;
    }

    private void SetZoom(double zoom)
    {
        _fitToView = false;
        _zoom = Math.Clamp(zoom, 0.1, 6.0);
        ApplyLayout(scrollToCurrent: false);
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom * 1.2);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom / 1.2);

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        _fitToView = true;
        ApplyLayout(scrollToCurrent: false);
    }

    // ---------- Navigation ----------

    private void GoToPage(int page, bool scroll = true)
    {
        if (_doc is null) return;
        page = Math.Clamp(page, 0, _doc.PageCount - 1);

        if (_mode == ViewMode.Continuous)
        {
            _currentPage = page;
            PageBox.Text = (page + 1).ToString();
            if (scroll) Scroller.ScrollToVerticalOffset(OffsetOfPage(page));
        }
        else
        {
            if (page == _currentPage && _items.Count > 0) return;
            _currentPage = page;
            RebuildItems();
        }
    }

    private void StepPage(int direction)
    {
        if (_doc is null) return;

        if (_mode == ViewMode.Facing)
        {
            int start = FacingGroupStart(_currentPage);
            int target = direction > 0
                ? (start == 0 ? 1 : start + 2)
                : (start <= 1 ? 0 : start - 2);
            GoToPage(target);
        }
        else
        {
            GoToPage(_currentPage + direction);
        }
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => StepPage(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => StepPage(+1);

    private void PageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && int.TryParse(PageBox.Text, out int p))
        {
            GoToPage(p - 1);
            Scroller.Focus();
            e.Handled = true;
        }
    }

    // ---------- Printing ----------

    private void Print_Click(object sender, RoutedEventArgs e) => ShowPrintPreview();

    private void ShowPrintPreview()
    {
        if (_doc is null) return;
        try
        {
            new PrintPreviewWindow(_doc, _currentPage) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PdfLiteViewer.log"),
                $"[dbg] preview failed: {ex}\n");
            MessageBox.Show(this, $"Print preview failed.\n\n{ex.Message}",
                "PDF Lite Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- Fullscreen ----------

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _preFsState = WindowState;
            _preFsStyle = WindowStyle;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;   // reset first so Maximized fills the whole screen
            WindowState = WindowState.Maximized;
            ToolbarHost.Visibility = Visibility.Collapsed;
            _fullscreen = true;
        }
        else
        {
            WindowStyle = _preFsStyle;
            WindowState = _preFsState;
            ToolbarHost.Visibility = Visibility.Visible;
            _fullscreen = false;
        }
        if (_fitToView)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ApplyLayout(scrollToCurrent: false));
    }

    // ---------- Input ----------

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        switch (e.Key)
        {
            case Key.O when ctrl: ShowOpenDialog(); break;
            case Key.P when ctrl: ShowPrintPreview(); break;
            case Key.F11: ToggleFullscreen(); break;
            case Key.Escape when _fullscreen: ToggleFullscreen(); break;

            case Key.D1 when !ctrl: SetMode(ViewMode.Single); break;
            case Key.D2 when !ctrl: SetMode(ViewMode.Facing); break;
            case Key.D3 when !ctrl: SetMode(ViewMode.Continuous); break;

            case Key.OemPlus when ctrl:
            case Key.Add when ctrl: SetZoom(_zoom * 1.2); break;
            case Key.OemMinus when ctrl:
            case Key.Subtract when ctrl: SetZoom(_zoom / 1.2); break;
            case Key.D0 when ctrl:
            case Key.NumPad0 when ctrl: _fitToView = true; ApplyLayout(false); break;

            case Key.PageDown:
            case Key.Right: StepPage(+1); break;
            case Key.PageUp:
            case Key.Left: StepPage(-1); break;
            case Key.Home: GoToPage(0); break;
            case Key.End: GoToPage(_doc?.PageCount - 1 ?? 0); break;

            default: return;
        }
        e.Handled = true;
    }

    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SetZoom(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1));
            e.Handled = true;
        }
        else if (_mode != ViewMode.Continuous && Scroller.ScrollableHeight < 1)
        {
            StepPage(e.Delta > 0 ? -1 : +1);
            e.Handled = true;
        }
    }

    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_doc is null || _mode != ViewMode.Continuous || _items.Count == 0) return;

        // Track the topmost visible page.
        double off = Scroller.VerticalOffset;
        double acc = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            acc += _items[i].DisplayHeight + PageMargin;
            if (acc > off + 1)
            {
                if (_currentPage != i)
                {
                    _currentPage = i;
                    PageBox.Text = (i + 1).ToString();
                }
                break;
            }
        }

        ScheduleRender();
    }

    // ---------- Rendering ----------

    private void ScheduleRender()
    {
        _renderTimer.Stop();
        _renderTimer.Start();
    }

    private void CancelPendingRenders()
    {
        _renderCts.Cancel();
        _renderCts.Dispose();
        _renderCts = new CancellationTokenSource();
    }

    private (int First, int Last) VisibleRange()
    {
        if (_mode != ViewMode.Continuous)
            return (0, _items.Count - 1);

        double top = Scroller.VerticalOffset;
        double bottom = top + Scroller.ViewportHeight;
        int first = 0, last = _items.Count - 1;
        double acc = 0;
        bool firstFound = false;

        for (int i = 0; i < _items.Count; i++)
        {
            double h = _items[i].DisplayHeight + PageMargin;
            if (!firstFound && acc + h > top) { first = i; firstFound = true; }
            if (acc > bottom) { last = i - 1; break; }
            acc += h;
        }
        return (first, last);
    }

    private async Task UpdateRenderedPagesAsync()
    {
        if (_doc is null || _items.Count == 0) return;

        CancelPendingRenders();
        var ct = _renderCts.Token;
        var doc = _doc;

        var (first, last) = VisibleRange();
        int lo = Math.Max(0, first - RenderBuffer);
        int hi = Math.Min(_items.Count - 1, last + RenderBuffer);

        // Free bitmaps far outside the viewport.
        for (int i = 0; i < _items.Count; i++)
        {
            if ((i < first - KeepBuffer || i > last + KeepBuffer) && _items[i].Image is not null)
            {
                _items[i].Image = null;
                _items[i].RenderedPixelWidth = 0;
            }
        }

        double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;

        // Render the visible pages first, then the buffer.
        var order = Enumerable.Range(lo, hi - lo + 1)
            .OrderBy(i => i < first || i > last ? 1 : 0)
            .ToList();

        foreach (int i in order)
        {
            var item = _items[i];
            int targetPx = Math.Min(MaxRenderPixelWidth, (int)Math.Round(item.DisplayWidth * dpiScale));
            if (targetPx < 8 || item.RenderedPixelWidth == targetPx)
                continue;

            try
            {
                var bmp = await doc.RenderPageAsync(item.PageIndex, targetPx, ct);
                if (ct.IsCancellationRequested) return;
                item.Image = bmp;
                item.RenderedPixelWidth = targetPx;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Skip pages that fail to render rather than crashing the viewer.
            }
        }
    }
}
