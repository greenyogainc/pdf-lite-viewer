using System.IO;
using PdfLiteViewer;

if (args.Length == 0) { Console.Error.WriteLine("usage: ChapterSmoke <pdf>..."); return 2; }

// PowerShell does not expand wildcards for native commands, so the documented
// `tools\fixtures\*.pdf` arrives as one literal path. Expand it here, or the run
// reports a single unreadable "file" and nothing is actually checked.
var files = new List<string>();
foreach (var arg in args)
{
    if (!arg.Contains('*') && !arg.Contains('?')) { files.Add(arg); continue; }

    var dir = Path.GetDirectoryName(arg);
    if (string.IsNullOrEmpty(dir)) dir = ".";
    var matches = Directory.Exists(dir)
        ? Directory.GetFiles(dir, Path.GetFileName(arg))
        : Array.Empty<string>();

    if (matches.Length == 0)
    {
        Console.Error.WriteLine($"no files match '{arg}'");
        return 2;
    }

    Array.Sort(matches, StringComparer.OrdinalIgnoreCase);   // GetFiles order is not guaranteed
    files.AddRange(matches);
}

if (files.Count == 0) { Console.Error.WriteLine("no files to check"); return 2; }

int failed = 0;
foreach (var path in files)
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
        failed++;
    }
}
// A smoke test that prints FAIL and exits 0 is not a smoke test.
return failed == 0 ? 0 : 1;

static void Dump(IEnumerable<ChapterItem> nodes, int indent)
{
    foreach (var n in nodes)
    {
        var nav = n.PageIndex is int p ? $"->p{p}" : "non-nav";
        Console.WriteLine($"{new string(' ', indent * 2)}- '{n.Title}' {nav} d={n.Depth}");
        Dump(n.Children, indent + 1);
    }
}
