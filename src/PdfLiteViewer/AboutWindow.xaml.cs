using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using GreenYoga.Support;

namespace PdfLiteViewer;

/// <summary>Offline About information and explicit-send native support.</summary>
public partial class AboutWindow : Window
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly AppSupportClient _supportClient;
    private readonly CancellationTokenSource _lifetime = new();
    private AppSupportDraft _draft = new();
    private bool _sending;
    private bool _closed;
    internal bool SupportSubmitted { get; private set; }

    public AboutWindow() : this(new AppSupportClient(Http)) { }
    internal AboutWindow(AppSupportClient supportClient)
    {
        _supportClient = supportClient;
        InitializeComponent();
        Strings.ApplyFlowDirection(this);
        VersionText.Text = string.Format(Strings.Get("AboutVersionFormat"), AppVersion());
        WebsiteLink.ToolTip = OpensInBrowserTooltip(SupportNavigationPolicy.WebsiteUrl);
        SoftwareLink.ToolTip = OpensInBrowserTooltip(SupportNavigationPolicy.SoftwareUrl);
        PrivacyLink.ToolTip = OpensInBrowserTooltip(SupportNavigationPolicy.PrivacyUrl);
        SupportDetailsText.Text = string.Format(Strings.Get("SupportDetailsFormat"),
            Strings.Get("AppTitle"), AppVersion(), Environment.OSVersion.VersionString, CultureInfo.CurrentUICulture.Name);
        Loaded += (_, _) => ContactSupportBtn.Focus();
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _lifetime.Dispose(); };
    }

    /// <summary>Marketing version of the running assembly (no build metadata).</summary>
    internal static string AppVersion()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
            return informational.Split('+')[0];
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private static string OpensInBrowserTooltip(string url) =>
        string.Format(Strings.Get("AboutOpensInBrowserTooltipFormat"), url);

    // ---------- About side ----------

    private void Website_Click(object sender, RoutedEventArgs e) => OpenExternal(SupportNavigationPolicy.WebsiteUrl);
    private void Software_Click(object sender, RoutedEventArgs e) => OpenExternal(SupportNavigationPolicy.SoftwareUrl);
    private void Privacy_Click(object sender, RoutedEventArgs e) => OpenExternal(SupportNavigationPolicy.PrivacyUrl);

    private void License_Click(object sender, RoutedEventArgs e)
    {
        bool show = LicenseText.Visibility != Visibility.Visible;
        if (show && LicenseText.Text.Length == 0)
            LicenseText.Text = LoadLicenseText();
        LicenseText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        LicenseToggle.Content = Strings.Get(show ? "AboutHideLicense" : "AboutViewLicense");
    }

    private static string LoadLicenseText()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PdfLiteViewer.LICENSE");
            if (stream is not null)
                return new StreamReader(stream).ReadToEnd();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        return "MIT License — https://opensource.org/licenses/MIT";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ContactSupport_Click(object sender, RoutedEventArgs e) => OpenSupportPane();
    internal void OpenSupportPane()
    {
        AboutPanel.Visibility = Visibility.Collapsed;
        SupportPanel.Visibility = Visibility.Visible;
        BackBtn.Visibility = Visibility.Visible;
        SupportPanel.UpdateLayout();
        if (SupportSubmitted) SupportAnotherBtn.Focus(); else SupportNameInput.Focus();
    }
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        SupportPanel.Visibility = Visibility.Collapsed;
        BackBtn.Visibility = Visibility.Collapsed;
        AboutPanel.Visibility = Visibility.Visible;
        ContactSupportBtn.Focus();
    }
    private void SendSupport_Click(object sender, RoutedEventArgs e) => _ = SendSupportAsync();
    internal AppSupportRequest CurrentSupportRequest() => _draft.GetRequest(
        "pdf-lite-viewer", AppVersion(), Environment.OSVersion.VersionString,
        string.IsNullOrEmpty(CultureInfo.CurrentUICulture.Name) ? "en" : CultureInfo.CurrentUICulture.Name,
        SupportNameInput.Text, SupportEmailInput.Text, SupportSubjectInput.Text, SupportMessageInput.Text);

    internal async Task SendSupportAsync()
    {
        if (_sending || _closed || SupportSubmitted) return;
        var request = CurrentSupportRequest();
        if (AppSupportClient.Validate(request) is { } invalid)
        {
            SetSupportStatus(Strings.Get(invalid == AppSupportStatus.InvalidEmail ? "SupportInvalidEmail" : "SupportRequired"));
            if (string.IsNullOrWhiteSpace(SupportNameInput.Text)) SupportNameInput.Focus();
            else if (invalid == AppSupportStatus.InvalidEmail || string.IsNullOrWhiteSpace(SupportEmailInput.Text)) SupportEmailInput.Focus();
            else if (string.IsNullOrWhiteSpace(SupportSubjectInput.Text)) SupportSubjectInput.Focus();
            else SupportMessageInput.Focus();
            return;
        }
        _sending = true;
        SupportForm.IsEnabled = false;
        BackBtn.IsEnabled = false;
        SupportSendBtn.Content = Strings.Get("SupportSending");
        SetSupportStatus(Strings.Get("SupportSending"));
        try
        {
            var result = await _supportClient.SendAsync(request, _lifetime.Token);
            if (_closed) return;
            if (result.Status == AppSupportStatus.Success)
            {
                SupportSubmitted = true;
                SupportForm.Visibility = Visibility.Collapsed;
                SupportAnotherBtn.Visibility = Visibility.Visible;
                SetSupportStatus(Strings.Get("SupportSent") + Environment.NewLine +
                    string.Format(Strings.Get("SupportReferenceFormat"), result.RequestId));
            }
            else
            {
                string key = result.Status switch
                {
                    AppSupportStatus.InvalidEmail => "SupportInvalidEmail",
                    AppSupportStatus.InvalidRequest => "SupportRequired",
                    AppSupportStatus.RateLimited => "SupportRateLimited",
                    AppSupportStatus.InProgress => "SupportInProgress",
                    AppSupportStatus.Conflict => "SupportConflict",
                    _ => "SupportSendFailed"
                };
                SetSupportStatus(Strings.Get(key));
            }
            SupportStatusText.Focus();
            SupportStatusText.BringIntoView();
        }
        catch (OperationCanceledException) when (_closed) { }
        finally
        {
            _sending = false;
            if (!_closed)
            {
                SupportForm.IsEnabled = true;
                BackBtn.IsEnabled = true;
                SupportSendBtn.Content = Strings.Get("SupportSend");
            }
        }
    }
    private void SetSupportStatus(string message)
    {
        SupportStatusText.Text = message;
        SupportStatusText.Visibility = Visibility.Visible;
    }
    private void SendAnother_Click(object sender, RoutedEventArgs e)
    {
        SupportSubmitted = false;
        _draft = new();
        SupportSubjectInput.Clear();
        SupportMessageInput.Clear();
        SupportStatusText.Visibility = Visibility.Collapsed;
        SupportAnotherBtn.Visibility = Visibility.Collapsed;
        SupportForm.Visibility = Visibility.Visible;
        SupportSubjectInput.Focus();
    }
    /// <summary>The single exit to the OS shell; only plain web URLs pass.</summary>
    private static void OpenExternal(string url)
    {
        if (!SupportNavigationPolicy.IsSafeExternalUrl(url))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }
}
