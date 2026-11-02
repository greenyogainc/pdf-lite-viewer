using System.IO;
using System.Printing;
using System.Text;
using PdfLiteViewer;

namespace HangProbe;

/// <summary>
/// The one check that lets a job through the real spooler call, run only with
/// <c>--real-print-to-pdf</c>. It proves the production path produces a PDF without using paper:
/// the only queue it will talk to is "Microsoft Print to PDF", confirmed by name and by its
/// prompt-for-a-file port, and the delegate refuses anything else. Every other HangProbe mode
/// keeps the throwing guard, because this machine's default printer is a physical one.
/// </summary>
internal static class RealPrintCheck
{
    private const string PromptPort = "PORTPROMPT:";
    // The Save As caption is OS text and changes with the Windows language. This mode opens no
    // other dialog, so the expectation matches whatever appears.
    private const string AnyCaption = "";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    public static async Task<List<Check>> RunAsync(Action<PrintQueue, PdfPrintPaginator, PrintTicket> spooler)
    {
        const string name = "print: a real job reaches the virtual PDF printer";
        var guard = PrintJob.Submit;
        var target = Path.Combine(Path.GetTempPath(), $"hangprobe-real-print-{Guid.NewGuid():N}.pdf");
        try
        {
            string? port = await Task.Run(PortOfPdfQueue);
            if (port is null)
                return new List<Check> { Skipped(name, $"'{PdfQueue}' is not installed on this machine") };
            if (port != PromptPort)
                return new List<Check> { new(name, false, $"'{PdfQueue}' is on port '{port}', not {PromptPort}; refusing to print to it") };

            var doc = new PdfDoc(PdfFixtures.RepoFixture("no-outline.pdf"));
            PrintJob.Submit = (queue, paginator, ticket) =>
            {
                if (queue.FullName != PdfQueue || queue.QueuePort.Name != PromptPort)
                    throw new InvalidOperationException($"HangProbe refuses to print to '{queue.FullName}'.");
                spooler(queue, paginator, ticket);
            };

            using var dialog = Program.Dialogs.Expect(AnyCaption, DialogAction.FillPath, path: target);
            var job = PrintJob.RunAsync(doc, new[] { 0 }, PdfQueue, 1, grayscale: false, draft: false, "HangProbe real print");
            if (await Task.WhenAny(job, Task.Delay(Limit)) != job)
            {
                // The job's foreground thread would keep this process alive, and coverlet only
                // writes its results when the process exits.
                Console.Error.WriteLine($"The real print job did not finish within {Limit.TotalSeconds:F0}s; ending the run.");
                Environment.Exit(3);
            }
            await job;

            bool written = await Poll.UntilAsync(() => IsPdf(target), Limit);
            return new List<Check>
            {
                dialog.Verify("print: the virtual printer's Save As dialog was answered"),
                new(name, written, written ? $"{new FileInfo(target).Length} byte PDF written" : "no PDF appeared at the chosen path"),
            };
        }
        finally
        {
            PrintJob.Submit = guard;
            try { File.Delete(target); } catch (IOException) { /* a temp file the spooler still holds */ }
        }
    }

    private static string? PortOfPdfQueue()
    {
        using var server = new LocalPrintServer();
        using var queue = PrintJob.ResolveQueue(server, PdfQueue);
        return queue?.QueuePort.Name;
    }

    private static bool IsPdf(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var header = new byte[5];
            return file.Read(header, 0, header.Length) == header.Length && Encoding.ASCII.GetString(header) == "%PDF-";
        }
        catch (IOException)
        {
            return false;       // not there yet, or the spooler still has it open
        }
    }
}
