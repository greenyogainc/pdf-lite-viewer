using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PdfLiteViewer;

namespace HangProbe;

/// <summary>
/// Guards the print preview against the stale render it used to paint.
///
/// Renders are serialized through a semaphore, which is not FIFO, and PDFium is only
/// asked to stop before its work begins — so on the third quick page turn an overtaken
/// render still produces a bitmap, and could finish *after* the render that replaced it
/// and repaint the sheet with the page the user had already left. Clicking fast will not
/// reproduce that on demand, so this drives it directly: PrintPreviewWindow.RenderOverride
/// hands out renders this file completes by hand, in whatever order it likes.
/// </summary>
internal static class PreviewRaceChecks
{
    public static async Task<List<Check>> RunAsync(PdfDoc doc)
    {
        var checks = new List<Check>();
        PrintPreviewWindow? window = null;

        try
        {
            var pending = new List<TaskCompletionSource<BitmapSource>>();
            var requested = new List<int>();

            // Deliberately never shown: the constructor is what matters (it lays the window
            // out and starts a first render), and an unshown window also makes printer
            // discovery bail out before it can queue a render of its own behind these.
            window = new PrintPreviewWindow(doc, 0);
            window.RenderOverride = (page, _, _) =>
            {
                // The token is ignored on purpose: this models a render already past its
                // cancellation checks — the one case that yields a bitmap nobody wants.
                requested.Add(page);
                var tcs = new TaskCompletionSource<BitmapSource>();
                pending.Add(tcs);
                return tcs.Task;
            };

            // The constructor's render went to the real renderer (the override was not
            // installed yet). Let it land, so the only completions still in flight below
            // are the two this check controls.
            for (int i = 0; i < 60 && (window.PageImage.Source is null || pending.Count > 0); i++)
            {
                Release(pending, Bitmap(4));
                await PumpAsync(window);
                await Task.Delay(50);
            }
            Release(pending, Bitmap(4));
            await PumpAsync(window);
            requested.Clear();

            // Two page turns, neither allowed to finish yet: R1 is overtaken by R2.
            var older = Bitmap(1);
            var newer = Bitmap(2);
            var t1 = window.StepPreviewAsync(+1);   // R1 -> pending[0]
            var t2 = window.StepPreviewAsync(+1);   // R2 -> pending[1]
            if (pending.Count != 2)
                throw new InvalidOperationException($"expected 2 held renders, have {pending.Count}");

            // Complete the NEWER request first (pending[1] — completion order is the point,
            // so this must index, not dequeue FIFO). Awaiting t2 then proves its ShowPageAsync
            // body ran to the end before the stale completion below is released.
            pending[1].SetResult(newer);
            await t2;
            await PumpAsync(window);

            // ...and only now the render that was already superseded. Before the fix, this
            // is the completion that overwrote the newer page.
            pending[0].SetResult(older);
            await t1;
            await PumpAsync(window);
            pending.Clear();

            var source = window.PageImage.Source;
            string shown =
                ReferenceEquals(source, newer) ? "the newer render" :
                ReferenceEquals(source, older) ? "the STALE render" :
                source is null ? "nothing" : "an unrelated bitmap";

            checks.Add(new Check("preview ignores an overtaken render",
                ReferenceEquals(source, newer),
                $"requested page(s) {string.Join(", ", requested)}, older render completed last; sheet shows {shown}"));

            int expectedIndex = Math.Clamp(2, 0, Math.Max(0, doc.PageCount - 1));
            string expectedLabel = string.Format(Strings.Get("PageLabelFormat"), expectedIndex + 1, doc.PageCount);
            checks.Add(new Check("preview page label matches the page shown",
                window.PageLabel.Text == expectedLabel,
                $"label reads '{window.PageLabel.Text}', expected '{expectedLabel}'"));
        }
        catch (Exception ex)
        {
            checks.Add(new Check("preview race check ran", false,
                $"{ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
            // Teardown noise must not mask the result above.
            try { window?.Close(); } catch { }
        }

        return checks;
    }

    /// <summary>Lets every render waiting so far finish; used to reach a known starting point.</summary>
    private static void Release(List<TaskCompletionSource<BitmapSource>> pending, BitmapSource bmp)
    {
        foreach (var tcs in pending) tcs.TrySetResult(bmp);
        pending.Clear();
    }

    private static BitmapSource Bitmap(int size)
    {
        var bmp = new WriteableBitmap(size, size, 96, 96, PixelFormats.Pbgra32, null);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Runs the message pump out to Background, i.e. past every queued continuation.</summary>
    private static Task PumpAsync(PrintPreviewWindow window) =>
        window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task;
}
