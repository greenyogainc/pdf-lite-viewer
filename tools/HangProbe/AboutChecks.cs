using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using PdfLiteViewer;

namespace HangProbe;

/// <summary>
/// Regression checks for the About/support surface: the navigation policy that fences
/// the embedded support view, the About window's accessibility contract, RTL behaviour,
/// and the WebView2 failure fallback (via the init-failure seam — no network involved).
/// </summary>
internal static class AboutChecks
{
    public static async Task<List<Check>> RunAsync()
    {
        var checks = new List<Check>();
        checks.AddRange(PolicyChecks());

        try
        {
            checks.AddRange(await WindowChecksAsync());
        }
        catch (Exception ex)
        {
            checks.Add(new Check("about window checks ran", false, ex.ToString()));
        }

        checks.Add(RtlCheck());
        return checks;
    }

    // ---------- SupportNavigationPolicy (pure) ----------

    private static IEnumerable<Check> PolicyChecks()
    {
        var cases = new (string? Uri, NavigationDecision Expected)[]
        {
            ("https://greenyogainc.com/contact/", NavigationDecision.AllowInView),
            ("https://greenyogainc.com/", NavigationDecision.AllowInView),
            ("https://WWW.GREENYOGAINC.COM/anything?x=1", NavigationDecision.AllowInView),
            ("https://api.greenyogainc.com/contact", NavigationDecision.AllowInView),
            ("https://example.com/", NavigationDecision.OpenInBrowser),
            ("http://greenyogainc.com/", NavigationDecision.OpenInBrowser),          // http never embeds
            ("https://evilgreenyogainc.com/", NavigationDecision.OpenInBrowser),     // suffix spoof
            ("https://greenyogainc.com.evil.example/", NavigationDecision.OpenInBrowser),
            ("https://sub.greenyogainc.com/", NavigationDecision.OpenInBrowser),     // unknown subdomain
            ("https://greenyogainc.com:8443/", NavigationDecision.OpenInBrowser),    // non-default port
            ("file:///C:/Windows/system32/", NavigationDecision.Cancel),
            ("javascript:alert(1)", NavigationDecision.Cancel),
            ("ms-appx-web://anything", NavigationDecision.Cancel),
            ("data:text/html,<h1>x</h1>", NavigationDecision.Cancel),
            ("about:blank", NavigationDecision.Cancel),
            ("not a uri at all", NavigationDecision.Cancel),
            ("", NavigationDecision.Cancel),
            (null, NavigationDecision.Cancel),
        };

        int wrong = 0;
        var details = new List<string>();
        foreach (var (uri, expected) in cases)
        {
            var got = SupportNavigationPolicy.Decide(uri);
            if (got != expected)
            {
                wrong++;
                details.Add($"'{uri}' -> {got}, expected {expected}");
            }
        }
        yield return new Check("support navigation policy", wrong == 0,
            wrong == 0 ? $"{cases.Length} cases verified" : string.Join("; ", details));

        bool shellGate =
            SupportNavigationPolicy.IsSafeExternalUrl("https://example.com/") &&
            SupportNavigationPolicy.IsSafeExternalUrl("http://example.com/") &&
            !SupportNavigationPolicy.IsSafeExternalUrl("file:///C:/x") &&
            !SupportNavigationPolicy.IsSafeExternalUrl("javascript:alert(1)") &&
            !SupportNavigationPolicy.IsSafeExternalUrl(null);
        yield return new Check("external-shell gate takes only web URLs", shellGate,
            "IsSafeExternalUrl accepts http/https and nothing else");
    }

    // ---------- AboutWindow (UI) ----------

    private static async Task<List<Check>> WindowChecksAsync()
    {
        var checks = new List<Check>();
        var about = new AboutWindow();
        try
        {
            about.Show();
            await PumpAsync();

            var version = AboutWindow.AppVersion();
            checks.Add(new Check("about: runtime version derived", Regex.IsMatch(version, @"^\d+\.\d+\.\d+$"),
                $"AppVersion() = '{version}'"));
            checks.Add(new Check("about: version shown", about.VersionText.Text.Contains(version),
                $"version line reads '{about.VersionText.Text}'"));

            checks.Add(new Check("about: brand mark has an accessible name",
                !string.IsNullOrWhiteSpace(AutomationProperties.GetName(about.BrandMark)),
                $"name = '{AutomationProperties.GetName(about.BrandMark)}'"));

            // The mark must actually decode — a bad pack URI renders silently blank
            // (that is how the entry-assembly-relative URI bug slipped out).
            try
            {
                var mark = about.BrandMark.Source as System.Windows.Media.Imaging.BitmapSource;
                checks.Add(new Check("about: brand mark bitmap decodes",
                    mark is not null && mark.PixelWidth > 100,
                    mark is null ? "Image.Source is null" : $"{mark.PixelWidth}x{mark.PixelHeight}"));
            }
            catch (Exception ex)
            {
                checks.Add(new Check("about: brand mark bitmap decodes", false, ex.Message));
            }

            var unnamed = new List<string>();
            foreach (var button in FindChildren<Button>(about))
            {
                bool named = !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)) ||
                             button.Content is string { Length: > 0 };
                if (!named) unnamed.Add(button.Name is { Length: > 0 } n ? n : "(anonymous)");
            }
            checks.Add(new Check("about: every button announces a name", unnamed.Count == 0,
                unnamed.Count == 0 ? "all buttons carry text content or an automation name"
                                   : string.Join(", ", unnamed)));

            checks.Add(new Check("about: initial keyboard focus", about.ContactSupportBtn.IsFocused,
                "Contact support is the focused element on open"));

            // License panel: loads the embedded MIT text on first toggle.
            about.LicenseToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await PumpAsync();
            checks.Add(new Check("about: license text loads offline",
                about.LicenseText.Visibility == Visibility.Visible && about.LicenseText.Text.Contains("MIT License"),
                $"{about.LicenseText.Text.Length} chars shown"));

            // Consent-before-load: opening the support pane must not create any web view.
            about.OpenSupportPane();
            await PumpAsync();
            checks.Add(new Check("support: nothing loads before consent",
                about.SupportConsentPanel.Visibility == Visibility.Visible &&
                about.WebViewHost.Visibility == Visibility.Collapsed &&
                about.WebViewHost.Child is null,
                "consent panel showing, no WebView2 instantiated"));

            // Failure fallback via the seam: retry + open-in-browser must be offered.
            AboutWindow.SimulateWebViewInitFailure = true;
            try
            {
                await about.LoadSupportAsync();
            }
            finally
            {
                AboutWindow.SimulateWebViewInitFailure = false;
            }
            await PumpAsync();
            checks.Add(new Check("support: init failure shows retry + browser fallback",
                about.SupportStatusPanel.Visibility == Visibility.Visible &&
                about.SupportErrorButtons.Visibility == Visibility.Visible &&
                about.SupportRetryBtn.IsVisible && about.SupportErrorBrowserBtn.IsVisible &&
                about.SupportStatusText.Text == Strings.Get("SupportLoadFailed"),
                $"status text: '{about.SupportStatusText.Text}'"));

            // A failed init must leave nothing behind: a half-initialized control kept
            // as WebViewHost.Child would leak once per retry click.
            checks.Add(new Check("support: failed init leaves no webview behind",
                about.WebViewHost.Child is null && about.WebViewForTest is null,
                "control disposed and detached after the failure path"));
        }
        finally
        {
            about.Close();
        }
        return checks;
    }

    /// <summary>Under an RTL culture the window must mirror; the check restores the culture.</summary>
    private static Check RtlCheck()
    {
        var previous = Thread.CurrentThread.CurrentUICulture;
        try
        {
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("ar");
            var rtl = new AboutWindow();
            var direction = rtl.FlowDirection;
            rtl.Close();
            return new Check("about: mirrors under Arabic", direction == FlowDirection.RightToLeft,
                $"FlowDirection = {direction}");
        }
        catch (Exception ex)
        {
            return new Check("about: mirrors under Arabic", false, ex.Message);
        }
        finally
        {
            Thread.CurrentThread.CurrentUICulture = previous;
        }
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(120);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static IEnumerable<T> FindChildren<T>(DependencyObject root) where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindChildren<T>(child)) yield return nested;
        }
    }
}
