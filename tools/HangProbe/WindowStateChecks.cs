using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PdfLiteViewer;
using static HangProbe.InputChecks;
using static HangProbe.PdfFixtures;

namespace HangProbe;

/// <summary>
/// Drives <see cref="MainWindow"/> through the states the stress document never reaches: no
/// document at all, a DPI change, opens that fail or are overtaken, outlines of every shape
/// (including loads that fail, are superseded or arrive after the document was replaced), the
/// render races, and the whole keyboard, wheel and drag matrix.
///
/// Every check works on a second <c>new MainWindow()</c> that it closes again, so the probe's
/// own window and the checks after this group see exactly what they saw before. Failures are
/// made deterministic at the seams the window already has: files that cannot be opened, a
/// document whose bytes are cleared, the private render lock and chapter gate held by hand,
/// and a UI thread that sleeps (never pumps) while background work finishes, so the order in
/// which continuations run is the order the check chose.
/// </summary>
internal static partial class WindowStateChecks
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>The documents the checks open, generated once per run.</summary>
    private sealed class Docs(string folder)
    {
        public string Folder { get; } = folder;
        public string Plain4 { get; } = Make(folder, "plain4.pdf", p => WriteDocument(p, 4));
        public string Plain7 { get; } = Make(folder, "plain7.pdf", p => WriteDocument(p, 7));

        /// <summary>Six pages: Part I (1) holds A (2) and B (3); Part II (4) holds C (5) and D (6).</summary>
        public string TwoParts { get; } = Make(folder, "two-parts.pdf", p => WriteDocument(p, 6, outline: new[]
        {
            new OutlineItem("Part I", Dest(1), new OutlineItem("Chapter A", Dest(2)), new OutlineItem("Chapter B", Dest(3))),
            new OutlineItem("Part II", Dest(4), new OutlineItem("Chapter C", Dest(5)), new OutlineItem("Chapter D", Dest(6))),
        }));

        public string ZeroPages { get; } = Make(folder, "zero.pdf", WriteZeroPagePdf);
        public string Strip { get; } = Make(folder, "strip.pdf", p => WriteSinglePagePdf(p, 200, 5000));
        public string Tiny { get; } = Make(folder, "tiny.pdf", p => WriteSinglePagePdf(p, 20, 20));
        public string Missing { get; } = Path.Combine(folder, $"missing-{Guid.NewGuid():N}.pdf");

        public static string Fixture(string name) => RepoFixture(name);

        private static string Make(string folder, string name, Action<string> write)
        {
            var path = Path.Combine(folder, name);
            write(path);
            return path;
        }
    }

    public static async Task<List<Check>> RunAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"hangprobe-windows-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var checks = new List<Check>();
        var cursor = Mouse.OverrideCursor;
        try
        {
            var docs = new Docs(folder);
            async Task Step(string name, Func<string, Task<Check>> body) => checks.Add(await CheckKit.RunAsync(name, () => body(name)));

            await Step("window: the page box puts back a page after text that is not one", name => PageBoxAsync(docs, name));
            await Step("window: with no document, never shown, every command is a no-op", name => NoDocumentAsync(false, docs, name));
            await Step("window: with no document, shown, every command is a no-op", name => NoDocumentAsync(true, docs, name));
            await Step("window: a DPI change re-runs the layout only when there is a document", name => DpiAsync(docs, name));
            await Step("window: a file that is not there is reported with the system's reason", name => MissingFileAsync(docs, name));
            await Step("window: a document without pages is reported in the user's language", name => ZeroPagesAsync(docs, name));
            await Step("window: a file another program holds open is reported, and the open document stays", name => LockedFileAsync(docs, name));
            await Step("window: an open that was overtaken leaves the newer one in charge", name => OvertakenOpensAsync(docs, name));
            await Step("window: a failure while applying a document falls back to the empty state", name => LastResortCatchAsync(docs, name));
            await Step("window: the chapter pane shows empty, loaded, nothing navigable and failed, and keeps its state when closed", name => PaneStatesAsync(docs, name));
            await Step("window: a chapter load that finishes after the document was replaced is dropped", name => StaleLoadAsync(docs, name, failing: false));
            await Step("window: a chapter load that fails after the document was replaced is dropped", name => StaleLoadAsync(docs, name, failing: true));
            await Step("window: a chapter load in flight when the document changes ends quietly", name => SupersededLoadAsync(docs, name));
            await Step("window: with no bookmark before page 2, page 1 has no active chapter in any mode", name => NamedDestinationsAsync(docs, name));
            await Step("window: nested chapters expand their parents and the sidebar finds the active row", name => NestedChaptersAsync(docs, name));
            await Step("window: a bookmark that goes nowhere selects without navigating", name => NonNavigableChaptersAsync(docs, name));
            await Step("window: a strip page stays inside the pixel budget and a speck is not rendered", name => PixelBudgetsAsync(docs, name));
            await Step("window: a page PDFium cannot render is logged and the viewer lives", name => RenderFailureAsync(docs, name));
            await Step("window: a cancelled render never leaves a bitmap of the old orientation", name => RenderCancellationAsync(docs, name));
            await Step("window: Ctrl+O and the Open button show the file dialog; cancel opens nothing, a path opens it", name => OpenDialogsAsync(docs, name));
            await Step("window: F1, Ctrl+P and their buttons open the About and print preview windows modally", name => ModalWindowsAsync(docs, name));
            await Step("window: a print preview that cannot be shown is reported, not thrown", name => PreviewFailureAsync(docs, name));
            await Step("window: shortcuts without their modifier, and keys typed into the page box or tree, do nothing", name => KeysThatMustNotFireAsync(docs, name));
            await Step("window: with the viewport focused, keys scroll when there is room and turn pages when there is not", name => ScrollerKeysAsync(docs, name));
            await Step("window: the wheel zooms with Control, turns fitted pages and otherwise scrolls", name => WheelMatrixAsync(docs, name));
            await Step("window: facing mode steps by spread from the cover to the end and back", name => FacingStepsAsync(docs, name));
            await Step("window: fullscreen with the pane closed and a fixed zoom changes neither", name => FullscreenVariantsAsync(docs, name));
            await Step("window: dropping text or non-PDF files does nothing, and the first PDF in a mixed list opens", name => DragVariantsAsync(docs, name));
        }
        catch (Exception ex)
        {
            checks.Add(new Check("window state checks ran", false, ex.ToString()));
        }
        finally
        {
            SetCtrl(false);
            Mouse.OverrideCursor = cursor;
            _detached?.Dispose();
            _detached = null;
            try { Directory.Delete(folder, recursive: true); } catch { /* temp files: the next run's folder is new anyway */ }
        }
        return checks;
    }

    // ---------- helpers ----------

    private static MainWindow NewWindow(bool shown)
    {
        var window = new MainWindow { Width = 1000, Height = 760, ShowInTaskbar = false };
        if (shown)
        {
            window.Left = 40;
            window.Top = 40;
            window.Show();
        }
        return window;
    }

    /// <summary>A window that is shown and has finished loading, so IsLoaded-gated handlers act.</summary>
    private static async Task<MainWindow> ShownWindowAsync()
    {
        var window = NewWindow(shown: true);
        if (!await Poll.UntilAsync(() => window.IsLoaded, Patience))
            throw new InvalidOperationException("a shown window never finished loading");
        await Pump(window);
        return window;
    }

    private static void CloseQuietly(Window? window)
    {
        try { window?.Close(); } catch { /* teardown noise must not mask the result */ }
    }

    /// <summary>Runs the dispatcher out to Background priority, i.e. past every continuation queued so far.</summary>
    private static Task Pump(Window window) =>
        window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task;

    private static async Task OpenAndRenderAsync(MainWindow window, string path)
    {
        await window.OpenFileAsync(path);
        await Pump(window);
    }

    /// <summary>The PDFium bytes of a document, which the checks clear to make every later read of it fail.</summary>
    private static byte[] BytesOf(PdfDoc doc) =>
        (byte[])typeof(PdfDoc).GetField("_bytes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(doc)!;

    private static void ClearBytes(PdfDoc doc) => Array.Clear(BytesOf(doc));

    private static HwndSource? _detached;

    /// <summary>
    /// A presentation source that belongs to no window. KeyEventArgs insists on one, and a window
    /// that was never shown has none of its own; routing needs only the visual tree.
    /// </summary>
    private static PresentationSource Detached =>
        _detached ??= new HwndSource(new HwndSourceParameters("hangprobe-keys") { ParentWindow = new IntPtr(-3) });

    /// <summary>Raises a key event on a window or one of its elements, shown or not. Returns whether it was handled.</summary>
    private static bool KeyOn(IInputElement target, Key key, RoutedEvent? routed = null)
    {
        var presentation = target is Visual visual ? PresentationSource.FromVisual(visual) : null;
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, presentation ?? Detached, Environment.TickCount, key)
        {
            RoutedEvent = routed ?? Keyboard.PreviewKeyDownEvent,
        };
        target.RaiseEvent(args);
        return args.Handled;
    }

    private static bool KeyWithCtrl(IInputElement target, Key key)
    {
        SetCtrl(true);
        try { return KeyOn(target, key); }
        finally { SetCtrl(false); }
    }

    // ---------- the page box ----------

    /// <summary>
    /// Text that cannot be a page must not stay in the box next to the page count, the way
    /// GoToPage already refreshes a number that clamps. With no document there is no page to
    /// put back, so the key is left alone.
    /// </summary>
    private static async Task<Check> PageBoxAsync(Docs docs, string name)
    {
        var wrong = new List<string>();
        var window = await ShownWindowAsync();
        try
        {
            window.SetMode(ViewMode.Single);
            await OpenAndRenderAsync(window, docs.Plain4);
            window.GoToPage(1);

            void Case(string label, string typed, Key key, bool handled, string expectedBox, int expectedPage)
            {
                window.PageBox.Text = typed;
                bool got = KeyOn(window.PageBox, key, Keyboard.KeyDownEvent);
                int page = int.TryParse(window.PageBox.Text, out int p) ? p : -1;
                if (got != handled) wrong.Add($"{label}: handled={got}, expected {handled}");
                if (window.PageBox.Text != expectedBox) wrong.Add($"{label}: the box reads '{window.PageBox.Text}', expected '{expectedBox}'");
                if (int.TryParse(expectedBox, out _) && page != expectedPage) wrong.Add($"{label}: page {page}, expected {expectedPage}");
            }

            Case("letters", "abc", Key.Enter, true, "2", 2);
            Case("empty", "", Key.Enter, true, "2", 2);
            Case("a sign alone", "-", Key.Enter, true, "2", 2);
            Case("a number", "3", Key.Enter, true, "3", 3);
            Case("a number past the end", "99", Key.Enter, true, "4", 4);
            Case("zero", "0", Key.Enter, true, "1", 1);
            Case("another key leaves the text alone", "abc", Key.A, false, "abc", 0);

            window.PageBox.Text = "2";
            window.GoToPage(1);
        }
        finally
        {
            CloseQuietly(window);
        }

        // No document: harmless, and nothing invented.
        var empty = NewWindow(shown: false);
        try
        {
            empty.PageBox.Text = "abc";
            bool handled = KeyOn(empty.PageBox, Key.Enter, Keyboard.KeyDownEvent);
            if (handled) wrong.Add("with no document the key was handled");
            if (empty.PageBox.Text != "abc") wrong.Add($"with no document the box was rewritten to '{empty.PageBox.Text}'");
        }
        finally
        {
            CloseQuietly(empty);
        }
        return CheckKit.Tally(name, 8, wrong);
    }
}
