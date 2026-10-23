using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using GreenYoga.Support;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using PdfLiteViewer;

namespace HangProbe;

/// <summary>
/// Regression checks for native support, accessibility, RTL, validation and delivery states.
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

    private static IEnumerable<Check> PolicyChecks()
    {
        yield return new Check("external-shell gate takes only web URLs",
            SupportNavigationPolicy.IsSafeExternalUrl("https://example.com") &&
            SupportNavigationPolicy.IsSafeExternalUrl("http://example.com") &&
            !SupportNavigationPolicy.IsSafeExternalUrl(null) &&
            !SupportNavigationPolicy.IsSafeExternalUrl("file:///C:/x") &&
            !SupportNavigationPolicy.IsSafeExternalUrl("javascript:alert(1)"), "About links remain gated");
    }

    // ---------- AboutWindow (UI) ----------

    private static async Task<List<Check>> WindowChecksAsync()
    {
        var checks = new List<Check>();
        int posts = 0;
        var pending = new TaskCompletionSource<HttpResponseMessage>();
        var handler = new SupportReply(async (request, ct) => {
            posts++;
            if (posts == 1) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
            return await pending.Task.WaitAsync(ct);
        });
        var about = new AboutWindow(new AppSupportClient(new HttpClient(handler)));
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

            about.LicenseToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await PumpAsync();
            checks.Add(new Check("about: license hides again",
                about.LicenseText.Visibility == Visibility.Collapsed
                && about.LicenseToggle.Content as string == Strings.Get("AboutViewLicense"),
                $"visibility={about.LicenseText.Visibility}, toggle='{about.LicenseToggle.Content}'"));

            about.OpenSupportPane();
            await PumpAsync();
            checks.Add(new Check("support: native form opens offline", posts == 0 &&
                about.SupportForm.Visibility == Visibility.Visible && about.SupportNameInput.IsFocused,
                "opening support performs no HTTP request"));
            checks.Add(new Check("support: Send visible at default window size",
                IsInSupportViewport(about, about.SupportSendBtn),
                $"window {about.ActualWidth}x{about.ActualHeight}, viewport {about.SupportPanel.ViewportHeight:F1}"));
            var defaultHeight = about.Height;
            about.Height = 620;
            about.SupportPanel.ScrollToBottom();
            await PumpAsync();
            await about.SendSupportAsync();
            await PumpAsync();
            checks.Add(new Check("support: invalid field visible after bottom submission",
                about.SupportNameInput.IsKeyboardFocused && IsInSupportViewport(about, about.SupportNameInput),
                $"focused={about.SupportNameInput.IsKeyboardFocused}, offset={about.SupportPanel.VerticalOffset:F1}"));
            about.Height = defaultHeight;
            await PumpAsync();
            checks.Add(new Check("support: required fields prevent submission", posts == 0 &&
                about.SupportStatusText.Text == Strings.Get("SupportRequired"), "invalid form stays local"));
            about.SupportNameInput.Text = "Ada";
            about.SupportEmailInput.Text = "ada@example.com";
            about.SupportSubjectInput.Text = "Question";
            about.SupportMessageInput.Text = "Please help.";
            about.BackBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            about.OpenSupportPane();
            checks.Add(new Check("support: back preserves draft", about.SupportMessageInput.Text == "Please help.", "draft retained"));
            var id = about.CurrentSupportRequest().RequestId;
            await about.SendSupportAsync();
            checks.Add(new Check("support: failure retains fields and request id",
                !about.SupportSubmitted && about.SupportMessageInput.Text == "Please help." &&
                about.CurrentSupportRequest().RequestId == id && about.SupportSendBtn.IsEnabled,
                "manual retry uses the original id"));
            var sending = about.SendSupportAsync();
            await about.SendSupportAsync();
            checks.Add(new Check("support: double sends blocked", posts == 2 && !about.SupportForm.IsEnabled,
                "one HTTP request is in flight"));
            pending.SetResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(JsonSerializer.Serialize(new { success = true, requestId = id })) });
            await sending;
            checks.Add(new Check("support: matching acknowledgment shows reference", about.SupportSubmitted &&
                about.SupportStatusText.Text.Contains(id) && about.SupportAnotherBtn.Visibility == Visibility.Visible,
                "success requires the request reference"));
            about.SupportAnotherBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            checks.Add(new Check("support: send another starts a new draft", !about.SupportSubmitted &&
                about.SupportMessageInput.Text.Length == 0 && about.CurrentSupportRequest().RequestId != id, "fresh draft"));

        }
        finally
        {
            about.Close();
        }
        bool cancelledOnClose = false;
        var closing = new AboutWindow(new AppSupportClient(new HttpClient(new SupportReply(async (_, token) => {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelledOnClose = true; throw; }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }))));
        closing.Show();
        closing.OpenSupportPane();
        closing.SupportNameInput.Text = "Ada";
        closing.SupportEmailInput.Text = "ada@example.com";
        closing.SupportSubjectInput.Text = "Question";
        closing.SupportMessageInput.Text = "Help please.";
        var closingSend = closing.SendSupportAsync();
        closing.Close();
        await closingSend;
        checks.Add(new Check("support: close cancels safely", cancelledOnClose, "in-flight send cancelled without touching closed controls"));
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

    private sealed class SupportReply(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => reply(request, token);
    }

    private static bool IsInSupportViewport(AboutWindow window, FrameworkElement element)
    {
        var bounds = element.TransformToAncestor(window.SupportPanel)
            .TransformBounds(new Rect(element.RenderSize));
        return new Rect(0, 0, window.SupportPanel.ViewportWidth, window.SupportPanel.ViewportHeight).Contains(bounds);
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(120);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    internal static IEnumerable<T> FindChildren<T>(DependencyObject root) where T : DependencyObject
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
