using System.IO;
using PdfLiteViewer;

if (args.Length == 0) { Console.Error.WriteLine("usage: ChapterSmoke <pdf>..."); return 2; }

foreach (var path in args)
{
    Console.WriteLine($"=== {Path.GetFileName(path)} ===");
    try
    {
        var doc = new PdfDoc(path);
        var roots = doc.GetChapters(CancellationToken.None, "Untitled chapter");
        Dump(roots, 0);
        var nav = ChapterItem.FlattenNavigable(roots);
        Console.WriteLine($"pages={doc.PageCount} roots={roots.Count} navigable={nav.Count}");
        foreach (var n in nav)
            Console.WriteLine($"  NAV depth={n.Depth} page={n.PageIndex} order={n.SourceOrder} '{n.Title}'");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
    }
}
return 0;

static void Dump(IEnumerable<ChapterItem> nodes, int indent)
{
    foreach (var n in nodes)
    {
        var nav = n.PageIndex is int p ? $"->p{p}" : "non-nav";
        Console.WriteLine($"{new string(' ', indent * 2)}- '{n.Title}' {nav} d={n.Depth}");
        Dump(n.Children, indent + 1);
    }
}
