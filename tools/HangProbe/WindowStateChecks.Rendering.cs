using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using PdfLiteViewer;

namespace HangProbe;

internal static partial class WindowStateChecks
{
    // ---------- render helpers ----------

    private static SemaphoreSlim RenderLock() =>
        (SemaphoreSlim)typeof(PdfDoc).GetField("RenderLock", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static bool AllRendered(MainWindow window) =>
        window.Items.Count > 0 && window.Items.All(i => i.Image is not null);

    private static async Task<MainWindow> RenderedWindowAsync(string path, ViewMode mode, int? page = null)
    {
        var window = await ShownWindowAsync();
        try
        {
            window.SetMode(mode);
            await window.OpenFileAsync(path);
            if (page is { } p) window.GoToPage(p);
            if (!await Poll.UntilAsync(() => AllRendered(window), Patience))
                throw new InvalidOperationException("the pages never rendered");
            await Pump(window);
            return window;
        }
        catch
        {
            CloseQuietly(window);
            throw;
        }
    }

    /// <summary>How many entries the app's log gained after <paramref name="mark"/>.</summary>
    private static int EntriesSince(long mark) =>
        Regex.Matches(ProbeLog.TextSince(mark), @"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] ", RegexOptions.Multiline).Count;

    // ---------- pixel budgets ----------

    /// <summary>
    /// Width alone does not bound a bitmap: a 200x5000 pt strip at the top zoom would ask for
    /// 3500x87500 pixels. The render is clamped to ~24M pixels, which for this strip is a
    /// 979-pixel-wide bitmap. A page so small at the lowest zoom that it would be under 8
    /// pixels wide is skipped instead of rendered, and so is a slot that has no width at all.
    /// </summary>
    private static async Task<Check> PixelBudgetsAsync(Docs docs, string name)
    {
        var wrong = new List<string>();
        string note = "";

        var strip = await ShownWindowAsync();
        try
        {
            strip.SetMode(ViewMode.Single);
            await OpenAndRenderAsync(strip, docs.Strip);
            strip.SetZoom(6);
            // sqrt(24M / (5000 / 200)) = 979.8, floored; the zoom is high enough that the width cap (3500) is not what binds.
            if (!await Poll.UntilAsync(() => strip.Items[0].RenderedPixelWidth == 979 && strip.Items[0].Image is not null, Patience))
                wrong.Add($"the strip never rendered at the clamped width of 979px (rendered width {strip.Items[0].RenderedPixelWidth}px)");
            else
            {
                var image = strip.Items[0].Image!;
                long pixels = (long)image.PixelWidth * image.PixelHeight;
                note = $"the strip rendered {image.PixelWidth}x{image.PixelHeight} = {pixels / 1_000_000.0:F1}M px";
                if (pixels > 24_000_000) wrong.Add($"the strip bitmap is {pixels / 1_000_000.0:F1}M pixels, over the 24M budget");
                if (pixels < 20_000_000) wrong.Add($"the strip bitmap is only {pixels / 1_000_000.0:F1}M pixels: the clamp is stricter than the budget");
                if (image.PixelWidth != strip.Items[0].RenderedPixelWidth) wrong.Add($"bitmap {image.PixelWidth}px wide, recorded as {strip.Items[0].RenderedPixelWidth}px");
            }
        }
        finally
        {
            CloseQuietly(strip);
        }

        var tiny = await ShownWindowAsync();
        try
        {
            tiny.SetMode(ViewMode.Single);
            await tiny.OpenFileAsync(docs.Tiny);
            tiny.SetZoom(0.1);                      // before the render timer's first tick
            await Task.Delay(300);
            await tiny.UpdateRenderedPagesAsync();
            await Task.Delay(300);
            if (tiny.Items[0].Image is not null || tiny.Items[0].RenderedPixelWidth != 0)
                wrong.Add($"a page {tiny.Items[0].DisplayWidth:F1} DIP wide was rendered ({tiny.Items[0].RenderedPixelWidth}px)");

            // A slot with no width (never the case once PdfDoc has validated the sizes) is skipped,
            // not divided by.
            tiny.Items[0].DisplayWidth = 0;
            await tiny.UpdateRenderedPagesAsync();
            if (tiny.Items[0].Image is not null) wrong.Add("a slot with no width was rendered");

            tiny.SetZoom(6);
            if (!await Poll.UntilAsync(() => tiny.Items[0].Image is not null, Patience)) wrong.Add("the tiny page never rendered once it was big enough");
        }
        finally
        {
            CloseQuietly(tiny);
        }
        return CheckKit.Tally(name, 2, wrong, note);
    }

    // ---------- a viewport nobody can see ----------

    /// <summary>
    /// A hidden window still has its containers, but none of them is visible: the page tracking
    /// and the render pass find nothing on screen and fall back to the current page instead of
    /// scrolling or rendering by a stale idea of the viewport.
    /// </summary>
    private static async Task<Check> HiddenViewportAsync(Docs docs, string name)
    {
        var wrong = new List<string>();
        var window = await ShownWindowAsync();
        try
        {
            window.SetMode(ViewMode.Continuous);
            await OpenAndRenderAsync(window, docs.Plain7);
            if (!await Poll.UntilAsync(() => window.Items.Count > 0 && window.Items[0].Image is not null, Patience))
                throw new InvalidOperationException("the first page never rendered");

            // Scrolling a little inside the page that is on top does not change the page.
            window.GoToPage(2);
            if (!await Poll.UntilAsync(() => window.PageBox.Text == "3", Patience)) wrong.Add("going to page 3 did not show it");
            await Task.Delay(300);
            await Pump(window);
            window.Scroller.ScrollToVerticalOffset(window.Scroller.VerticalOffset + 12);
            await Task.Delay(300);
            await Pump(window);
            if (window.PageBox.Text != "3") wrong.Add($"scrolling 12 pixels moved the page box to '{window.PageBox.Text}'");

            window.Hide();
            await Pump(window);
            window.GoToPage(4);
            await window.UpdateRenderedPagesAsync();
            await Pump(window);
            if (window.PageBox.Text != "5") wrong.Add($"going to page 5 in a hidden window left the page box at '{window.PageBox.Text}'");

            window.Show();
            await Pump(window);
            window.GoToPage(2);
            if (!await Poll.UntilAsync(() => window.PageBox.Text == "3" && window.Items[2].Image is not null, Patience))
                wrong.Add("the window did not render page 3 once it was shown again");
        }
        finally
        {
            CloseQuietly(window);
        }
        return CheckKit.Tally(name, 4, wrong);
    }

    // ---------- failing renders ----------

    /// <summary>
    /// A page PDFium cannot read (the bytes were cleared) is logged and skipped; the window and
    /// the page that was already on screen stay as they were.
    /// </summary>
    private static async Task<Check> RenderFailureAsync(Docs docs, string name)
    {
        var wrong = new List<string>();
        var window = await RenderedWindowAsync(docs.Plain4, ViewMode.Single);
        try
        {
            var before = window.Items[0].Image;
            ClearBytes(window.Document!);
            long mark = ProbeLog.Length();
            window.SetZoom(1.37);
            await window.UpdateRenderedPagesAsync();

            string logged = ProbeLog.TextSince(mark);
            if (!logged.Contains("PdfDoc")) wrong.Add($"the failed render left no entry naming PdfDoc in the log ({logged.Length} chars logged)");
            if (!ReferenceEquals(window.Items[0].Image, before)) wrong.Add("the failed render replaced the page that was on screen");
            if (await window.Dispatcher.InvokeAsync(() => 42) != 42) wrong.Add("the dispatcher stopped serving work");
            window.GoToPage(2);
            if (window.PageBox.Text != "3") wrong.Add($"navigation after the failure is broken (page box '{window.PageBox.Text}')");
        }
        finally
        {
            CloseQuietly(window);
        }
        return CheckKit.Tally(name, 4, wrong);
    }

    // ---------- cancellation ----------

    /// <summary>
    /// A render that is cancelled must not leave a bitmap of the old orientation behind. Three
    /// ways it can be cancelled are driven by hand: while it waits for the render lock (the lock
    /// is held), after it finished with its continuation already queued (the UI thread sleeps
    /// until the pool is done, rotates, then yields), and after a failed page, when the spread
    /// is replaced by a shorter one before the loop reaches the next slot.
    /// </summary>
    private static async Task<Check> RenderCancellationAsync(Docs docs, string name)
    {
        var wrong = new List<string>();
        var renderLock = RenderLock();

        // 1. Waiting for the lock.
        var window = await RenderedWindowAsync(docs.Plain4, ViewMode.Single);
        try
        {
            await renderLock.WaitAsync();
            try
            {
                window.SetZoom(1.2);
                var pending = window.UpdateRenderedPagesAsync();
                await Task.Delay(100);
                if (pending.IsCompleted) wrong.Add("a render ran while the render lock was held");
                window.RotateClockwise();
                await pending.WaitAsync(TimeSpan.FromSeconds(10));
                if (window.Items.Any(i => i.Image is not null)) wrong.Add("a bitmap of the old orientation was kept after the wait was cancelled");
                if (renderLock.CurrentCount != 0) wrong.Add($"the cancelled wait released a lock it never held (count {renderLock.CurrentCount})");
            }
            finally
            {
                renderLock.Release();
            }
            if (!await Poll.UntilAsync(() => AllRendered(window), Patience)) wrong.Add("after the lock was released the page never rendered in its new orientation");
        }
        finally
        {
            CloseQuietly(window);
        }

        // 2. Finished, continuation queued, then cancelled.
        window = await RenderedWindowAsync(docs.Plain4, ViewMode.Single);
        try
        {
            window.SetZoom(1.3);
            long mark = ThreadPool.CompletedWorkItemCount;
            var pending = window.UpdateRenderedPagesAsync();
            WaitForPool(mark, 1);
            window.RotateClockwise();
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
            if (window.Items.Any(i => i.Image is not null)) wrong.Add("a render that finished after the rotation still put its bitmap on the page");
        }
        finally
        {
            CloseQuietly(window);
        }

        // 3. A page fails, and the spread is replaced by a shorter one (the cover alone) before the
        // loop reaches the second slot, which no longer exists.
        window = await RenderedWindowAsync(docs.Plain7, ViewMode.Facing, page: 1);
        try
        {
            if (window.Items.Count != 2) throw new InvalidOperationException($"expected a two-page spread, got {window.Items.Count} slot(s)");
            ClearBytes(window.Document!);
            window.SetZoom(1.4);
            long logMark = ProbeLog.Length();
            long mark = ThreadPool.CompletedWorkItemCount;
            var pending = window.UpdateRenderedPagesAsync();
            WaitForPool(mark, 1);
            window.GoToPage(0);
            if (window.Items.Count != 1) throw new InvalidOperationException($"expected the cover alone, got {window.Items.Count} slot(s)");
            await pending.WaitAsync(TimeSpan.FromSeconds(10));    // throws if the loop indexed the old second slot
            int entries = EntriesSince(logMark);
            if (entries != 1) wrong.Add($"{entries} failure(s) were logged; the loop should have stopped after the first page once the spread changed");
        }
        finally
        {
            CloseQuietly(window);
        }
        return CheckKit.Tally(name, 3, wrong);
    }
}
