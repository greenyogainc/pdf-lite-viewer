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

    private readonly ItemsPanelTemplate _verticalPanel;
    private readonly ItemsPanelTemplate _horizontalPanel;

    public MainWindow()
    {
        InitializeComponent();
        Strings.ApplyFlowDirection(this);
        PagesHost.ItemsSource = _items;

        _verticalPanel = MakePanelTemplate(Orientation.Vertical);
        _horizontalPanel = MakePanelTemplate(Orientation.Horizontal);
        PagesHost.ItemsPanel = _verticalPanel;

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _renderTimer.Tick += (_, _) => { _renderTimer.Stop(); _ = UpdateRenderedPagesAsync(); };

        PageCountText.Text = string.Format(Strings.Get("PageCountFormat"), 0);

        Loaded += MainWindow_Loaded;
        // Refit when the page viewport changes — window resize, chapter pane
        // open/close, or splitter drag. Window.SizeChanged alone misses the
        // in-window column redistributions from the chapter sidebar.
        Scroller.SizeChanged += (_, _) =>
        {
            if (!_fitToView) return;
            // The layout pass for this resize hasn't completed yet, so
            // ViewportWidth/Height can still be stale here. Defer until after
            // layout settles so FitZoom() sees the real size.
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
        var dlg = new OpenFileDialog { Filter = Strings.Get("OpenFileDialogFilter"), Title = Strings.Get("OpenFileDialogTitle") };
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
            Strings.ShowError(this, string.Format(Strings.Get("OpenFileErrorMessage"), path, ex.Message));
            return;
        }

        Title = string.Format(Strings.Get("MainWindowTitleFormat"),
            System.IO.Path.GetFileName(path), Strings.Get("AppTitle"));
        EmptyHint.Visibility = Visibility.Collapsed;
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

    private void ApplyLayout(bool scrollToCurrent)
    {
        if (_doc is null) return;

        if (_fitToView)
            _zoom = FitZoom();

        foreach (var it in _items)
        {
            var (w, h) = _doc.GetDisplaySize(it.PageIndex);
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

    private void Rotate_Click(object sender, RoutedEventArgs e) => RotateClockwise();

    private void RotateClockwise()
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

        foreach (var it in _items)
        {
            it.Image = null;
            it.RenderedPixelWidth = 0;
        }

        ApplyLayout(scrollToCurrent: false);
    }

    // ---------- Navigation ----------

    private void GoToPage(int page, bool scroll = true, bool syncChapters = true)
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
            if (page == _currentPage && _items.Count > 0)
            {
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

    private void SetChapterPaneVisible(bool visible)
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

            // Keep suppress raised through UpdateLayout so TwoWay IsSelected
            // realizing a container cannot re-enter SelectedItemChanged → GoToPage.
            if (_chapterPaneVisible)
                BringChapterIntoView(active);
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

    private void BringChapterIntoView(ChapterItem item)
    {
        if (!ChapterTree.IsVisible) return;
        ChapterTree.UpdateLayout();
        if (FindChapterContainer(ChapterTree, item) is TreeViewItem tvi)
            tvi.BringIntoView();
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

        if (!ReferenceEquals(item, _selectedChapter))
        {
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
            System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PdfLiteViewer.log"),
                $"[dbg] preview failed: {ex}\n");
            Strings.ShowError(this, string.Format(Strings.Get("PrintPreviewFailedMessage"), ex.Message));
        }
    }

    // ---------- Fullscreen ----------

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
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
                    SyncChapterSelection();
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
