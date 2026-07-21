using System.IO;
using System.Windows;

namespace PdfLiteViewer;

public partial class App : Application
{
    /// <summary>PDF passed on the command line (e.g. via "Open with" / file association).</summary>
    public string? StartupFile { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0 && File.Exists(e.Args[0]))
            StartupFile = e.Args[0];

        DispatcherUnhandledException += (_, args) =>
        {
            LogError(args.Exception);
            MessageBox.Show(
                $"Unexpected error:\n\n{args.Exception.Message}\n\nDetails logged to:\n{LogPath}",
                "PDF Lite Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
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
