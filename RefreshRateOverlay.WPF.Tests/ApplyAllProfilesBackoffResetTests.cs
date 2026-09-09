using System;
using RefreshRateOverlay.WPF.Services;

namespace RefreshRateOverlay.WPF.Tests;

/// <summary>
/// Covers a real production bug found live (WRC.exe, post-crash): TrayApp's
/// ApplyAllProfiles unconditionally reset both EnforcementBackoffTrackers on
/// every call — correct for a genuine "switched to a different app," but
/// with Sticky Profiles on, ANY real focus change (even to something
/// completely unrelated) re-resolves back to the same sticky app and calls
/// ApplyAllProfiles for it again. Every one of those resets the backoff
/// counter, wiping out whatever escalation had built up against an ongoing
/// external fight for that same app — so as long as focus kept moving
/// around (which it does constantly in practice), the fight-detection
/// backoff from EnforcementBackoffTrackerTests could never actually engage,
/// even though nothing about the fight itself had changed.
///
/// Mirrors TrayApp.ApplyAllProfiles's fix exactly (see its own remarks): only
/// reset when the resolved app actually differs from the last one applied.
/// SimulateApplyAllProfiles below IS that one-line decision, using a real
/// EnforcementBackoffTracker (the actual production class) rather than a
/// fake — same "reproduce TrayApp's glue, not TrayApp itself" approach
/// StickyProfileEndToEndTests uses for the same reason (TrayApp can't be
/// instantiated in a test — see its remarks).
/// </summary>
public class ApplyAllProfilesBackoffResetTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string WRC = "WRC.exe";
    private const string OtherApp = "explorer.exe";

    private string? _lastAppliedProfileApp;

    /// <summary>Mirrors TrayApp.ApplyAllProfiles's backoff-reset decision.</summary>
    private void SimulateApplyAllProfiles(EnforcementBackoffTracker tracker, string app)
    {
        if (app != _lastAppliedProfileApp) tracker.Reset();
        _lastAppliedProfileApp = app;
    }

    [Fact]
    public void RealWorldRegression_ReapplyingSameStickyApp_DoesNotResetOngoingBackoff()
    {
        var tracker = new EnforcementBackoffTracker(new[] { TimeSpan.FromSeconds(5) }, giveUpAfter: 5);
        SimulateApplyAllProfiles(tracker, WRC); // WRC becomes sticky-active

        // WRC fights the overlay 3 times — same shape as the real log
        // (every revert succeeds, but keeps recurring).
        DateTime t = T0;
        for (int i = 0; i < 3; i++)
        {
            tracker.RecordResult(converged: true, t);
            t += TimeSpan.FromSeconds(6);
        }
        Assert.Equal(3, tracker.ConsecutiveFailures);

        // Focus bounces around to unrelated apps, but Sticky Profiles keeps
        // resolving back to WRC each time — ApplyAllProfiles gets called for
        // WRC again and again even though WRC itself never lost focus in any
        // meaningful sense.
        SimulateApplyAllProfiles(tracker, WRC);
        SimulateApplyAllProfiles(tracker, WRC);
        SimulateApplyAllProfiles(tracker, WRC);

        // The ongoing fight's escalation must survive all of that — it was
        // never really interrupted.
        Assert.Equal(3, tracker.ConsecutiveFailures);

        // Two more reverts should now be enough to give up (5 total).
        for (int i = 0; i < 2; i++)
        {
            tracker.RecordResult(converged: true, t);
            t += TimeSpan.FromSeconds(6);
        }
        Assert.True(tracker.GaveUp);
    }

    [Fact]
    public void SwitchingToADifferentApp_DoesResetBackoff()
    {
        var tracker = new EnforcementBackoffTracker(new[] { TimeSpan.FromSeconds(5) }, giveUpAfter: 5);
        SimulateApplyAllProfiles(tracker, WRC);

        tracker.RecordResult(converged: false, T0);
        tracker.RecordResult(converged: false, T0 + TimeSpan.FromSeconds(5));
        Assert.Equal(2, tracker.ConsecutiveFailures);

        // A genuine switch to a different app — e.g. the user actually closes
        // WRC and starts using something else, or Sticky Profiles is off —
        // is a fresh situation and should clear whatever WRC's own fight had
        // built up.
        SimulateApplyAllProfiles(tracker, OtherApp);

        Assert.Equal(0, tracker.ConsecutiveFailures);
        Assert.False(tracker.GaveUp);
    }
}
