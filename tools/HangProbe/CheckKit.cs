using System.IO;

namespace HangProbe;

/// <summary>
/// Small helpers the newer check groups share. A check that throws is reported as a failed
/// <see cref="Check"/> carrying the exception, so one broken check cannot abort the checks
/// after it (or the run).
/// </summary>
internal static class CheckKit
{
    public static Check Run(string name, Func<Check> check)
    {
        try { return check(); }
        catch (Exception ex) { return Threw(name, ex); }
    }

    public static async Task<Check> RunAsync(string name, Func<Task<Check>> check)
    {
        try { return await check(); }
        catch (Exception ex) { return Threw(name, ex); }
    }

    /// <summary>For a step that yields several checks: a throw leaves one failed check under <paramref name="name"/>.</summary>
    public static async Task<IEnumerable<Check>> RunManyAsync(string name, Func<Task<IEnumerable<Check>>> checks)
    {
        try { return await checks(); }
        catch (Exception ex) { return new[] { Threw(name, ex) }; }
    }

    /// <summary>One check for a table: it passes when no case came out wrong, and lists the wrong ones otherwise.</summary>
    public static Check Tally(string name, int cases, IReadOnlyCollection<string> wrong, string? note = null) =>
        new(name, wrong.Count == 0,
            wrong.Count == 0 ? $"{cases} cases verified" + (note is null ? "" : $"; {note}") : string.Join("; ", wrong));

    private static Check Threw(string name, Exception ex) => new(name, false, $"threw {ex.GetType().Name}: {ex.Message}");
}

/// <summary>The app's own error log, which checks read to prove something was logged rather than just thrown away.</summary>
internal static class ProbeLog
{
    public static string Path => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PdfLiteViewer.log");

    public static bool Contains(string marker)
    {
        try
        {
            using var file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file);
            return reader.ReadToEnd().Contains(marker, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;       // absent, or briefly held by a writer: the poll tries again
        }
    }

    public static Task<bool> UntilContainsAsync(string marker, TimeSpan timeout) =>
        Poll.UntilAsync(() => Contains(marker), timeout);

    /// <summary>The log's size in bytes (0 when it does not exist yet): a mark to read the entries written after it.</summary>
    public static long Length()
    {
        try { return new FileInfo(Path).Length; }
        catch (IOException) { return 0; }
    }

    /// <summary>Everything written after <paramref name="offset"/>.</summary>
    public static string TextSince(long offset)
    {
        try
        {
            using var file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            file.Seek(Math.Min(offset, file.Length), SeekOrigin.Begin);
            using var reader = new StreamReader(file);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return "";
        }
    }

    public static string NewMarker(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
