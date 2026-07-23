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
                    Thread.CurrentThread.CurrentUICulture = new CultureInfo(arg["--lang=".Length..]);
                }
                catch (CultureNotFoundException) { }
            }
            else if (StartupFile is null && File.Exists(arg))
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

    private static string LogPath =>
        Path.Combine(Path.GetTempPath(), "PdfLiteViewer.log");

    private static void LogError(Exception ex)
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
