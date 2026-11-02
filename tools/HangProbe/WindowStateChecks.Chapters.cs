using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using PdfLiteViewer;
using static HangProbe.InputChecks;

namespace HangProbe;

internal static partial class WindowStateChecks
{
    // ---------- chapter pane helpers ----------

    private static string PaneState(MainWindow window) =>
        window.ChapterTree.Visibility == Visibility.Visible ? "tree" :
        window.ChapterLoadingText.Visibility == Visibility.Visible ? "loading" :
        window.ChapterEmptyText.Visibility == Visibility.Visible ? "empty" :
        window.ChapterFailedText.Visibility == Visibility.Visible ? "failed" : "none";

    /// <summary>Waits for the pane to show one state; a pane that never gets there fails the whole check, since everything after it would be meaningless.</summary>
    private static async Task RequirePaneAsync(MainWindow window, string state)
    {
        if (!await Poll.UntilAsync(() => PaneState(window) == state, Patience))
            throw new InvalidOperationException($"the chapter pane never showed '{state}' (it shows '{PaneState(window)}')");
    }

    private static List<ChapterItem> Roots(MainWindow window) =>
        window.ChapterTree.ItemsSource as List<ChapterItem> ?? new List<ChapterItem>();

    private static IEnumerable<ChapterItem> Walk(IEnumerable<ChapterItem> items) =>
        items.SelectMany(i => new[] { i }.Concat(Walk(i.Children)));

    private static ChapterItem Chapter(MainWindow window, string title) =>
        Walk(Roots(window)).First(c => c.Title == title);

    private sealed class Unwatch(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>Calls <paramref name="changed"/> every time <paramref name="property"/> of <paramref name="target"/> changes.</summary>
    private static IDisposable Watch(DependencyObject target, DependencyProperty property, Action changed)
    {
        var descriptor = DependencyPropertyDescriptor.FromProperty(property, target.GetType());
        EventHandler handler = (_, _) => changed();
        descriptor.AddValueChanged(target, handler);
        return new Unwatch(() => descriptor.RemoveValueChanged(target, handler));
    }

    /// <summary>
    /// Blocks the UI thread - without pumping it - while the thread pool finishes
    /// <paramref name="items"/> work items started since <paramref name="mark"/>. Whatever those
    /// items post back to the dispatcher is then queued and cannot run until the caller yields,
    /// which is what lets a check decide the order continuations run in.
    ///
    /// The pool does not say which item is whose, and unrelated work (a timer of a window an
    /// earlier check closed) also counts, so the counter alone could be satisfied early. The
    /// work these checks start is a few milliseconds long; sleeping a fixed 300 ms first makes
    /// an early count harmless. Checks that depend on the order still verify it afterwards.
    /// </summary>
    private static void WaitForPool(long mark, int items)
    {
        Thread.Sleep(300);
        var clock = Stopwatch.StartNew();
        while (ThreadPool.CompletedWorkItemCount - mark < items || ThreadPool.PendingWorkItemCount > 0)
        {
            if (clock.Elapsed > Patience) throw new TimeoutException("the thread pool never finished the background work");
            Thread.Sleep(10);
        }
        Thread.Sleep(50);
    }

    private static async Task<MainWindow> WindowWithPaneAsync(string path, ViewMode mode = ViewMode.Single)
    {
        var window = await ShownWindowAsync();
        try
        {
            window.SetMode(mode);
            await OpenAndRenderAsync(window, path);
            window.SetChapterPaneVisible(true);
            await RequirePaneAsync(window, "tree");
            await Pump(window);
            return window;
        }
        catch
        {
            CloseQuietly(window);
            throw;
        }
    }

    // ---------- pane states ----------

    /// <summary>
    /// The pane before any document, with no outline, with an outline nothing in it navigates,
    /// with a dangling /Outlines reference, and after a load that fails (the document's bytes
    /// are cleared, so the outline parser cannot read it). Closing and reopening the pane
    /// shows the state it already has instead of loading again.
    /// </summary>
    private static async Task<Check> PaneStatesAsync(Docs docs, string name)
    {
        var wrong = new List<string>();
        var window = await ShownWindowAsync();
        try
        {
            window.SetMode(ViewMode.Single);
            window.SetChapterPaneVisible(true);
            if (PaneState(window) != "empty") wrong.Add($"before any document the pane shows '{PaneState(window)}'");

            await OpenAndRenderAsync(window, Docs.Fixture("no-outline.pdf"));
            await RequirePaneAsync(window, "empty");

            await OpenAndRenderAsync(window, Docs.Fixture("container-only.pdf"));
            await RequirePaneAsync(window, "tree");
            var section = Roots(window).SingleOrDefault();
            window.GoToPage(0);
            await Pump(window);
            if (section is null) wrong.Add($"container-only shows {Roots(window).Count} roots, expected 1");
            else if (Walk(Roots(window)).Any(c => c.IsSelected)) wrong.Add("a chapter nobody can navigate to was marked active");

            await OpenAndRenderAsync(window, Docs.Fixture("malformed-outline.pdf"));
            await RequirePaneAsync(window, "empty");

            // A load that fails: open with the pane closed, ruin the document, then open the pane.
            window.SetChapterPaneVisible(false);
            await OpenAndRenderAsync(window, Docs.Fixture("nested-internal.pdf"));
            var marker = ProbeLog.Length();
            ClearBytes(window.Document!);
            window.SetChapterPaneVisible(true);
            await RequirePaneAsync(window, "failed");
            if (ProbeLog.TextSince(marker).Length == 0) wrong.Add("the failed load was not logged");

            window.SetChapterPaneVisible(false);
            window.SetChapterPaneVisible(true);
            if (PaneState(window) != "failed") wrong.Add($"reopening the pane after a failed load shows '{PaneState(window)}' instead of the same state");

            // A load that worked also stays: closing the pane keeps the tree, reopening shows it at once.
            await OpenAndRenderAsync(window, Docs.Fixture("nested-internal.pdf"));
            await RequirePaneAsync(window, "tree");
            window.SetChapterPaneVisible(false);
            window.SetChapterPaneVisible(true);
            if (PaneState(window) != "tree") wrong.Add($"reopening a loaded pane shows '{PaneState(window)}'");

            // The toolbar toggle drives the pane as well: unchecking it closes the pane, checking it opens it.
            window.ChapterToggle.IsChecked = false;
            if (window.ChapterPane.Visibility != Visibility.Collapsed) wrong.Add("unchecking the chapters toggle did not close the pane");
            window.ChapterToggle.IsChecked = true;
            if (window.ChapterPane.Visibility != Visibility.Visible || PaneState(window) != "tree") wrong.Add("checking the chapters toggle did not open the pane on its tree");
        }
        finally
        {
            CloseQuietly(window);
        }
        return CheckKit.Tally(name, 8, wrong);
    }

    // ---------- loads that outlive their document ----------

    /// <summary>
    /// A's chapter load has finished on its worker, but its continuation is still queued behind
    /// the continuation that swaps in document B. The UI thread only sleeps until both workers
    /// are done and then yields, so B's continuation runs first and A's arrives stale.
    /// With <paramref name="failing"/> A's load fails (its bytes are cleared) instead of
    /// succeeding. Either way the pane must end up showing B's outline, and A's answer must
    /// never touch it, not even for a moment.
    /// </summary>
    private static async Task<Check> StaleLoadAsync(Docs docs, string name, bool failing)
    {
        // The order is arranged with sleeps on a thread pool this check does not own, so the
        // arrangement is verified and, if the pool was slow enough to spoil it, tried again.
        const int attempts = 3;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            var (arranged, wrong) = await StaleLoadAttemptAsync(docs, failing);
            if (arranged)
                return CheckKit.Tally(name, 5, wrong, (failing ? "the old load failed after the swap" : "the old load succeeded after the swap") +
                                                      (attempt > 1 ? $" (attempt {attempt})" : ""));
        }
        return new Check(name, false, $"the old load's answer never arrived after the document swap in {attempts} attempts, so the stale path was not reached");
    }

    private static async Task<(bool Arranged, List<string> Wrong)> StaleLoadAttemptAsync(Docs docs, bool failing)
    {
        var wrong = new List<string>();
        var window = await ShownWindowAsync();
        try
        {
            window.SetMode(ViewMode.Single);
            await OpenAndRenderAsync(window, docs.TwoParts);
            var a = window.Document!;
            if (!await Poll.UntilAsync(() => window.Items.Count > 0 && window.Items[0].Image is not null, Patience))
                throw new InvalidOperationException("the first page never rendered");
            await Pump(window);
            // A failing load must be A's first: a cached outline would be handed back without parsing.
            List<ChapterItem>? aRoots = null;
            if (failing) ClearBytes(a);
            else aRoots = a.GetChapters(CancellationToken.None, "untitled");

            // What happened, in order: the swap to B (its title is applied), and anything that
            // came from A's load - its outline reaching the tree, or its failure reaching the pane.
            var events = new List<string>();
            using var swapWatch = Watch(window, Window.TitleProperty, () => events.Add("swap"));
            using var treeWatch = Watch(window.ChapterTree, ItemsControl.ItemsSourceProperty,
                () => { if (aRoots is not null && ReferenceEquals(window.ChapterTree.ItemsSource, aRoots)) events.Add("A's outline"); });
            using var failedWatch = Watch(window.ChapterFailedText, UIElement.VisibilityProperty,
                () => { if (window.ChapterFailedText.Visibility == Visibility.Visible) events.Add("A's failure"); });

            long mark = ThreadPool.CompletedWorkItemCount;
            var opening = window.OpenFileAsync(Docs.Fixture("nested-internal.pdf"));
            WaitForPool(mark, 1);                      // B's continuation is queued

            mark = ThreadPool.CompletedWorkItemCount;
            window.SetChapterPaneVisible(true);        // A is still the open document: its load starts
            WaitForPool(mark, 1);                      // ...and its continuation is queued behind B's

            await opening;                             // yield: B's continuation, then A's stale one
            await RequirePaneAsync(window, "tree");
            await Task.Delay(150);
            await Pump(window);

            int swap = events.IndexOf("swap");
            int fromA = events.FindIndex(e => e.StartsWith("A's"));
            if (swap < 0 || fromA >= 0 && fromA < swap)
                return (false, wrong);                 // A's answer got in before the swap: not the order this check is about

            var roots = Roots(window);
            if (roots.Count != 1 || roots[0].Title != "Part I") wrong.Add($"the pane shows {roots.Count} root(s) ({string.Join(", ", roots.Select(r => r.Title))}), expected B's single 'Part I'");
            if (window.Document?.PageCount != 3) wrong.Add($"the open document has {window.Document?.PageCount} pages, expected B's 3");
            if (events.Any(e => e == "A's outline")) wrong.Add("the old document's outline was put in the tree after the swap");
            if (events.Any(e => e == "A's failure")) wrong.Add("the pane showed 'failed' for the old document's failure after the swap");
            if (PaneState(window) == "failed") wrong.Add("the pane ended on 'failed'");
            return (true, wrong);
        }
        finally
        {
            CloseQuietly(window);
        }
    }

    /// <summary>
    /// A's load is in flight, held on A's chapter gate, when B replaces A. The replacement
    /// cancels A's token; when the gate is released A's load ends in a cancellation that the
    /// window treats as "a newer load owns the pane" - no failure message, B's outline shown.
    /// </summary>
    private static async Task<Check> SupersededLoadAsync(Docs docs, string name)
    {
        var wrong = new List<string>();
        var window = await ShownWindowAsync();
        try
        {
            window.SetMode(ViewMode.Single);
            await OpenAndRenderAsync(window, docs.TwoParts);
            var gate = typeof(PdfDoc).GetField("_chaptersGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window.Document!)!;

            bool failedShown = false;
            using var failedWatch = Watch(window.ChapterFailedText, UIElement.VisibilityProperty,
                () => failedShown |= window.ChapterFailedText.Visibility == Visibility.Visible);

            Monitor.Enter(gate);
            bool held = true;
            try
            {
                window.SetChapterPaneVisible(true);
                await Pump(window);
                if (PaneState(window) != "loading") wrong.Add($"with the gate held the pane shows '{PaneState(window)}', expected 'loading'");

                await OpenAndRenderAsync(window, Docs.Fixture("nested-internal.pdf"));
                Monitor.Exit(gate);
                held = false;
            }
            finally
            {
                if (held) Monitor.Exit(gate);
            }

            await RequirePaneAsync(window, "tree");
            await Task.Delay(300);
            await Pump(window);
            var roots = Roots(window);
            if (roots.Count != 1 || roots[0].Title != "Part I") wrong.Add($"the pane shows {roots.Count} root(s), expected B's single 'Part I'");
            if (failedShown) wrong.Add("the superseded load reported a failure");
        }
        finally
        {
            CloseQuietly(window);
        }
        return CheckKit.Tally(name, 3, wrong);
    }

    // ---------- the active chapter ----------

    /// <summary>
    /// "Named Jump" starts on page 2, so page 1 has no active chapter: in every mode the
    /// highlight is cleared (and clearing nothing is fine) and comes back on page 2. In
    /// facing mode page 2 is an end spread with no right-hand page.
    /// </summary>
    private static async Task<Check> NamedDestinationsAsync(Docs docs, string name)
    {
        var wrong = new List<string>();
        foreach (var mode in new[] { ViewMode.Single, ViewMode.Facing, ViewMode.Continuous })
        {
            var window = await WindowWithPaneAsync(Docs.Fixture("named-destinations.pdf"), mode);
            try
            {
                var jump = Chapter(window, "Named Jump");
                window.GoToPage(0);
                await Pump(window);
                if (jump.IsSelected) wrong.Add($"{mode}: page 1 has an active chapter");

                window.GoToPage(1);
                if (!await Poll.UntilAsync(() => jump.IsSelected, Patience)) wrong.Add($"{mode}: page 2 did not activate its chapter");
                if (mode == ViewMode.Facing && window.Items.Count != 1) wrong.Add($"facing: the end spread has {window.Items.Count} slots, expected the single last page");

                window.GoToPage(0);
                if (!await Poll.UntilAsync(() => !jump.IsSelected, Patience)) wrong.Add($"{mode}: going back to page 1 left the chapter active");
            }
            finally
            {
                CloseQuietly(window);
            }
        }
        return CheckKit.Tally(name, 3, wrong);
    }

    /// <summary>
    /// Going to a nested chapter expands its parents, and the sidebar then looks for the
    /// chapter's row: found under an expanded parent, missed under a collapsed one, searched
    /// through an expanded parent that does not hold it, skipped when the pane closed, was
    /// hidden or the selection went away before the lookup ran, and not queued twice.
    /// </summary>
    private static async Task<Check> NestedChaptersAsync(Docs docs, string name)
    {
        var wrong = new List<string>();

        var window = await WindowWithPaneAsync(docs.TwoParts);
        try
        {
            var partI = Chapter(window, "Part I");
            var partII = Chapter(window, "Part II");
            var chapterA = Chapter(window, "Chapter A");
            var chapterC = Chapter(window, "Chapter C");
            if (partI.IsExpanded || partII.IsExpanded) wrong.Add("a part started expanded");

            // Found: Chapter A sits under Part I, which the jump expands.
            window.GoToPage(1);
            if (!await Poll.UntilAsync(() => chapterA.IsSelected && partI.IsExpanded, Patience)) wrong.Add("going to Chapter A did not select it and expand Part I");
            await Pump(window);
            await Task.Delay(150);

            // Searched through an expanded parent that does not hold it, then found in the next one.
            window.GoToPage(4);
            if (!await Poll.UntilAsync(() => chapterC.IsSelected && partII.IsExpanded, Patience)) wrong.Add("going to Chapter C did not select it and expand Part II");
            if (!partI.IsExpanded) wrong.Add("Part I collapsed on its own");
            await Pump(window);
            await Task.Delay(150);

            // Missed: Part II is collapsed again before the queued lookup runs, while Part I is
            // expanded and holds nothing of the kind. Two chapter changes in a row queue one
            // lookup, not two.
            window.GoToPage(5);
            window.GoToPage(4);
            if (!chapterC.IsSelected) wrong.Add("Chapter C was not selected");
            partII.IsExpanded = false;
            await Pump(window);
            await Task.Delay(150);

            // Skipped: the pane is closed before the lookup runs.
            window.GoToPage(1);
            window.SetChapterPaneVisible(false);
            await Pump(window);
            await Task.Delay(150);

            // Skipped: the window disappears before the lookup runs.
            window.SetChapterPaneVisible(true);
            window.GoToPage(2);
            window.Hide();
            await Pump(window);
            await Task.Delay(150);
            window.Show();

            // With the pane closed nothing is queued at all.
            window.SetChapterPaneVisible(false);
            window.GoToPage(3);
            if (!Chapter(window, "Part II").IsSelected) wrong.Add("a jump with the pane closed did not move the active chapter");
        }
        finally
        {
            CloseQuietly(window);
        }

        // The lookup is skipped when the selection went away before it ran.
        var gone = await WindowWithPaneAsync(Docs.Fixture("named-destinations.pdf"));
        try
        {
            gone.GoToPage(1);
            gone.GoToPage(0);
            await Pump(gone);
            await Task.Delay(150);
            if (Chapter(gone, "Named Jump").IsSelected) wrong.Add("the chapter stayed active after going back to page 1");
        }
        finally
        {
            CloseQuietly(gone);
        }

        // A window that was never shown is not visible, so its tree is never looked at.
        var unshown = NewWindow(shown: false);
        try
        {
            unshown.SetMode(ViewMode.Single);
            await OpenAndRenderAsync(unshown, Docs.Fixture("nested-internal.pdf"));
            unshown.SetChapterPaneVisible(true);
            await RequirePaneAsync(unshown, "tree");
            // It ignored the mode buttons too, so it is still facing: page 2 sits in the spread whose
            // right-hand page, 3, is the one that counts for the active chapter.
            unshown.GoToPage(1);
            await Pump(unshown);
            if (!Chapter(unshown, "Chapter B").IsSelected) wrong.Add("the unshown window did not select Chapter B for the spread of pages 2 and 3");
        }
        finally
        {
            CloseQuietly(unshown);
        }
        return CheckKit.Tally(name, 8, wrong);
    }

    /// <summary>
    /// In facing mode the chapter that counts for a spread is the one on its right-hand page, but
    /// a reader can pick the chapter on the left-hand page. That pick stays selected: the spread
    /// is already on screen, so it is neither rebuilt nor re-synced over the reader's choice.
    /// </summary>
    private static async Task<Check> FacingPickAsync(Docs docs, string name)
    {
        var wrong = new List<string>();
        var window = await WindowWithPaneAsync(docs.TwoParts, ViewMode.Facing);
        try
        {
            var chapterA = Chapter(window, "Chapter A");
            var chapterB = Chapter(window, "Chapter B");
            window.GoToPage(2);
            if (!await Poll.UntilAsync(() => chapterB.IsSelected, Patience)) wrong.Add("the spread of pages 2 and 3 did not select the chapter on page 3");
            var spread = window.Items;

            chapterA.IsSelected = true;
            await Poll.UntilAsync(() => !chapterB.IsSelected, TimeSpan.FromSeconds(5));
            await Pump(window);
            await Task.Delay(150);
            if (!chapterA.IsSelected || chapterB.IsSelected) wrong.Add($"the picked chapter was overwritten (A selected={chapterA.IsSelected}, B selected={chapterB.IsSelected})");
            if (!ReferenceEquals(spread, window.Items)) wrong.Add("picking a chapter on the visible spread rebuilt it");
            if (window.PageBox.Text != "2") wrong.Add($"the page box reads '{window.PageBox.Text}', expected the picked page 2");
        }
        finally
        {
            CloseQuietly(window);
        }
        return CheckKit.Tally(name, 3, wrong);
    }

    /// <summary>
    /// A bookmark with no page (a web link here) can be selected and expanded but never
    /// navigates. Clearing the current chapter's selection raises a change with no new item,
    /// which also navigates nowhere, and the next sync puts the selection back.
    /// </summary>
    private static async Task<Check> NonNavigableChaptersAsync(Docs docs, string name)
    {
        var wrong = new List<string>();
        var window = await WindowWithPaneAsync(Docs.Fixture("uri-external.pdf"));
        try
        {
            var internalChapter = Chapter(window, "Internal");
            var website = Chapter(window, "Website");
            if (!await Poll.UntilAsync(() => internalChapter.IsSelected, Patience)) wrong.Add("the first page did not select its chapter");
            string page = window.PageBox.Text;

            internalChapter.IsSelected = false;
            await Pump(window);
            if (window.PageBox.Text != page) wrong.Add($"clearing the selection moved the page to '{window.PageBox.Text}'");

            int echoes = window.ChapterEchoCount;
            window.GoToPage(0);
            if (!await Poll.UntilAsync(() => internalChapter.IsSelected, Patience)) wrong.Add("the sync did not select the active chapter again");
            await Pump(window);
            if (window.ChapterEchoCount <= echoes) wrong.Add("putting the selection back raised no echo for the guard to absorb");

            website.IsSelected = true;
            if (!await Poll.UntilAsync(() => !internalChapter.IsSelected, Patience)) wrong.Add("selecting the web link did not deselect the chapter");
            await Pump(window);
            if (!website.IsSelected) wrong.Add("the web link was not selected");
            if (window.PageBox.Text != page) wrong.Add($"selecting the web link moved the page to '{window.PageBox.Text}'");
        }
        finally
        {
            CloseQuietly(window);
        }
        return CheckKit.Tally(name, 6, wrong);
    }
}
