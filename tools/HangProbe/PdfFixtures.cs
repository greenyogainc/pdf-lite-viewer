using System.IO;
using System.Text;

namespace HangProbe;

/// <summary>
/// Hand-written PDFs for the checks that need a file shape the stress document does not
/// have: no pages, one page of a chosen size, or a page tree with an outline. Everything is
/// pure ASCII, so a character index is a byte offset, and the cross-reference table is
/// generated from the bytes actually written - hand-typed offsets make PDFium and PdfPig
/// rebuild the table, which is not the path a real file takes.
/// </summary>
internal static class PdfFixtures
{
    /// <summary>
    /// One entry of a generated outline. <paramref name="Target"/> is the raw destination or
    /// action entries for the item dictionary (see <see cref="Dest"/>); null leaves a
    /// container node. <paramref name="Title"/> null leaves out /Title altogether.
    /// </summary>
    internal sealed record OutlineItem(string? Title, string? Target = null, params OutlineItem[] Children);

    /// <summary>Object <c>n</c> of the file is <c>objects[n - 1]</c>; object 1 must be the catalog.</summary>
    internal static void Write(string path, params string[] objects)
    {
        var body = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        foreach (var content in objects)
        {
            offsets.Add(body.Length);
            body.Append(offsets.Count).Append(" 0 obj\n").Append(content).Append("\nendobj\n");
        }

        int xref = body.Length;
        body.Append("xref\n0 ").Append(offsets.Count + 1).Append('\n');
        body.Append("0000000000 65535 f \n");
        foreach (int off in offsets) body.Append(off.ToString("D10")).Append(" 00000 n \n");
        body.Append("trailer\n<< /Size ").Append(offsets.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xref).Append("\n%%EOF\n");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(body.ToString()));
    }

    /// <summary>A well-formed PDF whose page tree is empty, xref offsets and all.</summary>
    internal static void WriteZeroPagePdf(string path) =>
        Write(path,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>");

    /// <summary>One-page PDF whose MediaBox is exactly the given size in points.</summary>
    internal static void WriteSinglePagePdf(string path, int widthPt, int heightPt) =>
        WriteDocument(path, 1, mediaBox: $"[0 0 {widthPt} {heightPt}]");

    /// <summary>
    /// A document of <paramref name="pageCount"/> pages. <paramref name="pageEntries"/> is added
    /// to every page dictionary (a /CropBox, a /Rotate); <paramref name="outline"/> becomes the
    /// bookmark tree. Page n (1-based) is object n + 2, which <see cref="Dest"/> relies on.
    /// </summary>
    internal static void WriteDocument(string path, int pageCount, string mediaBox = "[0 0 612 792]",
        string pageEntries = "", params OutlineItem[] outline)
    {
        int content = pageCount + 3, font = pageCount + 4, outlines = pageCount + 5;
        var objects = new List<string>
        {
            outline.Length == 0
                ? "<< /Type /Catalog /Pages 2 0 R >>"
                : $"<< /Type /Catalog /Pages 2 0 R /Outlines {outlines} 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(3, pageCount).Select(n => $"{n} 0 R"))}] /Count {pageCount} >>",
        };
        for (int i = 0; i < pageCount; i++)
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox {mediaBox} {pageEntries} /Contents {content} 0 R " +
                        $"/Resources << /Font << /F1 {font} 0 R >> >> >>");

        const string text = "BT /F1 24 Tf 72 72 Td (Big) Tj ET\n";
        objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(text)} >>\nstream\n{text}endstream");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        if (outline.Length > 0)
        {
            var numbers = new Dictionary<OutlineItem, int>(ReferenceEqualityComparer.Instance);
            int next = outlines + 1;
            Number(outline, numbers, ref next);
            objects.Add($"<< /Type /Outlines /First {numbers[outline[0]]} 0 R /Last {numbers[outline[^1]]} 0 R /Count {numbers.Count} >>");
            Emit(outline, outlines, numbers, objects);
        }
        Write(path, objects.ToArray());
    }

    /// <summary>An internal destination: the 1-based page of a <see cref="WriteDocument"/> file.</summary>
    internal static string Dest(int page) => $"/Dest [{page + 2} 0 R /Fit]";

    private static void Number(IReadOnlyList<OutlineItem> items, Dictionary<OutlineItem, int> numbers, ref int next)
    {
        foreach (var item in items)
        {
            numbers[item] = next++;
            Number(item.Children, numbers, ref next);
        }
    }

    /// <summary>Pre-order, which is also object-number order.</summary>
    private static void Emit(IReadOnlyList<OutlineItem> siblings, int parent,
        Dictionary<OutlineItem, int> numbers, List<string> objects)
    {
        for (int i = 0; i < siblings.Count; i++)
        {
            var item = siblings[i];
            var entry = new StringBuilder("<< ");
            if (item.Title is not null) entry.Append("/Title (").Append(item.Title).Append(") ");
            entry.Append("/Parent ").Append(parent).Append(" 0 R ");
            if (i > 0) entry.Append("/Prev ").Append(numbers[siblings[i - 1]]).Append(" 0 R ");
            if (i < siblings.Count - 1) entry.Append("/Next ").Append(numbers[siblings[i + 1]]).Append(" 0 R ");
            if (item.Children.Length > 0)
                entry.Append("/First ").Append(numbers[item.Children[0]]).Append(" 0 R /Last ")
                    .Append(numbers[item.Children[^1]]).Append(" 0 R /Count ").Append(item.Children.Length).Append(' ');
            if (item.Target is not null) entry.Append(item.Target).Append(' ');
            objects.Add(entry.Append(">>").ToString());
            Emit(item.Children, numbers[item], numbers, objects);
        }
    }
}
