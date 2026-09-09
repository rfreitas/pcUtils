using System;

namespace RefreshRateOverlay.WPF.Services;

/// <summary>
/// Tracks how often one enforcement bucket — TrayApp keeps one for Rate/HDR
/// and one for G-SYNC — has needed to revert external drift recently, and
/// decides when it's allowed to try again. See TrayApp.ReconcileAll's
/// reconciliation-off branch: without this, something that keeps fighting a
/// setting (an unsupported mode after a monitor swap, a driver quirk,
/// another tool, or a crashed game repeatedly flapping its own display mode)
/// would get re-pushed on every single HardwareChange tick forever — for
/// G-SYNC, every 2s indefinitely, since PollGsyncMode's timer doesn't care
/// whether the last push actually worked.
///
/// Escalation is driven by how RECENTLY a revert was last needed, not by
/// whether the last one converged. A real production incident (WRC.exe,
/// after an Alt+Enter-triggered crash) showed why the outcome can't be the
/// signal: the game kept flapping its own display mode every few seconds,
/// and every individual revert TrayApp pushed back genuinely succeeded — so
/// a "converged means the fight is over, reset to zero" rule never let this
/// escalate past a single failure, fighting the game forever instead of ever
/// backing off. A push succeeding only means that one push worked; it says
/// nothing about whether whatever's on the other side is about to do it
/// again in five seconds, which is exactly what recurring reverts within a
/// short window (the "calm period") mean regardless of each one's own
/// outcome.
///
/// Pure and clock-injected (every method takes `nowUtc` explicitly) so it's
/// unit-testable without any real waiting.
/// </summary>
internal sealed class EnforcementBackoffTracker
{
    private static readonly TimeSpan[] DefaultSchedule =
    {
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
    };
    private const int DefaultGiveUpAfter = 5;

    // How long a bucket must go without needing another revert before the
    // NEXT one is treated as a brand-new, isolated incident instead of a
    // continuation of the same ongoing fight. Matches the schedule's own top
    // end by default — once backed off to the slowest cadence, a revert that
    // far apart is still plausibly the same recurring fight, not a fresh one.
    private static readonly TimeSpan DefaultCalmPeriod = TimeSpan.FromSeconds(60);

    private readonly TimeSpan[] _schedule;
    private readonly int _giveUpAfter;
    private readonly TimeSpan _calmPeriod;

    private int _consecutiveReverts;
    private DateTime _nextAttemptUtc = DateTime.MinValue;
    private DateTime _lastRevertUtc = DateTime.MinValue;

    public EnforcementBackoffTracker(
        TimeSpan[]? schedule = null, int giveUpAfter = DefaultGiveUpAfter, TimeSpan? calmPeriod = null)
    {
        _schedule = schedule ?? DefaultSchedule;
        _giveUpAfter = giveUpAfter;
        _calmPeriod = calmPeriod ?? DefaultCalmPeriod;
    }

    /// <summary>How many reverts in the current (still-active, per
    /// IsRecentlyActive) fight have happened — exposed so a caller can tell
    /// "first revert of a new fight" (0 beforehand) from "already fighting
    /// this" (&gt;0), e.g. to only balloon-notify once per fight instead of
    /// on every backed-off retry.</summary>
    public int ConsecutiveFailures => _consecutiveReverts;

    /// <summary>True once GiveUpAfter reverts have piled up within the same
    /// ongoing fight — automatic retries stop until Reset() (a fresh user- or
    /// focus-driven apply) clears it, or until enough calm time passes that a
    /// later revert starts an entirely new count (see RecordResult).</summary>
    public bool GaveUp => _consecutiveReverts >= _giveUpAfter;

    /// <summary>Whether an automatic enforcement push is allowed right now —
    /// false while backing off after a recent revert, or unconditionally
    /// false (regardless of nowUtc) once GaveUp, until Reset() or a
    /// sufficiently calm gap.</summary>
    public bool ShouldAttempt(DateTime nowUtc) => !GaveUp && nowUtc >= _nextAttemptUtc;

    /// <summary>True while a revert happened recently enough (within the
    /// calm period) that this bucket should still be considered "in an
    /// active fight" even on a tick that currently shows no drift — see
    /// RecordResult remarks and TrayApp.HandleEnforcementRecovery, which
    /// uses this to avoid clearing the streak on a single quiet tick that
    /// might just be the calm between two flaps of the same fight.</summary>
    public bool IsRecentlyActive(DateTime nowUtc) => nowUtc - _lastRevertUtc <= _calmPeriod;

    /// <summary>Records that an enforcement push just happened — called every
    /// time, regardless of whether SettleLoop.RunAsync's result (`converged`)
    /// says it stuck. Whether this continues the current fight (escalate) or
    /// starts a fresh one (count = 1) is decided purely by how recently the
    /// last revert happened, via IsRecentlyActive — not by convergence, per
    /// the class remarks on why outcome-based resets were the actual bug.
    /// `converged` is kept as a parameter for callers' own logging/
    /// notification decisions, not consulted here.</summary>
    public void RecordResult(bool converged, DateTime nowUtc)
    {
        _consecutiveReverts = IsRecentlyActive(nowUtc) ? _consecutiveReverts + 1 : 1;
        _lastRevertUtc = nowUtc;

        TimeSpan backoff = _schedule[Math.Min(_consecutiveReverts - 1, _schedule.Length - 1)];
        _nextAttemptUtc = nowUtc + backoff;
    }

    public void Reset()
    {
        _consecutiveReverts = 0;
        _nextAttemptUtc = DateTime.MinValue;
        _lastRevertUtc = DateTime.MinValue;
    }
}
