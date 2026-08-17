namespace RefreshRateOverlay.WPF.Services;

/// <summary>
/// Tracks which app, if any, ReconcileRateAndHdr/ReconcileGsyncMode should
/// attribute hardware drift to instead of ForegroundTracker.LastApp.
///
/// LastApp is stale for up to a few seconds after a process-start
/// early-apply (OnProcessStarted): ForegroundTracker needs ~1s of stable
/// focus before it updates, but a game's own pipeline can influence hardware
/// (HDR especially — see ProcessStartWatcher remarks) well before its window
/// ever takes focus. Left unguarded, drift during that window gets
/// attributed to whichever app used to be focused — and if that app has no
/// profile of its own, falls through to the shared default, corrupting it
/// for every app instead of recording a wrong guess against the app it
/// actually belongs to. Confirmed via log: launching Doom pushed a
/// default-HDR write attributed to 'explorer.exe' (stale LastApp) 4 seconds
/// before ForegroundTracker caught up to Doom actually having focus.
///
/// Extracted into its own pure, dependency-free type — same reasoning as
/// ReconcilePlanner — because this exact state machine (when to prefer the
/// guess over LastApp, when to stop) is where that bug actually lived, and
/// TrayApp itself is too coupled to real hardware I/O to unit test directly.
/// </summary>
internal sealed class AnticipatedAppTracker
{
    public string? Current { get; private set; }

    /// <summary>A process-start early-apply just fired for this app — prefer
    /// it over LastApp until a real focus change confirms/supersedes it, or
    /// its own outstanding window expires.</summary>
    public void Started(string app) => Current = app;

    /// <summary>A real, debounced focus change is always authoritative —
    /// supersedes any outstanding guess, whether it confirms it or not.</summary>
    public void RealFocusChanged() => Current = null;

    /// <summary>The early-apply's revert-check window for this app has
    /// expired — stop preferring it, unless a newer anticipation has since
    /// replaced it (guards against a second process starting, and its own
    /// window still being outstanding, before this one's timer fires).</summary>
    public void WindowExpired(string app)
    {
        if (Current == app) Current = null;
    }

    /// <summary>What ReconcileRateAndHdr/ReconcileGsyncMode should attribute
    /// drift to right now.</summary>
    public string ResolveApp(string lastApp) => Current ?? lastApp;
}
