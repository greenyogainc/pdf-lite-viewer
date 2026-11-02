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

    /// <summary>The one call that hands a document to the spooler. tools/HangProbe replaces it first thing, so no check can reach a real printer.</summary>
    internal static Action<PrintQueue, PdfPrintPaginator, PrintTicket> Submit = SubmitToSpooler;

    /// <summary>Test seam: tools/HangProbe substitutes a server that fails every call. Straight to the real one in production.</summary>
    internal static Func<LocalPrintServer> NewServer = () => new LocalPrintServer();

    /// <summary>Test seam for the default-printer lookup. Straight to the real one in production.</summary>
    internal static Func<PrintQueue> DefaultQueue = LocalPrintServer.GetDefaultPrintQueue;

    private static void SubmitToSpooler(PrintQueue queue, PdfPrintPaginator paginator, PrintTicket ticket) =>
        PrintQueue.CreateXpsDocumentWriter(queue).Write(paginator, ticket);

    /// <summary>Queue names plus the default one. Call from a worker thread.</summary>
    public static (List<string> Names, string? Default) EnumerateQueues()
    {
        try
        {
            using var server = NewServer();
            // Every PrintQueue wraps a spooler handle, and so does the collection: take the
            // names and let them all go.
            var names = new List<string>();
            using var queues = server.GetPrintQueues(QueueTypes);
            foreach (var queue in queues)
                using (queue) names.Add(queue.FullName);

            string? defaultName = null;
            try { using var def = DefaultQueue(); defaultName = def.FullName; } catch { }

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
            using var server = NewServer();
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
    internal static Size PaperFor(PrintTicket? ticket)
    {
        if (ticket?.PageMediaSize is not { Width: double w, Height: double h } || w < 50 || h < 50)
            return FallbackPaper;

        return ticket.PageOrientation is PageOrientation.Landscape or PageOrientation.ReverseLandscape
            ? new Size(h, w)
            : new Size(w, h);
    }

    /// <summary>
    /// Sends the job. The returned task completes when the spooler has the whole document,
    /// or the supplied <paramref name="cancellationToken"/> is signaled (in which case the
    /// paginator throws <see cref="OperationCanceledException"/> between pages and the
    /// print thread unwinds cleanly).
    /// </summary>
    public static Task RunAsync(PdfDoc doc, IReadOnlyList<int> pages, string queueName,
        int copies, bool grayscale, bool draft, string jobName,
        CancellationToken cancellationToken = default)
    {
        // Snapshot on the calling (UI) thread: the preview can be closed and the view rotated
        // while the job is still producing pages on its own thread, and every sheet must
        // match what the preview showed.
        var rotation = doc.Rotation;
        return RunOnStaThread(() =>
        {
            using var server = NewServer();
            using var queue = ResolveQueue(server, queueName)
                ?? throw new InvalidOperationException($"Printer '{queueName}' is unavailable.");

            var ticket = FirstTicket(() => queue.UserPrintTicket, () => queue.DefaultPrintTicket);
            ticket.CopyCount = copies;
            if (grayscale) ticket.OutputColor = OutputColor.Grayscale;
            if (draft) ticket.OutputQuality = OutputQuality.Draft;

            queue.CurrentJobSettings.Description = jobName;

            Submit(queue, new PdfPrintPaginator(doc, pages, PaperFor(ticket), rotation)
            {
                CancellationToken = cancellationToken
            }, ticket);
        }, cancellationToken);
    }

    /// <summary>
    /// Same threading and pagination path as <see cref="RunAsync"/>, writing to an XPS file
    /// instead of a queue. Used by tools/HangProbe to exercise print rendering without a printer.
    /// </summary>
    internal static Task WriteXpsAsync(PdfDoc doc, IReadOnlyList<int> pages, Size paper, string path,
        CancellationToken cancellationToken = default)
    {
        var rotation = doc.Rotation;
        return RunOnStaThread(() =>
        {
            File.Delete(path);
            using var xps = new XpsDocument(path, FileAccess.ReadWrite);
            XpsDocument.CreateXpsDocumentWriter(xps).Write(new PdfPrintPaginator(doc, pages, paper, rotation)
            {
                CancellationToken = cancellationToken
            });
            xps.Close();
        }, cancellationToken);
    }

    internal static PrintQueue? ResolveQueue(PrintServer server, string name)
    {
        try { return server.GetPrintQueue(name); }
        catch { /* fall through to a full enumeration */ }

        try
        {
            // The collection itself is left to the finalizer here: disposing it may dispose
            // the match being handed back, and this path only runs when GetPrintQueue failed.
            return PickQueue(server.GetPrintQueues(QueueTypes), queue => queue.FullName, name);
        }
        catch { return null; }
    }

    /// <summary>
    /// The first queue called <paramref name="name"/>, with every other one disposed. Only a
    /// print-server connection ever gets here, so it takes the queues as data: tools/HangProbe
    /// can check it on a machine that has no such connection.
    /// </summary>
    internal static T? PickQueue<T>(IEnumerable<T> queues, Func<T, string> nameOf, string name)
        where T : class, IDisposable
    {
        T? match = null;
        foreach (var queue in queues)
        {
            if (match is null && nameOf(queue) == name) match = queue;
            else queue.Dispose();
        }
        return match;
    }

    /// <summary>The queue's user ticket, else its default one, else a blank one.</summary>
    internal static PrintTicket FirstTicket(Func<PrintTicket?> user, Func<PrintTicket?> queueDefault) =>
        user() ?? queueDefault() ?? new PrintTicket();

    /// <summary>
    /// XPS serialization needs an STA thread, and every visual it serializes must belong to
    /// that thread — so the job gets a thread of its own rather than borrowing a pool one.
    /// </summary>
    private static Task RunOnStaThread(Action work, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    return;
                }
                work();
                tcs.SetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
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
