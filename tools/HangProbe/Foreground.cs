using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HangProbe;

/// <summary>
/// Real keyboard focus needs the window in the foreground, and Windows ignores
/// <see cref="Window.Activate"/> from a process the user is not working in. Checks that assert
/// real keyboard focus failed whenever something else on the desktop held the foreground. Sharing
/// the foreground thread's input queue for the length of the request is what lets it through.
/// </summary>
internal static class Foreground
{
    private const int Attempts = 3;
    private static readonly TimeSpan AttemptLimit = TimeSpan.FromSeconds(2);

    public static async Task<bool> TakeAsync(Window window)
    {
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            Request(window);
            if (await Poll.UntilAsync(() => window.IsActive, AttemptLimit)) return true;
        }
        return false;
    }

    /// <summary>Takes the foreground and moves focus to <paramref name="target"/>, trying again if something takes the foreground back.</summary>
    public static async Task<bool> FocusAsync(Window window, UIElement target, Func<bool> hasFocus)
    {
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            await TakeAsync(window);
            target.Focus();
            if (await Poll.UntilAsync(hasFocus, AttemptLimit)) return true;
        }
        return false;
    }

    private static void Request(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        uint own = GetCurrentThreadId();
        uint holder = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        bool attached = holder != 0 && holder != own && AttachThreadInput(own, holder, true);
        try
        {
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
            window.Activate();
        }
        finally
        {
            if (attached) AttachThreadInput(own, holder, false);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
