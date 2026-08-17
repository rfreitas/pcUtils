using RefreshRateOverlay.WPF.Services;

namespace RefreshRateOverlay.WPF.Tests;

/// <summary>
/// Regression coverage for the ProcessStartWatcher thread-marshaling bug:
/// ManagementEventWatcher.EventArrived fires on a ThreadPool thread by
/// default, and an earlier version raised ProcessStarted directly from
/// there — racing unsynchronized against every other event source in
/// TrayApp (all on the UI thread), which corrupted shared reconciliation
/// state (an Assetto Corsa rate profile silently reverting a few seconds
/// after being set) when a game's process start landed close to its own
/// foreground-focus event.
///
/// RaiseProcessStarted is the seam that makes the fix itself directly
/// testable: a live EventArrivedEventArgs can't practically be constructed
/// outside a real WMI callback, but the marshaling behavior — the actual
/// thing that was broken — doesn't depend on WMI at all.
/// </summary>
public class ProcessStartWatcherTests
{
    [Fact]
    public void RaiseProcessStarted_CalledFromBackgroundThread_HandlerRunsOnDispatcherThread()
    {
        ProcessStartWatcher watcher = null!;
        int dispatcherThreadId = -1;

        // Never calls Start() — no live WMI subscription needed to exercise
        // the marshaling logic, and Win32_ProcessStartTrace's elevation
        // requirement only applies once a trace session actually starts.
        StaTestHelper.Run(() =>
        {
            watcher = new ProcessStartWatcher();
            dispatcherThreadId = Thread.CurrentThread.ManagedThreadId;
        });

        int handlerThreadId = -1;
        string? receivedName = null;
        using var handled = new ManualResetEventSlim();

        watcher.ProcessStarted += (_, name) =>
        {
            handlerThreadId = Thread.CurrentThread.ManagedThreadId;
            receivedName = name;
            handled.Set();
        };

        // Simulates WMI's own background-thread delivery — the exact
        // condition that raced against ForegroundTracker before the fix.
        int callingThreadId = -1;
        var raiser = new Thread(() =>
        {
            callingThreadId = Thread.CurrentThread.ManagedThreadId;
            watcher.RaiseProcessStarted("acs.exe");
        });
        raiser.Start();
        raiser.Join();

        Assert.True(handled.Wait(TimeSpan.FromSeconds(2)), "ProcessStarted handler never fired.");

        Assert.Equal("acs.exe", receivedName);
        Assert.Equal(dispatcherThreadId, handlerThreadId);
        Assert.NotEqual(callingThreadId, handlerThreadId);
    }
}
