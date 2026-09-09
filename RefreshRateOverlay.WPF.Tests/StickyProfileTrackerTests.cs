using RefreshRateOverlay.WPF.Services;

namespace RefreshRateOverlay.WPF.Tests;

/// <summary>
/// Unit coverage for the pure stack logic behind the "Sticky Profiles" tray
/// option — see StickyProfileTracker remarks. StickyProfileEndToEndTests
/// covers the same stack driving real per-app profile resolution end to end;
/// this file isolates just the stack's own promote/prune/cascade behavior.
///
/// PruneClosedApps is exercised with a fake `isRunning` predicate rather than
/// real OS processes — the exact same seam TrayApp.StickyLivenessTick uses
/// against Process.GetProcessesByName, kept swappable specifically so this
/// logic is testable without spawning real processes.
/// </summary>
public class StickyProfileTrackerTests
{
    [Fact]
    public void ActiveApp_Empty_IsNull()
    {
        var tracker = new StickyProfileTracker();
        Assert.Null(tracker.ActiveApp);
    }

    [Fact]
    public void AppOpened_SingleApp_BecomesActive()
    {
        var tracker = new StickyProfileTracker();
        tracker.AppOpened("GameA.exe");

        Assert.Equal("GameA.exe", tracker.ActiveApp);
    }

    [Fact]
    public void AppOpened_SecondApp_WinsOverFirst()
    {
        // "if another app with sticky profile opens, that profile wins".
        var tracker = new StickyProfileTracker();
        tracker.AppOpened("GameA.exe");
        tracker.AppOpened("GameB.exe");

        Assert.Equal("GameB.exe", tracker.ActiveApp);
    }

    [Fact]
    public void PruneClosedApps_TopAppNoLongerRunning_FallsBackToPreviousApp()
    {
        // "when it closes the previous app wins".
        var tracker = new StickyProfileTracker();
        tracker.AppOpened("GameA.exe");
        tracker.AppOpened("GameB.exe");

        bool changed = tracker.PruneClosedApps(app => app != "GameB.exe");

        Assert.True(changed);
        Assert.Equal("GameA.exe", tracker.ActiveApp);
    }

    [Fact]
    public void PruneClosedApps_LastRemainingAppNoLongerRunning_LeavesNoActiveApp()
    {
        // Regression test for the reported production bug: a sticky app
        // (Doom) stayed active forever because nothing ever detected it had
        // actually closed. The fix is this poll — PruneClosedApps must drop
        // the last remaining tracked app the instant it's no longer running,
        // with no dependency on any other app ever taking over (DiRT Rally,
        // in the real report, had no profile of its own and correctly never
        // displaced Doom — that part was never the bug).
        var tracker = new StickyProfileTracker();
        tracker.AppOpened("DOOMTheDarkAges.exe");

        bool changed = tracker.PruneClosedApps(_ => false); // nothing is running

        Assert.True(changed);
        Assert.Null(tracker.ActiveApp);
    }

    [Fact]
    public void PruneClosedApps_NonTopAppNoLongerRunning_DoesNotDisturbActiveApp()
    {
        // Something further down the stack (not the currently active one)
        // going away shouldn't change who's active right now.
        var tracker = new StickyProfileTracker();
        tracker.AppOpened("GameA.exe");
        tracker.AppOpened("GameB.exe");

        bool changed = tracker.PruneClosedApps(app => app != "GameA.exe");

        Assert.True(changed);
        Assert.Equal("GameB.exe", tracker.ActiveApp);
    }

    [Fact]
    public void PruneClosedApps_EverythingStillRunning_ReturnsFalse_LeavesStackUntouched()
    {
        var tracker = new StickyProfileTracker();
        tracker.AppOpened("GameA.exe");
        tracker.AppOpened("GameB.exe");

        bool changed = tracker.PruneClosedApps(_ => true); // everything still running

        Assert.False(changed);
        Assert.Equal("GameB.exe", tracker.ActiveApp);
    }

    [Fact]
    public void PruneClosedApps_EmptyStack_IsNoOp_ReturnsFalse()
    {
        var tracker = new StickyProfileTracker();

        bool changed = tracker.PruneClosedApps(_ => false);

        Assert.False(changed);
        Assert.Null(tracker.ActiveApp);
    }

    [Fact]
    public void AppOpened_ReopeningAppAlreadyUnderneath_PromotesItBackToTop()
    {
        // Refocusing an app already open elsewhere in the stack brings it
        // back to the top rather than leaving whatever's above it in charge.
        var tracker = new StickyProfileTracker();
        tracker.AppOpened("GameA.exe");
        tracker.AppOpened("GameB.exe");

        tracker.AppOpened("GameA.exe");

        Assert.Equal("GameA.exe", tracker.ActiveApp);
    }

    [Fact]
    public void Clear_DropsEverything()
    {
        var tracker = new StickyProfileTracker();
        tracker.AppOpened("GameA.exe");
        tracker.AppOpened("GameB.exe");

        tracker.Clear();

        Assert.Null(tracker.ActiveApp);
        // Nothing left to prune — proves Clear() actually emptied the stack
        // rather than just resetting what counts as "active".
        Assert.False(tracker.PruneClosedApps(_ => false));
    }
}
