using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PdfLiteViewer;
using static HangProbe.InputChecks;

namespace HangProbe;

internal static partial class WindowStateChecks
{
    private static string State(MainWindow window) =>
        $"page {window.PageBox.Text}, zoom {window.ZoomText.Text}, rotation {window.Document?.Rotation}, " +
        $"mode {(window.ModeSingle.IsChecked == true ? "single" : window.ModeFacing.IsChecked == true ? "facing" : "continuous")}, " +
        $"{Application.Current.Windows.Count} window(s)";

    // ---------- Open ----------

    /// <summary>
    /// Ctrl+O and the Open button show the real file dialog, answered from another thread: Cancel
    /// opens nothing, and a typed path opens that document.
    /// </summary>
    private static async Task<Check> OpenDialogsAsync(Docs docs, string name)
    {
        var wrong = new List<string>();
        var window = await ShownWindowAsync();
        try
        {
            await OpenAndRenderAsync(window, docs.Plain7);
            var open = window.Document;
            var title = Strings.Get("OpenFileDialogTitle");

            using (var cancelled = Program.Dialogs.Expect(title, DialogAction.PressCancel))
            {
                bool handled = KeyWithCtrl(window, Key.O);
                if (!handled) wrong.Add("Ctrl+O was not handled");
                if (!cancelled.Satisfied) wrong.Add("Ctrl+O showed no file dialog");
            }
            if (!ReferenceEquals(window.Document, open)) wrong.Add("a cancelled Ctrl+O changed the document");

            using (var cancelled = Program.Dialogs.Expect(title, DialogAction.PressCancel))
            {
                Click(ButtonNamed(window, Strings.Get("Open")));
                if (!cancelled.Satisfied) wrong.Add("the Open button showed no file dialog");
            }
            if (!ReferenceEquals(window.Document, open)) wrong.Add("a cancelled Open button changed the document");

            var target = Docs.Fixture("no-outline.pdf");
            using (var accepted = Program.Dialogs.Expect(title, DialogAction.FillPath, path: target))
            {
                KeyWithCtrl(window, Key.O);
                if (!accepted.Satisfied) wrong.Add("Ctrl+O showed no file dialog to type a path into");
            }
            bool opened = await Poll.UntilAsync(() => window.Document is { } d && Path.GetFullPath(d.FilePath).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase), Patience);
            if (!opened) wrong.Add($"the path typed into the dialog did not open (document is '{window.Document?.FilePath}')");

            using (var accepted = Program.Dialogs.Expect(title, DialogAction.FillPath, path: docs.Plain4))
            {
                Click(ButtonNamed(window, Strings.Get("Open")));
                if (!accepted.Satisfied) wrong.Add("the Open button showed no file dialog to type a path into");
            }
            if (!await Poll.UntilAsync(() => window.Document?.PageCount == 4, Patience)) wrong.Add("the path typed after the Open button did not open");
        }
        finally
        {
            CloseQuietly(window);
        }
        return CheckKit.Tally(name, 6, wrong);
    }

    // ---------- modal windows ----------

    /// <summary>
    /// F1, Ctrl+P and their buttons block inside ShowDialog, so the window that opens is closed
    /// by a dispatcher action queued before the call. A print preview opened this way is closed
    /// before it can be used - nothing here presses Enter in it.
    /// </summary>
    private static async Task<Check> ModalWindowsAsync(Docs docs, string name)
    {
        var wrong = new List<string>();
        var window = await ShownWindowAsync();
        try
        {
            await OpenAndRenderAsync(window, docs.Plain4);

            T? Modal<T>(string label, Action open) where T : Window
            {
                T? seen = null;
                bool owned = false;
                window.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    seen = Application.Current.Windows.OfType<T>().FirstOrDefault(w => w.IsVisible);
                    owned = seen?.Owner == window;
                    seen?.Close();
                });
                open();
                if (seen is null) wrong.Add($"{label}: no {typeof(T).Name} appeared");
                else if (!owned) wrong.Add($"{label}: the {typeof(T).Name} was not owned by the main window");
                return seen;
            }

            Modal<AboutWindow>("F1", () => { if (!KeyOn(window, Key.F1)) wrong.Add("F1 was not handled"); });
            Modal<AboutWindow>("the About button", () => Click(ButtonNamed(window, Strings.Get("About"))));
            Modal<PrintPreviewWindow>("Ctrl+P", () => { if (!KeyWithCtrl(window, Key.P)) wrong.Add("Ctrl+P was not handled"); });
            Modal<PrintPreviewWindow>("the Print button", () => Click(ButtonNamed(window, Strings.Get("Print"))));

            if (Application.Current.Windows.OfType<AboutWindow>().Any(w => w.IsVisible) || Application.Current.Windows.OfType<PrintPreviewWindow>().Any(w => w.IsVisible))
                wrong.Add("a modal window outlived its call");
        }
        finally
        {
            CloseQuietly(window);
        }
        return CheckKit.Tally(name, 4, wrong);
    }

    /// <summary>
    /// A window that was never shown cannot own a dialog, so setting the preview's Owner throws.
    /// The window reports it instead of letting the exception escape the key handler.
    /// </summary>
    private static async Task<Check> PreviewFailureAsync(Docs docs, string name)
    {
        var wrong = new List<string>();
        var window = NewWindow(shown: false);
        try
        {
            await OpenAndRenderAsync(window, docs.Plain4);
            string reason;
            var stand = new Window();
            try { stand.Owner = window; reason = "(setting the owner worked)"; }
            catch (InvalidOperationException ex) { reason = ex.Message; }
            finally { stand.Close(); }

            var intro = Strings.Get("PrintPreviewFailedMessage").Split('{')[0].Trim();
            using var dialog = Program.Dialogs.Expect(Strings.Get("AppTitle"), DialogAction.PressOk, textContains: intro);
            Click(ButtonNamed(window, Strings.Get("Print")));
            if (!dialog.Satisfied) wrong.Add("no error dialog appeared");
            else if (!dialog.Text.Contains(reason)) wrong.Add($"the dialog does not carry the reason ('{reason}'): {dialog.Text}");
            // A window that was built but never closed would keep the app alive on its own.
            int orphans = Application.Current.Windows.OfType<PrintPreviewWindow>().Count();
            if (orphans != 0) wrong.Add($"{orphans} preview window(s) left registered with the app");
        }
        finally
        {
            CloseQuietly(window);
        }
        return CheckKit.Tally(name, 2, wrong);
    }

    // ---------- keys ----------

    /// <summary>
    /// The shortcuts that must NOT fire: a letter or digit without its modifier, the digits with
    /// Control, Escape outside fullscreen, and the arrow, paging and mode keys while the page box
    /// or the chapter tree is the one being typed into. Each leaves the window exactly as it was.
    /// </summary>
    private static async Task<Check> KeysThatMustNotFireAsync(Docs docs, string name)
    {
        var wrong = new List<string>();
        var window = await ShownWindowAsync();
        try
        {
            window.SetMode(ViewMode.Single);
            await OpenAndRenderAsync(window, docs.TwoParts);
            window.GoToPage(2);
            window.Activate();
            window.Scroller.Focus();
            await Pump(window);
            string baseline = State(window);

            void NotHandled(string label, bool handled)
            {
                if (handled) wrong.Add($"{label} was handled");
                if (State(window) != baseline) wrong.Add($"{label} changed the window: {State(window)} (was {baseline})");
            }

            NotHandled("Escape outside fullscreen", KeyOn(window, Key.Escape));
            foreach (var key in new[] { Key.O, Key.P, Key.R, Key.OemPlus, Key.Add, Key.OemMinus, Key.Subtract, Key.D0, Key.NumPad0 })
                NotHandled($"{key} without Control", KeyOn(window, key));
            foreach (var key in new[] { Key.D1, Key.D2, Key.D3 })
                NotHandled($"Ctrl+{key}", KeyWithCtrl(window, key));
            foreach (var key in new[] { Key.D1, Key.D2, Key.D3, Key.Left, Key.Right, Key.Home, Key.End })
                NotHandled($"{key} typed in the page box", KeyOn(window.PageBox, key));
            NotHandled("a letter typed in the page box", KeyOn(window.PageBox, Key.A));
            // Keys with no meaning here, including every value the key switch's jump tables skip.
            foreach (var key in new[] { Key.A, Key.Q, Key.Space, Key.Up, Key.Down, Key.Enter, Key.Tab, Key.F2, Key.ImeConvert, Key.ImeNonConvert, Key.ImeAccept, Key.ImeModeChange })
                NotHandled($"{key}", KeyOn(window, key));

            window.SetChapterPaneVisible(true);
            await RequirePaneAsync(window, "tree");
            window.Activate();
            window.ChapterTree.Focus();
            if (!await Poll.UntilAsync(() => window.ChapterTree.IsKeyboardFocusWithin, TimeSpan.FromSeconds(10)))
                wrong.Add("the chapter tree could not take keyboard focus");
            else
            {
                baseline = State(window);
                foreach (var key in new[] { Key.Left, Key.Right, Key.Home, Key.End, Key.PageUp, Key.PageDown })
                    NotHandled($"{key} in the chapter tree", KeyOn(window, key));
                NotHandled("a letter in the chapter tree", KeyOn(window, Key.A));
            }
            window.SetChapterPaneVisible(false);
            window.Scroller.Focus();
        }
        finally
        {
            CloseQuietly(window);
        }
        return CheckKit.Tally(name, 1 + 9 + 3 + 7 + 1 + 12 + 6 + 1, wrong);
    }

    /// <summary>
    /// With the viewport focused, the arrow, paging, Home and End keys scroll when there is room
    /// to scroll (a zoomed page) and turn pages when there is not.
    /// </summary>
    private static async Task<Check> ScrollerKeysAsync(Docs docs, string name)
    {
        var wrong = new List<string>();
        var window = await ShownWindowAsync();
        try
        {
            window.SetMode(ViewMode.Single);
            await OpenAndRenderAsync(window, docs.Plain7);
            window.Activate();
            window.Scroller.Focus();
            await Pump(window);
            window.UpdateLayout();
            if (window.Scroller.ScrollableWidth >= 1 || window.Scroller.ScrollableHeight >= 1)
                throw new InvalidOperationException("a page fitted to the view still has room to scroll");

            // No room: the keys turn pages.
            window.GoToPage(3);
            int Now() => Page(window);
            void Turns(string label, Key key, int expected)
            {
                bool handled = KeyOn(window.Scroller, key);
                if (!handled || Now() != expected) wrong.Add($"{label} with no room to scroll: handled={handled}, page {Now()}, expected {expected}");
            }
            Turns("Right", Key.Right, 5);
            Turns("PageDown", Key.PageDown, 6);
            Turns("PageUp", Key.PageUp, 5);
            Turns("Home", Key.Home, 1);
            Turns("End", Key.End, 7);

            // Room to scroll in both directions: the keys are left to the scroll viewer.
            window.GoToPage(3);
            window.SetZoom(4);
            await Pump(window);
            window.UpdateLayout();
            if (window.Scroller.ScrollableWidth < 1 || window.Scroller.ScrollableHeight < 1)
                throw new InvalidOperationException("a zoomed page has no room to scroll");
            int page = Now();
            foreach (var key in new[] { Key.Left, Key.Right, Key.PageUp, Key.PageDown, Key.Home, Key.End })
            {
                bool handled = KeyOn(window.Scroller, key);
                if (handled || Now() != page) wrong.Add($"{key} with room to scroll: handled={handled}, page {Now()}, expected {page}");
            }
        }
        finally
        {
            CloseQuietly(window);
        }
        return CheckKit.Tally(name, 5 + 6, wrong);
    }

    /// <summary>
    /// The wheel zooms with Control (both directions), turns pages in single mode when the page
    /// fits, and leaves everything else to the scroll viewer: a zoomed page with room to scroll,
    /// and continuous mode.
    /// </summary>
    private static async Task<Check> WheelMatrixAsync(Docs docs, string name)
    {
        var wrong = new List<string>();
        var window = await ShownWindowAsync();
        try
        {
            window.SetMode(ViewMode.Single);
            await OpenAndRenderAsync(window, docs.Plain7);
            window.GoToPage(3);
            window.UpdateLayout();

            SetCtrl(true);
            try
            {
                int zoom = ZoomPercent(window);
                if (!RaiseWheel(window.Scroller, 120) || ZoomPercent(window) <= zoom) wrong.Add($"Ctrl+wheel up did not zoom in ({zoom}% -> {window.ZoomText.Text})");
                zoom = ZoomPercent(window);
                if (!RaiseWheel(window.Scroller, -120) || ZoomPercent(window) >= zoom) wrong.Add($"Ctrl+wheel down did not zoom out ({zoom}% -> {window.ZoomText.Text})");
            }
            finally
            {
                SetCtrl(false);
            }

            window.SetMode(ViewMode.Single);
            KeyWithCtrl(window, Key.D0);
            await Pump(window);
            window.UpdateLayout();
            window.GoToPage(3);
            int page = Page(window);
            if (!RaiseWheel(window.Scroller, 120) || Page(window) != page - 1) wrong.Add($"wheel up on a page that fits: page {page} -> {Page(window)}");
            if (!RaiseWheel(window.Scroller, -120) || Page(window) != page) wrong.Add($"wheel down on a page that fits: page {page - 1} -> {Page(window)}");

            window.SetZoom(4);
            await Pump(window);
            window.UpdateLayout();
            page = Page(window);
            if (RaiseWheel(window.Scroller, -120) || Page(window) != page) wrong.Add($"wheel on a zoomed page was taken or turned the page (page {page} -> {Page(window)})");

            window.SetMode(ViewMode.Continuous);
            await Pump(window);
            if (RaiseWheel(window.Scroller, -120)) wrong.Add("the wheel was taken in continuous mode");
        }
        finally
        {
            SetCtrl(false);
            CloseQuietly(window);
        }
        return CheckKit.Tally(name, 6, wrong);
    }

    /// <summary>
    /// Facing mode pairs pages as [1], [2,3], [4,5]...: stepping forward from the cover, from a
    /// spread, and past the end; and back from the last spread, a middle one, the first spread
    /// and the cover.
    /// </summary>
    private static async Task<Check> FacingStepsAsync(Docs docs, string name)
    {
        var wrong = new List<string>();
        var window = await ShownWindowAsync();
        try
        {
            window.SetMode(ViewMode.Facing);
            await OpenAndRenderAsync(window, docs.Plain7);
            window.GoToPage(0);
            var steps = new (string Label, Key Key, int Page, int Slots)[]
            {
                ("forward from the cover", Key.Right, 2, 2),
                ("forward from a spread", Key.PageDown, 4, 2),
                ("forward again", Key.Right, 6, 2),
                ("forward into the end of the book", Key.Right, 7, 2),
                ("back from the last spread", Key.Left, 4, 2),
                ("back from a middle spread", Key.PageUp, 2, 2),
                ("back from the first spread", Key.Left, 1, 1),
                ("back from the cover", Key.Left, 1, 1),
            };
            foreach (var (label, key, page, slots) in steps)
            {
                KeyOn(window, key);
                if (Page(window) != page || window.Items.Count != slots)
                    wrong.Add($"{label}: page {Page(window)} with {window.Items.Count} slot(s), expected page {page} with {slots}");
            }
        }
        finally
        {
            CloseQuietly(window);
        }
        return CheckKit.Tally(name, 8, wrong);
    }

    /// <summary>Fullscreen with the chapter pane closed, and with a zoom that is not "fit".</summary>
    private static async Task<Check> FullscreenVariantsAsync(Docs docs, string name)
    {
        var wrong = new List<string>();
        var window = await ShownWindowAsync();
        try
        {
            await OpenAndRenderAsync(window, docs.Plain4);
            window.SetZoom(1.5);
            string zoom = window.ZoomText.Text;
            window.ToggleFullscreen();
            if (window.ToolbarHost.Visibility != Visibility.Collapsed) wrong.Add("fullscreen kept the toolbar");
            if (window.ChapterPane.Visibility != Visibility.Collapsed) wrong.Add("fullscreen opened the chapter pane");
            window.ToggleFullscreen();
            await Pump(window);
            if (window.ToolbarHost.Visibility != Visibility.Visible) wrong.Add("leaving fullscreen kept the toolbar hidden");
            if (window.ChapterPane.Visibility != Visibility.Collapsed) wrong.Add("leaving fullscreen opened a pane that was closed");
            if (window.ZoomText.Text != zoom) wrong.Add($"fullscreen changed a fixed zoom from {zoom} to {window.ZoomText.Text}");
        }
        finally
        {
            CloseQuietly(window);
        }
        return CheckKit.Tally(name, 5, wrong);
    }

    // ---------- drag and drop ----------

    /// <summary>
    /// Dragging text only, a list with no PDF, and a list whose PDF is not first: only a PDF in
    /// the list is accepted, and it is the PDF that opens, whatever its position or capitalisation.
    /// </summary>
    private static async Task<Check> DragVariantsAsync(Docs docs, string name)
    {
        var wrong = new List<string>();
        var window = await ShownWindowAsync();
        try
        {
            await OpenAndRenderAsync(window, docs.Plain7);
            var open = window.Document;
            var upper = Path.Combine(docs.Folder, "UPPER.PDF");
            File.Copy(docs.Plain4, upper, overwrite: true);

            var refused = new (string Label, IDataObject Data)[]
            {
                ("text only", new DataObject(DataFormats.Text, "just words")),
                ("a list with no PDF", new DataObject(DataFormats.FileDrop, new[] { "notes.txt", "scan.docx" })),
            };
            foreach (var (label, data) in refused)
            {
                var over = MakeDrag(data, DragDrop.DragOverEvent, window);
                window.RaiseEvent(over);
                if (over.Effects != DragDropEffects.None || !over.Handled) wrong.Add($"{label}: drag over gave {over.Effects}, handled={over.Handled}");
                window.RaiseEvent(MakeDrag(data, DragDrop.DropEvent, window));
            }
            await Task.Delay(400);
            if (!ReferenceEquals(window.Document, open)) wrong.Add("dropping something that is not a PDF changed the document");

            var mixed = new DataObject(DataFormats.FileDrop, new[] { "notes.txt", upper, docs.Plain7 });
            var overMixed = MakeDrag(mixed, DragDrop.DragOverEvent, window);
            window.RaiseEvent(overMixed);
            if (overMixed.Effects != DragDropEffects.Copy) wrong.Add($"a list with a PDF in the middle gave {overMixed.Effects} on drag over");
            window.RaiseEvent(MakeDrag(mixed, DragDrop.DropEvent, window));
            if (!await Poll.UntilAsync(() => window.Document?.PageCount == 4 && Path.GetFileName(window.Document.FilePath) == "UPPER.PDF", Patience))
                wrong.Add($"the first PDF in a mixed list did not open (document is '{window.Document?.FilePath}')");
        }
        finally
        {
            CloseQuietly(window);
        }
        return CheckKit.Tally(name, 5, wrong);
    }
}
