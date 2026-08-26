using System.IO;
using System.Text;

namespace StoreShots;

/// <summary>
/// Generates the demo document the Store screenshots show: the app's own user guide,
/// with a nested chapter outline, mixed page orientations, and no version numbers
/// (so the captures never go stale when the version bumps).
/// Pure PDF syntax, deterministic output — same approach as tools/HangProbe/StressPdf.
/// </summary>
internal static class DemoPdf
{
    private sealed record Chapter(string Title, int Depth, string[] Paragraphs, bool Landscape = false);

    private static readonly Chapter[] Chapters =
    {
        new("Welcome", 0, new[]
        {
            "PDF Lite Viewer is a free, lightweight PDF viewer for Windows.",
            "It opens PDF documents and displays them well - and that is the whole idea.",
            "No accounts, no bundled extras, no clutter. Just your document.",
            "This guide is itself a PDF: everything you see here works on any document you open.",
        }),
        new("Opening documents", 0, new[]
        {
            "Open a PDF with Ctrl+O, drag a file onto the window, or double-click",
            "any .pdf file once the viewer is set as the default PDF app.",
            "Large documents open instantly: pages are rendered lazily as you reach them.",
        }),
        new("Viewing modes", 0, new[]
        {
            "Three modes cover the ways people actually read:",
            "Facing shows two pages side by side, like an open book.",
            "Single shows one page at a time - ideal with full screen (F11).",
            "Scroll runs the whole document as one continuous vertical strip.",
        }),
        new("Facing pages", 1, new[]
        {
            "Press 2 for facing mode. The cover stays alone, then pages pair up",
            "exactly the way a printed book lays them out.",
        }),
        new("Single page", 1, new[]
        {
            "Press 1 for single-page mode, and F11 for a distraction-free full screen.",
            "Press Escape to leave full screen again.",
        }),
        new("Continuous scroll", 1, new[]
        {
            "Press 3 to scroll the document as one strip. Only the pages near the",
            "viewport are kept in memory, so even huge documents stay fast.",
        }),
        new("Chapter navigation", 0, new[]
        {
            "Press F4 to open the chapter sidebar. It shows the document's embedded",
            "outline as a tree - this guide's outline is on the left right now.",
            "Click a chapter to jump to it. The current chapter stays highlighted",
            "as you read, and nested sections expand as you reach them.",
        }),
        new("Zoom and rotation", 0, new[]
        {
            "Zoom with Ctrl and + or -, or Ctrl and the mouse wheel.",
            "Ctrl+0 fits the page to the window again.",
            "Ctrl+R rotates the view 90 degrees clockwise - the file on disk is never changed.",
        }),
        new("Wide pages", 1, new[]
        {
            "Landscape pages, like this one, are handled naturally in every mode.",
        }, Landscape: true),
        new("Printing", 0, new[]
        {
            "Press Ctrl+P for the print preview. Pick a printer, a page range,",
            "the number of copies, and black-and-white or draft mode.",
            "The preview shows exactly how each page lands on the selected paper size.",
        }),
        new("Keyboard reference", 0, new[]
        {
            "Ctrl+O  Open        Ctrl+P  Print       F4  Chapters",
            "1 / 2 / 3  View modes            F11  Full screen",
            "Arrows / PgUp / PgDn  Turn pages    Home / End  First / last page",
            "Ctrl + / - / 0  Zoom              Ctrl+R  Rotate",
        }),
        new("Languages", 0, new[]
        {
            "The interface ships in fourteen languages, including full right-to-left",
            "layout for Arabic. The viewer follows your Windows display language.",
        }),
        new("About and support", 0, new[]
        {
            "The About window (F1) shows the version, the MIT license, and links to",
            "Green Yoga Inc - the publisher - plus built-in web support.",
            "Support opens only when you ask it to; PDF viewing itself never goes online.",
        }),
    };

    public static string Create(string path)
    {
        var body = new MemoryStream();
        var offsets = new List<long> { 0 };

        void Obj(int number, string content)
        {
            while (offsets.Count <= number) offsets.Add(0);
            offsets[number] = body.Position;
            Write(body, $"{number} 0 obj\n{content}\nendobj\n");
        }

        Write(body, "%PDF-1.4\n");

        int pageCount = Chapters.Length + 1;               // cover + one page per chapter
        int firstPageObj = 5;                              // 1 catalog, 2 pages, 3+4 fonts
        int outlineRoot = firstPageObj + 2 * pageCount;

        var kids = new StringBuilder();
        for (int i = 0; i < pageCount; i++)
            kids.Append(firstPageObj + 2 * i).Append(" 0 R ");

        Obj(1, $"<< /Type /Catalog /Pages 2 0 R /Outlines {outlineRoot} 0 R /PageMode /UseOutlines >>");
        Obj(2, $"<< /Type /Pages /Kids [{kids.ToString().TrimEnd()}] /Count {pageCount} >>");
        Obj(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>");
        Obj(4, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        // Cover page.
        EmitPage(Obj, firstPageObj, landscape: false, CoverContent());

        // Chapter pages.
        for (int i = 0; i < Chapters.Length; i++)
        {
            var ch = Chapters[i];
            EmitPage(Obj, firstPageObj + 2 * (i + 1), ch.Landscape, ChapterContent(ch, i));
        }

        // Outline: nested via Depth (0 = root chapter, 1 = child of the previous depth-0 chapter).
        EmitOutline(Obj, outlineRoot, firstPageObj);

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

    private static void EmitPage(Action<int, string> obj, int pageObj, bool landscape, string content)
    {
        string box = landscape ? "0 0 792 612" : "0 0 612 792";
        obj(pageObj,
            $"<< /Type /Page /Parent 2 0 R /MediaBox [{box}] /Contents {pageObj + 1} 0 R " +
            "/Resources << /Font << /Fb 3 0 R /Fr 4 0 R >> >> >>");
        obj(pageObj + 1, $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream");
    }

    private static string CoverContent()
    {
        var t = new StringBuilder();
        // Soft green cover band.
        t.Append("0.10 0.42 0.18 rg 0 512 612 280 re f\n");
        t.Append("1 1 1 rg BT /Fb 40 Tf 72 640 Td (PDF Lite Viewer) Tj ET\n");
        t.Append("1 1 1 rg BT /Fr 18 Tf 72 600 Td (User Guide) Tj ET\n");
        t.Append("0 0 0 rg BT /Fr 14 Tf 72 430 Td (A free, lightweight PDF viewer for Windows.) Tj ET\n");
        t.Append("BT /Fr 14 Tf 72 406 Td (No bloat - it opens PDFs and displays them well.) Tj ET\n");
        t.Append("0.4 0.4 0.4 rg BT /Fr 11 Tf 72 100 Td (Green Yoga Inc - Freeware, MIT License) Tj ET\n");
        return t.ToString();
    }

    private static string ChapterContent(Chapter ch, int index)
    {
        bool land = ch.Landscape;
        int width = land ? 792 : 612;
        int top = land ? 540 : 700;
        var t = new StringBuilder();
        // Heading + rule.
        t.Append($"0.10 0.42 0.18 rg BT /Fb 26 Tf 72 {top} Td ({Escape(ch.Title)}) Tj ET\n");
        t.Append($"0.10 0.42 0.18 RG 2 w 72 {top - 14} m {width - 72} {top - 14} l S\n");
        int y = top - 56;
        foreach (var p in ch.Paragraphs)
        {
            t.Append($"0 0 0 rg BT /Fr 13 Tf 72 {y} Td ({Escape(p)}) Tj ET\n");
            y -= 24;
        }
        // Footer page marker (chapter number, stable regardless of app version).
        t.Append($"0.5 0.5 0.5 rg BT /Fr 10 Tf 72 60 Td (PDF Lite Viewer - User Guide) Tj ET\n");
        return t.ToString();
    }

    private static void EmitOutline(Action<int, string> obj, int outlineRoot, int firstPageObj)
    {
        // Build node list: page 0 is the cover (no outline entry); chapter i -> page i+1.
        int n = Chapters.Length;
        var first = new int?[n];
        var last = new int?[n];
        var parent = new int?[n];
        var prev = new int?[n];
        var next = new int?[n];
        var childCount = new int[n];

        int? lastRootIndex = null;
        var lastAtDepth = new Dictionary<int, int>();
        var rootChildren = new List<int>();

        for (int i = 0; i < n; i++)
        {
            int depth = Chapters[i].Depth;
            if (depth == 0)
            {
                if (lastRootIndex is int lr) { next[lr] = i; prev[i] = lr; }
                lastRootIndex = i;
                rootChildren.Add(i);
            }
            else
            {
                int p = lastAtDepth[depth - 1];
                parent[i] = p;
                if (last[p] is int sib) { next[sib] = i; prev[i] = sib; }
                else first[p] = i;
                last[p] = i;
                childCount[p]++;
            }
            lastAtDepth[depth] = i;
        }

        obj(outlineRoot,
            $"<< /Type /Outlines /First {outlineRoot + 1 + rootChildren[0]} 0 R " +
            $"/Last {outlineRoot + 1 + rootChildren[^1]} 0 R /Count {n} >>");

        for (int i = 0; i < n; i++)
        {
            int self = outlineRoot + 1 + i;
            int pageObj = firstPageObj + 2 * (i + 1);
            var sb = new StringBuilder();
            sb.Append($"<< /Title ({Escape(Chapters[i].Title)}) ");
            sb.Append($"/Parent {(parent[i] is int p ? outlineRoot + 1 + p : outlineRoot)} 0 R ");
            if (prev[i] is int pr) sb.Append($"/Prev {outlineRoot + 1 + pr} 0 R ");
            if (next[i] is int nx) sb.Append($"/Next {outlineRoot + 1 + nx} 0 R ");
            if (first[i] is int f) sb.Append($"/First {outlineRoot + 1 + f} 0 R /Last {outlineRoot + 1 + last[i]!.Value} 0 R /Count {childCount[i]} ");
            sb.Append($"/Dest [{pageObj} 0 R /Fit] >>");
            obj(self, sb.ToString());
        }
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static void Write(Stream s, string ascii)
    {
        var bytes = Encoding.ASCII.GetBytes(ascii);
        s.Write(bytes, 0, bytes.Length);
    }
}
