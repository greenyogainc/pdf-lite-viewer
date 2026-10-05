using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PdfLiteViewer;

namespace HangProbe;

/// <summary>
/// Drives keyboard, pointer and drag input on the live window. The handlers stay private;
/// the probe raises the same routed events the window already listens for.
/// Ctrl shortcuts read <see cref="Keyboard.Modifiers"/>, which follows the real keyboard,
/// so those cases hold the Control key down for the duration of the event.
/// </summary>
internal static class InputChecks
{
    // Left Ctrl, not VK_CONTROL. WPF asks GetKeyState for LeftCtrl and RightCtrl
    // specifically; the generic VK_CONTROL bit does not light those up.
    private const byte VkLeftCtrl = 0xA2;
    private const uint KeyeventfKeyup = 0x0002;

    public static async Task<List<Check>> RunAsync(MainWindow window, string stressPdf, Func<Task> settle)
    {
        var checks = new List<Check>();
        var rotation = window.Document?.Rotation ?? PDFtoImage.PdfRotation.Rotate0;
        try
        {
            checks.Add(await FullscreenRestoresAsync(window, settle));
            checks.AddRange(await KeysAsync(window, settle));
            checks.AddRange(await WheelAsync(window, settle));
            checks.AddRange(await ClickSurfaceAsync(window, settle));
            checks.AddRange(await DragAsync(window, stressPdf, settle));
        }
        catch (Exception ex)
        {
            checks.Add(new Check("input checks ran", false, ex.ToString()));
        }
        finally
        {
            SetCtrl(false);
            if (window.ToolbarHost.Visibility != Visibility.Visible)
                window.ToggleFullscreen();
            RestoreRotation(window, rotation);
        }
        return checks;
    }

    private static async Task<Check> FullscreenRestoresAsync(MainWindow window, Func<Task> settle)
    {
        const string name = "fullscreen hides the toolbar and restores the chapter pane";
        window.SetChapterPaneVisible(true);
        await settle();
        window.ToggleFullscreen();
        bool hidden = window.ToolbarHost.Visibility == Visibility.Collapsed
                      && window.ChapterPane.Visibility == Visibility.Collapsed;
        window.ToggleFullscreen();
        await settle();
        bool restored = window.ToolbarHost.Visibility == Visibility.Visible
                        && window.ChapterPane.Visibility == Visibility.Visible
                        && window.ZoomText.Text.EndsWith('%');
        return new Check(name, hidden && restored,
            $"hidden={hidden}, restored={restored}, zoom '{window.ZoomText.Text}'");
    }

    private static async Task<List<Check>> KeysAsync(MainWindow window, Func<Task> settle)
    {
        var checks = new List<Check>();
        window.SetMode(ViewMode.Single);
        window.GoToPage(2);
        await settle();

        // Page box keeps caret keys. Raised on the box so OriginalSource is the box.
        int page = Page(window);
        bool pageBoxKept = !RaisePreviewKey(window.PageBox, Key.Left) && Page(window) == page;
        checks.Add(new Check("keys: page box keeps Left", pageBoxKept,
            $"page {page} -> {Page(window)}"));

        // Chapter tree keeps its own navigation while it has focus.
        window.SetChapterPaneVisible(true);
        await settle();
        window.ChapterTree.Focus();
        await settle();
        bool treeKept = window.ChapterTree.IsKeyboardFocusWithin
                        && !RaisePreviewKey(window, Key.Left)
                        && Page(window) == page;
        checks.Add(new Check("keys: chapter tree keeps Left", treeKept,
            $"focus={window.ChapterTree.IsKeyboardFocusWithin}, page {Page(window)}"));
        window.Scroller.Focus();

        // Zoomed single page: Left/Right pan instead of turning the page.
        window.SetZoom(4);
        await settle();
        window.UpdateLayout();
        bool canPan = window.Scroller.ScrollableWidth >= 1;
        bool panned = canPan && !RaisePreviewKey(window.Scroller, Key.Left) && Page(window) == page;
        checks.Add(new Check("keys: zoomed page pans instead of turning", panned,
            $"scrollable {window.Scroller.ScrollableWidth:F0}px, page {Page(window)}"));

        // Back to fit, where Left turns the page.
        SetCtrl(true);
        try { RaisePreviewKey(window, Key.D0); }
        finally { SetCtrl(false); }
        await settle();
        int beforeStep = Page(window);
        bool stepped = RaisePreviewKey(window, Key.Right) && Page(window) == beforeStep + 1;
        bool paged = RaisePreviewKey(window, Key.PageDown) && Page(window) == beforeStep + 2;
        bool home = RaisePreviewKey(window, Key.Home) && Page(window) == 1;
        bool end = RaisePreviewKey(window, Key.End) && Page(window) == window.Document!.PageCount;
        RaisePreviewKey(window, Key.PageUp);
        checks.Add(new Check("keys: paging, Home and End move", stepped && paged && home && end,
            $"step={stepped}, pageDown={paged}, home={home}, end={end}, now {Page(window)}"));

        window.GoToPage(0);
        bool single = RaisePreviewKey(window, Key.D1) && window.ModeSingle.IsChecked == true;
        bool facing = RaisePreviewKey(window, Key.D2) && window.ModeFacing.IsChecked == true;
        bool continuous = RaisePreviewKey(window, Key.D3) && window.ModeContinuous.IsChecked == true;
        checks.Add(new Check("keys: 1/2/3 change view mode", single && facing && continuous,
            $"single={single}, facing={facing}, continuous={continuous}"));

        window.SetMode(ViewMode.Single);
        bool paneWasOpen = window.ChapterPane.Visibility == Visibility.Visible;
        RaisePreviewKey(window, Key.F4);
        bool paneFlipped = window.ChapterPane.Visibility == Visibility.Visible != paneWasOpen;
        RaisePreviewKey(window, Key.F4);
        bool paneRestored = window.ChapterPane.Visibility == Visibility.Visible == paneWasOpen;
        checks.Add(new Check("keys: F4 toggles the chapter pane", paneFlipped && paneRestored,
            $"flipped={paneFlipped}, restored={paneRestored}"));

        window.SetChapterPaneVisible(true);
        bool entered = RaisePreviewKey(window, Key.F11) && window.ToolbarHost.Visibility == Visibility.Collapsed;
        bool exited = RaisePreviewKey(window, Key.Escape) && window.ToolbarHost.Visibility == Visibility.Visible
                      && window.ChapterPane.Visibility == Visibility.Visible;
        checks.Add(new Check("keys: F11 and Escape toggle fullscreen", entered && exited,
            $"entered={entered}, exited={exited}"));

        var rotation = window.Document!.Rotation;
        int zoom = ZoomPercent(window);
        ModifierKeys mods;
        int afterPlus;
        SetCtrl(true);
        try
        {
            mods = Keyboard.Modifiers;
            RaisePreviewKey(window, Key.R);
            RaisePreviewKey(window, Key.OemPlus);
            afterPlus = ZoomPercent(window);
            RaisePreviewKey(window, Key.Add);
            RaisePreviewKey(window, Key.OemMinus);
            RaisePreviewKey(window, Key.Subtract);
            RaisePreviewKey(window, Key.NumPad0);
        }
        finally { SetCtrl(false); }
        await settle();
        bool rotated = window.Document.Rotation != rotation;
        bool zoomed = afterPlus > zoom;
        bool refit = window.ZoomText.Text.EndsWith('%');
        checks.Add(new Check("keys: Ctrl rotates, zooms and refits", rotated && zoomed && refit,
            $"rotated={rotated}, zoom {zoom}% -> {window.ZoomText.Text}, modifiers={mods}"));
        return checks;
    }

    private static async Task<List<Check>> WheelAsync(MainWindow window, Func<Task> settle)
    {
        var checks = new List<Check>();
        window.SetMode(ViewMode.Continuous);   // leaving single so the next SetMode fires
        window.SetMode(ViewMode.Single);        // Mode_Checked turns fit back on
        window.GoToPage(1);
        await settle();
        window.UpdateLayout();

        int page = Page(window);
        bool turned = window.Scroller.ScrollableHeight < 1
                      && RaiseWheel(window.Scroller, -120)
                      && Page(window) == page + 1;
        checks.Add(new Check("wheel: turns the page when the page already fits", turned,
            $"scrollable height {window.Scroller.ScrollableHeight:F0}px, page {page} -> {Page(window)}"));

        int zoom = ZoomPercent(window);
        ModifierKeys mods;
        SetCtrl(true);
        try
        {
            mods = Keyboard.Modifiers;
            RaiseWheel(window.Scroller, 120);
        }
        finally { SetCtrl(false); }
        await settle();
        bool zoomed = ZoomPercent(window) > zoom;
        checks.Add(new Check("wheel: Ctrl+wheel zooms", zoomed,
            $"zoom {zoom}% -> {window.ZoomText.Text}, modifiers={mods}"));
        return checks;
    }

    /// <summary>
    /// The click handlers are separate from the key switch, which inlines the same work.
    /// Raising Click on the real buttons is what covers them.
    /// </summary>
    private static async Task<List<Check>> ClickSurfaceAsync(MainWindow window, Func<Task> settle)
    {
        var checks = new List<Check>();
        window.SetMode(ViewMode.Single);
        window.GoToPage(2);
        await settle();

        int zoom = ZoomPercent(window);
        Click(ButtonNamed(window, Strings.Get("ZoomInTooltip")));
        await settle();
        int zoomed = ZoomPercent(window);
        bool zoomedIn = zoomed > zoom;
        Click(ButtonNamed(window, Strings.Get("ZoomOutTooltip")));
        await settle();
        bool zoomedOut = ZoomPercent(window) < zoomed;
        Click(ButtonNamed(window, Strings.Get("Fit")));
        await settle();
        bool fit = window.ZoomText.Text.EndsWith('%');
        checks.Add(new Check("clicks: zoom and fit", zoomedIn && zoomedOut && fit,
            $"zoom {zoom}% -> in {zoomedIn}, out {zoomedOut}, now '{window.ZoomText.Text}'"));

        var rotation = window.Document!.Rotation;
        Click(ButtonNamed(window, Strings.Get("Rotate")));
        for (int i = 0; i < 3; i++) window.RotateClockwise();
        bool rotatedAll = window.Document.Rotation == rotation;
        checks.Add(new Check("clicks: rotate walks every quarter turn", rotatedAll,
            $"back to {window.Document.Rotation}"));

        window.SetMode(ViewMode.Facing);
        window.GoToPage(0);
        await settle();
        Click(ButtonNamed(window, Strings.Get("NextPageTooltip")));
        await settle();
        bool stepped = Page(window) == 2;
        Click(ButtonNamed(window, Strings.Get("PrevPageTooltip")));
        await settle();
        bool steppedBack = Page(window) == 1;
        checks.Add(new Check("clicks: facing prev/next step by a spread", stepped && steppedBack,
            $"next={stepped}, prev={steppedBack}, page {Page(window)}"));

        window.PageBox.Text = "6";
        bool entered = RaiseKey(window.PageBox, Key.Enter, Keyboard.KeyDownEvent) && Page(window) == 6;
        checks.Add(new Check("clicks: page box Enter jumps", entered, $"page box '{window.PageBox.Text}'"));

        window.SetChapterPaneVisible(true);
        await settle();
        Click(ButtonNamed(window, Strings.Get("CloseChaptersTooltip")));
        bool closed = window.ChapterPane.Visibility == Visibility.Collapsed;
        checks.Add(new Check("clicks: chapter close hides the pane", closed,
            $"pane={window.ChapterPane.Visibility}"));

        Click(ButtonNamed(window, Strings.Get("FullscreenTooltip")));
        bool enteredFs = window.ToolbarHost.Visibility == Visibility.Collapsed;
        window.ToggleFullscreen();
        checks.Add(new Check("clicks: fullscreen button hides the toolbar", enteredFs,
            $"toolbar={window.ToolbarHost.Visibility}"));
        return checks;
    }

    private static Button ButtonNamed(DependencyObject root, string label)
    {
        var button = AboutChecks.FindChildren<Button>(root).FirstOrDefault(b =>
            AutomationProperties.GetName(b) == label ||
            b.ToolTip as string == label ||
            b.Content as string == label);
        return button ?? throw new InvalidOperationException($"button '{label}' was not in the window");
    }

    private static void Click(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static async Task<List<Check>> DragAsync(MainWindow window, string stressPdf, Func<Task> settle)
    {
        var checks = new List<Check>();
        var notes = MakeDrag(new[] { "notes.txt" }, DragDrop.DragOverEvent, window);
        window.RaiseEvent(notes);
        var pdfOver = MakeDrag(new[] { stressPdf }, DragDrop.DragOverEvent, window);
        window.RaiseEvent(pdfOver);
        checks.Add(new Check("drag: pdf copies, anything else is refused",
            notes.Effects == DragDropEffects.None && pdfOver.Effects == DragDropEffects.Copy && notes.Handled && pdfOver.Handled,
            $"notes={notes.Effects}, pdf={pdfOver.Effects}"));

        var fixture = FindFixture() ?? stressPdf;
        int before = window.Document?.PageCount ?? 0;
        var drop = MakeDrag(new[] { fixture }, DragDrop.DropEvent, window);
        window.RaiseEvent(drop);
        await settle();
        await settle();
        bool opened = drop.Handled || window.Document is not null;
        // Drop starts OpenFileAsync and discards the task. Wait until the page count moves
        // when the dropped file is a different document; the stress file itself is a no-op count.
        if (!string.Equals(fixture, stressPdf, StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i < 10 && window.Document?.PageCount == before; i++)
                await settle();
            opened = window.Document?.PageCount > 0 && window.Document.PageCount != before
                     && window.Title.Contains(Path.GetFileName(fixture), StringComparison.OrdinalIgnoreCase);
        }
        checks.Add(new Check("drag: dropping a pdf opens it", opened,
            $"'{Path.GetFileName(fixture)}' -> {window.Document?.PageCount} page(s), title '{window.Title}'"));

        if (!string.Equals(window.Document?.FilePath, stressPdf, StringComparison.OrdinalIgnoreCase))
        {
            await window.OpenFileAsync(stressPdf);
            await settle();
        }
        return checks;
    }

    private static string? FindFixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "fixtures", "no-outline.pdf");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// <see cref="DragEventArgs"/>'s constructor is internal. The probe builds the same
    /// args the drop target would see from a real file drag.
    /// </summary>
    private static DragEventArgs MakeDrag(string[] files, RoutedEvent routed, DependencyObject target)
    {
        var ctor = typeof(DragEventArgs).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            new[] { typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point) },
            modifiers: null) ?? throw new MissingMethodException("DragEventArgs constructor");
        var args = (DragEventArgs)ctor.Invoke(new object[]
        {
            new DataObject(DataFormats.FileDrop, files),
            DragDropKeyStates.None,
            DragDropEffects.All,
            target,
            new Point(20, 20),
        });
        args.RoutedEvent = routed;
        return args;
    }

    private static bool RaisePreviewKey(IInputElement source, Key key) =>
        RaiseKey(source, key, Keyboard.PreviewKeyDownEvent);

    private static bool RaiseKey(IInputElement source, Key key, RoutedEvent routed)
    {
        if (source is not Visual visual)
            throw new InvalidOperationException("key source is not a visual");
        var presentation = PresentationSource.FromVisual(visual)
            ?? throw new InvalidOperationException("key source has no presentation source");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, presentation, Environment.TickCount, key)
        {
            RoutedEvent = routed,
        };
        source.RaiseEvent(args);
        return args.Handled;
    }

    private static bool RaiseWheel(IInputElement source, int delta)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
        };
        source.RaiseEvent(args);
        return args.Handled;
    }

    private static void SetCtrl(bool down)
    {
        // keybd_event posts to the foreground thread. GetKeyState (what
        // Keyboard.Modifiers reads) updates only once that thread pulls the
        // message. Activate first so the message lands on this UI thread, then
        // pump it. Scan code 0x1D is Left Ctrl; the generic VK_CONTROL bit is
        // not what WPF checks.
        Application.Current?.MainWindow?.Activate();
        keybd_event(VkLeftCtrl, 0x1D, down ? 0u : KeyeventfKeyup, UIntPtr.Zero);
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            static () => { },
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private static void RestoreRotation(MainWindow window, PDFtoImage.PdfRotation rotation)
    {
        if (window.Document is null) return;
        for (int i = 0; i < 4 && window.Document.Rotation != rotation; i++)
            window.RotateClockwise();
    }

    private static int Page(MainWindow window) =>
        int.TryParse(window.PageBox.Text, out int page) ? page : -1;

    private static int ZoomPercent(MainWindow window) =>
        int.TryParse(window.ZoomText.Text.TrimEnd('%'), out int percent) ? percent : -1;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
