using System.IO;
using System.Text;

namespace HangProbe;

/// <summary>
/// Writes a many-page PDF on the fly, so the probe has a realistic large document
/// without a multi-megabyte fixture living in git. Pure PDF syntax, byte-identical
/// for a given page count.
/// </summary>
internal static class StressPdf
{
    public static string Create(int pageCount, string path)
    {
        var body = new MemoryStream();
        var offsets = new List<long> { 0 };   // object 0 is the free head

        void Obj(int number, string content)
        {
            while (offsets.Count <= number) offsets.Add(0);
            offsets[number] = body.Position;
            Write(body, $"{number} 0 obj\n{content}\nendobj\n");
        }

        Write(body, "%PDF-1.4\n");

        // 1 catalog, 2 page tree, 3 font, then per page: (4 + 2i) page, (5 + 2i) contents,
        // then the outline root followed by one bookmark per page.
        int outlineRoot = 4 + 2 * pageCount;
        var kids = new StringBuilder();
        for (int i = 0; i < pageCount; i++)
            kids.Append(4 + 2 * i).Append(" 0 R ");

        Obj(1, $"<< /Type /Catalog /Pages 2 0 R /Outlines {outlineRoot} 0 R >>");
        Obj(2, $"<< /Type /Pages /Kids [{kids.ToString().TrimEnd()}] /Count {pageCount} >>");
        Obj(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        for (int i = 0; i < pageCount; i++)
        {
            int pageObj = 4 + 2 * i;
            int streamObj = pageObj + 1;

            Obj(pageObj,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {MediaBox(i)}] /Contents {streamObj} 0 R " +
                "/Resources << /Font << /F1 3 0 R >> >> >>");

            // A few text lines per page: enough for PDFium to do real work, small on disk.
            var text = new StringBuilder();
            text.Append("BT /F1 24 Tf 72 700 Td (Page ").Append(i + 1).Append(") Tj ET\n");
            for (int line = 0; line < 12; line++)
                text.Append("BT /F1 11 Tf 72 ").Append(660 - line * 18)
                    .Append(" Td (The quick brown fox jumps over the lazy dog. Line ")
                    .Append(line + 1).Append(") Tj ET\n");

            var stream = text.ToString();
            Obj(streamObj, $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream");
        }

        // A bookmark per page: the sidebar's tree, and the "which chapter am I in" sync that
        // runs on every scroll, both have to stay cheap on a book-sized outline.
        Obj(outlineRoot,
            $"<< /Type /Outlines /First {outlineRoot + 1} 0 R /Last {outlineRoot + pageCount} 0 R " +
            $"/Count {pageCount} >>");

        for (int i = 0; i < pageCount; i++)
        {
            int self = outlineRoot + 1 + i;
            var links = new StringBuilder();
            if (i > 0) links.Append($" /Prev {self - 1} 0 R");
            if (i < pageCount - 1) links.Append($" /Next {self + 1} 0 R");

            Obj(self, $"<< /Title (Chapter {i + 1}) /Parent {outlineRoot} 0 R " +
                      $"/Dest [{4 + 2 * i} 0 R /Fit]{links} >>");
        }

        long xrefPos = body.Position;
        int size = offsets.Count;
        var xref = new StringBuilder();
        xref.Append("xref\n0 ").Append(size).Append('\n');
        xref.Append("0000000000 65535 f \n");
        for (int i = 1; i < size; i++)
            xref.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        xref.Append("trailer\n<< /Size ").Append(size).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xrefPos).Append("\n%%EOF\n");
        Write(body, xref.ToString());

        File.WriteAllBytes(path, body.ToArray());
        return path;
    }

    /// <summary>
    /// Deliberately not a uniform document: real PDFs mix portrait, landscape and A4 pages,
    /// and a virtualizing panel has to estimate the height of every page it has not realized
    /// yet. Mixed sizes are what makes that estimate wrong, so the probe scrolls through them.
    /// </summary>
    private static string MediaBox(int pageIndex) => pageIndex % 9 == 8
        ? "792 612"                                  // landscape letter
        : pageIndex % 14 == 13 ? "595 842"           // A4
        : "612 792";                                 // portrait letter

    private static void Write(Stream s, string ascii)
    {
        var bytes = Encoding.ASCII.GetBytes(ascii);
        s.Write(bytes, 0, bytes.Length);
    }
}
