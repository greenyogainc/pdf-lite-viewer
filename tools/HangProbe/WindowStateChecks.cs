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

    public static async Task<List<Check>> RunAsync(string stressPdf, Func<Task> settle)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"hangprobe-windows-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var checks = new List<Check>();
        var cursor = Mouse.OverrideCursor;
        try
        {
            var docs = new Docs(folder);
            checks.AddRange(await CheckKit.RunManyAsync("window: page box Enter", () => PageBoxAsync(docs)));
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
    private static async Task<IEnumerable<Check>> PageBoxAsync(Docs docs)
    {
        const string name = "window: the page box puts back a page after text that is not one";
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
        return new[] { CheckKit.Tally(name, 8, wrong) };
    }
}
