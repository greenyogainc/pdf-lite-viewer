using System.Diagnostics;
using System.Windows.Threading;

namespace HangProbe;

internal readonly record struct Stall(string Scenario, TimeSpan Duration, TimeSpan WorstStall, TimeSpan Budget)
{
    public bool Failed => WorstStall > Budget;
}

/// <summary>
/// Measures UI-thread responsiveness the same way a user (and the Store's hang
/// telemetry) perceives it: from another thread, queue a no-op at input priority
/// and time how long the UI thread takes to run it. A long round trip means the
/// message pump was busy — i.e. the window was frozen for that long.
/// </summary>
internal sealed class UiWatchdog : IDisposable
{
    private const int PingIntervalMs = 15;

    private readonly Dispatcher _dispatcher;
    private readonly Thread _thread;
    private volatile bool _stop;
    private long _worstTicks;

    public UiWatchdog(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _thread = new Thread(Loop) { IsBackground = true, Name = "ui-watchdog" };
        _thread.Start();
    }

    private void Loop()
    {
        while (!_stop)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var op = _dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
                op.Task.Wait(TimeSpan.FromMinutes(5));
            }
            catch (OperationCanceledException) { return; }
            // Task.Wait wraps the cancellation, so this is the clause that actually fires
            // when the dispatcher shuts down with a ping in flight.
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException) { return; }
            sw.Stop();

            long ticks = sw.Elapsed.Ticks;
            long seen;
            while (ticks > (seen = Interlocked.Read(ref _worstTicks)))
                Interlocked.CompareExchange(ref _worstTicks, ticks, seen);

            Thread.Sleep(PingIntervalMs);
        }
    }

    private TimeSpan TakeWorst() => TimeSpan.FromTicks(Interlocked.Exchange(ref _worstTicks, 0));

    /// <summary>
    /// Runs one scenario and reports the worst UI-thread stall seen while it ran,
    /// including the layout/render pass it triggers.
    /// </summary>
    public async Task<Stall> MeasureAsync(string scenario, TimeSpan budget, Func<Task> action)
    {
        await SettleAsync();
        TakeWorst();

        var sw = Stopwatch.StartNew();
        await action();
        await SettleAsync();
        sw.Stop();

        return new Stall(scenario, sw.Elapsed, TakeWorst(), budget);
    }

    public Task<Stall> MeasureAsync(string scenario, TimeSpan budget, Action action) =>
        MeasureAsync(scenario, budget, () => { action(); return Task.CompletedTask; });

    /// <summary>Lets queued layout, render and background-priority work drain.</summary>
    public async Task SettleAsync()
    {
        await _dispatcher.InvokeAsync(() => { }, DispatcherPriority.SystemIdle);
        await Task.Delay(120);
        await _dispatcher.InvokeAsync(() => { }, DispatcherPriority.SystemIdle);
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
