using System.IO;
using System.Printing;
using System.Text;
using System.Windows;
using PDFtoImage;
using PdfLiteViewer;
using static HangProbe.PdfFixtures;

namespace HangProbe;

/// <summary>
/// Guards <see cref="PrintJob"/> and the paginator around the one call that must never run
/// here - handing a document to the spooler.
///
/// This machine's default printer is a physical one, so <c>PrintJob.Submit</c> is replaced by
/// a throwing guard when the probe starts. A check below may swap in a recording delegate for
/// the length of one job, restores the guard in a <c>finally</c>, and first confirms the guard
/// is in place. The only real queue any check names is the literal "Microsoft Print to PDF",
/// and only to read its ticket: no queue name taken from the enumeration is ever submitted
/// to, and the recorder never calls the real submit.
/// </summary>
internal static class PrintSpoolerChecks
{
    public static async Task<List<Check>> RunAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"hangprobe-spooler-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            string At(string name) => Path.Combine(folder, name);
            var checks = new List<Check>
            {
                CheckKit.Run("spooler: paper size from a print ticket", PaperFromTickets),
                await CheckKit.RunAsync("spooler: resolving a queue", ResolveQueueAsync),
                CheckKit.Run("spooler: a queue found by walking the list", PickQueueTable),
                CheckKit.Run("spooler: the job's ticket falls back in order", TicketFallback),
                await CheckKit.RunAsync("spooler: no print system at all", NoPrintSystemAsync),
                await CheckKit.RunAsync("spooler: no default printer", NoDefaultPrinterAsync),
                await CheckKit.RunAsync("spooler: an unavailable queue faults the job", () => UnavailableQueueAsync(At)),
                await CheckKit.RunAsync("spooler: the job hands the spooler what was asked for", () => RecordedJobsAsync(At)),
                await CheckKit.RunAsync("spooler: a job cancelled before it starts is cancelled", () => CancelledBeforeStartAsync(At)),
                await CheckKit.RunAsync("spooler: a job cancelled while paginating is cancelled", () => CancelledWhilePaginatingAsync(At)),
                await CheckKit.RunAsync("spooler: an XPS write cancelled after it started is cancelled", () => XpsCancelledMidJobAsync(At)),
                await CheckKit.RunAsync("spooler: an XPS write that fails surfaces its exception", () => XpsFailureAsync(At)),
                CheckKit.Run("spooler: the paginator reports its pages and size", () => PaginatorProperties(At)),
            };
            return checks;
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* temp files */ }
        }
    }

    // ---------- paper size ----------

    private static Check PaperFromTickets()
    {
        const string name = "spooler: paper size from a print ticket";
        var fallback = PrintJob.FallbackPaper;
        PrintTicket Ticket(double? width, double? height, PageOrientation? orientation)
        {
            var ticket = new PrintTicket();
            if (width is { } w && height is { } h) ticket.PageMediaSize = new PageMediaSize(w, h);
            if (orientation is { } o) ticket.PageOrientation = o;
            return ticket;
        }

        var cases = new List<(string Label, PrintTicket? Ticket, Size Expected)>
        {
            ("no ticket", null, fallback),
            ("a ticket with no media size", Ticket(null, null, null), fallback),
            ("a ticket with only an orientation", Ticket(null, null, PageOrientation.Landscape), fallback),
            ("a named size without dimensions", new PrintTicket { PageMediaSize = new PageMediaSize(PageMediaSizeName.NorthAmericaLetter) }, fallback),
            ("width under 50", Ticket(49, 1000, null), fallback),
            ("height under 50", Ticket(1000, 49.9, null), fallback),
            ("both under 50", Ticket(10, 10, null), fallback),
            ("exactly 50 by 50", Ticket(50, 50, null), new Size(50, 50)),
            ("portrait by default", Ticket(816, 1056, null), new Size(816, 1056)),
            ("portrait", Ticket(816, 1056, PageOrientation.Portrait), new Size(816, 1056)),
            ("reverse portrait", Ticket(816, 1056, PageOrientation.ReversePortrait), new Size(816, 1056)),
            ("unknown orientation", Ticket(816, 1056, PageOrientation.Unknown), new Size(816, 1056)),
            ("landscape swaps the sides", Ticket(816, 1056, PageOrientation.Landscape), new Size(1056, 816)),
            ("reverse landscape swaps the sides", Ticket(816, 1056, PageOrientation.ReverseLandscape), new Size(1056, 816)),
        };

        // A ticket read from XML can carry one dimension without the other, which the
        // constructors cannot produce.
        var a4Width = FromXml("""<psf:Feature name="psk:PageMediaSize"><psf:Option><psf:ScoredProperty name="psk:MediaSizeWidth"><psf:Value xsi:type="xsd:integer">210000</psf:Value></psf:ScoredProperty></psf:Option></psf:Feature>""");
        var a4Height = FromXml("""<psf:Feature name="psk:PageMediaSize"><psf:Option><psf:ScoredProperty name="psk:MediaSizeHeight"><psf:Value xsi:type="xsd:integer">297000</psf:Value></psf:ScoredProperty></psf:Option></psf:Feature>""");
        var a4 = FromXml("""
            <psf:Feature name="psk:PageMediaSize"><psf:Option>
              <psf:ScoredProperty name="psk:MediaSizeWidth"><psf:Value xsi:type="xsd:integer">210000</psf:Value></psf:ScoredProperty>
              <psf:ScoredProperty name="psk:MediaSizeHeight"><psf:Value xsi:type="xsd:integer">297000</psf:Value></psf:ScoredProperty>
            </psf:Option></psf:Feature>
            <psf:Feature name="psk:PageOrientation"><psf:Option name="psk:Landscape"/></psf:Feature>
            """);
        cases.Add(("XML: width only", a4Width, fallback));
        cases.Add(("XML: height only", a4Height, fallback));

        var wrong = new List<string>();
        foreach (var (label, ticket, expected) in cases)
        {
            var got = PrintJob.PaperFor(ticket);
            if (got != expected) wrong.Add($"{label}: {got.Width:F1}x{got.Height:F1}, expected {expected.Width:F1}x{expected.Height:F1}");
        }

        // A4 landscape from a real ticket: 297 x 210 mm in 1/96 inch.
        var landscape = PrintJob.PaperFor(a4);
        if (Math.Abs(landscape.Width - 297 / 25.4 * 96) > 1 || Math.Abs(landscape.Height - 210 / 25.4 * 96) > 1)
            wrong.Add($"XML: A4 landscape came out {landscape.Width:F1}x{landscape.Height:F1}");
        return CheckKit.Tally(name, cases.Count + 1, wrong);
    }

    private static PrintTicket FromXml(string features)
    {
        var xml = $"""
            <psf:PrintTicket xmlns:psf="http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework"
                             xmlns:psk="http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords"
                             xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                             xmlns:xsd="http://www.w3.org/2001/XMLSchema" version="1">
              {features}
            </psf:PrintTicket>
            """;
        return new PrintTicket(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
    }

    // ---------- queues ----------

    private static async Task<Check> ResolveQueueAsync()
    {
        const string name = "spooler: resolving a queue";
        var wrong = new List<string>();
        string note = "";
        await Task.Run(() =>
        {
            // Disposing a LocalPrintServer does not take it out of service (measured: it still looks
            // queues up and enumerates them), so a disposed one answers like a live one.
            var disposed = new LocalPrintServer();
            disposed.Dispose();
            using (var gone = PrintJob.ResolveQueue(disposed, "no-such-printer-" + Guid.NewGuid().ToString("N")))
                if (gone is not null) wrong.Add("a disposed server resolved a made-up name");

            // A server whose every call fails: the lookup throws, then so does the enumeration, and
            // the answer is "no queue" rather than an exception. A healthy machine has no other way
            // to make PrintServer's non-virtual calls throw, so null stands in for the dead spooler.
            if (PrintJob.ResolveQueue(null!, "anything") is not null) wrong.Add("a failing server resolved a queue");

            // A live server falls back to walking its queues for a name it cannot look up, and finds nothing.
            using var live = new LocalPrintServer();
            var made = "no-such-printer-" + Guid.NewGuid().ToString("N");
            using (var missing = PrintJob.ResolveQueue(live, made))
                if (missing is not null) wrong.Add("a made-up name resolved to a queue");

            if (!PdfQueueExists()) { note = $"'{PdfQueue}' is not installed, so the positive lookup was skipped"; return; }
            using var pdf = PrintJob.ResolveQueue(live, PdfQueue);
            if (pdf is null || pdf.FullName != PdfQueue) wrong.Add($"'{PdfQueue}' resolved to '{pdf?.FullName ?? "nothing"}'");
        });
        return CheckKit.Tally(name, 4, wrong, note.Length == 0 ? null : note);
    }

    private sealed class FakeQueue(string name) : IDisposable
    {
        public string Name { get; } = name;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// A print-server connection is the only thing that reaches this fallback, and this machine
    /// has none: keep the first match open, and release every other queue's spooler handle.
    /// </summary>
    private static Check PickQueueTable()
    {
        const string name = "spooler: a queue found by walking the list";
        var wrong = new List<string>();

        FakeQueue other = new("other"), first = new("wanted"), second = new("wanted"), last = new("last");
        var match = PrintJob.PickQueue(new[] { other, first, second, last }, q => q.Name, "wanted");
        if (!ReferenceEquals(match, first)) wrong.Add("the first matching queue was not the one returned");
        if (first.Disposed) wrong.Add("the returned queue was disposed");
        if (!other.Disposed || !second.Disposed || !last.Disposed) wrong.Add("a queue that was not returned stayed open");

        var unmatched = new FakeQueue("unmatched");
        if (PrintJob.PickQueue(new[] { unmatched }, q => q.Name, "wanted") is not null) wrong.Add("a wrong name matched");
        if (!unmatched.Disposed) wrong.Add("an unmatched queue stayed open");
        if (PrintJob.PickQueue(Array.Empty<FakeQueue>(), q => q.Name, "wanted") is not null) wrong.Add("an empty list matched");

        return CheckKit.Tally(name, 5, wrong);
    }

    private static Check TicketFallback()
    {
        const string name = "spooler: the job's ticket falls back in order";
        var wrong = new List<string>();
        PrintTicket user = new(), queueDefault = new();

        var chosen = PrintJob.FirstTicket(() => user, () => throw new InvalidOperationException("the default ticket was read"));
        if (!ReferenceEquals(chosen, user)) wrong.Add("the user ticket was not preferred");
        if (!ReferenceEquals(PrintJob.FirstTicket(() => null, () => queueDefault), queueDefault)) wrong.Add("no fallback to the queue's default");
        if (PrintJob.FirstTicket(() => null, () => null) is null) wrong.Add("no blank ticket when the queue has neither");

        return CheckKit.Tally(name, 3, wrong);
    }

    private static async Task<Check> NoPrintSystemAsync()
    {
        const string name = "spooler: no print system at all";
        var original = PrintJob.NewServer;
        var wrong = new List<string>();
        try
        {
            PrintJob.NewServer = () => throw new InvalidOperationException("no print system on this machine");
            var (names, defaultName) = await Task.Run(PrintJob.EnumerateQueues);
            if (names.Count != 0) wrong.Add($"{names.Count} queue(s) listed");
            if (defaultName is not null) wrong.Add($"default '{defaultName}'");
            var paper = await Task.Run(() => PrintJob.PaperFor("any queue"));
            if (paper != PrintJob.FallbackPaper) wrong.Add($"paper {paper.Width:F0}x{paper.Height:F0} instead of the fallback");
        }
        finally
        {
            PrintJob.NewServer = original;
        }
        if (!ReferenceEquals(PrintJob.NewServer, original)) wrong.Add("the NewServer seam was not restored");
        return CheckKit.Tally(name, 3, wrong);
    }

    private static async Task<Check> NoDefaultPrinterAsync()
    {
        const string name = "spooler: no default printer";
        var original = PrintJob.DefaultQueue;
        var wrong = new List<string>();
        try
        {
            var real = await Task.Run(PrintJob.EnumerateQueues);
            PrintJob.DefaultQueue = () => throw new InvalidOperationException("no default printer is set");
            var without = await Task.Run(PrintJob.EnumerateQueues);
            if (without.Default is not null) wrong.Add($"default '{without.Default}' reported");
            if (!without.Names.SequenceEqual(real.Names))
                wrong.Add($"names changed: [{string.Join(", ", without.Names)}] vs [{string.Join(", ", real.Names)}]");
        }
        finally
        {
            PrintJob.DefaultQueue = original;
        }
        if (!ReferenceEquals(PrintJob.DefaultQueue, original)) wrong.Add("the DefaultQueue seam was not restored");
        return CheckKit.Tally(name, 3, wrong);
    }

    // ---------- jobs ----------

    private static async Task<Check> UnavailableQueueAsync(Func<string, string> at)
    {
        const string name = "spooler: an unavailable queue faults the job";
        var path = at("unavailable.pdf");
        WriteDocument(path, 1);
        var doc = new PdfDoc(path);
        var queue = "no-such-printer-" + Guid.NewGuid().ToString("N");
        try
        {
            await PrintJob.RunAsync(doc, new[] { 0 }, queue, 1, false, false, "probe job");
            return new Check(name, false, "the job completed");
        }
        catch (OperationCanceledException)
        {
            return new Check(name, false, "the job was reported as cancelled, not failed");
        }
        catch (InvalidOperationException ex)
        {
            // The probe's own guard throws InvalidOperationException too; its message tells them apart.
            bool unavailable = ex.Message.Contains("unavailable") && ex.Message.Contains(queue);
            return new Check(name, unavailable, unavailable ? ex.Message : $"a different InvalidOperationException: {ex.Message}");
        }
    }

    private sealed class Recording
    {
        public int Calls;
        public string? Queue, Description, ThreadName;
        public int? Copies;
        public OutputColor? Color;
        public OutputQuality? Quality;
        public int PageCount;
        public bool PageCountValid;
        public Size PageSize;
        public Rect ContentBox;
        public ApartmentState Apartment;
        public PdfRotation LiveRotation;
    }

    /// <summary>
    /// Runs real jobs against the "Microsoft Print to PDF" queue with the submit call replaced by
    /// a recorder: the queue, ticket and paginator are the real ones, and nothing is sent. The
    /// view is rotated back while the job is held at the recorder, so the sheet geometry it
    /// sees can only be the rotation snapshotted when the job was requested.
    /// </summary>
    private static async Task<Check> RecordedJobsAsync(Func<string, string> at)
    {
        const string name = "spooler: the job hands the spooler what was asked for";
        if (!PdfQueueExists())
            return Skipped(name, $"no '{PdfQueue}' queue on this machine");
        if (!GuardInPlace())
            return new Check(name, false, "the print guard is not in place, so no job was started");

        var path = at("recorded.pdf");
        WriteDocument(path, 3);
        var doc = new PdfDoc(path);
        var guard = PrintJob.Submit;
        var expectedPaper = await Task.Run(() => PrintJob.PaperFor(PdfQueue));

        var cases = new (int Copies, bool Gray, bool Draft, PdfRotation Rotation, int[] Pages)[]
        {
            (1, false, false, PdfRotation.Rotate0, new[] { 0, 1, 2 }),
            (3, true, false, PdfRotation.Rotate90, new[] { 2, 0 }),
            (2, false, true, PdfRotation.Rotate180, new[] { 1 }),
            (5, true, true, PdfRotation.Rotate270, new[] { 0, 1 }),
        };
        var wrong = new List<string>();
        var before = doc.Rotation;
        try
        {
            foreach (var (copies, gray, draft, rotation, pages) in cases)
            {
                string label = $"{copies} copies, gray={gray}, draft={draft}, {rotation}";
                var seen = new Recording();
                using var viewRotated = new ManualResetEventSlim();
                PrintJob.Submit = (queue, paginator, ticket) =>
                {
                    seen.Calls++;
                    seen.Queue = queue.FullName;
                    seen.Description = queue.CurrentJobSettings.Description;
                    seen.Copies = ticket.CopyCount;
                    seen.Color = ticket.OutputColor;
                    seen.Quality = ticket.OutputQuality;
                    seen.PageCount = paginator.PageCount;
                    seen.PageCountValid = paginator.IsPageCountValid;
                    seen.PageSize = paginator.PageSize;
                    seen.ThreadName = Thread.CurrentThread.Name;
                    seen.Apartment = Thread.CurrentThread.GetApartmentState();
                    viewRotated.Wait(TimeSpan.FromSeconds(10));
                    seen.LiveRotation = doc.Rotation;
                    seen.ContentBox = paginator.GetPage(0).ContentBox;
                };

                doc.Rotation = rotation;
                var job = PrintJob.RunAsync(doc, pages, PdfQueue, copies, gray, draft, $"probe job {copies}");
                doc.Rotation = PdfRotation.Rotate0;     // the user rotates the view after the job was requested
                viewRotated.Set();
                await job.WaitAsync(TimeSpan.FromSeconds(60));

                bool quarterTurn = rotation is PdfRotation.Rotate90 or PdfRotation.Rotate270;
                if (seen.Calls != 1) wrong.Add($"{label}: the recorder ran {seen.Calls} times");
                if (seen.Queue != PdfQueue) wrong.Add($"{label}: queue '{seen.Queue}'");
                if (seen.Description != $"probe job {copies}") wrong.Add($"{label}: job name '{seen.Description}'");
                if (seen.Copies != copies) wrong.Add($"{label}: copies {seen.Copies}");
                if ((seen.Color == OutputColor.Grayscale) != gray) wrong.Add($"{label}: colour {seen.Color?.ToString() ?? "unset"}");
                if ((seen.Quality == OutputQuality.Draft) != draft) wrong.Add($"{label}: quality {seen.Quality?.ToString() ?? "unset"}");
                if (seen.PageCount != pages.Length || !seen.PageCountValid) wrong.Add($"{label}: {seen.PageCount} page(s), valid={seen.PageCountValid}");
                if (seen.PageSize != expectedPaper) wrong.Add($"{label}: sheet {seen.PageSize.Width:F0}x{seen.PageSize.Height:F0}, queue paper {expectedPaper.Width:F0}x{expectedPaper.Height:F0}");
                if (seen.ThreadName != "pdf-print" || seen.Apartment != ApartmentState.STA) wrong.Add($"{label}: ran on '{seen.ThreadName}' ({seen.Apartment})");
                if (seen.LiveRotation != PdfRotation.Rotate0) wrong.Add($"{label}: the view rotation was not changed back before the page was made");
                if ((seen.ContentBox.Width > seen.ContentBox.Height) != quarterTurn)
                    wrong.Add($"{label}: the page is {seen.ContentBox.Width:F0}x{seen.ContentBox.Height:F0}, which does not follow the requested rotation");
            }
        }
        finally
        {
            PrintJob.Submit = guard;
            doc.Rotation = before;
        }
        if (!GuardInPlace()) wrong.Add("THE PRINT GUARD WAS NOT RESTORED");
        return CheckKit.Tally(name, cases.Length, wrong, "recorded only: nothing was sent to a queue");
    }

    private static async Task<Check> CancelledBeforeStartAsync(Func<string, string> at)
    {
        const string name = "spooler: a job cancelled before it starts is cancelled";
        var path = at("cancelled-early.pdf");
        WriteDocument(path, 1);
        var doc = new PdfDoc(path);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var wrong = new List<string>();
        // A made-up queue: a cancelled job must stop before it looks the queue up at all.
        var job = PrintJob.RunAsync(doc, new[] { 0 }, "no-such-printer-" + Guid.NewGuid().ToString("N"), 1, false, false, "probe job", cts.Token);
        await WaitQuietly(job);
        if (!job.IsCanceled) wrong.Add($"RunAsync ended {job.Status}{(job.IsFaulted ? ": " + job.Exception!.GetBaseException().Message : "")}");

        var xps = at("cancelled-early.xps");
        var write = PrintJob.WriteXpsAsync(doc, new[] { 0 }, PrintJob.FallbackPaper, xps, cts.Token);
        await WaitQuietly(write);
        if (!write.IsCanceled) wrong.Add($"WriteXpsAsync ended {write.Status}");
        if (File.Exists(xps)) wrong.Add("a cancelled XPS write left a file");
        return CheckKit.Tally(name, 2, wrong);
    }

    /// <summary>
    /// The recorder cancels the job's own token and asks the paginator for a page, so the
    /// cancellation surfaces from inside the work item, after the job has started.
    /// </summary>
    private static async Task<Check> CancelledWhilePaginatingAsync(Func<string, string> at)
    {
        const string name = "spooler: a job cancelled while paginating is cancelled";
        if (!PdfQueueExists())
            return Skipped(name, $"no '{PdfQueue}' queue on this machine");
        if (!GuardInPlace())
            return new Check(name, false, "the print guard is not in place, so no job was started");

        var path = at("cancelled-late.pdf");
        WriteDocument(path, 2);
        var doc = new PdfDoc(path);
        var guard = PrintJob.Submit;
        using var cts = new CancellationTokenSource();
        int reached = 0;
        Task job;
        try
        {
            PrintJob.Submit = (_, paginator, _) =>
            {
                reached++;
                cts.Cancel();
                paginator.GetPage(0);       // the paginator observes the cancel and throws
            };
            job = PrintJob.RunAsync(doc, new[] { 0, 1 }, PdfQueue, 1, false, false, "probe job", cts.Token);
            await WaitQuietly(job);
        }
        finally
        {
            PrintJob.Submit = guard;
        }

        var wrong = new List<string>();
        if (reached != 1) wrong.Add($"the job reached the submit call {reached} times");
        if (!job.IsCanceled) wrong.Add($"the job ended {job.Status}{(job.IsFaulted ? ": " + job.Exception!.GetBaseException().Message : "")}");
        if (!GuardInPlace()) wrong.Add("THE PRINT GUARD WAS NOT RESTORED");
        return CheckKit.Tally(name, 3, wrong);
    }

    private static async Task<Check> XpsCancelledMidJobAsync(Func<string, string> at)
    {
        const string name = "spooler: an XPS write cancelled after it started is cancelled";
        var path = at("cancelled-mid.pdf");
        WriteDocument(path, 40);
        var doc = new PdfDoc(path);
        var xps = at("cancelled-mid.xps");
        using var cts = new CancellationTokenSource();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var job = PrintJob.WriteXpsAsync(doc, Enumerable.Range(0, 40).ToList(), PrintJob.FallbackPaper, xps, cts.Token);
        // The package file exists only once the work item is running.
        bool started = await Poll.UntilAsync(() => File.Exists(xps), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(5));
        var startedAt = clock.Elapsed;
        cts.Cancel();
        await WaitQuietly(job);

        var wrong = new List<string>();
        if (!started) wrong.Add("the job never started writing");
        if (!job.IsCanceled) wrong.Add($"the job ended {job.Status}{(job.IsFaulted ? ": " + job.Exception!.GetBaseException().GetType().Name + " " + job.Exception.GetBaseException().Message : "")}");
        return CheckKit.Tally(name, 2, wrong, $"cancelled {(clock.Elapsed - startedAt).TotalMilliseconds:F0} ms after the file appeared, job ended {job.Status}");
    }

    private static async Task<Check> XpsFailureAsync(Func<string, string> at)
    {
        const string name = "spooler: an XPS write that fails surfaces its exception";
        var path = at("failing.pdf");
        WriteDocument(path, 1);
        var doc = new PdfDoc(path);
        // A folder that does not exist: the package cannot be created.
        var target = Path.Combine(at("no-such-folder"), "out.xps");
        var job = PrintJob.WriteXpsAsync(doc, new[] { 0 }, PrintJob.FallbackPaper, target);
        await WaitQuietly(job);

        var wrong = new List<string>();
        if (!job.IsFaulted) wrong.Add($"the job ended {job.Status}");
        else if (job.Exception!.GetBaseException() is OperationCanceledException) wrong.Add("the failure was reported as a cancellation");
        return CheckKit.Tally(name, 2, wrong, job.IsFaulted ? $"faulted with {job.Exception!.GetBaseException().GetType().Name}" : null);
    }

    private static async Task WaitQuietly(Task job)
    {
        try { await job.WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (TimeoutException) { throw; }
        catch { /* the caller inspects the task */ }
    }

    // ---------- paginator ----------

    private static Check PaginatorProperties(Func<string, string> at)
    {
        const string name = "spooler: the paginator reports its pages and size";
        var path = at("paginator.pdf");
        WriteDocument(path, 3);
        var doc = new PdfDoc(path);
        var paper = new Size(300, 400);
        var paginator = new PdfPrintPaginator(doc, new[] { 2, 0, 1 }, paper, PdfRotation.Rotate0);
        var wrong = new List<string>();
        if (!paginator.IsPageCountValid) wrong.Add("the page count is not reported as valid");
        if (paginator.PageCount != 3) wrong.Add($"PageCount {paginator.PageCount}");
        if (paginator.PageSize != paper) wrong.Add($"PageSize {paginator.PageSize}");
        paginator.PageSize = new Size(1, 1);
        if (paginator.PageSize != paper) wrong.Add("PageSize can be changed after construction");
        if (paginator.Source is not null) wrong.Add("Source is not null");
        if (new PdfPrintPaginator(doc, Array.Empty<int>(), paper, PdfRotation.Rotate0).PageCount != 0) wrong.Add("an empty selection has pages");
        return CheckKit.Tally(name, 6, wrong);
    }
}
