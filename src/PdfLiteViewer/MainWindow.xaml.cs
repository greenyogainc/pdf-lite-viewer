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

    /// <summary>The open document, for tools/HangProbe's layout assertions.</summary>
    internal PdfDoc? Document => _doc;

    private ViewMode _mode = ViewMode.Facing;
    private int _currentPage;               // 0-based
    private double _zoom = 1.0;
    private bool _fitToView = true;
    private double _contentHeight;          // exact total height of all pages, in pixels

    // Replaced wholesale on every rebuild (never mutated in place), so a 3000-page
    // document costs the ItemsControl one collection reset instead of 3000.
    private List<PageItem> _items = new();
    private readonly DispatcherTimer _renderTimer;
    private CancellationTokenSource _renderCts = new();

    // The only indices whose PageItem.Image may be non-null, so eviction walks this
    // window instead of every page — on a 50,000-page document the old full scan ran
    // on the UI thread at every settled render update. Empty when _retainedHi < _retainedLo.
    private int _retainedLo = int.MaxValue;
    private int _retainedHi = -1;

    /// <summary>Items visited by the last eviction pass — tools/HangProbe asserts this
    /// stays proportional to the retained window, not the document.</summary>
    internal int LastEvictionScanLength;

    /// <summary>Guards against a slow open being applied after a newer one started.</summary>
    private int _openGeneration;

    private bool _fullscreen;
    private WindowState _preFsState;
    private WindowStyle _preFsStyle;

    // Chapter sidebar state. The loaded tree is cached per PdfDoc; switching documents
    // cancels any in-flight load and invalidates the previous document's chapters.
    private enum ChapterLoadState { NotLoaded, Loading, Loaded, Empty, Failed }

    private bool _chapterPaneVisible;
    private bool _preFsChapterVisible;
    private GridLength _chapterPaneWidth = new(270);
    private ChapterLoadState _chapterState = ChapterLoadState.NotLoaded;
    private List<ChapterItem>? _chapterRoots;
    private List<ChapterItem> _navigableChapters = new();   // flattened, sorted by (PageIndex, SourceOrder)
    private ChapterItem? _selectedChapter;
    private CancellationTokenSource _chapterCts = new();
    private bool _suppressChapterNav;
    private bool _chapterScrollQueued;

    /// <summary>Realization echoes absorbed by the chapter-selection guard. tools/HangProbe
    /// asserts this moved during its sidebar-scroll check — proof the guarded path was
    /// actually exercised, not merely never reached.</summary>
    internal int ChapterEchoCount;

    private readonly ItemsPanelTemplate _verticalPanel;
    private readonly ItemsPanelTemplate _horizontalPanel;
    private readonly ItemsPanelTemplate _virtualPanel;

    private ScrollViewer? _scroller;
    private Panel? _pagesPanel;   // re-resolved whenever the items panel template changes

    /// <summary>
    /// The page viewport. It lives inside <c>PagesHost</c>'s control template so that the
    /// items panel can act as the scrolling host in continuous mode (the prerequisite for
    /// UI virtualization), so it is resolved from the template rather than being a field.
    /// </summary>
    internal ScrollViewer Scroller
    {
        get
        {
            if (_scroller is null)
            {
                PagesHost.ApplyTemplate();
                _scroller = (ScrollViewer)PagesHost.Template.FindName("PART_Scroller", PagesHost);
            }
            return _scroller;
        }
    }

    private ItemsPresenter? _presenter;

    private ItemsPresenter Presenter
    {
        get
        {
            if (_presenter is null)
            {
                PagesHost.ApplyTemplate();
                _presenter = (ItemsPresenter)PagesHost.Template.FindName("PART_Presenter", PagesHost);
            }
            return _presenter;
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        Strings.ApplyFlowDirection(this);

        _verticalPanel = MakeStackPanelTemplate(Orientation.Vertical);
        _horizontalPanel = MakeStackPanelTemplate(Orientation.Horizontal);
        _virtualPanel = MakeVirtualPanelTemplate();
        PagesHost.ItemsPanel = _verticalPanel;

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _renderTimer.Tick += (_, _) => { _renderTimer.Stop(); _ = UpdateRenderedPagesAsync(); };

        PageCountText.Text = string.Format(Strings.Get("PageCountFormat"), 0);

        Loaded += MainWindow_Loaded;
    }

    private static ItemsPanelTemplate MakeStackPanelTemplate(Orientation orientation)
    {
        var factory = new FrameworkElementFactory(typeof(StackPanel));
        factory.SetValue(StackPanel.OrientationProperty, orientation);
        return new ItemsPanelTemplate(factory);
    }

    /// <summary>Continuous mode: only the pages near the viewport are ever realized.</summary>
    private static ItemsPanelTemplate MakeVirtualPanelTemplate()
    {
        var factory = new FrameworkElementFactory(typeof(VirtualizingStackPanel));
        factory.SetValue(VirtualizingStackPanel.OrientationProperty, Orientation.Vertical);
        return new ItemsPanelTemplate(factory);
    }

    /// <summary>
    /// Refit when the page viewport changes — window resize, chapter pane open/close, or
    /// splitter drag. Window.SizeChanged alone misses the in-window column redistributions
    /// from the chapter sidebar.
    /// </summary>
    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_fitToView) return;
        // The layout pass for this resize hasn't completed yet, so ViewportWidth/Height can
        // still be stale here. Defer until after layout settles so FitZoom() sees the real size.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ApplyLayout(scrollToCurrent: false));
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var startupFile = ((App)Application.Current).StartupFile;
        if (startupFile is not null)
            _ = OpenFileAsync(startupFile);
    }

    // ---------- File handling ----------

    private void Open_Click(object sender, RoutedEventArgs e) => ShowOpenDialog();

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var pdf = files.FirstOrDefault(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
            if (pdf is not null) _ = OpenFileAsync(pdf);
        }
    }

    private void ShowOpenDialog()
    {
        var dlg = new OpenFileDialog { Filter = Strings.Get("OpenFileDialogFilter"), Title = Strings.Get("OpenFileDialogTitle") };
        if (dlg.ShowDialog() == true)
            _ = OpenFileAsync(dlg.FileName);
    }

    /// <summary>
    /// Loads a document. Reading the bytes and asking PDFium for the page count and every
    /// page size happens on a worker thread: on a big file, a network share, or a
    /// cloud-placeholder file that has to be hydrated first, that work runs for seconds and
    /// would otherwise freeze the window — including on the file-association launch path,
    /// where it freezes the app before it has ever drawn.
    /// </summary>
    internal async Task OpenFileAsync(string path)
    {
        int generation = ++_openGeneration;
        EmptyHint.Visibility = Visibility.Collapsed;
        LoadingHint.Visibility = Visibility.Visible;

        PdfDoc doc;
        try
        {
            doc = await Task.Run(() => new PdfDoc(path));
        }
        catch (Exception ex)
        {
            if (generation != _openGeneration) return;   // a newer open owns the UI now
            LoadingHint.Visibility = Visibility.Collapsed;
            EmptyHint.Visibility = _doc is null ? Visibility.Visible : Visibility.Collapsed;
            Strings.ShowError(this, string.Format(Strings.Get("OpenFileErrorMessage"), path, ex.Message));
            return;
        }

        if (generation != _openGeneration) return;

        LoadingHint.Visibility = Visibility.Collapsed;
        _doc = doc;
        Title = string.Format(Strings.Get("MainWindowTitleFormat"),
            System.IO.Path.GetFileName(path), Strings.Get("AppTitle"));
        _currentPage = 0;
        _fitToView = true;
        _doc.Rotation = PDFtoImage.PdfRotation.Rotate0;
        PageCountText.Text = string.Format(Strings.Get("PageCountFormat"), _doc.PageCount);
        ResetChapters();
        RebuildItems();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasPdf(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
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

    internal void SetMode(ViewMode mode)
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
        var items = new List<PageItem>();

        switch (_mode)
        {
            case ViewMode.Continuous:
                // Virtualizing panel + panel-as-scroll-host: page containers are realized
                // only around the viewport, so cost stops scaling with page count. The
                // presenter must fill the viewport here — left to shrink-wrap, it would size
                // itself to whichever pages happen to be realized, and pages of differing
                // widths would slide sideways as you scroll.
                PagesHost.ItemsPanel = _virtualPanel;
                Scroller.CanContentScroll = true;
                SetPresenterAlignment(HorizontalAlignment.Stretch, VerticalAlignment.Top);
                for (int i = 0; i < _doc.PageCount; i++)
                    items.Add(new PageItem { PageIndex = i });
                break;

            case ViewMode.Single:
                // One or two items: plain panels, and the ScrollViewer keeps pixel scrolling
                // so a zoomed-in page pans smoothly and stays centred.
                PagesHost.ItemsPanel = _verticalPanel;
                Scroller.CanContentScroll = false;
                SetPresenterAlignment(HorizontalAlignment.Center, VerticalAlignment.Center);
                items.Add(new PageItem { PageIndex = _currentPage });
                break;

            case ViewMode.Facing:
                PagesHost.ItemsPanel = _horizontalPanel;
                Scroller.CanContentScroll = false;
                SetPresenterAlignment(HorizontalAlignment.Center, VerticalAlignment.Center);
                int start = FacingGroupStart(_currentPage);
                items.Add(new PageItem { PageIndex = start });
                if (start != 0 && start + 1 < _doc.PageCount)
                    items.Add(new PageItem { PageIndex = start + 1 });
                break;
        }

        _items = items;
        _retainedLo = int.MaxValue;   // fresh items carry no bitmaps yet
        _retainedHi = -1;

        // Size the pages before the panel ever sees them. Handed a list of zero-height items,
        // a virtualizing panel concludes the whole document fits on screen and realizes every
        // page — which is the very cost virtualization is here to avoid.
        SizeItems();

        PagesHost.ItemsSource = _items;
        _pagesPanel = null;              // a new panel is built for the new ItemsPanel template

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
            ? _items.Max(it => _doc.GetDisplaySize(it.PageIndex).Width) * 96.0 / 72.0
            : 100;

        if (_mode == ViewMode.Continuous)
            return Math.Max(0.05, (viewW - PageMargin - 24) / maxW);

        // Single / facing: fit the whole page group inside the viewport.
        double groupW = _items.Sum(it => _doc.GetDisplaySize(it.PageIndex).Width * 96.0 / 72.0 + PageMargin);
        double maxH = _items.Max(it => _doc.GetDisplaySize(it.PageIndex).Height) * 96.0 / 72.0;
        double zw = (viewW - 24) / groupW;
        double zh = (viewH - PageMargin - 4) / maxH;
        return Math.Max(0.05, Math.Min(zw, zh));
    }

    /// <summary>Applies the current zoom to every page slot and totals the content height.</summary>
    private void SizeItems()
    {
        if (_doc is null) return;

        if (_fitToView)
            _zoom = FitZoom();

        _contentHeight = 0;
        foreach (var it in _items)
        {
            var (w, h) = _doc.GetDisplaySize(it.PageIndex);
            it.DisplayWidth = w * 96.0 / 72.0 * _zoom;
            it.DisplayHeight = h * 96.0 / 72.0 * _zoom;
            _contentHeight += it.DisplayHeight + PageMargin;
        }
    }

    private void ApplyLayout(bool scrollToCurrent)
    {
        if (_doc is null) return;

        SizeItems();

        ZoomText.Text = $"{Math.Round(_zoom * 100)}%";
        PageBox.Text = (_currentPage + 1).ToString();

        if (scrollToCurrent && _mode == ViewMode.Continuous)
            ScrollToPage(_currentPage);

        ScheduleRender();
    }

    private double OffsetOfPage(int page)
    {
        double off = 0;
        for (int i = 0; i < page && i < _items.Count; i++)
            off += _items[i].DisplayHeight + PageMargin;
        return off;
    }

    /// <summary>
    /// Puts a page at the top of the viewport in continuous mode.
    ///
    /// The offset our own page heights predict is only a first guess: the panel virtualizes,
    /// so it *estimates* the height of every page it has not realized yet, and in a document
    /// with mixed page sizes that estimate drifts — asking for page 1501 landed on 1546. So
    /// jump to the estimate, then measure where the page actually is and close the gap. Each
    /// pass realizes pages nearer the target, which sharpens the panel's estimate, so this
    /// settles in two or three passes over a handful of containers.
    /// </summary>
    private void ScrollToPage(int page)
    {
        if (_mode != ViewMode.Continuous)
        {
            Scroller.ScrollToVerticalOffset(OffsetOfPage(page));
            return;
        }

        for (int pass = 0; pass < 6; pass++)
        {
            Scroller.UpdateLayout();

            // Already realized: nudge by exactly how far it sits from the top edge.
            if (ContainerOffset(page) is double delta)
            {
                if (Math.Abs(delta) < 0.5) return;
                Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + delta);
                continue;
            }

            // Still off screen: measure from the page that *is* at the top, using our own
            // exact heights for the stretch in between, rescaled into the panel's offsets.
            int anchor = TopVisiblePage();
            if (anchor < 0 || anchor == page) return;

            double gap = (OffsetOfPage(page) - OffsetOfPage(anchor)) * PanelOffsetScale()
                       + (ContainerOffset(anchor) ?? 0);
            if (Math.Abs(gap) < 0.5) return;
            Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + gap);
        }
    }

    /// <summary>
    /// Ratio between the panel's scroll offsets and real content pixels. They differ because
    /// the panel estimates the pages it has not realized; applying it up front lands the first
    /// jump within a page or two, which is what keeps the correction loop to a couple of passes.
    /// </summary>
    private double PanelOffsetScale()
    {
        double extent = Scroller.ExtentHeight;
        return _contentHeight > 0 && extent > 0 ? extent / _contentHeight : 1.0;
    }

    private void SetPresenterAlignment(HorizontalAlignment horizontal, VerticalAlignment vertical)
    {
        Presenter.HorizontalAlignment = horizontal;
        Presenter.VerticalAlignment = vertical;
    }

    /// <summary>The items panel currently in use, or null before the first layout pass.</summary>
    private Panel? PagesPanel()
    {
        if (_pagesPanel is not null) return _pagesPanel;
        if (Scroller.Content is not ItemsPresenter presenter) return null;
        presenter.ApplyTemplate();
        if (VisualTreeHelper.GetChildrenCount(presenter) == 0) return null;
        return _pagesPanel = VisualTreeHelper.GetChild(presenter, 0) as Panel;
    }

    /// <summary>Vertical position of a page's container relative to the viewport, if realized.</summary>
    private double? ContainerOffset(int page)
    {
        if (PagesHost.ItemContainerGenerator.ContainerFromIndex(page) is not FrameworkElement container ||
            !container.IsVisible)
            return null;

        return container.TransformToAncestor(Scroller).Transform(default).Y;
    }

    /// <summary>
    /// The page covering the top edge of the viewport, read from the containers the panel
    /// actually laid out. Deriving it from the scroll offset instead would inherit the
    /// panel's estimate for unrealized pages and report the wrong page number.
    /// </summary>
    private int TopVisiblePage()
    {
        var panel = PagesPanel();
        if (panel is null) return -1;

        int covering = -1, firstBelow = -1;
        double coveringTop = double.NegativeInfinity, firstBelowTop = double.PositiveInfinity;

        foreach (UIElement child in panel.Children)
        {
            if (child is not FrameworkElement element || !element.IsVisible) continue;
            int index = PagesHost.ItemContainerGenerator.IndexFromContainer(child);
            if (index < 0) continue;

            double top = element.TransformToAncestor(Scroller).Transform(default).Y;
            if (top <= 1 && top + element.ActualHeight > 1)
            {
                if (top > coveringTop) { coveringTop = top; covering = index; }
            }
            else if (top > 1 && top < firstBelowTop)
            {
                firstBelowTop = top;
                firstBelow = index;
            }
        }

        return covering >= 0 ? covering : firstBelow;
    }

    internal void SetZoom(double zoom)
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

    private void Rotate_Click(object sender, RoutedEventArgs e) => RotateClockwise();

    internal void RotateClockwise()
    {
        if (_doc is null) return;

        // Cancel before changing Rotation so an in-flight render cannot write a
        // bitmap produced with the previous orientation (especially 180°, where
        // display size is unchanged and the pixel-width cache would skip a redo).
        CancelPendingRenders();

        _doc.Rotation = _doc.Rotation switch
        {
            PDFtoImage.PdfRotation.Rotate0 => PDFtoImage.PdfRotation.Rotate90,
            PDFtoImage.PdfRotation.Rotate90 => PDFtoImage.PdfRotation.Rotate180,
            PDFtoImage.PdfRotation.Rotate180 => PDFtoImage.PdfRotation.Rotate270,
            _ => PDFtoImage.PdfRotation.Rotate0,
        };

        // Only the retained window can hold bitmaps; clearing just that keeps a rotate
        // O(window) instead of O(document).
        for (int i = _retainedLo; i <= _retainedHi; i++)
        {
            _items[i].Image = null;
            _items[i].RenderedPixelWidth = 0;
        }
        _retainedLo = int.MaxValue;
        _retainedHi = -1;

        ApplyLayout(scrollToCurrent: false);
    }

    // ---------- Navigation ----------

    internal void GoToPage(int page, bool scroll = true, bool syncChapters = true)
    {
        if (_doc is null) return;
        page = Math.Clamp(page, 0, _doc.PageCount - 1);

        if (_mode == ViewMode.Continuous)
        {
            _currentPage = page;
            PageBox.Text = (page + 1).ToString();
            if (scroll) ScrollToPage(page);
        }
        else
        {
            if (page == _currentPage && _items.Count > 0)
            {
                // Still refresh the box: "0" or "99999" clamped to the current page must
                // not leave the stale typed value standing next to the page count.
                PageBox.Text = (page + 1).ToString();
                if (syncChapters) SyncChapterSelection();
                return;
            }
            _currentPage = page;
            RebuildItems();
        }

        if (syncChapters)
            SyncChapterSelection();
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

    // ---------- Chapters ----------

    private void ChapterToggle_Changed(object sender, RoutedEventArgs e) =>
        SetChapterPaneVisible(ChapterToggle.IsChecked == true);

    private void ChapterClose_Click(object sender, RoutedEventArgs e) => SetChapterPaneVisible(false);

    internal void SetChapterPaneVisible(bool visible)
    {
        if (_chapterPaneVisible == visible) return;
        _chapterPaneVisible = visible;

        // Keep the toggle's checked state an exact mirror of pane visibility.
        if (ChapterToggle.IsChecked != visible)
            ChapterToggle.IsChecked = visible;

        if (visible)
        {
            ChapterColumn.MinWidth = 180;
            ChapterColumn.Width = _chapterPaneWidth;
            ChapterPane.Visibility = Visibility.Visible;
            ChapterSplitter.Visibility = Visibility.Visible;

            if (_chapterState == ChapterLoadState.NotLoaded)
                BeginChapterLoad();
            else
                ShowChapterState(_chapterState);
        }
        else
        {
            // Closing the pane keeps the loaded tree; only the layout footprint goes away.
            if (ChapterColumn.ActualWidth > 0)
                _chapterPaneWidth = new GridLength(ChapterColumn.ActualWidth);
            ChapterColumn.MinWidth = 0;
            ChapterColumn.Width = new GridLength(0);
            ChapterPane.Visibility = Visibility.Collapsed;
            ChapterSplitter.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Drops the previous document's chapter state; reloads only if the pane is open.</summary>
    private void ResetChapters()
    {
        _chapterCts.Cancel();
        _chapterCts.Dispose();
        _chapterCts = new CancellationTokenSource();

        _chapterRoots = null;
        _navigableChapters = new List<ChapterItem>();
        _selectedChapter = null;
        _chapterState = ChapterLoadState.NotLoaded;
        ChapterTree.ItemsSource = null;

        if (_chapterPaneVisible)
            BeginChapterLoad();
        else
            ShowChapterState(ChapterLoadState.NotLoaded);
    }

    private async void BeginChapterLoad()
    {
        if (_doc is null)
        {
            _chapterState = ChapterLoadState.Empty;
            ShowChapterState(_chapterState);
            return;
        }

        _chapterCts.Cancel();
        _chapterCts.Dispose();
        _chapterCts = new CancellationTokenSource();
        var ct = _chapterCts.Token;
        var doc = _doc;

        _chapterState = ChapterLoadState.Loading;
        ShowChapterState(_chapterState);

        try
        {
            // Resolve localization on the UI thread — Task.Run pool threads do not
            // inherit CurrentUICulture (and --lang= only sets the startup thread).
            var untitled = Strings.Get("UntitledChapter");
            // PdfPig parsing runs off the UI thread so the PDF keeps rendering immediately.
            var roots = await Task.Run(() => doc.GetChapters(ct, untitled), ct);

            // Apply only if this document is still the active one.
            if (ct.IsCancellationRequested || !ReferenceEquals(doc, _doc))
                return;

            _chapterRoots = roots;
            _navigableChapters = ChapterItem.FlattenNavigable(roots);
            ChapterTree.ItemsSource = roots;
            _chapterState = roots.Count == 0 ? ChapterLoadState.Empty : ChapterLoadState.Loaded;
            ShowChapterState(_chapterState);
            SyncChapterSelection();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer document/load; that load owns the pane UI.
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            if (!ReferenceEquals(doc, _doc))
                return;
            _chapterState = ChapterLoadState.Failed;
            ShowChapterState(_chapterState);
        }
    }

    private void ShowChapterState(ChapterLoadState state)
    {
        ChapterTree.Visibility = state == ChapterLoadState.Loaded ? Visibility.Visible : Visibility.Collapsed;
        ChapterLoadingText.Visibility = state == ChapterLoadState.Loading ? Visibility.Visible : Visibility.Collapsed;
        ChapterEmptyText.Visibility = state == ChapterLoadState.Empty ? Visibility.Visible : Visibility.Collapsed;
        ChapterFailedText.Visibility = state == ChapterLoadState.Failed ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Page index used for "current chapter" highlighting. In facing mode this is the
    /// right-hand page of the visible spread (or the cover alone), so a bookmark on either
    /// page of the spread can become active.
    /// </summary>
    private int GetChapterSyncPage()
    {
        if (_mode != ViewMode.Facing || _doc is null)
            return _currentPage;

        int start = FacingGroupStart(_currentPage);
        if (start != 0 && start + 1 < _doc.PageCount)
            return start + 1;
        return start;
    }

    /// <summary>
    /// The active chapter is the deepest/later outline node with the greatest
    /// PageIndex &lt;= the sync page. Among same-page candidates, an existing selection
    /// on that page is kept so tree clicks don't flash away; otherwise Depth then
    /// SourceOrder win. Facing mode uses <see cref="GetChapterSyncPage"/>.
    /// </summary>
    private void SyncChapterSelection()
    {
        if (_doc is null || _navigableChapters.Count == 0) return;

        int syncPage = GetChapterSyncPage();

        // Binary search: last entry with PageIndex <= syncPage (list is sorted by
        // PageIndex, Depth, SourceOrder — "greatest page, deepest/later node").
        int lo = 0, hi = _navigableChapters.Count - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_navigableChapters[mid].PageIndex!.Value <= syncPage) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (best < 0)
        {
            // Pages before the first bookmark have no active chapter — clear any stale highlight.
            ClearChapterSelection();
            return;
        }

        int bestPage = _navigableChapters[best].PageIndex!.Value;

        // Keep the current selection when it still targets this page band — preserves
        // explicit tree clicks among same-page outline siblings.
        if (_selectedChapter?.PageIndex == bestPage)
        {
            if (!_selectedChapter.IsSelected)
                _selectedChapter.IsSelected = true;
            return;
        }

        var active = _navigableChapters[best];
        if (ReferenceEquals(active, _selectedChapter)) return;

        _suppressChapterNav = true;
        try
        {
            if (_selectedChapter is not null)
                _selectedChapter.IsSelected = false;
            _selectedChapter = active;
            active.IsSelected = true;

            for (var p = active.Parent; p is not null; p = p.Parent)
                p.IsExpanded = true;

            if (_chapterPaneVisible)
                BringChapterIntoView();
        }
        finally
        {
            _suppressChapterNav = false;
        }
    }

    private void ClearChapterSelection()
    {
        if (_selectedChapter is null) return;
        _suppressChapterNav = true;
        try
        {
            _selectedChapter.IsSelected = false;
            _selectedChapter = null;
        }
        finally
        {
            _suppressChapterNav = false;
        }
    }

    /// <summary>
    /// Scrolls the sidebar to the active chapter, once the navigation has settled.
    ///
    /// This used to run inline on every page change: a forced layout of the whole tree plus
    /// a walk over every outline node, repeated for each intermediate scroll position of a
    /// jump. On a book with a chapter per page that was most of a second of frozen UI per
    /// jump. Deferring coalesces a burst of navigation into one pass at the end.
    /// </summary>
    private void BringChapterIntoView()
    {
        if (_chapterScrollQueued || !ChapterTree.IsVisible) return;
        _chapterScrollQueued = true;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _chapterScrollQueued = false;

            var target = _selectedChapter;   // whatever navigation settled on
            if (target is null || !_chapterPaneVisible || !ChapterTree.IsVisible) return;

            // Keep suppression raised: realizing a container applies the TwoWay IsSelected
            // binding, which would otherwise re-enter SelectedItemChanged → GoToPage.
            _suppressChapterNav = true;
            try
            {
                if (FindChapterContainer(ChapterTree, target) is TreeViewItem tvi)
                    tvi.BringIntoView();
            }
            finally
            {
                _suppressChapterNav = false;
            }
        });
    }

    /// <summary>Best-effort container lookup; only realized (expanded) containers exist.</summary>
    private static DependencyObject? FindChapterContainer(ItemsControl parent, object target)
    {
        foreach (var child in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(child) is not TreeViewItem container)
                continue;
            if (ReferenceEquals(child, target))
                return container;
            if (container.IsExpanded && FindChapterContainer(container, target) is { } found)
                return found;
        }
        return null;
    }

    private void ChapterTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // Programmatic sync sets _suppressChapterNav so it never recursively navigates.
        if (_suppressChapterNav) return;
        if (e.NewValue is not ChapterItem item) return;

        // Containers realized *after* a programmatic sync — a collapsed parent expanding,
        // the pane becoming visible, or plain sidebar scrolling recycling containers —
        // re-apply the TwoWay IsSelected binding and re-raise this event with the
        // already-active chapter, outside any suppression window and repeatedly.
        // Navigating on those echoes yanks the reader back to the chapter's first page.
        // At this event an echo is indistinguishable from a click on the active chapter,
        // so that click is deliberately a no-op (the reader is already inside the
        // chapter, and the next scroll or page change re-syncs the selection anyway).
        if (ReferenceEquals(item, _selectedChapter)) { ChapterEchoCount++; return; }

        _suppressChapterNav = true;
        try
        {
            if (_selectedChapter is not null)
                _selectedChapter.IsSelected = false;
            _selectedChapter = item;
        }
        finally
        {
            _suppressChapterNav = false;
        }

        // Container, URI, external-file, and embedded-file nodes have no PageIndex:
        // selecting them only selects/expands — no URI or file is ever launched.
        // Skip chapter sync so same-page siblings (and facing left-page picks) keep the
        // node the user clicked instead of being overwritten by SourceOrder / right-page rules.
        if (item.PageIndex is int pageIndex)
            GoToPage(pageIndex, syncChapters: false);
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
            App.LogError(ex);
            Strings.ShowError(this, string.Format(Strings.Get("PrintPreviewFailedMessage"), ex.Message));
        }
    }

    // ---------- About ----------

    private void About_Click(object sender, RoutedEventArgs e) => ShowAbout();

    internal void ShowAbout() => new AboutWindow { Owner = this }.ShowDialog();

    // ---------- Fullscreen ----------

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    internal void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            // Remember pane visibility; fullscreen always hides the chapter pane.
            _preFsChapterVisible = _chapterPaneVisible;
            if (_chapterPaneVisible)
                SetChapterPaneVisible(false);

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

            if (_preFsChapterVisible)
                SetChapterPaneVisible(true);
        }
        if (_fitToView)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ApplyLayout(scrollToCurrent: false));
    }

    // ---------- Input ----------

    // Wired to PreviewKeyDown (tunneling), not KeyDown: ScrollViewer and the
    // mode RadioButtons have their own built-in bubble-phase handling for
    // arrow/paging keys (scrolling, group navigation) that would otherwise
    // swallow the keystroke before it ever reached a bubble-phase handler here.
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        // Let the page-number box keep the keys that mean typing/caret movement inside it,
        // so the window's global shortcuts don't fire while the user edits the page number.
        if (ReferenceEquals(e.OriginalSource, PageBox) && e.Key is Key.D1 or Key.D2 or Key.D3
            or Key.Left or Key.Right or Key.Home or Key.End)
            return;

        // Let the chapter tree keep its own keyboard navigation (expand/collapse, first/last,
        // paging through nodes) instead of turning those keys into page turns.
        if (ChapterTree.IsKeyboardFocusWithin && e.Key is Key.Left or Key.Right
            or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            return;

        // When the page viewport itself has focus and there's actually room to pan
        // a zoomed-in page, let ScrollViewer's own scrolling handle these keys
        // instead of always stealing them for page-turning. The >= 1 threshold matches
        // Scroller_PreviewMouseWheel: WPF fit-to-view layout can leave a sub-pixel
        // ScrollableWidth/Height that is not real room to scroll.
        if (ReferenceEquals(e.OriginalSource, Scroller))
        {
            bool wantsHorizontalScroll = e.Key is Key.Left or Key.Right && Scroller.ScrollableWidth >= 1;
            bool wantsVerticalScroll = e.Key is Key.PageUp or Key.PageDown or Key.Home or Key.End && Scroller.ScrollableHeight >= 1;
            if (wantsHorizontalScroll || wantsVerticalScroll)
                return;
        }

        switch (e.Key)
        {
            case Key.O when ctrl: ShowOpenDialog(); break;
            case Key.P when ctrl: ShowPrintPreview(); break;
            case Key.R when ctrl: RotateClockwise(); break;
            case Key.F1: ShowAbout(); break;
            case Key.F4: SetChapterPaneVisible(!_chapterPaneVisible); break;
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
        int top = TopVisiblePage();
        if (top >= 0 && top != _currentPage)
        {
            _currentPage = top;
            PageBox.Text = (top + 1).ToString();
            SyncChapterSelection();
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

    /// <summary>
    /// Which pages are actually on screen, taken from the laid-out containers rather than
    /// from scroll arithmetic — see <see cref="TopVisiblePage"/> for why the offset lies.
    /// </summary>
    private (int First, int Last) VisibleRange()
    {
        if (_mode != ViewMode.Continuous)
            return (0, _items.Count - 1);

        var panel = PagesPanel();
        if (panel is null)
            return (_currentPage, _currentPage);

        double viewportHeight = Scroller.ViewportHeight;
        int first = int.MaxValue, last = -1;

        foreach (UIElement child in panel.Children)
        {
            if (child is not FrameworkElement element || !element.IsVisible) continue;
            int index = PagesHost.ItemContainerGenerator.IndexFromContainer(child);
            if (index < 0) continue;

            double top = element.TransformToAncestor(Scroller).Transform(default).Y;
            if (top + element.ActualHeight <= 0 || top >= viewportHeight) continue;   // cached, not shown

            first = Math.Min(first, index);
            last = Math.Max(last, index);
        }

        return last < 0 ? (_currentPage, _currentPage) : (first, last);
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

        // Free bitmaps far outside the viewport. Only the previously retained window can
        // hold any, so the walk is proportional to that window — never to the document.
        int keepLo = Math.Max(0, first - KeepBuffer);
        int keepHi = Math.Min(_items.Count - 1, last + KeepBuffer);
        LastEvictionScanLength = Math.Max(0, _retainedHi - _retainedLo + 1);
        for (int i = _retainedLo; i <= _retainedHi; i++)
        {
            if (i >= keepLo && i <= keepHi) continue;
            _items[i].Image = null;
            _items[i].RenderedPixelWidth = 0;
        }
        // Renders below land inside lo..hi ⊆ keepLo..keepHi, so this stays the superset
        // of every index that can carry a bitmap after this pass.
        _retainedLo = keepLo;
        _retainedHi = keepHi;

        double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;

        // Render the visible pages first, then the buffer.
        var order = Enumerable.Range(lo, hi - lo + 1)
            .OrderBy(i => i < first || i > last ? 1 : 0)
            .ToList();

        foreach (int i in order)
        {
            // A failed render falls through to the next iteration, so re-check the
            // token here: a concurrent RebuildItems may have replaced _items with a
            // shorter list, and indexing it would throw on the UI thread.
            if (ct.IsCancellationRequested) return;
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
