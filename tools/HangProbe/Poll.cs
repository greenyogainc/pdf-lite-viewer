using System.Diagnostics;

namespace HangProbe;

/// <summary>
/// Waits for a condition instead of sleeping a fixed time: a check that polls passes as soon
/// as the state is right and fails with a clear timeout when it never is.
/// </summary>
internal static class Poll
{
    /// <summary>
    /// Re-evaluates <paramref name="condition"/> until it holds or <paramref name="timeout"/>
    /// elapses, yielding to the caller's context between tries (on the UI thread that keeps
    /// the dispatcher pumping). Returns whether the condition held.
    /// </summary>
    public static async Task<bool> UntilAsync(Func<bool> condition, TimeSpan timeout, TimeSpan? interval = null)
    {
        var delay = interval ?? TimeSpan.FromMilliseconds(25);
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (condition()) return true;
            if (clock.Elapsed >= timeout) return false;
            await Task.Delay(delay);
        }
    }
}
