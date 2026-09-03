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
            else if (StartupFile is null && IsDocumentArgument(arg))
            {
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

    /// <summary>
    /// Whether a command-line token names the document to open. A .pdf path counts even when
    /// it no longer exists: the window's open path then reports the missing file, whereas
    /// dropping it here started the app silently empty after a double-click on a PDF that had
    /// just moved. Anything else that is not an existing file is ignored - tools/HangProbe and
    /// tools/StoreShots run this same App with a page count or an output directory as their
    /// first argument. Pure and static so the probe can exercise it directly.
    /// </summary>
    internal static bool IsDocumentArgument(string arg) =>
        !arg.StartsWith('-') &&
        (File.Exists(arg) || arg.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

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
