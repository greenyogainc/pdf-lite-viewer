using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Xps;
using System.Windows.Xps.Packaging;

namespace PdfLiteViewer;

/// <summary>
/// Everything that talks to the print spooler, kept off the UI thread.
///
/// Both halves used to run on it: enumerating queues contacts every print server the
/// machine knows about (an offline network printer stalls that for as long as the RPC
/// takes to give up), and the job itself renders every selected page at 300 DPI before
/// it returns. On a long document that is minutes of frozen window.
///
/// Page generation happens on a dedicated STA thread here. That is safe because the
/// visuals the paginator builds are created and consumed on that same thread, and the
/// page bitmaps are frozen, so nothing is touched from two threads at once.
/// </summary>
internal static class PrintJob
{
    private static readonly EnumeratedPrintQueueTypes[] QueueTypes =
    {
        EnumeratedPrintQueueTypes.Local,
        EnumeratedPrintQueueTypes.Connections,
    };

    /// <summary>Letter, in device-independent pixels — used when the spooler tells us nothing.</summary>
    public static readonly Size FallbackPaper = new(816, 1056);

    /// <summary>Queue names plus the default one. Call from a worker thread.</summary>
    public static (List<string> Names, string? Default) EnumerateQueues()
    {
        try
        {
            using var server = new LocalPrintServer();
            var names = server.GetPrintQueues(QueueTypes).Select(q => q.FullName).ToList();

            string? defaultName = null;
            try { defaultName = LocalPrintServer.GetDefaultPrintQueue().FullName; } catch { }

            return (names, defaultName);
        }
        catch
        {
            return (new List<string>(), null);   // no print system available
        }
    }

    /// <summary>Paper size for a queue, in device-independent pixels. Call from a worker thread.</summary>
    public static Size PaperFor(string queueName)
    {
        try
        {
            using var server = new LocalPrintServer();
            using var queue = ResolveQueue(server, queueName);
            return queue is null ? FallbackPaper : PaperFor(queue.UserPrintTicket);
        }
        catch
        {
            return FallbackPaper;
        }
    }

    /// <summary>
    /// Media size in DIPs, matching what <c>PrintDialog.PrintableArea*</c> reports, so the
    /// preview and the printed sheet place the page image identically.
    /// </summary>
    private static Size PaperFor(PrintTicket? ticket)
    {
        var media = ticket?.PageMediaSize;
        if (media?.Width is not double w || media.Height is not double h || w < 50 || h < 50)
            return FallbackPaper;

        return ticket?.PageOrientation is PageOrientation.Landscape or PageOrientation.ReverseLandscape
            ? new Size(h, w)
            : new Size(w, h);
    }

    /// <summary>Sends the job. The returned task completes when the spooler has the whole document.</summary>
    public static Task RunAsync(PdfDoc doc, IReadOnlyList<int> pages, string queueName,
        int copies, bool grayscale, bool draft, string jobName) =>
        RunOnStaThread(() =>
        {
            using var server = new LocalPrintServer();
            using var queue = ResolveQueue(server, queueName)
                ?? throw new InvalidOperationException($"Printer '{queueName}' is unavailable.");

            var ticket = queue.UserPrintTicket ?? queue.DefaultPrintTicket ?? new PrintTicket();
            ticket.CopyCount = copies;
            if (grayscale) ticket.OutputColor = OutputColor.Grayscale;
            if (draft) ticket.OutputQuality = OutputQuality.Draft;

            queue.CurrentJobSettings.Description = jobName;

            var writer = PrintQueue.CreateXpsDocumentWriter(queue);
            writer.Write(new PdfPrintPaginator(doc, pages, PaperFor(ticket)), ticket);
        });

    /// <summary>
    /// Same threading and pagination path as <see cref="RunAsync"/>, writing to an XPS file
    /// instead of a queue. Used by tools/HangProbe to exercise print rendering without a printer.
    /// </summary>
    internal static Task WriteXpsAsync(PdfDoc doc, IReadOnlyList<int> pages, Size paper, string path) =>
        RunOnStaThread(() =>
        {
            File.Delete(path);
            using var xps = new XpsDocument(path, FileAccess.ReadWrite);
            XpsDocument.CreateXpsDocumentWriter(xps).Write(new PdfPrintPaginator(doc, pages, paper));
            xps.Close();
        });

    private static PrintQueue? ResolveQueue(PrintServer server, string name)
    {
        try { return server.GetPrintQueue(name); }
        catch { /* fall through to a full enumeration */ }

        try { return server.GetPrintQueues(QueueTypes).FirstOrDefault(q => q.FullName == name); }
        catch { return null; }
    }

    /// <summary>
    /// XPS serialization needs an STA thread, and every visual it serializes must belong to
    /// that thread — so the job gets a thread of its own rather than borrowing a pool one.
    /// </summary>
    private static Task RunOnStaThread(Action work)
    {
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                work();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
            finally
            {
                // Serializing visuals can create a dispatcher on this thread; without this
                // the thread would stay alive waiting for messages that never come.
                System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown();
            }
        })
        {
            Name = "pdf-print",
            IsBackground = false,   // a job in flight must survive the preview window closing
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
