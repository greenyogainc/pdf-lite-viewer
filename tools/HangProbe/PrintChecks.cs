using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PdfLiteViewer;
using static HangProbe.InputChecks;
using static HangProbe.PdfFixtures;

namespace HangProbe;

/// <summary>
/// Drives the print preview through its keys, buttons, guards and failure paths without ever
/// sending a page to a printer.
///
/// This machine's default printer is a physical one, and a preview that is shown selects it on
/// its own, so every preview here has <c>PrintOverride</c> installed before it is shown. A check
/// that prints for real swaps the queue for a made-up name (the job fails at the lookup) or for
/// "Microsoft Print to PDF" with <c>PrintJob.Submit</c> replaced by a recorder, restoring the
/// guard in a <c>finally</c>. Nothing presses a key in a preview that has a real queue
/// selected unless an override is in place.
/// </summary>
internal static class PrintChecks
{
    private const string PdfQueue = "Microsoft Print to PDF";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public static async Task<List<Check>> RunAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"hangprobe-print-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var checks = new List<Check>();
        var cursor = Mouse.OverrideCursor;
        try
        {
            var path = Path.Combine(folder, "plain4.pdf");
            WriteDocument(path, 4);
            var doc = new PdfDoc(path);
            async Task Step(string name, Func<string, Task<Check>> body) => checks.Add(await CheckKit.RunAsync(name, () => body(name)));

            checks.Add(CheckKit.Run("print: the default printer is picked when the list has it", PickPrinterTable));
            await Step("print: keys step, Enter and Ctrl+P print, Escape closes, and a job in flight keeps the window", name => KeysAsync(doc, name));
            await Step("print: Print and Cancel buttons", name => ButtonsAsync(doc, name));
            await Step("print: a second job, an empty range and no printer are refused", name => GuardsAsync(doc, name));
            await Step("print: copies, colour and quality reach the job as the window shows them", name => SnapshotAsync(doc, name));
            await Step("print: a finished job closes a loaded window and leaves one never shown alone", name => SuccessAsync(doc, name));
            await Step("print: a failing job is reported and the controls come back", name => FailureAsync(doc, name));
            await Step("print: closing during a job hands the cursor back and leaves a later cursor alone", name => CursorAsync(doc, name));
            await Step("print: a render that is cancelled is ignored and one that fails keeps the page", name => RenderFailuresAsync(doc, name));
            await Step("print: paper size follows the last printer chosen", name => PaperSizeAsync(doc, name));
            await Step("print: settings changed before the window loaded do nothing yet", name => BeforeLoadAsync(doc, name));
            await Step("print: printer discovery that returns after the window closed is dropped", name => DiscoveryAsync(doc, name));
        }
        catch (Exception ex)
        {
            checks.Add(new Check("print checks ran", false, ex.ToString()));
        }
        finally
        {
            InputChecks.SetCtrl(false);
            Mouse.OverrideCursor = cursor;
            try { Directory.Delete(folder, recursive: true); } catch { /* temp files */ }
        }
        return checks;
    }

    // ---------- harness ----------

    private static readonly BitmapSource Blank = Frozen();

    private static BitmapSource Frozen()
    {
        var bmp = new WriteableBitmap(4, 4, 96, 96, PixelFormats.Pbgra32, null);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>A preview with both test seams installed before anything can use them.</summary>
    private sealed class Harness
    {
        public required PrintPreviewWindow Window { get; init; }
        public int Renders;
        public int Jobs;
        public bool Closed;
        public TaskCompletionSource? Held;
    }

    private static Harness Build(PdfDoc doc, int page = 0)
    {
        var window = new PrintPreviewWindow(doc, page) { ShowInTaskbar = false };
        var harness = new Harness { Window = window };
        window.PrintOverride = () => { harness.Jobs++; return harness.Held?.Task ?? Task.CompletedTask; };
        window.RenderOverride = (_, _, _) => { harness.Renders++; return Task.FromResult(Blank); };
        window.Closed += (_, _) => harness.Closed = true;
        return harness;
    }

    private static string FakeQueue() => "no-such-printer-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// A shown, loaded preview that has finished discovering printers and then been moved off
    /// the real default onto a made-up queue, with every earlier update delivered or dropped.
    /// </summary>
    private static async Task<Harness> ShownAsync(PdfDoc doc, int page = 0)
    {
        var harness = Build(doc, page);
        var window = harness.Window;
        try
        {
            window.Show();
            if (!await Poll.UntilAsync(() => window.IsLoaded, Patience)) throw new InvalidOperationException("the preview never loaded");
            // Printer discovery selects the default printer once it returns. A machine with no
            // printers never does, so a missing selection is waited out, not failed.
            await Poll.UntilAsync(() => window.PrinterBox.SelectedItem is not null, TimeSpan.FromSeconds(10));

            window.PrinterBox.Items.Clear();
            var fake = FakeQueue();
            window.PrinterBox.Items.Add(fake);
            int baseline = harness.Renders;
            window.PrinterBox.SelectedItem = fake;
            if (!await Poll.UntilAsync(() => harness.Renders > baseline, Patience))
                throw new InvalidOperationException("the paper size of the made-up queue never arrived");
            await Pump(window);
            return harness;
        }
        catch
        {
            CloseQuietly(window);
            throw;
        }
    }

    private static void CloseQuietly(Window? window)
    {
        try { window?.Close(); } catch { /* teardown noise must not mask the result */ }
    }

    private static Task Pump(Window window) =>
        window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task;

    private static bool Press(Harness h, Key key) => RaiseKey(h.Window, key, Keyboard.KeyDownEvent);

    /// <summary>
    /// A key that bubbles up to the window from <paramref name="origin"/>, with the window itself
    /// as the only element on the route: a text box would otherwise take the arrow for its own
    /// caret before the window ever saw it, and the window's decision is what is being checked.
    /// </summary>
    private static bool PressFrom(Harness h, UIElement origin, Key key)
    {
        var presentation = PresentationSource.FromVisual(h.Window) ?? throw new InvalidOperationException("the preview has no presentation source");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, presentation, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
            Source = origin,
        };
        h.Window.RaiseEvent(args);
        return args.Handled;
    }

    // ---------- the printer picker ----------

    private static Check PickPrinterTable()
    {
        const string name = "print: the default printer is picked when the list has it";
        var cases = new (string Label, string[] Names, string? Default, string? Expected)[]
        {
            ("default first", new[] { "A", "B", "C" }, "A", "A"),
            ("default in the middle", new[] { "A", "B", "C" }, "B", "B"),
            ("default last", new[] { "A", "B", "C" }, "C", "C"),
            ("default not installed", new[] { "A", "B" }, "Gone", "A"),
            ("no default at all", new[] { "A", "B" }, null, "A"),
            ("default but no printers", Array.Empty<string>(), "A", null),
            ("no default and no printers", Array.Empty<string>(), null, null),
            ("names are compared exactly", new[] { "a", "B" }, "A", "a"),
        };
        var wrong = new List<string>();
        foreach (var (label, names, def, expected) in cases)
        {
            var got = PrintPreviewWindow.PickPrinter(names, def);
            if (got != expected) wrong.Add($"{label}: '{got ?? "null"}', expected '{expected ?? "null"}'");
        }
        return CheckKit.Tally(name, cases.Length, wrong);
    }

    // ---------- keys ----------

    private static async Task<Check> KeysAsync(PdfDoc doc, string name)
    {
        var wrong = new List<string>();

        // Keys that do not print or close: P alone, other keys, arrows typed into a box, arrows elsewhere.
        var h = await ShownAsync(doc);
        try
        {
            if (Press(h, Key.P) || h.Jobs != 0) wrong.Add($"P without Control was handled or printed (jobs {h.Jobs})");
            // Keys with no meaning in the preview, including the ones its jump table skips over.
            foreach (var key in new[] { Key.A, Key.End, Key.Home, Key.Up, Key.Down, Key.Space, Key.Tab })
                if (Press(h, key)) wrong.Add($"{key} was handled");
            if (PressFrom(h, h.Window.CopiesBox, Key.D1)) wrong.Add("a digit typed into the copies box was handled");
            string label = h.Window.PageLabel.Text;
            bool left = PressFrom(h, h.Window.CopiesBox, Key.Left), right = PressFrom(h, h.Window.CopiesBox, Key.Right);
            if (left || right || h.Window.PageLabel.Text != label)
                wrong.Add($"arrows typed into the copies box were taken (left={left}, right={right}) or moved the page ('{label}' -> '{h.Window.PageLabel.Text}')");
            if (!PressFrom(h, h.Window.PageLabel, Key.Right) || h.Window.PageLabel.Text != "2 / 4") wrong.Add("an arrow that did not come from a text box did not step the page");
            Press(h, Key.Left);
            foreach (var (key, expected) in new[] { (Key.Right, "2 / 4"), (Key.PageDown, "3 / 4"), (Key.Left, "2 / 4"), (Key.PageUp, "1 / 4") })
            {
                bool handled = Press(h, key);
                if (!handled || h.Window.PageLabel.Text != expected) wrong.Add($"{key}: handled={handled}, label '{h.Window.PageLabel.Text}', expected '{expected}'");
            }
        }
        finally
        {
            CloseQuietly(h.Window);
        }

        // Enter starts a job that is held open: the window stays, Escape does not close it, and
        // the job finishing closes it.
        h = await ShownAsync(doc);
        try
        {
            h.Held = new TaskCompletionSource();
            if (!Press(h, Key.Enter)) wrong.Add("Enter was not handled");
            if (h.Jobs != 1) wrong.Add($"Enter started {h.Jobs} job(s)");
            if (h.Window.CancelBtn.IsEnabled || h.Window.PrinterBox.IsEnabled) wrong.Add("the settings stayed live while the job ran");
            if (Press(h, Key.Escape) || h.Closed) wrong.Add("Escape closed the window or was handled while a job was running");
            if (!Press(h, Key.Right) || h.Window.PageLabel.Text != "2 / 4") wrong.Add("the preview stopped responding while the job ran");
            h.Held.SetResult();
            if (!await Poll.UntilAsync(() => h.Closed, Patience)) wrong.Add("the window stayed open after its job finished");
        }
        finally
        {
            CloseQuietly(h.Window);
        }

        // Ctrl+P is the same as Enter.
        h = await ShownAsync(doc);
        try
        {
            SetCtrl(true);
            try
            {
                if (!Press(h, Key.P)) wrong.Add("Ctrl+P was not handled");
            }
            finally
            {
                SetCtrl(false);
            }
            if (h.Jobs != 1) wrong.Add($"Ctrl+P started {h.Jobs} job(s)");
            if (!await Poll.UntilAsync(() => h.Closed, Patience)) wrong.Add("the window stayed open after the Ctrl+P job");
        }
        finally
        {
            CloseQuietly(h.Window);
        }

        // Escape closes an idle preview.
        h = await ShownAsync(doc);
        try
        {
            if (!Press(h, Key.Escape) || !h.Closed) wrong.Add("Escape did not close an idle preview");
            if (h.Jobs != 0) wrong.Add("closing started a job");
        }
        finally
        {
            CloseQuietly(h.Window);
        }
        return CheckKit.Tally(name, 4, wrong);
    }

    private static async Task<Check> ButtonsAsync(PdfDoc doc, string name)
    {
        var wrong = new List<string>();
        var h = await ShownAsync(doc);
        try
        {
            Click(ButtonNamed(h.Window, Strings.Get("PrintBtn")));
            if (h.Jobs != 1) wrong.Add($"the Print button started {h.Jobs} job(s)");
            if (!await Poll.UntilAsync(() => h.Closed, Patience)) wrong.Add("the window stayed open after the Print button's job");
        }
        finally
        {
            CloseQuietly(h.Window);
        }

        h = await ShownAsync(doc);
        try
        {
            Click(ButtonNamed(h.Window, Strings.Get("CancelBtn")));
            if (!h.Closed) wrong.Add("the Cancel button did not close the window");
            if (h.Jobs != 0) wrong.Add("Cancel started a job");
        }
        finally
        {
            CloseQuietly(h.Window);
        }
        return CheckKit.Tally(name, 2, wrong);
    }

    // ---------- guards ----------

    private static async Task<Check> GuardsAsync(PdfDoc doc, string name)
    {
        var wrong = new List<string>();

        var h = await ShownAsync(doc);
        try
        {
            h.Held = new TaskCompletionSource();
            var first = h.Window.PrintAsync();
            await h.Window.PrintAsync();
            if (h.Jobs != 1) wrong.Add($"a second Print while one was running started {h.Jobs} job(s) in all");
            h.Held.SetResult();
            await first;
        }
        finally
        {
            CloseQuietly(h.Window);
        }

        h = await ShownAsync(doc);
        try
        {
            h.Window.RangeMode.SelectedIndex = 2;
            h.Window.RangeBox.Text = "nope";
            await Pump(h.Window);
            if (h.Window.EmptyRangeHint.Visibility != Visibility.Visible) wrong.Add("an unreadable range did not show the empty-range hint");
            await h.Window.PrintAsync();
            if (h.Jobs != 0) wrong.Add($"an empty range started {h.Jobs} job(s)");
        }
        finally
        {
            CloseQuietly(h.Window);
        }

        h = await ShownAsync(doc);
        try
        {
            h.Window.PrinterBox.SelectedItem = null;
            await Pump(h.Window);
            await h.Window.PrintAsync();
            if (h.Jobs != 0) wrong.Add($"no printer selected started {h.Jobs} job(s)");
            if (h.Window.PrintBtn.IsEnabled) wrong.Add("Print stayed enabled with no printer selected");
        }
        finally
        {
            CloseQuietly(h.Window);
        }
        return CheckKit.Tally(name, 3, wrong);
    }

    // ---------- what reaches the job ----------

    private static bool GuardInPlace()
    {
        try
        {
            PrintJob.Submit(null!, null!, null!);
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static bool PdfQueueExists()
    {
        try
        {
            using var server = new LocalPrintServer();
            using var queue = PrintJob.ResolveQueue(server, PdfQueue);
            return queue?.FullName == PdfQueue;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The window snapshots copies, colour and quality when Print is pressed. The job runs for
    /// real up to the spooler call against "Microsoft Print to PDF"; the call itself is a
    /// recorder, so nothing is sent.
    /// </summary>
    private static async Task<Check> SnapshotAsync(PdfDoc doc, string name)
    {
        if (!PdfQueueExists()) return new Check(name, true, $"skipped: no '{PdfQueue}' queue on this machine");
        if (!GuardInPlace()) return new Check(name, false, "the print guard is not in place, so no job was started");

        var cases = new (string Copies, bool Gray, bool Draft, int Expected)[]
        {
            ("1", false, false, 1), ("7", true, false, 7), ("abc", false, true, 1), ("", false, false, 1),
            ("0", true, true, 1), ("-3", false, false, 1), ("99", false, false, 99), ("100", false, false, 99),
            ("5000", false, false, 99), ("99999999999", false, false, 1), (" 4 ", false, false, 4),
        };
        var wrong = new List<string>();
        var guard = PrintJob.Submit;
        var seen = new List<(int? Copies, OutputColor? Color, OutputQuality? Quality, int Pages, string Queue, string? Job)>();
        try
        {
            PrintJob.Submit = (queue, paginator, ticket) =>
                seen.Add((ticket.CopyCount, ticket.OutputColor, ticket.OutputQuality, paginator.PageCount, queue.FullName, queue.CurrentJobSettings.Description));
            foreach (var (copies, gray, draft, expected) in cases)
            {
                var window = new PrintPreviewWindow(doc, 0) { ShowInTaskbar = false };
                try
                {
                    window.PrinterBox.Items.Add(PdfQueue);
                    window.PrinterBox.SelectedItem = PdfQueue;
                    window.CopiesBox.Text = copies;
                    window.BwCheck.IsChecked = gray;
                    window.DraftCheck.IsChecked = draft;
                    int before = seen.Count;
                    await window.PrintAsync().WaitAsync(TimeSpan.FromSeconds(60));
                    string label = $"copies '{copies}', gray={gray}, draft={draft}";
                    if (seen.Count != before + 1) { wrong.Add($"{label}: the spooler call ran {seen.Count - before} times"); continue; }
                    var job = seen[^1];
                    if (job.Copies != expected) wrong.Add($"{label}: {job.Copies} copies reached the job, expected {expected}");
                    if ((job.Color == OutputColor.Grayscale) != gray) wrong.Add($"{label}: colour {job.Color?.ToString() ?? "unset"}");
                    if ((job.Quality == OutputQuality.Draft) != draft) wrong.Add($"{label}: quality {job.Quality?.ToString() ?? "unset"}");
                    if (job.Pages != 4 || job.Queue != PdfQueue || job.Job != "plain4.pdf") wrong.Add($"{label}: {job.Pages} page(s) to '{job.Queue}' named '{job.Job}'");
                }
                finally
                {
                    CloseQuietly(window);
                }
            }
        }
        finally
        {
            PrintJob.Submit = guard;
        }
        if (!GuardInPlace()) wrong.Add("THE PRINT GUARD WAS NOT RESTORED");
        return CheckKit.Tally(name, cases.Length, wrong, "recorded only: nothing was sent to a queue");
    }

    // ---------- outcomes ----------

    private static async Task<Check> SuccessAsync(PdfDoc doc, string name)
    {
        var wrong = new List<string>();

        var loaded = await ShownAsync(doc);
        try
        {
            await loaded.Window.PrintAsync();
            if (!loaded.Closed) wrong.Add("a finished job left a loaded preview open");
        }
        finally
        {
            CloseQuietly(loaded.Window);
        }

        var unshown = Build(doc);
        try
        {
            unshown.Window.PrinterBox.Items.Add(FakeQueue());
            unshown.Window.PrinterBox.SelectedIndex = 0;
            await unshown.Window.PrintAsync();
            if (unshown.Jobs != 1) wrong.Add($"the unshown preview ran {unshown.Jobs} job(s)");
            if (unshown.Closed) wrong.Add("a finished job closed a preview that was never shown");
            if (!unshown.Window.CancelBtn.IsEnabled) wrong.Add("the controls did not come back");
        }
        finally
        {
            CloseQuietly(unshown.Window);
        }
        return CheckKit.Tally(name, 3, wrong);
    }

    private static async Task<Check> FailureAsync(PdfDoc doc, string name)
    {
        var wrong = new List<string>();
        var intro = Strings.Get("PrintingFailedMessage").Split('{')[0].Trim();
        var caption = Strings.Get("AppTitle");

        void ControlsBack(Harness h, string label)
        {
            var w = h.Window;
            if (!(w.CancelBtn.IsEnabled && w.PrinterBox.IsEnabled && w.RangeMode.IsEnabled && w.CopiesBox.IsEnabled && w.BwCheck.IsEnabled && w.DraftCheck.IsEnabled && w.PrintBtn.IsEnabled))
                wrong.Add($"{label}: the controls did not come back");
            if (Mouse.OverrideCursor is not null) wrong.Add($"{label}: the wait cursor stayed");
            if (h.Closed) wrong.Add($"{label}: the window closed over its own error");
        }

        // The job itself fails.
        var h = await ShownAsync(doc);
        try
        {
            var marker = ProbeLog.NewMarker("probe-print-fails");
            h.Window.PrintOverride = () => { h.Jobs++; return Task.FromException(new InvalidOperationException(marker)); };
            using var dialog = Program.Dialogs.Expect(caption, DialogAction.PressOk, textContains: marker);
            await h.Window.PrintAsync();
            if (!dialog.Satisfied) wrong.Add("a failing job showed no error");
            else if (!dialog.Text.StartsWith(intro) || !dialog.Text.Contains(marker)) wrong.Add($"the error reads '{dialog.Text}'");
            if (!ProbeLog.Contains(marker)) wrong.Add("the failure was not logged");
            ControlsBack(h, "a failing job");
        }
        finally
        {
            CloseQuietly(h.Window);
        }

        // No override: the real job path, which cannot find a made-up queue.
        h = await ShownAsync(doc);
        try
        {
            var queue = (string)h.Window.PrinterBox.SelectedItem!;
            if (!queue.StartsWith("no-such-printer-")) throw new InvalidOperationException("the selected queue is not the made-up one; refusing to print");
            h.Window.PrintOverride = null;
            using var dialog = Program.Dialogs.Expect(caption, DialogAction.PressOk, textContains: queue);
            await h.Window.PrintAsync();
            if (!dialog.Satisfied) wrong.Add("a made-up queue showed no error");
            else if (!dialog.Text.StartsWith(intro) || !dialog.Text.Contains("unavailable")) wrong.Add($"the error reads '{dialog.Text}'");
            ControlsBack(h, "a made-up queue");
        }
        finally
        {
            CloseQuietly(h.Window);
        }
        return CheckKit.Tally(name, 2, wrong);
    }

    private static async Task<Check> CursorAsync(PdfDoc doc, string name)
    {
        var wrong = new List<string>();
        var h = await ShownAsync(doc);
        try
        {
            Mouse.OverrideCursor = null;
            h.Held = new TaskCompletionSource();
            var printing = h.Window.PrintAsync();
            if (!ReferenceEquals(Mouse.OverrideCursor, Cursors.Wait)) wrong.Add("a running job did not show the wait cursor");

            h.Window.Close();
            if (Mouse.OverrideCursor is not null) wrong.Add("closing the window mid-job kept the wait cursor");
            // The window is unloaded a moment after Close returns; a job takes far longer than that.
            await Pump(h.Window);
            if (h.Window.IsLoaded) wrong.Add("the window was still loaded after it closed");

            // A preview opened later owns the cursor now; the first job finishing must not clear it.
            Mouse.OverrideCursor = Cursors.Cross;
            h.Held.SetResult();
            await printing;
            if (!ReferenceEquals(Mouse.OverrideCursor, Cursors.Cross)) wrong.Add("a job finishing after its window closed changed the cursor");
            if (h.Jobs != 1) wrong.Add($"{h.Jobs} job(s) ran");
        }
        finally
        {
            Mouse.OverrideCursor = null;
            CloseQuietly(h.Window);
        }
        return CheckKit.Tally(name, 4, wrong);
    }

    // ---------- the page on the sheet ----------

    private static async Task<Check> RenderFailuresAsync(PdfDoc doc, string name)
    {
        var wrong = new List<string>();
        var window = new PrintPreviewWindow(doc, 0) { ShowInTaskbar = false };
        try
        {
            var first = new WriteableBitmap(2, 2, 96, 96, PixelFormats.Pbgra32, null);
            first.Freeze();
            window.RenderOverride = (_, _, _) => Task.FromResult<BitmapSource>(first);
            await window.ShowPageAsync();
            if (!ReferenceEquals(window.PageImage.Source, first)) wrong.Add("the page was not shown");

            window.RenderOverride = (_, _, _) => throw new OperationCanceledException();
            await window.ShowPageAsync();
            if (!ReferenceEquals(window.PageImage.Source, first)) wrong.Add("a cancelled render replaced the page");

            var marker = ProbeLog.NewMarker("probe-render-fails");
            window.RenderOverride = (_, _, _) => Task.FromException<BitmapSource>(new InvalidOperationException(marker));
            await window.ShowPageAsync();
            if (!ReferenceEquals(window.PageImage.Source, first)) wrong.Add("a failed render blanked or replaced the page");
            if (!ProbeLog.Contains(marker)) wrong.Add("the failed render was not logged");
        }
        finally
        {
            CloseQuietly(window);
        }
        return CheckKit.Tally(name, 3, wrong);
    }

    private static async Task<Check> PaperSizeAsync(PdfDoc doc, string name)
    {
        var wrong = new List<string>();
        var h = await ShownAsync(doc);
        try
        {
            // Clearing the selection has no printer to ask about.
            int renders = h.Renders;
            h.Window.PrinterBox.SelectedItem = null;
            await Task.Delay(500);
            await Pump(h.Window);
            if (h.Renders != renders) wrong.Add("clearing the printer re-rendered the sheet");

            // Two choices before the first answer arrives: only the second is applied.
            var a = FakeQueue();
            var b = FakeQueue();
            h.Window.PrinterBox.Items.Add(a);
            h.Window.PrinterBox.Items.Add(b);
            renders = h.Renders;
            h.Window.PrinterBox.SelectedItem = a;
            h.Window.PrinterBox.SelectedItem = b;
            if (!await Poll.UntilAsync(() => h.Renders > renders, Patience)) wrong.Add("the second printer's paper size never arrived");
            await Task.Delay(1500);
            await Pump(h.Window);
            if (h.Renders != renders + 1) wrong.Add($"{h.Renders - renders} paper-size updates were applied for two quick choices, expected 1");
            var paper = PrintJob.FallbackPaper;
            if (h.Window.Paper.Width != paper.Width || h.Window.Paper.Height != paper.Height) wrong.Add($"the sheet is {h.Window.Paper.Width}x{h.Window.Paper.Height}, expected the fallback {paper.Width}x{paper.Height}");
        }
        finally
        {
            CloseQuietly(h.Window);
        }
        return CheckKit.Tally(name, 3, wrong);
    }

    // ---------- before the window loads ----------

    private static async Task<Check> BeforeLoadAsync(PdfDoc doc, string name)
    {
        var wrong = new List<string>();

        // Never shown: IsLoaded is false, so each of these handlers returns before it acts.
        var h = Build(doc);
        try
        {
            var w = h.Window;
            await Pump(w);
            await Task.Delay(300);
            int renders = h.Renders;
            string label = w.PageLabel.Text;
            bool printWas = w.PrintBtn.IsEnabled;

            w.BwCheck.IsChecked = true;
            w.RangeMode.SelectedIndex = 2;
            w.RangeBox.Text = "1-2";
            w.PrinterBox.Items.Add("probe queue");
            w.PrinterBox.SelectedItem = "probe queue";
            await Pump(w);
            await Task.Delay(300);

            if (h.Renders != renders) wrong.Add("a setting changed before the load re-rendered the sheet");
            if (w.PageLabel.Text != label) wrong.Add($"the page label changed from '{label}' to '{w.PageLabel.Text}'");
            if (w.RangeBox.Visibility == Visibility.Visible) wrong.Add("the range box appeared before the window loaded");
            if (w.PrintBtn.IsEnabled != printWas) wrong.Add("Print changed state before the window loaded");
        }
        finally
        {
            CloseQuietly(h.Window);
        }

        // Loaded, but not on the custom range: typing in the range box changes nothing.
        var shown = await ShownAsync(doc);
        try
        {
            int renders = shown.Renders;
            string label = shown.Window.PageLabel.Text;
            shown.Window.RangeMode.SelectedIndex = 0;
            shown.Window.RangeBox.Text = "3";
            await Pump(shown.Window);
            await Task.Delay(300);
            if (shown.Window.PageLabel.Text != label) wrong.Add($"the page label changed from '{label}' to '{shown.Window.PageLabel.Text}' for a range that is not in use");
            if (shown.Renders != renders) wrong.Add("a range that is not in use re-rendered the sheet");
            if (shown.Window.RangeBox.Visibility == Visibility.Visible) wrong.Add("the range box is showing for the All pages mode");

            // ...and the black-and-white box re-renders once the window is loaded.
            shown.Window.BwCheck.IsChecked = true;
            if (!await Poll.UntilAsync(() => shown.Renders > renders, Patience)) wrong.Add("Black and white did not re-render a loaded preview");
        }
        finally
        {
            CloseQuietly(shown.Window);
        }
        return CheckKit.Tally(name, 2, wrong);
    }

    // ---------- printer discovery ----------

    /// <summary>
    /// Discovery runs on a worker and returns to the window later. If the window is gone by then
    /// the printers are dropped; if it is shown but has not finished loading, they are used.
    /// </summary>
    private static async Task<Check> DiscoveryAsync(PdfDoc doc, string name)
    {
        var wrong = new List<string>();
        string note = "";

        // Shown and closed before the worker returns: nothing may be added to a closed window.
        var gone = Build(doc);
        try
        {
            gone.Window.Show();
            gone.Window.Close();
            await Task.Delay(2500);
            await Pump(gone.Window);
            if (gone.Window.PrinterBox.Items.Count != 0) wrong.Add($"{gone.Window.PrinterBox.Items.Count} printer(s) were added to a closed window");
        }
        finally
        {
            CloseQuietly(gone.Window);
        }

        // The worker's answer is already waiting when the window is shown: the answer is used. The
        // UI thread sleeps until the worker is done, so the answer is queued ahead of everything
        // Show() sets in motion. Show() itself lays the window out and loads it before it returns,
        // so there is no moment at which the answer could meet a window that is visible but not
        // loaded; that is measured here rather than assumed.
        long mark = ThreadPool.CompletedWorkItemCount;
        var early = Build(doc);
        try
        {
            WaitForPoolWork(mark);
            early.Window.Show();
            bool loadedAtShow = early.Window.IsLoaded;
            await Poll.UntilAsync(() => early.Window.PrinterBox.Items.Count > 0 || early.Window.IsLoaded && early.Window.PageImage.Source is not null, TimeSpan.FromSeconds(10));
            int printers = early.Window.PrinterBox.Items.Count;
            if (printers > 0 && early.Window.PrinterBox.SelectedItem is null) wrong.Add("printers were found but none was selected");
            if (early.Jobs != 0) wrong.Add("a job ran");
            note = $"Show() returned with IsLoaded={loadedAtShow}" + (printers == 0 ? "; this machine lists no printers" : $"; {printers} printer(s) listed");
        }
        finally
        {
            CloseQuietly(early.Window);
        }
        return CheckKit.Tally(name, 2, wrong, note.Length == 0 ? null : note);
    }

    /// <summary>Blocks the UI thread, without pumping it, until the thread pool has finished the work a new preview starts since <paramref name="mark"/>.</summary>
    private static void WaitForPoolWork(long mark)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // Two items: the queue enumeration, and the first page the constructor renders.
        while (ThreadPool.CompletedWorkItemCount - mark < 2 || ThreadPool.PendingWorkItemCount > 0)
        {
            if (clock.Elapsed > Patience) throw new TimeoutException("printer discovery never finished");
            Thread.Sleep(10);
        }
        Thread.Sleep(50);
    }
}
