using System.Runtime.InteropServices;
using System.Text;

namespace HangProbe;

/// <summary>How an expectation answers the dialog it matches.</summary>
internal enum DialogAction
{
    /// <summary>Press OK (IDOK).</summary>
    PressOk,

    /// <summary>Press Cancel (IDCANCEL).</summary>
    PressCancel,

    /// <summary>Type a path into a file dialog's file-name box, then press OK.</summary>
    FillPath,
}

/// <summary>A dialog the dismisser handled: its caption and the message text.</summary>
internal sealed record DialogRecord(string Caption, string Text);

/// <summary>
/// One scoped promise that a dialog will appear: created by <see cref="DialogDismisser.Expect"/>,
/// disposed when the check that wanted it is done. A scope that ends without the dialog having
/// appeared is reported by <see cref="DialogDismisser.FinalChecks"/>, and <see cref="Verify"/>
/// fails for it, so a check cannot pass without exercising the dialog path it names.
/// </summary>
internal sealed class DialogExpectation : IDisposable
{
    private readonly DialogDismisser _owner;
    private readonly List<DialogRecord> _handled = new();
    private int _claimed;
    private int _fired;
    private bool _retired;

    internal DialogExpectation(DialogDismisser owner, string captionContains, DialogAction action,
        string? path, string? textContains, int times)
    {
        _owner = owner;
        CaptionContains = captionContains;
        Action = action;
        Path = path;
        TextContains = textContains;
        Times = times;
    }

    public string CaptionContains { get; }
    public string? TextContains { get; }
    public DialogAction Action { get; }
    public string? Path { get; }
    public int Times { get; }

    /// <summary>How many dialogs have been answered so far.</summary>
    public int Fired => Volatile.Read(ref _fired);

    public bool Satisfied => Fired >= Times;

    /// <summary>The dialogs answered so far, in order.</summary>
    public IReadOnlyList<DialogRecord> Handled
    {
        get { lock (_handled) return _handled.ToList(); }
    }

    /// <summary>Message text of the first answered dialog, or empty if none appeared.</summary>
    public string Text => Handled.FirstOrDefault()?.Text ?? "";

    /// <summary>Resolves once the expected dialog has been answered, or false after <paramref name="timeout"/>.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout) => Poll.UntilAsync(() => Satisfied, timeout);

    public Check Verify(string name)
    {
        var handled = Handled;
        return new Check(name, Satisfied, Satisfied
            ? $"{handled.Count} dialog(s) answered ({Action}): {Describe(handled)}"
            : $"{Wanted} was never answered");
    }

    /// <summary>What this expectation was waiting for, in words.</summary>
    internal string Wanted =>
        (CaptionContains.Length == 0 ? "a dialog of any caption" : $"a dialog with caption containing '{CaptionContains}'") +
        (TextContains is null ? "" : $" and text containing '{TextContains}'");

    internal static string Describe(IEnumerable<DialogRecord> records) =>
        string.Join(" | ", records.Select(r => $"[{r.Caption}] {r.Text.ReplaceLineEndings(" ")}"));

    /// <summary>Reserves a slot for a matching dialog. Called from the dismisser thread only.</summary>
    internal bool TryClaim(string caption, string text)
    {
        if (_claimed >= Times) return false;
        if (!caption.Contains(CaptionContains, StringComparison.OrdinalIgnoreCase)) return false;
        if (TextContains is not null && !text.Contains(TextContains, StringComparison.OrdinalIgnoreCase)) return false;
        _claimed++;
        return true;
    }

    /// <summary>Called once the dialog has actually been answered.</summary>
    internal void Fire(DialogRecord record)
    {
        lock (_handled) _handled.Add(record);
        Interlocked.Increment(ref _fired);
    }

    public void Dispose()
    {
        if (_retired) return;
        _retired = true;
        _owner.Retire(this);
    }
}

/// <summary>
/// Handles the native dialogs this process opens, so production code that shows them modally
/// can be exercised for real. A modal dialog blocks the UI thread inside the native message
/// loop, so everything here happens on a background thread.
///
/// Only visible top-level <c>#32770</c> windows owned by this process are ever touched.
/// A dialog that no <see cref="Expect"/> scope claims is still closed - the run must not hang
/// on it - but it is counted and its text kept, and <see cref="FinalChecks"/> fails the run.
///
/// It runs for the whole process lifetime in every HangProbe mode, starting before any
/// window can exist.
/// </summary>
internal sealed class DialogDismisser : IDisposable
{
    private const string DialogClass = "#32770";
    private const int PollMs = 50;
    private const int SettleMs = 100;       // let a new dialog finish creating its controls
    private const int RetryMs = 500;
    private const int FillGiveUpMs = 10_000;
    private const int ReadFailureLimit = 20;

    // An Open dialog's file-name box is a ComboBoxEx32 with this control ID; the Edit sits
    // two levels down (ComboBoxEx32 > ComboBox > Edit).
    private const int FileNameComboId = 0x047C;
    // A Save dialog nests its file-name Edit deep inside a DirectUI host, under this ID. The
    // address toolbar has the same ID, so the class has to match too.
    private const int SaveFileNameEditId = 1001;
    // MessageBox's message text is the static control with ID -1.
    private const int MessageTextId = 0xFFFF;

    private const int IdOk = 1;
    private const int IdCancel = 2;
    private const int IdNo = 7;

    // What an unclaimed (or stuck) dialog is sent, in turn: the safest answers first.
    private static readonly (uint Message, int Id)[] Dismissals =
    {
        (Native.WmCommand, IdCancel),
        (Native.WmCommand, IdNo),
        (Native.WmCommand, IdOk),
        (Native.WmClose, 0),
    };

    private sealed record Seen(string Caption, string Text, bool Expected);

    private sealed class Handling(long firstSeen)
    {
        public readonly long FirstSeen = firstSeen;
        public long LastAct;
        public int Attempts;
        public int ReadFailures;
        public bool Resolved;
        public bool Fired;
        public string Caption = "";
        public string Text = "";
        public DialogExpectation? Match;
    }

    private readonly object _gate = new();
    private readonly List<DialogExpectation> _active = new();
    private readonly List<DialogExpectation> _unfulfilled = new();
    private readonly List<Seen> _seen = new();
    private readonly List<string> _faults = new();
    private readonly Dictionary<IntPtr, Handling> _handling = new();
    private readonly uint _processId = (uint)Environment.ProcessId;
    private readonly Thread _thread;
    private volatile bool _stop;

    public DialogDismisser()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "dialog-dismisser" };
        _thread.Start();
    }

    /// <summary>
    /// Registers an expectation. Dispose the result when the check that wanted it is done;
    /// <paramref name="times"/> dialogs are answered, any further match is unexpected.
    /// </summary>
    public DialogExpectation Expect(string captionContains, DialogAction action, string? path = null,
        string? textContains = null, int times = 1)
    {
        if (action == DialogAction.FillPath && string.IsNullOrEmpty(path))
            throw new ArgumentException("FillPath needs a path.", nameof(path));
        var expectation = new DialogExpectation(this, captionContains, action, path, textContains, times);
        lock (_gate) _active.Add(expectation);
        return expectation;
    }

    internal void Retire(DialogExpectation expectation)
    {
        lock (_gate)
        {
            _active.Remove(expectation);
            if (!expectation.Satisfied) _unfulfilled.Add(expectation);
        }
    }

    /// <summary>The two run-wide checks every HangProbe mode ends with.</summary>
    public IReadOnlyList<Check> FinalChecks()
    {
        List<Seen> unexpected;
        int total;
        string[] faults;
        List<DialogExpectation> missed;
        lock (_gate)
        {
            total = _seen.Count;
            unexpected = _seen.Where(s => !s.Expected).ToList();
            faults = _faults.ToArray();
            missed = _unfulfilled.Concat(_active.Where(e => !e.Satisfied)).ToList();
        }

        string unexpectedDetail = unexpected.Count == 0 && faults.Length == 0
            ? $"{total} dialog(s) appeared, every one expected"
            : $"{unexpected.Count} unexpected: " +
              DialogExpectation.Describe(unexpected.Select(s => new DialogRecord(s.Caption, s.Text))) +
              (faults.Length == 0 ? "" : $"; dismisser faults: {string.Join(" | ", faults)}");
        string missedDetail = missed.Count == 0
            ? "every expectation was answered"
            : "never answered: " + string.Join(", ", missed.Select(e => e.Wanted));

        return new[]
        {
            new Check("no unexpected dialogs", unexpected.Count == 0 && faults.Length == 0, unexpectedDetail),
            new Check("every dialog expectation fired", missed.Count == 0, missedDetail),
        };
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    // ---------- dismisser thread ----------

    private void Loop()
    {
        while (!_stop)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                // A fault must not end the loop (a later dialog would then hang the run), but
                // it must not pass silently either.
                lock (_gate) _faults.Add(ex.Message);
            }
            Thread.Sleep(PollMs);
        }
    }

    private void Tick()
    {
        var present = FindDialogs();
        foreach (var gone in _handling.Keys.Where(hwnd => !present.Contains(hwnd)).ToList())
            _handling.Remove(gone);

        long now = Environment.TickCount64;
        foreach (var hwnd in present)
        {
            if (!_handling.TryGetValue(hwnd, out var handling))
                _handling[hwnd] = handling = new Handling(now);
            if (now - handling.FirstSeen < SettleMs) continue;
            if (!handling.Resolved && !Resolve(hwnd, handling)) continue;
            if (handling.Attempts > 0 && now - handling.LastAct < RetryMs) continue;
            Act(hwnd, handling, now);
        }
    }

    private HashSet<IntPtr> FindDialogs()
    {
        var found = new HashSet<IntPtr>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            Native.GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId != _processId) return true;
            var className = new StringBuilder(64);
            if (Native.GetClassName(hwnd, className, className.Capacity) > 0 && className.ToString() == DialogClass)
                found.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Reads the dialog's caption and message and claims it for an expectation, if one matches.</summary>
    private bool Resolve(IntPtr hwnd, Handling handling)
    {
        var caption = Native.ReadText(hwnd);
        var messageControl = Native.GetDlgItem(hwnd, MessageTextId);
        var text = messageControl == IntPtr.Zero ? "" : Native.ReadText(messageControl);
        // A null read means the dialog's thread did not answer in time; look again next tick.
        if ((caption is null || text is null) && ++handling.ReadFailures < ReadFailureLimit)
            return false;

        handling.Caption = caption ?? "";
        handling.Text = text ?? "";
        lock (_gate)
        {
            handling.Match = _active.FirstOrDefault(e => e.TryClaim(handling.Caption, handling.Text));
            _seen.Add(new Seen(handling.Caption, handling.Text, handling.Match is not null));
        }
        handling.Resolved = true;
        return true;
    }

    private void Act(IntPtr hwnd, Handling handling, long now)
    {
        var expectation = handling.Match;
        if (expectation is null || handling.Attempts >= 3)
        {
            // Unclaimed, or the answer did not close it: walk the dismissals so the run cannot hang.
            var (message, id) = Dismissals[handling.Attempts % Dismissals.Length];
            Native.PostMessage(hwnd, message, (IntPtr)id, IntPtr.Zero);
        }
        else if (expectation.Action == DialogAction.FillPath)
        {
            if (!TryFillPath(hwnd, expectation.Path!))
            {
                // The file-name box is not there yet; retry on the next tick, but not forever.
                if (now - handling.FirstSeen < FillGiveUpMs) return;
                Native.PostMessage(hwnd, Native.WmCommand, (IntPtr)IdCancel, IntPtr.Zero);
                handling.Attempts = 3;      // abandon the expectation; it stays unfired and fails its check
                handling.LastAct = now;
                return;
            }
            Record(expectation, handling);
            Native.PostMessage(hwnd, Native.WmCommand, (IntPtr)IdOk, IntPtr.Zero);
        }
        else
        {
            int id = expectation.Action == DialogAction.PressCancel ? IdCancel : IdOk;
            Record(expectation, handling);
            Native.PostMessage(hwnd, Native.WmCommand, (IntPtr)id, IntPtr.Zero);
        }

        handling.Attempts++;
        handling.LastAct = now;
    }

    /// <summary>
    /// Runs before the answer is posted: the modal call can return, and its check read the
    /// expectation, as soon as the answer lands.
    /// </summary>
    private static void Record(DialogExpectation expectation, Handling handling)
    {
        if (handling.Fired) return;
        handling.Fired = true;
        expectation.Fire(new DialogRecord(handling.Caption, handling.Text));
    }

    /// <summary>Sets the file-name box to <paramref name="path"/>; false while the box does not exist or did not take it.</summary>
    private static bool TryFillPath(IntPtr dialog, string path)
    {
        var edit = FileNameEdit(dialog);
        if (edit == IntPtr.Zero) return false;
        return Native.SetText(edit, path) && Native.ReadText(edit) == path;
    }

    private static IntPtr FileNameEdit(IntPtr dialog)
    {
        var comboEx = Native.GetDlgItem(dialog, FileNameComboId);
        if (comboEx != IntPtr.Zero)
        {
            var combo = Native.FindWindowEx(comboEx, IntPtr.Zero, "ComboBox", null);
            return combo == IntPtr.Zero ? IntPtr.Zero : Native.FindWindowEx(combo, IntPtr.Zero, "Edit", null);
        }

        var found = IntPtr.Zero;
        Native.EnumChildWindows(dialog, (child, _) =>
        {
            var className = new StringBuilder(16);
            if (Native.GetDlgCtrlID(child) != SaveFileNameEditId ||
                Native.GetClassName(child, className, className.Capacity) == 0 || className.ToString() != "Edit")
                return true;
            found = child;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static class Native
    {
        public const uint WmClose = 0x0010;
        public const uint WmCommand = 0x0111;
        private const uint WmSetText = 0x000C;
        private const uint WmGetText = 0x000D;
        private const uint WmGetTextLength = 0x000E;
        private const uint SmtoAbortIfHung = 0x0002;
        private const uint TimeoutMs = 500;

        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern int GetDlgCtrlID(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hwnd, StringBuilder text, int capacity);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDlgItem(IntPtr dialog, int id);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);

        // PostMessage never waits on the target's thread, which may be inside a modal loop or
        // blocked; the reads and writes below bound their wait instead.
        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam,
            StringBuilder? lParam, uint flags, uint timeout, out IntPtr result);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam,
            string lParam, uint flags, uint timeout, out IntPtr result);

        /// <summary>The window's text, or null if its thread did not answer in time.</summary>
        public static string? ReadText(IntPtr hwnd)
        {
            if (SendMessageTimeout(hwnd, WmGetTextLength, IntPtr.Zero, (StringBuilder?)null,
                    SmtoAbortIfHung, TimeoutMs, out var length) == IntPtr.Zero)
                return null;
            var buffer = new StringBuilder((int)length + 1);
            if (SendMessageTimeout(hwnd, WmGetText, (IntPtr)buffer.Capacity, buffer,
                    SmtoAbortIfHung, TimeoutMs, out _) == IntPtr.Zero)
                return null;
            return buffer.ToString();
        }

        public static bool SetText(IntPtr hwnd, string text) =>
            SendMessageTimeout(hwnd, WmSetText, IntPtr.Zero, text, SmtoAbortIfHung, TimeoutMs, out _) != IntPtr.Zero;
    }
}
