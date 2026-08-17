using RefreshRateOverlay.WPF.Services;

namespace RefreshRateOverlay.WPF.Tests;

/// <summary>
/// Regression coverage for the Doom HDR-on-default bug: reconciliation
/// attributed a launching app's own hardware drift to whichever app used to
/// be focused (ForegroundTracker.LastApp, stale for a few seconds after a
/// process-start early-apply), which fell through to the shared default
/// since that stale app had no profile. See AnticipatedAppTracker remarks.
/// </summary>
public class AnticipatedAppTrackerTests
{
    [Fact]
    public void ResolveApp_NoAnticipation_ReturnsLastApp()
    {
        var tracker = new AnticipatedAppTracker();
        Assert.Equal("explorer.exe", tracker.ResolveApp("explorer.exe"));
    }

    [Fact]
    public void ResolveApp_WithAnticipation_PrefersAnticipatedApp_NotStaleLastApp()
    {
        // The exact Doom scenario: ForegroundTracker hasn't caught up yet
        // (still reports 'explorer.exe'), but the process-start early-apply
        // already fired for 'DOOMTheDarkAges.exe' — drift observed right now
        // belongs to Doom, not explorer.
        var tracker = new AnticipatedAppTracker();
        tracker.Started("DOOMTheDarkAges.exe");

        Assert.Equal("DOOMTheDarkAges.exe", tracker.ResolveApp("explorer.exe"));
    }

    [Fact]
    public void RealFocusChanged_ClearsAnticipation_EvenWhenItDoesNotMatch()
    {
        var tracker = new AnticipatedAppTracker();
        tracker.Started("DOOMTheDarkAges.exe");

        // A genuine focus change to a THIRD app (neither the anticipated app
        // nor whatever LastApp used to be) is still authoritative — the
        // guess is over regardless of whether it was right.
        tracker.RealFocusChanged();

        Assert.Equal("chrome.exe", tracker.ResolveApp("chrome.exe"));
    }

    [Fact]
    public void WindowExpired_MatchingApp_ClearsAnticipation()
    {
        var tracker = new AnticipatedAppTracker();
        tracker.Started("DOOMTheDarkAges.exe");

        tracker.WindowExpired("DOOMTheDarkAges.exe");

        Assert.Equal("explorer.exe", tracker.ResolveApp("explorer.exe"));
    }

    [Fact]
    public void WindowExpired_StaleApp_DoesNotClobberNewerAnticipation()
    {
        // Two processes started close together: the first one's 9s revert
        // timer fires after a second, different anticipation has already
        // replaced it. The stale timer must not clear the newer guess.
        var tracker = new AnticipatedAppTracker();
        tracker.Started("FirstGame.exe");
        tracker.Started("SecondGame.exe");

        tracker.WindowExpired("FirstGame.exe");

        Assert.Equal("SecondGame.exe", tracker.ResolveApp("explorer.exe"));
    }

    [Fact]
    public void Started_TwiceInARow_LatestWins()
    {
        var tracker = new AnticipatedAppTracker();
        tracker.Started("FirstGame.exe");
        tracker.Started("SecondGame.exe");

        Assert.Equal("SecondGame.exe", tracker.ResolveApp("explorer.exe"));
    }
}
