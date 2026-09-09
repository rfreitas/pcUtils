using System;
using RefreshRateOverlay.WPF.Services;

namespace RefreshRateOverlay.WPF.Tests;

/// <summary>
/// Covers EnforcementBackoffTracker — the consecutive-failure/backoff state
/// TrayApp.ReconcileAll's reconciliation-off branch uses to stop hammering a
/// setting that won't stick (see TrayApp.HandleEnforcementResult). Every
/// method takes `nowUtc` explicitly so these run without any real waiting.
/// </summary>
public class EnforcementBackoffTrackerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FreshTracker_ShouldAttemptIsTrue_NotGivenUp()
    {
        var tracker = new EnforcementBackoffTracker();

        Assert.True(tracker.ShouldAttempt(T0));
        Assert.False(tracker.GaveUp);
        Assert.Equal(0, tracker.ConsecutiveFailures);
    }

    [Fact]
    public void FirstFailure_BacksOffForFirstScheduleStep()
    {
        var tracker = new EnforcementBackoffTracker(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15) }, giveUpAfter: 5);

        tracker.RecordResult(converged: false, T0);

        Assert.Equal(1, tracker.ConsecutiveFailures);
        Assert.False(tracker.ShouldAttempt(T0 + TimeSpan.FromSeconds(5) - TimeSpan.FromMilliseconds(1)));
        Assert.True(tracker.ShouldAttempt(T0 + TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void RepeatedFailures_EscalateThroughSchedule_ThenHoldAtLastStep()
    {
        var schedule = new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30) };
        var tracker = new EnforcementBackoffTracker(schedule, giveUpAfter: 10);

        tracker.RecordResult(false, T0);
        Assert.False(tracker.ShouldAttempt(T0 + TimeSpan.FromSeconds(5) - TimeSpan.FromMilliseconds(1)));
        Assert.True(tracker.ShouldAttempt(T0 + TimeSpan.FromSeconds(5)));

        DateTime t1 = T0 + TimeSpan.FromSeconds(5);
        tracker.RecordResult(false, t1); // 2nd failure -> schedule[1] = 15s
        Assert.False(tracker.ShouldAttempt(t1 + TimeSpan.FromSeconds(15) - TimeSpan.FromMilliseconds(1)));
        Assert.True(tracker.ShouldAttempt(t1 + TimeSpan.FromSeconds(15)));

        DateTime t2 = t1 + TimeSpan.FromSeconds(15);
        tracker.RecordResult(false, t2); // 3rd failure -> schedule[2] = 30s
        Assert.False(tracker.ShouldAttempt(t2 + TimeSpan.FromSeconds(30) - TimeSpan.FromMilliseconds(1)));
        Assert.True(tracker.ShouldAttempt(t2 + TimeSpan.FromSeconds(30)));

        DateTime t3 = t2 + TimeSpan.FromSeconds(30);
        tracker.RecordResult(false, t3); // 4th failure -> past schedule length, holds at the last step (30s)
        Assert.False(tracker.ShouldAttempt(t3 + TimeSpan.FromSeconds(30) - TimeSpan.FromMilliseconds(1)));
        Assert.True(tracker.ShouldAttempt(t3 + TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void ConsecutiveFailuresReachingThreshold_SetsGaveUp_AndBlocksFurtherAttemptsRegardlessOfTime()
    {
        var tracker = new EnforcementBackoffTracker(new[] { TimeSpan.FromSeconds(1) }, giveUpAfter: 3);
        DateTime t = T0;

        tracker.RecordResult(false, t); t += TimeSpan.FromSeconds(1);
        tracker.RecordResult(false, t); t += TimeSpan.FromSeconds(1);
        Assert.False(tracker.GaveUp);

        tracker.RecordResult(false, t); // 3rd consecutive failure -> GaveUp
        Assert.True(tracker.GaveUp);

        // Even long after any backoff window would have expired, GaveUp blocks it.
        Assert.False(tracker.ShouldAttempt(t + TimeSpan.FromDays(1)));
    }

    [Fact]
    public void RealWorldRegression_RepeatedSuccessfulRevertsWithinCalmPeriod_StillEscalateToGiveUp()
    {
        // Exact shape of the reported production bug: WRC.exe, after an
        // Alt+Enter-triggered crash, kept flapping its own display mode every
        // few seconds — and every single one of TrayApp's reverts SUCCEEDED
        // ("Rate changed to 100 Hz" in the log, repeatedly). The old rule
        // ("converged -> reset to zero") treated each individual success as
        // "the fight is over," so this never escalated past a single
        // failure — it fought the game forever instead of ever backing off,
        // and re-ballooned on every single revert too (see
        // TrayApp.ReconcileAll's freshStreak check, which reads
        // ConsecutiveFailures). This is the regression guard for that.
        var tracker = new EnforcementBackoffTracker(new[] { TimeSpan.FromSeconds(5) }, giveUpAfter: 5);
        DateTime t = T0;

        for (int i = 0; i < 5; i++)
        {
            Assert.True(tracker.ShouldAttempt(t), $"attempt {i + 1} should still be allowed");
            tracker.RecordResult(converged: true, t); // every single revert SUCCEEDS
            t += TimeSpan.FromSeconds(6); // matches the game's own ~5-10s cadence — well within the calm period
        }

        Assert.True(tracker.GaveUp);
    }

    [Fact]
    public void RevertAfterCalmPeriodElapsed_TreatedAsFreshIncident_DoesNotAccumulateWithAnUnrelatedOlderFight()
    {
        var tracker = new EnforcementBackoffTracker(
            new[] { TimeSpan.FromSeconds(5) }, giveUpAfter: 5, calmPeriod: TimeSpan.FromSeconds(30));

        tracker.RecordResult(converged: true, T0);
        Assert.Equal(1, tracker.ConsecutiveFailures);

        // A genuinely separate, later incident — long after the calm period —
        // must not pick up where the earlier, unrelated one left off.
        DateTime muchLater = T0 + TimeSpan.FromMinutes(5);
        tracker.RecordResult(converged: true, muchLater);

        Assert.Equal(1, tracker.ConsecutiveFailures);
    }

    [Fact]
    public void IsRecentlyActive_TrueWithinCalmPeriod_FalseAfterItElapses()
    {
        var tracker = new EnforcementBackoffTracker(calmPeriod: TimeSpan.FromSeconds(30));
        tracker.RecordResult(converged: true, T0);

        Assert.True(tracker.IsRecentlyActive(T0 + TimeSpan.FromSeconds(30)));
        Assert.False(tracker.IsRecentlyActive(T0 + TimeSpan.FromSeconds(30) + TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void ConvergedLongAfterCalmPeriodElapsed_StartsFreshCount_ClearsPriorGiveUp()
    {
        var tracker = new EnforcementBackoffTracker(
            new[] { TimeSpan.FromSeconds(1) }, giveUpAfter: 2, calmPeriod: TimeSpan.FromSeconds(10));
        tracker.RecordResult(false, T0);
        tracker.RecordResult(false, T0 + TimeSpan.FromSeconds(1));
        Assert.True(tracker.GaveUp);

        // A later, unrelated revert — long enough after the last one that
        // it's a fresh incident, not a continuation of the fight that
        // already triggered give-up.
        DateTime muchLater = T0 + TimeSpan.FromSeconds(1) + TimeSpan.FromMinutes(5);
        tracker.RecordResult(converged: true, muchLater);

        Assert.False(tracker.GaveUp);
        Assert.Equal(1, tracker.ConsecutiveFailures);
    }

    [Fact]
    public void Reset_ClearsFailureCountAndGiveUp_SameAsConverged()
    {
        var tracker = new EnforcementBackoffTracker(new[] { TimeSpan.FromSeconds(1) }, giveUpAfter: 1);
        tracker.RecordResult(false, T0);
        Assert.True(tracker.GaveUp);

        tracker.Reset();

        Assert.False(tracker.GaveUp);
        Assert.Equal(0, tracker.ConsecutiveFailures);
        Assert.True(tracker.ShouldAttempt(T0));
    }
}
