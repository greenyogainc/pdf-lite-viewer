using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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

        // Non-UI thread exceptions (a Task.Run body that throws past an await, a thread-pool
        // worker that faults) would otherwise terminate the process via UnhandledException
        // with no diagnostic. Logging here gives support something to look at, even if the
        // shutdown that follows is the OS handling a crash that we could not contain.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                LogError(ex);
            else
                LogError(new Exception($"Non-Exception throw on background thread: {args.ExceptionObject}"));
        };

        // Tasks that fault and are never observed (.Value, await, or Exception) escalate
        // to process termination on .NET 10. Subscribe and observe: log everything we see
        // so a flaky renderer cannot take the viewer down unnoticed.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogError(args.Exception);
            args.SetObserved();
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

    // Serializes concurrent AppendAllText calls (File.AppendAllText opens, seeks, writes,
    // and closes on every invocation; two threads racing on the same file would interleave
    // bytes inside an open/write/close cycle and corrupt the log).
    private static readonly object _logLock = new();

    internal static void LogError(Exception ex)
    {
        try
        {
            lock (_logLock)
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
