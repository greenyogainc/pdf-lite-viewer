using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace PdfLiteViewer;

/// <summary>
/// About / support window. The About side is plain local UI. The support side embeds
/// the greenyogainc.com contact form in a WebView2 — created only after the user
/// explicitly consents, because loading it is the one thing in this app that goes
/// online. Navigation is fenced by <see cref="SupportNavigationPolicy"/>: Green Yoga
/// HTTPS origins stay in the view, ordinary links leave via the default browser,
/// everything else is dropped, and downloads are refused outright.
/// </summary>
public partial class AboutWindow : Window
{
    private enum SupportState { Consent, Loading, Loaded, Failed }

    /// <summary>Test seam (tools/HangProbe): forces the WebView2 init path to fail so the
    /// error/fallback UI can be verified without uninstalling the runtime.</summary>
    internal static bool SimulateWebViewInitFailure;

    private WebView2? _webView;
    private bool _supportLoadedOnce;

    /// <summary>Test access (tools/StoreShots submit check) to the live web view.</summary>
    internal WebView2? WebViewForTest => _webView;

    public AboutWindow()
    {
        InitializeComponent();
        Strings.ApplyFlowDirection(this);

        VersionText.Text = string.Format(Strings.Get("AboutVersionFormat"), AppVersion());

        WebsiteLink.ToolTip = OpensInBrowserTooltip(SupportNavigationPolicy.WebsiteUrl);
        SoftwareLink.ToolTip = OpensInBrowserTooltip(SupportNavigationPolicy.SoftwareUrl);
        PrivacyLink.ToolTip = OpensInBrowserTooltip(SupportNavigationPolicy.PrivacyUrl);
        SupportBrowserBtn.ToolTip = OpensInBrowserTooltip(SupportNavigationPolicy.SupportUrl);
        SupportErrorBrowserBtn.ToolTip = SupportBrowserBtn.ToolTip;

        Loaded += (_, _) => ContactSupportBtn.Focus();
        Closed += (_, _) => { _webView?.Dispose(); _webView = null; };
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

    // ---------- Support side ----------

    private void ContactSupport_Click(object sender, RoutedEventArgs e) => OpenSupportPane();

    internal void OpenSupportPane()
    {
        AboutPanel.Visibility = Visibility.Collapsed;
        SupportPanel.Visibility = Visibility.Visible;
        BackBtn.Visibility = Visibility.Visible;

        if (_supportLoadedOnce && _webView is not null)
        {
            ShowSupportState(SupportState.Loaded);
        }
        else
        {
            ShowSupportState(SupportState.Consent);
            SupportLoadBtn.Focus();
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        SupportPanel.Visibility = Visibility.Collapsed;
        BackBtn.Visibility = Visibility.Collapsed;
        AboutPanel.Visibility = Visibility.Visible;
        ContactSupportBtn.Focus();
    }

    private void LoadSupport_Click(object sender, RoutedEventArgs e) => _ = LoadSupportAsync();
    private void RetrySupport_Click(object sender, RoutedEventArgs e) => _ = LoadSupportAsync();

    private void OpenSupportInBrowser_Click(object sender, RoutedEventArgs e) =>
        OpenExternal(SupportNavigationPolicy.SupportUrl);

    /// <summary>Creates the WebView2 on first use and navigates to the support form.
    /// Every failure mode (runtime missing, environment/init failure, network) lands in
    /// the Failed state, which offers retry and the default-browser fallback.</summary>
    internal async Task LoadSupportAsync()
    {
        ShowSupportState(SupportState.Loading);
        try
        {
            if (SimulateWebViewInitFailure)
                throw new InvalidOperationException("Simulated WebView2 initialization failure (test seam).");

            if (_webView is null)
            {
                // The packaged app must not write next to its exe; keep the browser
                // profile under LocalAppData like any other per-user state.
                var dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PdfLiteViewer", "WebView2");
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDir);

                var view = new WebView2();
                WebViewHost.Child = view;
                await view.EnsureCoreWebView2Async(environment);
                ConfigureWebView(view.CoreWebView2);
                _webView = view;
            }

            _webView.CoreWebView2.Navigate(SupportNavigationPolicy.SupportUrl);
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            App.LogError(ex);
            ShowSupportError(Strings.Get("SupportRuntimeMissing"));
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            DropWebView();
            ShowSupportError(Strings.Get("SupportLoadFailed"));
        }
    }

    private void ConfigureWebView(CoreWebView2 core)
    {
        var s = core.Settings;
        s.AreHostObjectsAllowed = false;       // no native bridge, ever
        s.IsWebMessageEnabled = false;         // and no postMessage channel either
        s.AreDevToolsEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsGeneralAutofillEnabled = false;    // a support form is no place to bank
        s.IsPasswordAutosaveEnabled = false;   // saved credentials

        core.NavigationStarting += Core_NavigationStarting;
        core.NewWindowRequested += Core_NewWindowRequested;
        core.DownloadStarting += (_, e) => { e.Cancel = true; e.Handled = true; };
        core.NavigationCompleted += Core_NavigationCompleted;
        core.ProcessFailed += Core_ProcessFailed;
    }

    private void Core_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        switch (SupportNavigationPolicy.Decide(e.Uri))
        {
            case NavigationDecision.AllowInView:
                break;
            case NavigationDecision.OpenInBrowser:
                e.Cancel = true;
                OpenExternal(e.Uri);
                break;
            default:
                e.Cancel = true;
                break;
        }
    }

    private void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;   // popups never open; the target either navigates in place or leaves
        switch (SupportNavigationPolicy.Decide(e.Uri))
        {
            case NavigationDecision.AllowInView:
                _webView?.CoreWebView2.Navigate(e.Uri);
                break;
            case NavigationDecision.OpenInBrowser:
                OpenExternal(e.Uri);
                break;
        }
    }

    private void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            _supportLoadedOnce = true;
            ShowSupportState(SupportState.Loaded);
        }
        else if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
        {
            // Offline, DNS failure, TLS failure, server error — all land here.
            ShowSupportError(Strings.Get("SupportLoadFailed"));
        }
    }

    private void Core_ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        // The browser process died under the form; drop the control so retry rebuilds it.
        DropWebView();
        ShowSupportError(Strings.Get("SupportLoadFailed"));
    }

    private void DropWebView()
    {
        WebViewHost.Child = null;
        _webView?.Dispose();
        _webView = null;
        _supportLoadedOnce = false;
    }

    private void ShowSupportState(SupportState state)
    {
        SupportConsentPanel.Visibility = state == SupportState.Consent ? Visibility.Visible : Visibility.Collapsed;
        SupportStatusPanel.Visibility = state is SupportState.Loading or SupportState.Failed
            ? Visibility.Visible : Visibility.Collapsed;
        SupportErrorButtons.Visibility = state == SupportState.Failed ? Visibility.Visible : Visibility.Collapsed;
        WebViewHost.Visibility = state == SupportState.Loaded ? Visibility.Visible : Visibility.Collapsed;
        if (state == SupportState.Loading)
            SupportStatusText.Text = Strings.Get("SupportLoading");
    }

    private void ShowSupportError(string message)
    {
        ShowSupportState(SupportState.Failed);
        SupportStatusText.Text = message;
        SupportRetryBtn.Focus();
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
