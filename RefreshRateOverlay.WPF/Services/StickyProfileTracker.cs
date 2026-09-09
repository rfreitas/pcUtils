using System.Collections.Generic;

namespace RefreshRateOverlay.WPF.Services;

/// <summary>
/// Pure stack logic behind the "Sticky Profiles" tray option: while enabled,
/// the most recently opened app that has its own profile stays the effective
/// app — even after focus moves to some other, non-profiled window — until
/// that app's process actually exits. Opening a second profiled app while the
/// first is still running promotes it to the top instead of replacing the
/// first outright, so closing the second one falls back to the first (still
/// running) rather than straight to no profile at all.
///
/// The pop side (PruneClosedApps) is a poll, not an event — see its remarks
/// for why an event-driven release path (a WMI process-exit trace) was tried
/// and dropped after a real production bug: a closed game stayed
/// sticky-active forever because the exit event never fired.
///
/// Extracted into its own pure, dependency-free type — same reasoning as
/// AnticipatedAppTracker/ReconcilePlanner — so this stack behavior (promote
/// on (re)open, prune on close, cascade to whatever's underneath) can be
/// verified without TrayApp's real focus polling or real OS processes.
/// </summary>
internal sealed class StickyProfileTracker
{
    private readonly List<string> _stack = new();

    /// <summary>The app whose profile should currently win, or null if no
    /// sticky-tracked app is open.</summary>
    public string? ActiveApp => _stack.Count > 0 ? _stack[^1] : null;

    /// <summary>An app with its own profile gained focus (or is being
    /// reconfirmed) — becomes the new top, whether it was already tracked
    /// further down the stack or not. Callers are expected to only invoke
    /// this for apps that actually have a profile (TrayApp.HasAnyProfile);
    /// this type has no opinion of its own on what counts as "has a
    /// profile".</summary>
    public void AppOpened(string app)
    {
        _stack.Remove(app);
        _stack.Add(app);
    }

    /// <summary>Turning the feature off (tray toggle) drops all tracking —
    /// re-enabling starts fresh rather than resuming a stale stack.</summary>
    public void Clear() => _stack.Clear();

    /// <summary>The sole release mechanism for the stack: drops every
    /// tracked app the caller reports as no longer actually running (real
    /// caller: TrayApp polling Process.GetProcessesByName on a timer — see
    /// TrayApp.StickyLivenessTick). Deliberately a poll rather than an
    /// event: this app's process-start watcher (the sibling of the
    /// process-exit watcher this used to depend on, same underlying WMI
    /// trace mechanism) has thrown intermittent Access Denied failures in
    /// production for weeks — concrete evidence that WMI event delivery
    /// here can't be trusted as the ONLY way a sticky app ever gets
    /// released. A missed/never-fired event used to strand a closed app as
    /// permanently "active"; a poll that doesn't depend on any event being
    /// delivered can't get stuck that way. If the active (top) app is
    /// pruned, whatever's underneath it (if anything, and if still running)
    /// becomes active instead — cascading exactly like a normal pop would.
    /// Returns whether anything was actually dropped, so the caller knows
    /// whether the effective app changed and needs reapplying.</summary>
    public bool PruneClosedApps(Func<string, bool> isRunning) =>
        _stack.RemoveAll(app => !isRunning(app)) > 0;
}
