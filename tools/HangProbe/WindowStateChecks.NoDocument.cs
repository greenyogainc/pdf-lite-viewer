using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PdfLiteViewer;
using static HangProbe.InputChecks;

namespace HangProbe;

internal static partial class WindowStateChecks
{
    // ---------- no document ----------

    /// <summary>
    /// Everything a user can reach before the first document: all of it must be a no-op or the
    /// documented empty state. A window that was never shown also has no layout and no
    /// <c>IsLoaded</c>, which changes what the mode buttons and the chapter pane can do.
    /// </summary>
    private static async Task<Check> NoDocumentAsync(bool shown, Docs docs, string name)
    {
        var wrong = new List<string>();
        var window = shown ? await ShownWindowAsync() : NewWindow(shown: false);
        try
        {
            string box = window.PageBox.Text, zoom = window.ZoomText.Text, count = window.PageCountText.Text;
            int windowsBefore = Application.Current.Windows.Count;

            window.GoToPage(5);
            window.RotateClockwise();
            await window.UpdateRenderedPagesAsync();
            window.SetZoom(3);
            Click(ButtonNamed(window, Strings.Get("NextPageTooltip")));
            Click(ButtonNamed(window, Strings.Get("PrevPageTooltip")));
            Click(ButtonNamed(window, Strings.Get("ZoomInTooltip")));
            Click(ButtonNamed(window, Strings.Get("ZoomOutTooltip")));
            Click(ButtonNamed(window, Strings.Get("Rotate")));
            Click(ButtonNamed(window, Strings.Get("Print")));

            // Fullscreen with a zoom that is not "fit": the layout pass it queues is skipped.
            window.ToggleFullscreen();
            if (window.ToolbarHost.Visibility != Visibility.Collapsed) wrong.Add("fullscreen left the toolbar visible");
            window.ToggleFullscreen();
            if (window.ToolbarHost.Visibility != Visibility.Visible) wrong.Add("leaving fullscreen left the toolbar hidden");
            Click(ButtonNamed(window, Strings.Get("Fit")));
            window.ToggleFullscreen();
            window.ToggleFullscreen();

            foreach (var key in new[] { Key.Right, Key.Left, Key.PageDown, Key.PageUp, Key.Home, Key.End })
                if (!KeyOn(window, key)) wrong.Add($"{key} was not handled");
            foreach (var key in new[] { Key.R, Key.P, Key.OemPlus, Key.OemMinus, Key.D0 })
                if (!KeyWithCtrl(window, key)) wrong.Add($"Ctrl+{key} was not handled");
            if (!RaiseWheel(window.Scroller, -120)) wrong.Add("the wheel was not handled");

            if (window.Document is not null) wrong.Add("a document appeared");
            if (window.Items.Count != 0) wrong.Add($"{window.Items.Count} page slot(s) appeared");
            if (window.PageBox.Text != box) wrong.Add($"the page box changed from '{box}' to '{window.PageBox.Text}'");
            if (window.ZoomText.Text != zoom) wrong.Add($"the zoom label changed from '{zoom}' to '{window.ZoomText.Text}'");
            if (window.PageCountText.Text != count) wrong.Add($"the page count changed from '{count}' to '{window.PageCountText.Text}'");
            if (Application.Current.Windows.Count != windowsBefore) wrong.Add("a window was opened (the print preview?)");

            // The mode buttons: a loaded window takes the mode, one that was never shown ignores the click.
            window.SetMode(ViewMode.Continuous);
            bool continuous = window.ModeContinuous.IsChecked == true;
            if (!continuous) wrong.Add("the Scroll button did not check");

            // The chapter pane opens on the empty state, and closing it without a layout keeps the width it had.
            window.SetChapterPaneVisible(true);
            if (window.ChapterPane.Visibility != Visibility.Visible) wrong.Add("the chapter pane did not open");
            if (window.ChapterToggle.IsChecked != true) wrong.Add("the chapters toggle did not follow the pane");
            if (window.ChapterEmptyText.Visibility != Visibility.Visible || window.ChapterTree.Visibility == Visibility.Visible
                || window.ChapterLoadingText.Visibility == Visibility.Visible || window.ChapterFailedText.Visibility == Visibility.Visible)
                wrong.Add("the pane without a document is not showing only the empty message");
            window.SetChapterPaneVisible(false);
            if (window.ChapterPane.Visibility != Visibility.Collapsed || window.ChapterColumn.Width.Value != 0)
                wrong.Add($"closing the pane left it {window.ChapterPane.Visibility} at width {window.ChapterColumn.Width.Value}");
            window.SetChapterPaneVisible(true);
            double reopened = window.ChapterColumn.Width.Value;
            if (shown ? reopened <= 0 : reopened != 270)
                wrong.Add($"reopening the pane gave it width {reopened}{(shown ? "" : ", not the 270 it had before any layout")}");

            // Opening a document afterwards uses the state the commands above left behind.
            await OpenAndRenderAsync(window, docs.Plain4);
            int slots = window.Items.Count;
            int expected = shown ? 4 : 1;
            if (slots != expected)
                wrong.Add($"after the open there are {slots} slot(s); {(shown ? "a loaded window took the Scroll mode, so all 4 pages" : "a window that was never shown ignored the Scroll mode, so the facing cover alone")} was expected");
            if (!await Poll.UntilAsync(() => window.ChapterEmptyText.Visibility == Visibility.Visible, Patience))
                wrong.Add("the open pane never showed the empty message for a document without an outline");
            if (!shown)
            {
                // No layout yet: FitZoom falls back to its 100 DIP minimum viewport.
                int percent = ZoomPercent(window);
                if (percent < 5 || percent > 10) wrong.Add($"fit zoom without a viewport is {percent}%, expected the minimum-viewport figure (about 8%)");
            }
        }
        finally
        {
            CloseQuietly(window);
        }
        return CheckKit.Tally(name, 1, wrong, "every command left the empty window as it was");
    }

    // ---------- DPI ----------

    private static void RaiseOnDpiChanged(MainWindow window, DpiScale from, DpiScale to) =>
        typeof(MainWindow).GetMethod("OnDpiChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, new object[] { from, to });

    /// <summary>
    /// A window dragged to a monitor with another scale keeps its DIP size, so nothing resizes
    /// and nothing would re-render. OnDpiChanged re-runs the layout, and the render pass then
    /// targets the new density. The DPI is raised through WPF's own root-DPI setter and, when
    /// that does not reach the override, by calling the protected method.
    /// </summary>
    private static async Task<Check> DpiAsync(Docs docs, string name)
    {
        var wrong = new List<string>();
        string how = "VisualTreeHelper.SetRootDpi";

        var empty = await ShownWindowAsync();
        var emptyDpi = VisualTreeHelper.GetDpi(empty);
        try
        {
            empty.PageBox.Text = "stale";
            var doubled = new DpiScale(emptyDpi.DpiScaleX * 2, emptyDpi.DpiScaleY * 2);
            VisualTreeHelper.SetRootDpi(empty, doubled);
            RaiseOnDpiChanged(empty, emptyDpi, doubled);
            await Pump(empty);
            await Task.Delay(150);
            await Pump(empty);
            if (empty.PageBox.Text != "stale") wrong.Add($"with no document the layout ran and rewrote the page box to '{empty.PageBox.Text}'");
        }
        finally
        {
            VisualTreeHelper.SetRootDpi(empty, emptyDpi);
            CloseQuietly(empty);
        }

        var window = await ShownWindowAsync();
        var original = VisualTreeHelper.GetDpi(window);
        try
        {
            await OpenAndRenderAsync(window, docs.Plain4);
            if (!await Poll.UntilAsync(() => window.Items.Count > 0 && window.Items[0].Image is not null, Patience))
                throw new InvalidOperationException("the first page never rendered");
            int before = window.Items[0].RenderedPixelWidth;

            window.PageBox.Text = "stale";
            window.ZoomText.Text = "stale";
            var doubled = new DpiScale(original.DpiScaleX * 2, original.DpiScaleY * 2);
            VisualTreeHelper.SetRootDpi(window, doubled);
            bool ran = await Poll.UntilAsync(() => window.PageBox.Text != "stale", TimeSpan.FromSeconds(3));
            if (!ran)
            {
                how = "calling OnDpiChanged directly (SetRootDpi did not reach the override)";
                RaiseOnDpiChanged(window, original, doubled);
                ran = await Poll.UntilAsync(() => window.PageBox.Text != "stale", TimeSpan.FromSeconds(5));
            }
            if (!ran) wrong.Add("the layout did not re-run after the DPI change");
            else
            {
                if (window.PageBox.Text != "1") wrong.Add($"the page box reads '{window.PageBox.Text}' after the layout, expected '1'");
                if (!window.ZoomText.Text.EndsWith('%')) wrong.Add($"the zoom label reads '{window.ZoomText.Text}'");
            }

            if (how.StartsWith("VisualTreeHelper"))
            {
                bool retargeted = await Poll.UntilAsync(() => window.Items.Count > 0 && window.Items[0].RenderedPixelWidth > before, Patience);
                if (!retargeted) wrong.Add($"the page stayed rendered at {window.Items[0].RenderedPixelWidth}px instead of following the density (was {before}px)");
            }
        }
        finally
        {
            VisualTreeHelper.SetRootDpi(window, original);
            CloseQuietly(window);
        }
        return CheckKit.Tally(name, 2, wrong, $"raised through {how}");
    }

    // ---------- opening ----------

    /// <summary>The system's own wording for why a read fails, so a check compares against it rather than against English.</summary>
    private static string ReasonFor(string path)
    {
        try { File.ReadAllBytes(path); return "(the read succeeded)"; }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Opens a file that cannot be opened, answers the error dialog, and returns what it said.</summary>
    private static async Task<(bool Shown, string Text)> OpenFailingAsync(MainWindow window, string path)
    {
        using var dialog = Program.Dialogs.Expect(Strings.Get("AppTitle"), DialogAction.PressOk, textContains: Path.GetFileName(path));
        await window.OpenFileAsync(path);
        return (dialog.Satisfied, dialog.Text);
    }

    private static async Task<Check> MissingFileAsync(Docs docs, string name)
    {
        var wrong = new List<string>();
        var window = NewWindow(shown: false);
        try
        {
            string reason = ReasonFor(docs.Missing);
            var (shown, text) = await OpenFailingAsync(window, docs.Missing);
            if (!shown) wrong.Add("no error dialog appeared");
            else
            {
                if (!text.Contains(reason)) wrong.Add($"the dialog does not carry the system's reason ('{reason}'): {text}");
                if (text.Contains(Strings.Get("NoPagesMessage"))) wrong.Add("a missing file was reported as a document without pages");
            }
            if (window.Document is not null) wrong.Add("a document is open");
            if (window.EmptyHint.Visibility != Visibility.Visible) wrong.Add($"the empty hint is {window.EmptyHint.Visibility}, expected Visible with nothing open");
            if (window.LoadingHint.Visibility != Visibility.Collapsed) wrong.Add($"the loading hint is {window.LoadingHint.Visibility}");
            if (window.Title != Strings.Get("AppTitle")) wrong.Add($"the title became '{window.Title}'");
        }
        finally
        {
            CloseQuietly(window);
        }
        return CheckKit.Tally(name, 5, wrong);
    }

    private static async Task<Check> ZeroPagesAsync(Docs docs, string name)
    {
        var wrong = new List<string>();
        var window = NewWindow(shown: false);
        try
        {
            var (shown, text) = await OpenFailingAsync(window, docs.ZeroPages);
            if (!shown) wrong.Add("no error dialog appeared");
            else
            {
                if (!text.Contains(Strings.Get("NoPagesMessage"))) wrong.Add($"the dialog lacks the translated message: {text}");
                if (text.Contains("contains no pages")) wrong.Add("the English exception text leaked into the dialog");
            }
            if (window.Document is not null) wrong.Add("a document is open");
            if (window.EmptyHint.Visibility != Visibility.Visible) wrong.Add("the empty hint is not shown");
        }
        finally
        {
            CloseQuietly(window);
        }
        return CheckKit.Tally(name, 4, wrong);
    }

    private static async Task<Check> LockedFileAsync(Docs docs, string name)
    {
        var wrong = new List<string>();
        var locked = Path.Combine(docs.Folder, "locked.pdf");
        File.Copy(docs.Plain4, locked, overwrite: true);
        var window = NewWindow(shown: false);
        try
        {
            await OpenAndRenderAsync(window, docs.Plain7);
            var open = window.Document;
            string title = window.Title, count = window.PageCountText.Text;

            string reason;
            bool shown;
            string text;
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                reason = ReasonFor(locked);
                (shown, text) = await OpenFailingAsync(window, locked);
            }
            if (!shown) wrong.Add("no error dialog appeared");
            else if (!text.Contains(reason)) wrong.Add($"the dialog does not carry the system's reason ('{reason}'): {text}");
            if (!ReferenceEquals(window.Document, open)) wrong.Add("the open document was replaced or closed");
            if (window.Title != title || window.PageCountText.Text != count) wrong.Add($"title/page count changed to '{window.Title}' / '{window.PageCountText.Text}'");
            if (window.EmptyHint.Visibility != Visibility.Collapsed) wrong.Add($"the empty hint is {window.EmptyHint.Visibility} over an open document");
            if (window.LoadingHint.Visibility != Visibility.Collapsed) wrong.Add($"the loading hint is {window.LoadingHint.Visibility}");
        }
        finally
        {
            CloseQuietly(window);
        }
        return CheckKit.Tally(name, 5, wrong);
    }

    /// <summary>
    /// The first open fails and the second succeeds with no await between them, so by the time
    /// the failure is delivered a newer open owns the window: the failure is dropped without a
    /// dialog. Two successful opens, the first overtaken, end on the second.
    /// </summary>
    private static async Task<Check> OvertakenOpensAsync(Docs docs, string name)
    {
        var wrong = new List<string>();
        var window = NewWindow(shown: false);
        try
        {
            var failing = window.OpenFileAsync(docs.Missing);
            var good = window.OpenFileAsync(docs.Plain7);
            await Task.WhenAll(failing, good);
            await Pump(window);
            if (window.Document?.FilePath != docs.Plain7) wrong.Add($"after a failing and a good open the document is '{window.Document?.FilePath ?? "none"}'");
            if (!window.Title.Contains(Path.GetFileName(docs.Plain7))) wrong.Add($"the title is '{window.Title}'");
            if (window.EmptyHint.Visibility != Visibility.Collapsed || window.LoadingHint.Visibility != Visibility.Collapsed)
                wrong.Add($"hints after the good open: empty {window.EmptyHint.Visibility}, loading {window.LoadingHint.Visibility}");

            var first = window.OpenFileAsync(docs.Plain4);
            var second = window.OpenFileAsync(docs.Plain4);
            var third = window.OpenFileAsync(docs.TwoParts);
            await Task.WhenAll(first, second, third);
            await Pump(window);
            if (window.Document?.FilePath != docs.TwoParts || window.Document.PageCount != 6)
                wrong.Add($"after three quick opens the document is '{window.Document?.FilePath ?? "none"}'");
            if (window.PageCountText.Text != string.Format(Strings.Get("PageCountFormat"), 6)) wrong.Add($"page count text '{window.PageCountText.Text}'");
        }
        finally
        {
            CloseQuietly(window);
        }
        return CheckKit.Tally(name, 5, wrong);
    }

    /// <summary>
    /// Nothing in the apply step is expected to throw, so the last-resort catch has to be
    /// provoked: a one-shot hook on the title throws from inside the apply. The window must
    /// land on the empty state, say why, and open the next document normally.
    /// </summary>
    private static async Task<Check> LastResortCatchAsync(Docs docs, string name)
    {
        var wrong = new List<string>();
        var window = NewWindow(shown: false);
        try
        {
            await OpenAndRenderAsync(window, docs.Plain4);
            window.SetChapterPaneVisible(true);
            var marker = ProbeLog.NewMarker("probe-apply");
            var descriptor = DependencyPropertyDescriptor.FromProperty(Window.TitleProperty, typeof(Window));
            EventHandler? hook = null;
            hook = (_, _) =>
            {
                descriptor.RemoveValueChanged(window, hook!);
                throw new InvalidOperationException(marker);
            };
            descriptor.AddValueChanged(window, hook);

            using var dialog = Program.Dialogs.Expect(Strings.Get("AppTitle"), DialogAction.PressOk, textContains: marker);
            await window.OpenFileAsync(docs.Plain7);
            await Pump(window);

            if (!dialog.Satisfied) wrong.Add("no error dialog appeared");
            if (window.Document is not null) wrong.Add($"a document is still open ('{window.Document.FilePath}')");
            if (window.Items.Count != 0) wrong.Add($"{window.Items.Count} page slot(s) remain");
            if (window.Title != Strings.Get("AppTitle")) wrong.Add($"the title reads '{window.Title}'");
            if (window.PageCountText.Text != string.Format(Strings.Get("PageCountFormat"), 0)) wrong.Add($"the page count reads '{window.PageCountText.Text}'");
            if (window.EmptyHint.Visibility != Visibility.Visible) wrong.Add("the empty hint is not shown");
            if (window.LoadingHint.Visibility != Visibility.Collapsed) wrong.Add("the loading hint is still shown");
            if (window.ChapterEmptyText.Visibility != Visibility.Visible) wrong.Add("the open chapter pane does not show its empty message");
            if (!ProbeLog.Contains(marker)) wrong.Add("the failure was not logged");

            await OpenAndRenderAsync(window, docs.Plain7);
            if (window.Document?.PageCount != 7 || !window.Title.Contains(Path.GetFileName(docs.Plain7)))
                wrong.Add($"reopening afterwards gave '{window.Title}' with {window.Document?.PageCount.ToString() ?? "no"} pages");
        }
        finally
        {
            CloseQuietly(window);
        }
        return CheckKit.Tally(name, 10, wrong);
    }
}
