namespace PdfLiteViewer;
/// <summary>About web links. Native support submissions use AppSupportClient.</summary>
public static class SupportNavigationPolicy
{
    public const string WebsiteUrl = "https://greenyogainc.com/";
    public const string SoftwareUrl = "https://greenyogainc.com/software/";
    public const string PrivacyUrl = "https://greenyogainc.com/privacy/";
    public static bool IsSafeExternalUrl(string? uriString) =>
        Uri.TryCreate(uriString, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
