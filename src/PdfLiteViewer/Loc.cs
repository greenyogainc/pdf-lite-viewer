using System.Globalization;
using System.Resources;
using System.Windows;
using System.Windows.Markup;

namespace PdfLiteViewer;

/// <summary>Thin wrapper so code-behind and XAML share one lookup path into Strings.resx.</summary>
internal static class Strings
{
    private static readonly ResourceManager Manager =
        new("PdfLiteViewer.Strings", typeof(Strings).Assembly);

    /// <summary>Two-letter codes of the RTL languages we actually ship translations for.
    /// Layout only mirrors for these — an untranslated RTL OS (he/fa/ur) would otherwise
    /// get a backwards layout wrapped around English fallback text. Add a code here when
    /// an RTL translation is added.</summary>
    private static readonly HashSet<string> RightToLeftLanguages =
        new(StringComparer.OrdinalIgnoreCase) { "ar" };

    public static string Get(string key) => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static bool IsRightToLeft =>
        CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft &&
        RightToLeftLanguages.Contains(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

    /// <summary>Options for native MessageBox.Show so RTL dialogs fully mirror (read direction + alignment).</summary>
    public static MessageBoxOptions MessageBoxOptions =>
        IsRightToLeft ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None;

    /// <summary>Applies the culture's reading direction to a window: RTL mirroring plus the
    /// xml:lang that drives WPF's language-aware font/glyph selection (e.g. correct Han
    /// variants for zh-Hans vs zh-Hant, which otherwise fall back to a Japanese font).</summary>
    public static void ApplyFlowDirection(Window window)
    {
        window.FlowDirection = IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        window.Language = XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag);
    }

    /// <summary>Shows a localized error dialog with the app title and RTL-aware options.</summary>
    public static void ShowError(Window? owner, string message)
    {
        var caption = Get("AppTitle");
        if (owner is null)
            MessageBox.Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.OK, MessageBoxOptions);
        else
            MessageBox.Show(owner, message, caption, MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.OK, MessageBoxOptions);
    }
}

/// <summary>XAML markup extension: {local:Loc KeyName} resolves via Strings.resx at load time.</summary>
[MarkupExtensionReturnType(typeof(string))]
public class LocExtension : MarkupExtension
{
    public string Key { get; }

    public LocExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
}
