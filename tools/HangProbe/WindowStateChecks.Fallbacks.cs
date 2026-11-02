using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using PdfLiteViewer;

namespace HangProbe;

internal static partial class WindowStateChecks
{
    /// <summary>
    /// The continuous-mode helpers carry fallbacks for a viewport with nothing laid out in it: a
    /// scroll ratio of 1 when there is no extent (or no content) to compare, and the current page
    /// when the items presenter has no panel. Production never gets there - ApplyTemplate on the
    /// presenter always builds the panel, which is measured first - so the second half injects
    /// the state: the cached presenter is swapped for one that belongs to no template. What is
    /// checked is the fallback itself: no exception, and the current page still rendered and
    /// reachable.
    /// </summary>
    private static async Task<Check> NoLayoutFallbacksAsync(Docs docs, string name)
    {
        var wrong = new List<string>();
        var instance = BindingFlags.NonPublic | BindingFlags.Instance;
        var pagesPanel = typeof(MainWindow).GetMethod("PagesPanel", instance)!;
        var scale = typeof(MainWindow).GetMethod("PanelOffsetScale", instance)!;

        // The scale: no content at all, then content that has not been laid out.
        var fresh = NewWindow(shown: false);
        try
        {
            if (!Equals(scale.Invoke(fresh, null), 1.0)) wrong.Add($"with no document the scroll ratio is {scale.Invoke(fresh, null)}, expected 1");
            await OpenAndRenderAsync(fresh, docs.Plain7);
            if (!Equals(scale.Invoke(fresh, null), 1.0)) wrong.Add($"with a document but no layout the scroll ratio is {scale.Invoke(fresh, null)}, expected 1");
            if (pagesPanel.Invoke(fresh, null) is not Panel) wrong.Add("a window that was never laid out has no items panel (it is built when the presenter's template is applied)");
        }
        finally
        {
            CloseQuietly(fresh);
        }

        // The panel: the presenter is replaced by one that was never templated.
        var window = await ShownWindowAsync();
        try
        {
            window.SetMode(ViewMode.Continuous);
            await OpenAndRenderAsync(window, docs.Plain7);
            if (!await Poll.UntilAsync(() => window.Items.Count > 0 && window.Items[0].Image is not null, Patience))
                throw new InvalidOperationException("the first page never rendered");
            if (pagesPanel.Invoke(window, null) is not Panel) wrong.Add("a laid-out continuous window has no items panel");

            typeof(MainWindow).GetField("_presenter", instance)!.SetValue(window, new ItemsPresenter());
            typeof(MainWindow).GetField("_pagesPanel", instance)!.SetValue(window, null);
            if (pagesPanel.Invoke(window, null) is not null) wrong.Add("a presenter without a template still produced a panel");

            window.GoToPage(4);
            if (window.PageBox.Text != "5") wrong.Add($"going to page 5 with no panel left the page box at '{window.PageBox.Text}'");
            await window.UpdateRenderedPagesAsync();
            if (!await Poll.UntilAsync(() => window.Items[4].Image is not null, Patience)) wrong.Add("with no panel the current page was not rendered");
        }
        finally
        {
            CloseQuietly(window);
        }
        return CheckKit.Tally(name, 5, wrong);
    }
}
