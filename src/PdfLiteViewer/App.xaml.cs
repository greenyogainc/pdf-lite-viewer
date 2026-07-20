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

        base.OnStartup(e);
    }
}
