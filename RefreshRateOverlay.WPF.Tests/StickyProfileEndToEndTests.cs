using System;
using System.Collections.Generic;
using System.IO;
using RefreshRateOverlay.WPF.Services;

namespace RefreshRateOverlay.WPF.Tests;

/// <summary>
/// End-to-end coverage for the "Sticky Profiles" tray option, driving the
/// real production pieces TrayApp composes it from — StickyProfileTracker,
/// ProfileService/ProfileSetting (backed by a real temp-file IniStore, same
/// treatment SyncableSettingTests gives it), and ReconcilePlanner.Resolve
/// (the exact profile-or-default rule TrayApp.ResolveTarget uses) — through
/// the full open/switch/close lifecycle across TWO separate test-app
/// profiles. TrayApp itself can't be instantiated in a test (it's wired
/// directly to real display/HDR/NVIDIA hardware), so this reproduces its
/// glue (HasAnyProfile, ResolveStickyApp, and the OnAppChanged/
/// StickyLivenessTick shapes) rather than the hardware-facing apply path —
/// see TrayApp.ResolveStickyApp/OnAppChanged/StickyLivenessTick, which this
/// mirrors line for line. `_runningApps` stands in for real OS process
/// state (TrayApp asks Process.GetProcessesByName instead).
///
/// Scenario, matching the feature's spec exactly: App A (profiled) opens and
/// becomes active; focus moves to an unrelated, unprofiled app and A stays
/// active (sticky survives losing focus); App B (a DIFFERENT profile) opens
/// and wins, since it's the most-recently-opened profiled app; B's process
/// exits and A — still running — wins back; A's process exits too and
/// nothing profiled remains open, falling through to the shared default.
///
/// RealWorldRegression_* covers the actual production bugs this class was
/// added to fix — see each one's own remarks. ProcessStartsBeforeCompanion...
/// additionally mirrors TrayApp.OnProcessStarted's early-apply sticky
/// promotion via SimulateProcessStarted, not just OnAppChanged's.
/// </summary>
public class StickyProfileEndToEndTests : IDisposable
{
    private const string AppA = "TestGameA.exe";
    private const string AppB = "TestGameB.exe";
    private const string UnprofiledApp = "explorer.exe";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rro_sticky_e2e_{Guid.NewGuid():N}.ini");
    private readonly IniStore _ini;
    private readonly ProfileService _profiles;
    private readonly StickyProfileTracker _sticky = new();

    // Stands in for real OS process state — TrayApp.IsProcessRunning asks
    // Process.GetProcessesByName instead. An app is "running" from the
    // moment it's focused until a test explicitly closes it.
    private readonly HashSet<string> _runningApps = new(StringComparer.OrdinalIgnoreCase);

    public StickyProfileEndToEndTests()
    {
        _ini = new IniStore(_path);
        _profiles = new ProfileService(_ini);

        // Shared default: 60Hz, HDR off.
        _profiles.Rate.WriteDefault(60);
        _profiles.Hdr.WriteDefault(false);

        // Two distinct test-app profiles — deliberately different from each
        // other and from the default, so a wrong resolution (stuck on the
        // wrong app, or falling through to default early) is unambiguous.
        _profiles.Rate.WriteProfile(AppA, 120);
        _profiles.Hdr.WriteProfile(AppA, true);

        _profiles.Rate.WriteProfile(AppB, 144);
        _profiles.Hdr.WriteProfile(AppB, false);
    }

    public void Dispose()
    {
        _ini.Dispose();
        try { File.Delete(_path); } catch { /* best effort */ }
    }

    // ---- mirrors TrayApp's glue exactly (see class remarks) ----

    private bool HasAnyProfile(string app) =>
        _profiles.Rate.HasProfile(app) || _profiles.Hdr.HasProfile(app);

    private string ResolveStickyApp(string app) =>
        _sticky.ActiveApp is { } activeApp ? activeApp : app;

    private (int Rate, bool Hdr) ResolveTarget(string app) => (
        ReconcilePlanner.Resolve(_profiles.Rate.TryReadProfile(app, out int r), r, _profiles.Rate.ReadDefault()),
        ReconcilePlanner.Resolve(_profiles.Hdr.TryReadProfile(app, out bool h), h, _profiles.Hdr.ReadDefault()));

    /// <summary>TrayApp.OnAppChanged's sticky-relevant slice: promote in the
    /// stack if profiled, then resolve. Gaining focus implies the process is
    /// running, same as reality.
    ///
    /// isFirstAppChange mirrors TrayApp's _seenFirstAppChange guard: the very
    /// first AppChanged a ForegroundTracker instance ever raises only reports
    /// whatever was ALREADY focused before Start() was called — not an
    /// "opened" event this app actually observed happen — so it must never
    /// promote that app to sticky on the strength of that alone. Defaults to
    /// false (a genuine, observed focus change) so every existing call site
    /// below keeps its current, already-correct meaning; only the new
    /// startup-ambiguity regression tests pass true.</summary>
    private string SimulateFocusChange(string app, bool isFirstAppChange = false)
    {
        _runningApps.Add(app);
        if (!isFirstAppChange && HasAnyProfile(app)) _sticky.AppOpened(app);
        return ResolveStickyApp(app);
    }

    /// <summary>TrayApp.StickyLivenessTick: the app's process is no longer
    /// running — the POLL-based release mechanism this class was rewritten
    /// to use (see StickyProfileTracker.PruneClosedApps remarks), not an
    /// event. lastFocusedApp stands in for ForegroundTracker.LastApp.
    /// Deliberately does not gate on the app being tracked — matches
    /// production, where PruneClosedApps is called unconditionally every
    /// tick and is a no-op for anything not on the stack.</summary>
    private string SimulateAppClosed(string app, string lastFocusedApp)
    {
        _runningApps.Remove(app);
        _sticky.PruneClosedApps(a => _runningApps.Contains(a));
        return ResolveStickyApp(lastFocusedApp);
    }

    /// <summary>TrayApp.OnProcessStarted's sticky-relevant slice: the
    /// speculative early-apply-on-launch path (a profiled app's process just
    /// started, well before it necessarily has real focus). Promotes to
    /// sticky immediately, same as a real focus change does — unlike
    /// SimulateFocusChange there's no isFirstAppChange-style ambiguity to
    /// guard against here, since ProcessStartWatcher only ever fires for a
    /// genuinely new process, never a replay of something already
    /// running.</summary>
    private string SimulateProcessStarted(string app)
    {
        _runningApps.Add(app);
        if (HasAnyProfile(app)) _sticky.AppOpened(app);
        return ResolveStickyApp(app);
    }

    [Fact]
    public void FullLifecycle_TwoProfiledApps_ResolvesCorrectlyAtEveryStep()
    {
        // 1. App A opens (gains focus) — its profile becomes active.
        string effective = SimulateFocusChange(AppA);
        Assert.Equal(AppA, effective);
        Assert.Equal((120, true), ResolveTarget(effective));

        // 2. Focus moves to an unrelated, unprofiled app — sticky keeps A
        // active instead of falling through to the shared default.
        effective = SimulateFocusChange(UnprofiledApp);
        Assert.Equal(AppA, effective);
        Assert.Equal((120, true), ResolveTarget(effective));

        // 3. App B opens — a DIFFERENT profile, and the most recently
        // opened — so it wins over the still-open A.
        effective = SimulateFocusChange(AppB);
        Assert.Equal(AppB, effective);
        Assert.Equal((144, false), ResolveTarget(effective));

        // 4. Focus moves away from B too — B, not A, stays active, since B
        // is the most recently opened sticky app.
        effective = SimulateFocusChange(UnprofiledApp);
        Assert.Equal(AppB, effective);

        // 5. App B's process exits — falls back to A, still running underneath.
        string afterBExit = SimulateAppClosed(AppB, lastFocusedApp: UnprofiledApp);
        Assert.Equal(AppA, afterBExit);
        Assert.Equal((120, true), ResolveTarget(afterBExit));

        // 6. App A's process exits too — nothing sticky-tracked remains, so
        // real focus (the unprofiled app) resolves through to the default.
        string afterAExit = SimulateAppClosed(AppA, lastFocusedApp: UnprofiledApp);
        Assert.Equal(UnprofiledApp, afterAExit);
        Assert.Equal((60, false), ResolveTarget(afterAExit));
    }

    [Fact]
    public void RealWorldRegression_SoleStickyAppExiting_ReleasesIt_EvenThoughOnlyAnUnprofiledAppEverFocusedInBetween()
    {
        // Exact shape of the reported production bug (Doom Eternal / DiRT
        // Rally): Doom (AppA, profiled) becomes sticky-active. The user then
        // plays DiRT Rally — which has NO profile of its own — for a long
        // session. DiRT correctly never displaces Doom (AppOpened_SecondApp_
        // WinsOverFirst in StickyProfileTrackerTests already covers "only a
        // PROFILED app can win" — confirmed with the user this was never the
        // bug). The actual bug: even after Doom's process genuinely exited,
        // its profile kept applying forever, because the only release path
        // was an event (a WMI process-exit trace) that never fired. This
        // test is the fix's regression guard: closing the SOLE sticky app
        // must release it via the liveness poll alone — with no other
        // profiled app ever having taken over in the meantime.
        string effective = SimulateFocusChange(AppA);
        Assert.Equal(AppA, effective);

        // DiRT Rally: unprofiled, focused for a long session. Must NOT
        // displace Doom — this part was correct all along.
        effective = SimulateFocusChange(UnprofiledApp);
        Assert.Equal(AppA, effective);
        Assert.Equal((120, true), ResolveTarget(effective));

        // Doom's process actually exits. The liveness poll must release it.
        effective = SimulateAppClosed(AppA, lastFocusedApp: UnprofiledApp);

        Assert.Equal(UnprofiledApp, effective);
        Assert.Equal((60, false), ResolveTarget(effective)); // shared default, not Doom's stale profile
        Assert.Null(_sticky.ActiveApp);
    }

    [Fact]
    public void SimulateAppClosed_ForUntrackedApp_DoesNotDisturbActiveStickyApp()
    {
        // PruneClosedApps runs unconditionally every tick in production, not
        // gated on the app being sticky-tracked — an unrelated process
        // exiting (or never having been tracked at all) must be a no-op.
        SimulateFocusChange(AppA);

        string result = SimulateAppClosed("SomeRandomHelper.exe", lastFocusedApp: AppA);

        Assert.Equal(AppA, result);
        Assert.Equal(AppA, _sticky.ActiveApp); // untouched
    }

    [Fact]
    public void ReopeningAppAlreadyOpenUnderneath_PromotesItBackToActive()
    {
        // A regains focus while B is active — matches the tracker's
        // "reconfirming an open app promotes it" semantics (see
        // StickyProfileTracker.AppOpened remarks): the user re-engaging with
        // A should make its profile win again, not require closing B first.
        SimulateFocusChange(AppA);
        SimulateFocusChange(AppB);
        Assert.Equal(AppB, _sticky.ActiveApp);

        string effective = SimulateFocusChange(AppA);

        Assert.Equal(AppA, effective);
        Assert.Equal((120, true), ResolveTarget(effective));
    }

    [Fact]
    public void RealWorldRegression_AppAlreadyFocusedAtStartup_DoesNotBecomeSticky()
    {
        // The overlay (re)starts while a profiled game is already open and
        // focused — has been running for a while, nothing to do with this
        // launch. ForegroundTracker's very first AppChanged only reports that
        // pre-existing state; it was never actually observed "opening," so
        // treating it as a real open event (and sticking to it) is a guess,
        // not something this app witnessed. The profile should still apply
        // normally to whatever's focused — that part was never wrong — but
        // it must not become sticky purely from this ambiguous first sample.
        string effective = SimulateFocusChange(AppA, isFirstAppChange: true);
        Assert.Equal(AppA, effective); // profile still applies to the actually-focused app
        Assert.Null(_sticky.ActiveApp); // but never latched as sticky

        // Focus now moves to an unrelated, unprofiled app. Since A was never
        // sticky, this correctly falls through to the shared default —
        // A's profile does NOT keep incorrectly reasserting itself.
        effective = SimulateFocusChange(UnprofiledApp);
        Assert.Equal(UnprofiledApp, effective);
        Assert.Equal((60, false), ResolveTarget(effective));
    }

    [Fact]
    public void RealWorldRegression_AppOpenedAfterStartup_StillBecomesSticky()
    {
        // The startup-ambiguity guard must be scoped to only the very first
        // AppChanged — every later one is a real, observed transition and
        // sticks exactly as before. First sample here is the (unprofiled)
        // desktop/shell foreground at launch; A opening afterward is genuine.
        SimulateFocusChange(UnprofiledApp, isFirstAppChange: true);
        string effective = SimulateFocusChange(AppA);

        Assert.Equal(AppA, effective);
        Assert.Equal(AppA, _sticky.ActiveApp);
    }

    [Fact]
    public void RealWorldRegression_ProcessStartsBeforeCompanionLauncherStealsFocus_StaysStickyThroughout()
    {
        // Exact shape of the WRC.exe / EAAntiCheat.GameServiceLauncher.exe
        // production sequence: a companion launcher process (unprofiled)
        // takes real OS focus first, then the profiled game's OWN process
        // starts (early-apply fires) but doesn't have real focus yet, then
        // the launcher takes real focus again briefly before the game's
        // window finally shows. Before OnProcessStarted promoted to sticky,
        // this second launcher focus fell through to the shared default —
        // a real, observed rate flip — because nothing was sticky-tracked
        // yet at that point. It must now stay pinned to the game throughout.
        string effective = SimulateFocusChange(UnprofiledApp, isFirstAppChange: true);
        Assert.Equal(UnprofiledApp, effective); // launcher grabs focus before the game process exists

        effective = SimulateProcessStarted(AppA); // game process starts, early-applies, becomes sticky
        Assert.Equal(AppA, effective);
        Assert.Equal(AppA, _sticky.ActiveApp);

        // Launcher regains real focus (the game's own window still hasn't
        // shown) — must resolve through the now-sticky game, not fall back
        // to the launcher/default the way it would have before the fix.
        effective = SimulateFocusChange(UnprofiledApp);
        Assert.Equal(AppA, effective);
        Assert.Equal((120, true), ResolveTarget(effective));

        // The game's own window finally takes real focus — no-op change,
        // still the same sticky app.
        effective = SimulateFocusChange(AppA);
        Assert.Equal(AppA, effective);
    }

    [Fact]
    public void SimulateProcessStarted_AppWithNoProfile_DoesNotBecomeSticky()
    {
        // Mirrors HasAnyProfile's gate in production: OnProcessStarted itself
        // already only fires for profiled apps (see TrayApp.OnProcessStarted's
        // early return), but this locks in that the sticky-promotion slice
        // specifically never latches an unprofiled process, same as
        // SimulateFocusChange never does for a real focus change.
        string effective = SimulateProcessStarted(UnprofiledApp);

        Assert.Equal(UnprofiledApp, effective);
        Assert.Null(_sticky.ActiveApp);
    }
}
