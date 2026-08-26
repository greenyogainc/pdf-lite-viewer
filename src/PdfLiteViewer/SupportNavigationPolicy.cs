namespace PdfLiteViewer;

/// <summary>What the embedded support view should do with a requested navigation.</summary>
public enum NavigationDecision
{
    /// <summary>Green Yoga-owned HTTPS origin: allow it inside the embedded view.</summary>
    AllowInView,
    /// <summary>Ordinary web link outside Green Yoga: hand it to the user's default browser.</summary>
    OpenInBrowser,
    /// <summary>Anything else (file:, custom schemes, malformed): drop it entirely.</summary>
    Cancel,
}

/// <summary>
/// Navigation policy for the About window's embedded support view. Pure and static so
/// tools/HangProbe can exercise it exhaustively without a WebView2 runtime.
///
/// The embedded view exists for exactly one thing — the greenyogainc.com support form —
/// so top-level navigations and popups stay inside Green Yoga's HTTPS origins (the form
/// posts to api.greenyogainc.com) and everything else leaves the app: normal web links
/// go to the default browser, and non-web schemes go nowhere at all.
/// </summary>
public static class SupportNavigationPolicy
{
    public const string SupportUrl = "https://greenyogainc.com/contact/";
    public const string WebsiteUrl = "https://greenyogainc.com/";
    public const string SoftwareUrl = "https://greenyogainc.com/software/";
    public const string PrivacyUrl = "https://greenyogainc.com/privacy/";

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "greenyogainc.com",
        "www.greenyogainc.com",
        "api.greenyogainc.com",
    };

    public static NavigationDecision Decide(string? uriString)
    {
        if (string.IsNullOrWhiteSpace(uriString) ||
            !Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
            return NavigationDecision.Cancel;

        bool isHttps = uri.Scheme == Uri.UriSchemeHttps;
        bool isHttp = uri.Scheme == Uri.UriSchemeHttp;
        if (!isHttps && !isHttp)
            return NavigationDecision.Cancel;

        // Exact host match only — no suffix matching, so "evilgreenyogainc.com" and
        // "greenyogainc.com.evil.example" both fall through to the browser.
        // Non-default ports are not Green Yoga's; treat them as ordinary web links.
        if (isHttps && uri.IsDefaultPort && AllowedHosts.Contains(uri.Host))
            return NavigationDecision.AllowInView;

        return NavigationDecision.OpenInBrowser;
    }

    /// <summary>
    /// True when the URI is safe to hand to the OS shell (default browser). Only plain
    /// web URLs qualify; this is the single gate in front of every Process.Start.
    /// </summary>
    public static bool IsSafeExternalUrl(string? uriString) =>
        Uri.TryCreate(uriString, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
