using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Media;
using PDFtoImage;
using PdfLiteViewer;
using SkiaSharp;
using static HangProbe.PdfFixtures;

namespace HangProbe;

/// <summary>
/// Guards <see cref="PdfDoc"/> beyond what the stress document reaches: the page-size
/// invariants every layout divides by, the bookmark mapper on outline shapes real files
/// have (generated at run time, so each check reads exactly the shape it names), the chapter
/// cache, the Skia-to-WPF pixel conversion for formats PDFium does not hand back by default,
/// and the render lock under cancellation. Where a check asked what PDFium or PdfPig actually
/// does, the detail says so.
/// </summary>
internal static class DocumentChecks
{
    private const string Untitled = "Untitled!";

    public static async Task<List<Check>> RunAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"hangprobe-documents-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            string At(string name) => Path.Combine(folder, name);
            var checks = new List<Check>
            {
                CheckKit.Run("pdf: size invariants reject every impossible table", SizeInvariants),
                CheckKit.Run("pdf: a CropBox that misses the MediaBox is refused at open", () => CropBoxes(At)),
                CheckKit.Run("pdf: chapters map every outline shape", () => OutlineShapes(At)),
                CheckKit.Run("pdf: an unsupported outline action is dropped, not navigable", () => EmbeddedAction(At)),
                await CheckKit.RunAsync("pdf: chapters are cached, shared and honour cancellation", () => ChapterCacheAsync(At)),
                CheckKit.Run("pdf: Skia bitmaps convert to BGRA pixels", SkiaConversion),
                await CheckKit.RunAsync("pdf: a cancelled render leaves the render lock usable", () => RenderCancellationAsync(At)),
                CheckKit.Run("pdf: display size swaps for quarter turns only", () => DisplaySize(At)),            };
            return checks;
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* temp files: the next run's folder is new anyway */ }
        }
    }

    // ---------- page sizes ----------

    private static Check SizeInvariants()
    {
        const string name = "pdf: size invariants reject every impossible table";
        var letter = (612.0, 792.0);
        var cases = new (string Label, int Count, (double, double)[] Sizes, string? MessagePart)[]
        {
            ("count above the sizes", 3, new[] { letter, letter }, "3 pages but 2 page sizes"),
            ("count below the sizes", 1, new[] { letter, letter }, "1 pages but 2 page sizes"),
            ("zero width on the second page", 2, new[] { letter, (0.0, 50.0) }, "Page 2"),
            ("zero height", 1, new[] { (50.0, 0.0) }, "Page 1"),
            ("both zero", 1, new[] { (0.0, 0.0) }, "Page 1"),
            ("negative width", 1, new[] { (-5.0, 50.0) }, "Page 1"),
            ("negative height on the third page", 3, new[] { letter, letter, (50.0, -0.5) }, "Page 3"),
            ("valid, mixed orientation", 2, new[] { letter, (792.0, 612.0) }, null),
            ("valid, tiny", 1, new[] { (0.5, 0.5) }, null),
        };
        var wrong = new List<string>();
        foreach (var (label, count, sizes, part) in cases)
        {
            try
            {
                PdfDoc.RequireConsistentSizes(count, sizes);
                if (part is not null) wrong.Add($"{label}: accepted");
            }
            catch (UnreadablePagesException ex)
            {
                if (part is null) wrong.Add($"{label}: refused ({ex.Message})");
                else if (!ex.Message.Contains(part)) wrong.Add($"{label}: message '{ex.Message}' lacks '{part}'");
            }
            catch (Exception ex)
            {
                wrong.Add($"{label}: {ex.GetType().Name} instead of UnreadablePagesException");
            }
        }
        return CheckKit.Tally(name, cases.Length, wrong);
    }

    /// <summary>
    /// PDFium substitutes Letter for a MediaBox that is empty, so a collapsed MediaBox never
    /// reaches the layouts as 0x0. What does collapse a page is a CropBox that misses the
    /// MediaBox, or only touches its edge.
    /// </summary>
    private static Check CropBoxes(Func<string, string> file)
    {
        const string name = "pdf: a CropBox that misses the MediaBox is refused at open";
        var cases = new (string Label, string MediaBox, string Extra, bool Opens, (double, double)? Size)[]
        {
            ("disjoint CropBox", "[0 0 612 792]", "/CropBox [1000 1000 1100 1100]", false, null),
            ("CropBox touching the right edge (zero width)", "[0 0 612 792]", "/CropBox [612 0 700 100]", false, null),
            ("CropBox touching the top edge (zero height)", "[0 0 612 792]", "/CropBox [0 792 100 900]", false, null),
            ("CropBox overlapping a corner", "[0 0 612 792]", "/CropBox [600 780 700 800]", true, (12, 12)),
            ("CropBox inside the page", "[0 0 612 792]", "/CropBox [100 100 300 400]", true, (200, 300)),
            ("empty MediaBox", "[0 0 0 0]", "", true, (612, 792)),
        };
        var wrong = new List<string>();
        var seen = new List<string>();
        int index = 0;
        foreach (var (label, mediaBox, extra, opens, size) in cases)
        {
            var path = file($"crop-{index++}.pdf");
            WriteDocument(path, 1, mediaBox, extra);
            try
            {
                var doc = new PdfDoc(path);
                var actual = doc.PageSizes[0];
                seen.Add($"{label}: opens as {actual.Width:F0}x{actual.Height:F0}");
                if (!opens) wrong.Add($"{label}: opened as {actual.Width:F0}x{actual.Height:F0}");
                else if (size is { } want && (Math.Abs(actual.Width - want.Item1) > 0.5 || Math.Abs(actual.Height - want.Item2) > 0.5))
                    wrong.Add($"{label}: {actual.Width:F1}x{actual.Height:F1}, expected {want.Item1}x{want.Item2}");
            }
            catch (UnreadablePagesException ex)
            {
                seen.Add($"{label}: refused");
                if (opens) wrong.Add($"{label}: refused ({ex.Message})");
                else if (!ex.Message.Contains("non-positive size")) wrong.Add($"{label}: refused for another reason ({ex.Message})");
            }
        }
        return CheckKit.Tally(name, cases.Length, wrong, string.Join("; ", seen));
    }

    // ---------- outline mapping ----------

    private sealed record Expected(string Title, int? Page, int Depth, int Parent);

    private static Check OutlineShapes(Func<string, string> file)
    {
        const string name = "pdf: chapters map every outline shape";
        var outline = new[]
        {
            new OutlineItem("Chapter One", Dest(1),
                new OutlineItem("", Dest(2)),
                new OutlineItem("   ", Dest(2),
                    new OutlineItem("Deep", Dest(3)))),
            new OutlineItem("Remote", "/A << /S /GoToR /F (other.pdf) /D [0 /Fit] >>",
                new OutlineItem("Remote child", Dest(3))),
            new OutlineItem("Dangling", "/Dest [99 0 R /Fit]"),
            new OutlineItem("Past the end", "/Dest [5 /Fit]"),
            new OutlineItem("Before the start", "/Dest [-1 /Fit]"),
            new OutlineItem("Container", null,
                new OutlineItem("Under container", Dest(2))),
            new OutlineItem("Link", "/A << /S /URI /URI (https://example.com) >>"),
            new OutlineItem("Last", Dest(2)),
        };
        var path = file("outline.pdf");
        WriteDocument(path, 3, outline: outline);
        var doc = new PdfDoc(path);
        var roots = doc.GetChapters(CancellationToken.None, Untitled);

        // Pre-order, so SourceOrder is the index and Parent is another index.
        var expected = new Expected[]
        {
            new("Chapter One", 0, 0, -1),
            new(Untitled, 1, 1, 0),                 // a blank title
            new(Untitled, 1, 1, 0),                 // a whitespace-only title
            new("Deep", 2, 2, 2),
            new("Remote", null, 0, -1),             // GoToR: kept, but it leaves the document
            new("Remote child", 2, 1, 4),           // ...and its children are still reachable
            new("Dangling", null, 0, -1),           // a destination that is not a page
            new("Past the end", null, 0, -1),       // page 6 of 3
            new("Before the start", null, 0, -1),   // PdfPig reports page 0
            new("Container", null, 0, -1),
            new("Under container", 1, 1, 9),
            new("Link", null, 0, -1),
            new("Last", 1, 0, -1),
        };

        var walked = new List<ChapterItem>();
        void Walk(IEnumerable<ChapterItem> items)
        {
            foreach (var item in items)
            {
                walked.Add(item);
                Walk(item.Children);
            }
        }
        Walk(roots);

        var wrong = new List<string>();
        if (walked.Count != expected.Length)
            wrong.Add($"{walked.Count} nodes [{string.Join(", ", walked.Select(w => w.Title))}], expected {expected.Length}");
        for (int i = 0; i < Math.Min(walked.Count, expected.Length); i++)
        {
            var got = walked[i];
            var want = expected[i];
            int parent = got.Parent is null ? -1 : walked.IndexOf(got.Parent);
            if (got.Title != want.Title || got.PageIndex != want.Page || got.Depth != want.Depth
                || got.SourceOrder != i || parent != want.Parent
                || (got.Parent is not null && !got.Parent.Children.Contains(got)))
                wrong.Add($"#{i} '{got.Title}': page {got.PageIndex?.ToString() ?? "-"}, depth {got.Depth}, order {got.SourceOrder}, parent #{parent}" +
                          $" - expected '{want.Title}', page {want.Page?.ToString() ?? "-"}, depth {want.Depth}, order {i}, parent #{want.Parent}");
        }
        if (roots.Any(r => r.Parent is not null)) wrong.Add("a root has a parent");
        return CheckKit.Tally(name, expected.Length, wrong);
    }

    /// <summary>
    /// PdfPig 0.1.15 builds a node only for a destination, a GoTo, a GoToR or a URI action. An
    /// outline item with any other action (an embedded-file GoToE, a Launch) is dropped along
    /// with its children before PdfDoc sees it, so PdfDoc's own "not an embedded node" exclusion
    /// has nothing to exclude. Whatever PdfPig does, such an item must never navigate.
    /// </summary>
    private static Check EmbeddedAction(Func<string, string> file)
    {
        const string name = "pdf: an unsupported outline action is dropped, not navigable";
        var outline = new[]
        {
            new OutlineItem("Before", Dest(1)),
            new OutlineItem("Embedded", "/A << /S /GoToE /F (x.pdf) /D [0 /Fit] /T << /R /C /N (inner.pdf) >> >>",
                new OutlineItem("Embedded child", Dest(2))),
            new OutlineItem("Launcher", "/A << /S /Launch /F (tool.exe) >>"),
            new OutlineItem("After", Dest(3)),
        };
        var path = file("embedded.pdf");
        WriteDocument(path, 3, outline: outline);
        var roots = new PdfDoc(path).GetChapters(CancellationToken.None, Untitled);

        var wrong = new List<string>();
        var before = roots.FirstOrDefault(r => r.Title == "Before");
        var after = roots.FirstOrDefault(r => r.Title == "After");
        if (before?.PageIndex != 0) wrong.Add("'Before' lost its page");
        if (after?.PageIndex != 2) wrong.Add("'After' lost its page");
        var embedded = roots.FirstOrDefault(r => r.Title == "Embedded");
        var launcher = roots.FirstOrDefault(r => r.Title == "Launcher");
        if (embedded?.PageIndex is not null) wrong.Add("the GoToE item navigates");
        if (launcher?.PageIndex is not null) wrong.Add("the Launch item navigates");
        return CheckKit.Tally(name, 4, wrong,
            $"PdfPig {(embedded is null ? "dropped" : "kept")} the GoToE item" +
            $"{(embedded is null ? "" : $" (page {embedded.PageIndex?.ToString() ?? "none"})")} and {(launcher is null ? "dropped" : "kept")} the Launch item");
    }

    private static async Task<Check> ChapterCacheAsync(Func<string, string> file)
    {
        const string name = "pdf: chapters are cached, shared and honour cancellation";
        var wrong = new List<string>();
        var withOutline = file("cached-outline.pdf");
        WriteDocument(withOutline, 2, outline: new[] { new OutlineItem("One", Dest(1)), new OutlineItem("", Dest(2)) });
        var bare = file("cached-bare.pdf");
        WriteDocument(bare, 2);

        var doc = new PdfDoc(withOutline);
        var first = doc.GetChapters(CancellationToken.None, Untitled);
        if (first.Count != 2) wrong.Add($"{first.Count} chapters, expected 2");
        if (!ReferenceEquals(first, doc.GetChapters(CancellationToken.None, "another label")))
            wrong.Add("a second call parsed again instead of returning the cached list");
        if (first[1].Title != Untitled) wrong.Add($"the blank title reads '{first[1].Title}', not the first call's label");

        // A caller that has given up is not handed a cached answer.
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            if (!Throws<OperationCanceledException>(() => doc.GetChapters(cancelled.Token, Untitled)))
                wrong.Add("a cancelled caller was handed the cached list");

            // On a document nobody has asked yet, the same: nothing is parsed for it.
            var fresh = new PdfDoc(withOutline);
            if (!Throws<OperationCanceledException>(() => fresh.GetChapters(cancelled.Token, Untitled)))
                wrong.Add("a cancelled first call returned");

            // ...and it must not be remembered: a caller that is still interested parses with its
            // own token, instead of being handed the first caller's cancellation.
            try
            {
                var retried = fresh.GetChapters(CancellationToken.None, Untitled);
                if (retried.Count != 2) wrong.Add($"the call after a cancelled first call returned {retried.Count} chapters, expected 2");
            }
            catch (OperationCanceledException)
            {
                wrong.Add("a cancelled first call poisoned the document: every later call throws OperationCanceledException");
            }
        }

        // Concurrent first callers share one parse.
        var shared = new PdfDoc(withOutline);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => shared.GetChapters(CancellationToken.None, Untitled))));
        if (results.Any(r => !ReferenceEquals(r, results[0]))) wrong.Add("concurrent callers got different lists");

        var empty = new PdfDoc(bare);
        var none = empty.GetChapters(CancellationToken.None, Untitled);
        if (none.Count != 0) wrong.Add($"a document without an outline returned {none.Count} chapters");
        if (!ReferenceEquals(none, empty.GetChapters(CancellationToken.None, Untitled))) wrong.Add("the empty answer was not cached either");
        return CheckKit.Tally(name, 7, wrong);
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    // ---------- pixels ----------

    private static Check SkiaConversion()
    {
        const string name = "pdf: Skia bitmaps convert to BGRA pixels";
        var wrong = new List<string>();

        // RGBA bytes: an opaque orange pixel and a half-transparent (premultiplied) one.
        var rgbaBytes = new byte[] { 255, 128, 0, 255, 100, 50, 25, 128 };
        using (var rgba = new SKBitmap(2, 1, SKColorType.Rgba8888, SKAlphaType.Premul))
        {
            Marshal.Copy(rgbaBytes, 0, rgba.GetPixels(), rgbaBytes.Length);
            var converted = PdfDoc.ToBitmapSource(rgba);
            var pixels = Pixels(converted, 8);
            var wanted = new byte[] { 0, 128, 255, 255, 25, 50, 100, 128 };
            if (converted.Format != PixelFormats.Pbgra32) wrong.Add($"Rgba8888 became {converted.Format}");
            if (!converted.IsFrozen) wrong.Add("the converted bitmap is not frozen");
            if (converted.PixelWidth != 2 || converted.PixelHeight != 1) wrong.Add($"size {converted.PixelWidth}x{converted.PixelHeight}");
            for (int i = 0; i < wanted.Length; i++)
                if (Math.Abs(pixels[i] - wanted[i]) > 1) wrong.Add($"Rgba8888 byte {i} is {pixels[i]}, expected {wanted[i]}");

            var after = new byte[rgbaBytes.Length];
            Marshal.Copy(rgba.GetPixels(), after, 0, after.Length);
            if (!after.SequenceEqual(rgbaBytes)) wrong.Add("the caller's bitmap was modified");
        }

        var bgraBytes = new byte[] { 1, 2, 3, 255, 10, 20, 30, 128 };
        using (var bgra = new SKBitmap(2, 1, SKColorType.Bgra8888, SKAlphaType.Premul))
        {
            Marshal.Copy(bgraBytes, 0, bgra.GetPixels(), bgraBytes.Length);
            var passed = PdfDoc.ToBitmapSource(bgra);
            if (!Pixels(passed, 8).SequenceEqual(bgraBytes)) wrong.Add("Bgra8888 pixels changed on the way through");
            if (passed.Format != PixelFormats.Pbgra32 || !passed.IsFrozen) wrong.Add($"Bgra8888 gave {passed.Format}, frozen={passed.IsFrozen}");
            if (bgra.GetPixels() == IntPtr.Zero) wrong.Add("the caller's Bgra8888 bitmap was disposed");
        }
        return CheckKit.Tally(name, 2, wrong);
    }

    private static byte[] Pixels(System.Windows.Media.Imaging.BitmapSource source, int bytes)
    {
        var pixels = new byte[bytes];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        return pixels;
    }

    // ---------- render lock ----------

    private static async Task<Check> RenderCancellationAsync(Func<string, string> file)
    {
        const string name = "pdf: a cancelled render leaves the render lock usable";
        var wrong = new List<string>();
        var field = typeof(PdfDoc).GetField("RenderLock", BindingFlags.NonPublic | BindingFlags.Static);
        if (field?.GetValue(null) is not SemaphoreSlim renderLock)
            return new Check(name, false, "PdfDoc.RenderLock was not found by reflection; update this check with the field");

        var path = file("render.pdf");
        WriteDocument(path, 1);
        var doc = new PdfDoc(path);
        if (renderLock.CurrentCount != 1) wrong.Add($"the lock started at {renderLock.CurrentCount}");

        // Already cancelled: the wait itself refuses.
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            if (!await ThrowsAsync<OperationCanceledException>(() => doc.RenderPageAsync(0, 200, cancelled.Token)))
                wrong.Add("a cancelled token did not throw OperationCanceledException");
        }
        if (renderLock.CurrentCount != 1) wrong.Add($"the lock count is {renderLock.CurrentCount} after a cancelled render");

        // Cancelled while queued behind another render: the waiter never acquired the lock,
        // so it must not release one either (that would push the count past 1).
        await renderLock.WaitAsync();
        try
        {
            using var queued = new CancellationTokenSource();
            var waiting = doc.RenderPageAsync(0, 200, queued.Token);
            await Task.Delay(100);
            if (waiting.IsCompleted) wrong.Add("the queued render did not wait for the lock");
            queued.Cancel();
            if (!await ThrowsAsync<OperationCanceledException>(() => waiting))
                wrong.Add("cancelling a queued render did not throw OperationCanceledException");
        }
        finally
        {
            renderLock.Release();
        }
        if (renderLock.CurrentCount != 1) wrong.Add($"the lock count is {renderLock.CurrentCount} after a queued render was cancelled");

        var rendered = await doc.RenderPageAsync(0, 200, CancellationToken.None);
        if (rendered.PixelWidth != 200 || !rendered.IsFrozen) wrong.Add($"the next render is {rendered.PixelWidth}px wide, frozen={rendered.IsFrozen}");
        if (renderLock.CurrentCount != 1) wrong.Add($"the lock count is {renderLock.CurrentCount} at the end");
        return CheckKit.Tally(name, 6, wrong);
    }

    private static async Task<bool> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); return false; }
        catch (T) { return true; }
    }

    // ---------- display size ----------

    private static Check DisplaySize(Func<string, string> file)
    {
        const string name = "pdf: display size swaps for quarter turns only";
        var path = file("display.pdf");
        WriteSinglePagePdf(path, 400, 600);
        var doc = new PdfDoc(path);
        var wrong = new List<string>();
        var cases = new (PdfRotation Rotation, (double, double) Size)[]
        {
            (PdfRotation.Rotate0, (400, 600)),
            (PdfRotation.Rotate90, (600, 400)),
            (PdfRotation.Rotate180, (400, 600)),
            (PdfRotation.Rotate270, (600, 400)),
        };
        if (doc.Rotation != PdfRotation.Rotate0) wrong.Add($"a new document starts rotated {doc.Rotation}");
        foreach (var (rotation, size) in cases)
        {
            if (doc.GetDisplaySize(0, rotation) != size) wrong.Add($"{rotation} explicit: {doc.GetDisplaySize(0, rotation)}, expected {size}");
            doc.Rotation = rotation;
            if (doc.GetDisplaySize(0) != size) wrong.Add($"{rotation} live: {doc.GetDisplaySize(0)}, expected {size}");
            if (doc.PageSizes[0] != (400.0, 600.0)) wrong.Add($"{rotation}: the stored page size changed to {doc.PageSizes[0]}");
        }
        return CheckKit.Tally(name, cases.Length, wrong);
    }
}
