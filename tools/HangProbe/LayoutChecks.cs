using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfLiteViewer;

namespace HangProbe;

internal readonly record struct Check(string Name, bool Ok, string Detail);

/// <summary>
/// Guards the behaviour the virtualization fix could plausibly break: pages must still be
/// centred, the scrollbar must still span the whole document, and jumping to a page must
/// still land on it. A fast viewer that scrolls to the wrong page is not a fixed viewer.
/// </summary>
internal static class LayoutChecks
{
    private const double PageMargin = 16;   // 8 either side, from the item template

    public static async Task<List<Check>> RunAsync(MainWindow window, int pages, Func<Task> settle)
    {
        var checks = new List<Check>();

        window.SetMode(ViewMode.Continuous);
        window.UpdateLayout();
        await settle();

        int realized = CountRealized(window.PagesHost, pages);
        checks.Add(new Check("continuous mode virtualizes",
            realized < 60,
            $"{realized} of {pages} page containers realized"));

        // The page at the top of the viewport — not merely the first realized one, which in
        // recycling mode can be a cached container parked outside the visible area.
        int index = TopmostRealizedPage(window.PagesHost, window.Scroller, pages);
        var border = index < 0 ? null : PageBorder(window.PagesHost, index);
        var doc = window.Document;
        if (border is null || doc is null)
            return Fail(checks, "no page is visible in the viewport — nothing else can be checked");

        // Scale the app is drawing at, recovered from a page whose PDF size we know.
        double scale = border.ActualWidth / (doc.GetDisplaySize(index).Width * 96.0 / 72.0);
        double expectedExtent = 0;
        for (int i = 0; i < pages; i++)
            expectedExtent += doc.GetDisplaySize(i).Height * 96.0 / 72.0 * scale + PageMargin;

        // The scrollbar is inherently approximate under virtualization — the panel estimates
        // the pages it has not realized, so the thumb is a few percent off on a document with
        // mixed page sizes. This only guards against it being *wildly* wrong (e.g. an extent
        // in item counts rather than pixels); exact positioning is the two checks below.
        double extent = window.Scroller.ExtentHeight;
        checks.Add(new Check("scrollbar spans the document",
            Math.Abs(extent - expectedExtent) / expectedExtent < 0.05,
            $"extent {extent:F0}px vs expected {expectedExtent:F0}px " +
            $"({(extent - expectedExtent) / expectedExtent * 100:+0.0;-0.0}%)"));

        var scroller = window.Scroller;
        bool centred = IsCentred(border, scroller, out double offBy);
        checks.Add(new Check("page is centred in the viewport", centred,
            $"off centre by {offBy:F1}px" + (centred ? "" : Chain(border, scroller))));

        // Jump into the middle of a mixed-size document: whatever the panel estimated for
        // the pages it skipped, the page the user asked for has to end up at the top.
        int target = pages / 2;
        window.GoToPage(target);
        window.UpdateLayout();
        int landed = TopmostRealizedPage(window.PagesHost, window.Scroller, pages);
        checks.Add(new Check("go-to-page lands on the page",
            landed == target,
            $"asked for page {target + 1}, viewport shows page {landed + 1}"));
        checks.Add(new Check("page box tracks the scroll position",
            window.PageBox.Text == (target + 1).ToString(),
            $"page box reads '{window.PageBox.Text}', expected '{target + 1}'"));

        // Everything above would still pass if pages had stopped rendering entirely.
        checks.Add(await RenderedCheckAsync("continuous", window, pages));

        // Facing mode keeps the old non-virtualizing path — the spread must stay centred.
        window.SetMode(ViewMode.Facing);
        window.GoToPage(4);
        window.UpdateLayout();
        await settle();
        checks.Add(new Check("facing spread is centred",
            IsSpreadCentred(window.PagesHost, window.Scroller, out offBy, out int spreadPages),
            $"{spreadPages} page(s), off centre by {offBy:F1}px"));
        checks.Add(await RenderedCheckAsync("facing", window, 2));

        return checks;
    }

    /// <summary>
    /// The page on screen must be carrying a real bitmap, not an empty white sheet.
    /// Rendering is asynchronous by design, so allow it a few seconds to arrive.
    /// </summary>
    private static async Task<Check> RenderedCheckAsync(string mode, MainWindow window, int pages)
    {
        const string name = "visible page is rendered";
        int index = -1;

        for (int attempt = 0; attempt < 30; attempt++)
        {
            index = TopmostRealizedPage(window.PagesHost, window.Scroller, pages);
            if (index >= 0 &&
                FindImage(window.PagesHost.ItemContainerGenerator.ContainerFromIndex(index)) is { } image &&
                image.Source is BitmapSource bitmap && bitmap.PixelWidth > 0)
            {
                return new Check($"{mode}: {name}", true,
                    $"page {index + 1} shows a {bitmap.PixelWidth}x{bitmap.PixelHeight} bitmap " +
                    $"after {attempt * 100}ms");
            }
            await Task.Delay(100);
        }

        return new Check($"{mode}: {name}", false,
            index < 0 ? "no page visible after 3s" : $"page {index + 1} still has no bitmap after 3s");
    }

    private static Image? FindImage(DependencyObject? root)
    {
        if (root is null) return null;
        if (root is Image image) return image;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindImage(VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        return null;
    }

    /// <summary>Every element from the page up to the viewport, with its width and x offset.</summary>
    private static string Chain(FrameworkElement from, ScrollViewer scroller)
    {
        var parts = new List<string>();
        for (DependencyObject? d = from; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is not FrameworkElement fe) continue;
            double x = fe.TransformToAncestor(scroller).Transform(new Point(0, 0)).X;
            parts.Add($"{fe.GetType().Name}(x={x:F1} w={fe.ActualWidth:F1} ha={fe.HorizontalAlignment})");
            if (ReferenceEquals(fe, scroller)) break;
        }
        return "\n       " + string.Join("\n       ", parts) +
               $"\n       viewport={scroller.ViewportWidth:F1} extent={scroller.ExtentWidth:F1}";
    }

    private static List<Check> Fail(List<Check> checks, string why)
    {
        checks.Add(new Check("layout checks", false, why));
        return checks;
    }

    private static int CountRealized(ItemsControl host, int pages)
    {
        int realized = 0;
        for (int i = 0; i < pages; i++)
            if (host.ItemContainerGenerator.ContainerFromIndex(i) is not null)
                realized++;
        return realized;
    }

    private static FrameworkElement? PageBorder(ItemsControl host, int index) =>
        host.ItemContainerGenerator.ContainerFromIndex(index) is DependencyObject c ? FindBorder(c) : null;

    /// <summary>Index of the page covering the top edge of the viewport.</summary>
    private static int TopmostRealizedPage(ItemsControl host, ScrollViewer viewport, int pages)
    {
        int best = -1;
        double bestDistance = double.MaxValue;

        for (int i = 0; i < pages; i++)
        {
            if (host.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container ||
                !container.IsVisible)
                continue;

            double top = container.TransformToAncestor(viewport).Transform(new Point(0, 0)).Y;
            if (top + container.ActualHeight <= 0 || top >= viewport.ViewportHeight) continue;

            double distance = Math.Abs(top);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return best;
    }

    /// <summary>Both pages of a facing spread, taken together, must straddle the viewport centre.</summary>
    private static bool IsSpreadCentred(ItemsControl host, ScrollViewer viewport, out double offBy, out int count)
    {
        offBy = double.MaxValue;
        count = 0;
        double left = double.MaxValue, right = double.MinValue;

        for (int i = 0; i < 2; i++)
        {
            if (host.ItemContainerGenerator.ContainerFromIndex(i) is not DependencyObject c ||
                FindBorder(c) is not { } border || !border.IsVisible)
                continue;

            count++;
            double x = border.TransformToAncestor(viewport).Transform(new Point(0, 0)).X;
            left = Math.Min(left, x);
            right = Math.Max(right, x + border.ActualWidth);
        }

        if (count == 0 || viewport.ViewportWidth <= 0) return false;
        offBy = Math.Abs((left + right) / 2 - viewport.ViewportWidth / 2);
        return offBy <= 3;
    }

    private static bool IsCentred(FrameworkElement element, ScrollViewer viewport, out double offBy)
    {
        offBy = double.MaxValue;
        if (!element.IsVisible || viewport.ViewportWidth <= 0) return false;

        var topLeft = element.TransformToAncestor(viewport).Transform(new Point(0, 0));
        double elementCentre = topLeft.X + element.ActualWidth / 2;
        double viewportCentre = viewport.ViewportWidth / 2;
        offBy = Math.Abs(elementCentre - viewportCentre);
        return offBy <= 3;
    }

    private static Border? FindBorder(DependencyObject root)
    {
        if (root is Border b && b.ActualHeight > 0) return b;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindBorder(VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        return null;
    }
}
