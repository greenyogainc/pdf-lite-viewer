using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;

namespace PdfLiteViewer;

public partial class App : Application
{
    /// <summary>PDF passed on the command line (e.g. via "Open with" / file association).</summary>
    public string? StartupFile { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        foreach (var arg in e.Args)
        {
            // Hidden override for support/screenshots — forces UI culture regardless of OS language.
            if (arg.StartsWith("--lang=", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var culture = new CultureInfo(arg["--lang=".Length..]);
                    // Set both the startup thread and the defaults used by thread-pool
                    // workers (chapter extraction, etc.) so --lang= applies app-wide.
                    Thread.CurrentThread.CurrentUICulture = culture;
                    CultureInfo.DefaultThreadCurrentUICulture = culture;
                }
                catch (CultureNotFoundException) { }
            }
            else if (StartupFile is null && !arg.StartsWith("--", StringComparison.Ordinal))
            {
                // Taken as given, even when it does not exist: the window's open path
                // reports a missing or unreadable file, whereas skipping it here started
                // the app silently empty after a double-click on a PDF that had just moved.
                StartupFile = arg;
            }
        }

        DispatcherUnhandledException += (_, args) =>
        {
            LogError(args.Exception);
            Strings.ShowError(null, string.Format(Strings.Get("UnhandledErrorMessage"), args.Exception.Message, LogPath));
            args.Handled = true;   // keep the viewer alive
        };

        base.OnStartup(e);
    }

    private static string LogPath =>
        Path.Combine(Path.GetTempPath(), "PdfLiteViewer.log");

    internal static void LogError(Exception ex)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
