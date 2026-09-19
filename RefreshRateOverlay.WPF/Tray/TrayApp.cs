using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using RefreshRateOverlay.WPF.Rendering;
using RefreshRateOverlay.WPF.Services;
using Shared;

namespace RefreshRateOverlay.WPF.Tray;

/// <summary>
/// Owns the tray icon and all services. WPF has no native tray-icon control,
/// so this still uses System.Windows.Forms.NotifyIcon/ContextMenuStrip (a
/// common, dependency-free pattern for WPF apps) while the settings surface
/// itself is the WPF OverlayWindow.
/// </summary>
internal sealed class TrayApp : IDisposable
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_RESTORE = 9;

    private readonly IniStore            _ini;
    private readonly ProfileService      _profiles;
    private readonly ForegroundTracker   _tracker;
    private readonly ProcessStartWatcher _processStartWatcher;
    private readonly SystemMessageSink   _msgSink;
    private readonly HotkeyService       _hotkey;

    private bool _hdrSupported;
    private int  _currentRate;

    // One SyncableSetting per default+profile setting with a live readback —
    // see SyncableSetting remarks. _syncables is what ReconcileAll walks;
    // adding a fourth setting of this shape means constructing one more
    // instance and appending it here, not writing a new ReconcileXxx method.
    private readonly SyncableSetting<int>             _rateSetting;
    private readonly SyncableSetting<bool>            _hdrSetting;
    private readonly SyncableSetting<GsyncGlobalMode> _gsyncSetting;
    private readonly List<ISyncableSetting>           _syncables;

    // Tray-menu-togglable: whether OnProcessStarted speculatively applies a
    // profile ahead of focus at all. Persisted so the user's choice survives
    // a restart.
    private bool _earlyApplyOnStart;

    // Tray-menu-togglable: whether a profiled app that has gained focus stays
    // the effective app (ResolveStickyApp) even after focus moves elsewhere,
    // until its process actually exits — see StickyProfileTracker remarks.
    // Persisted so the user's choice survives a restart.
    private bool _stickyProfilesEnabled;
    private readonly StickyProfileTracker _stickyProfiles = new();

    // Tray-menu-togglable: whether ReconcileAll absorbs an external change
    // into the INI (on, the default) or reasserts the INI's own target back
    // onto hardware instead, undoing the external change — see ReconcileAll
    // remarks. Persisted so the user's choice survives a restart.
    private bool _reconciliationEnabled;

    // Backoff state for reconciliation-off enforcement — one per push bucket
    // (Rate+HDR always pushed together, G-SYNC separate), since that's how
    // ApplyProfile/ApplyGsyncMode already group things. See
    // EnforcementBackoffTracker and ReconcileAll's reconciliation-off branch.
    private readonly EnforcementBackoffTracker _displayBackoff = new();
    private readonly EnforcementBackoffTracker _gsyncBackoff = new();

    // Guards against overlapping G-SYNC enforcement settle loops — Display's
    // equivalent is already handled by _settleCts cancelling any prior
    // SettleAsync, but G-SYNC's enforcement push has no such supersede
    // mechanism (a normal ApplyGsyncMode call is fire-and-forget, unrelated
    // to this), so this simple flag is enough to stop a fresh HardwareChange
    // (e.g. leaving the NVIDIA App again) from starting a second DRS session
    // write while one is still settling.
    private bool _gsyncEnforceInFlight;

    private const int DisplaySettleWindowMs = 5000;
    private const int DisplaySettlePollMs   = 400;

    // Coarser than Display's — each retry here is a DRS session open/write/
    // save (see NvidiaGsyncService remarks on session cost and the
    // last-write-wins collision risk), not a cheap hardware read, so this
    // deliberately polls less often than Rate/HDR's settle loop.
    private const int GsyncSettleWindowMs = 3000;
    private const int GsyncSettlePollMs   = 750;

    // Polls actual process liveness for whatever's sticky-tracked — the sole
    // release mechanism for the pop side (see StickyLivenessTick remarks for
    // why event-driven exit detection was dropped in favor of this).
    private readonly System.Windows.Forms.Timer _stickyLivenessTimer;

    // True from the moment ApplyDisplayState issues a change until SettleAsync
    // confirms hardware landed on the target (or times out) — see SettleAsync
    // and OnDisplayChange remarks. While true, OnDisplayChange treats any
    // WM_DISPLAYCHANGE as expected settling noise from our own apply, not an
    // external change.
    private bool _settling;

    private bool _gsyncAvailable;

    // Write-through cache of NVIDIA's global G-SYNC mode — the single source
    // of truth every read in this class uses (_gsyncSetting's tryReadLive,
    // ApplyGsyncMode's push guard). Nothing calls NvidiaGsyncService.
    // TryGetGlobalMode() on any recurring cadence anymore — a live read costs
    // ~130ms (NVIDIA's DRS API has no narrower entry point than loading its
    // entire ~8,000-profile database; see plans/2026-09-10-gsync-cache.md for
    // the measurements), so it's paid exactly twice: once at startup
    // (seeding, below) and once whenever focus leaves the NVIDIA App (see
    // RefreshGsyncCacheIfLeavingNvidiaApp) — the point someone is most likely
    // to have just changed it by hand. Every write updates this field
    // alongside the real NVIDIA push (ApplyGsyncMode, EnforceGsyncAsync), so
    // it can only go stale from an external change not yet detected, never
    // from anything this app itself does.
    private GsyncGlobalMode _gsyncCache;

    private readonly NotifyIcon _tray;
    private Icon? _currentIcon;

    private OverlayWindow? _overlay;

    // Confirms hardware state against the last intended (rate, HDR) target for a
    // short window after every apply, since SetDisplayConfig's HDR call can return
    // before the driver/TV actually finish renegotiating the HDMI link — a rate
    // change issued right after can get silently clamped by the still-settling
    // link. A fresh ApplyDisplayState call cancels whatever settle pass was
    // still running for the previous target. Distinct from ReconcileAll/
    // ReconciliationEnabled below: this is confirming OUR OWN apply landed,
    // not reacting to an externally-caused change — see SettleAsync vs
    // ReconcileAll remarks.
    private CancellationTokenSource? _settleCts;

    private const string TaskName        = "RefreshRateOverlay.WPF";
    private static readonly string ExePath = Path.Combine(AppContext.BaseDirectory, "RefreshRateOverlay.WPF.exe");
    private const string TaskDescription = "Launches RefreshRateOverlay (WPF) at logon.";

    public TrayApp()
    {
        string iniPath = Path.Combine(AppContext.BaseDirectory, "RefreshSettings.ini");
        _ini      = new IniStore(iniPath);
        _profiles = new ProfileService(_ini);

        _earlyApplyOnStart = _profiles.ReadEarlyApplyOnStart();
        _stickyProfilesEnabled = _profiles.ReadStickyProfilesEnabled();
        _reconciliationEnabled = _profiles.ReadReconciliationEnabled();

        (_hdrSupported, bool hdrEnabled) = HdrService.GetState();
        _currentRate = DisplayService.GetCurrentRate();

        int storedDefaultRate = _profiles.Rate.ReadDefault();
        int seedRate = storedDefaultRate;
        if (_currentRate > 0 &&
            ReconcilePlanner.PlanSeed(_profiles.Rate.HasDefault(), storedDefaultRate, _currentRate, _reconciliationEnabled)
                == SeedAction.AdoptLive)
        {
            seedRate = _currentRate;
        }
        _rateSetting = new SyncableSetting<int>(
            "Refresh rate", _profiles.Rate,
            isAvailable: () => true,
            tryReadLive: (out int v) => { v = _currentRate; return true; },
            describe: v => $"{v} Hz",
            initialDefault: seedRate);
        if (seedRate != storedDefaultRate) _profiles.Rate.WriteDefault(seedRate);

        bool storedDefaultHdr = _profiles.Hdr.ReadDefault();
        bool seedHdr = storedDefaultHdr;
        if (_hdrSupported &&
            ReconcilePlanner.PlanSeed(_profiles.Hdr.HasDefault(), storedDefaultHdr, hdrEnabled, _reconciliationEnabled)
                == SeedAction.AdoptLive)
        {
            seedHdr = hdrEnabled;
        }
        _hdrSetting = new SyncableSetting<bool>(
            "HDR", _profiles.Hdr,
            isAvailable: () => _hdrSupported,
            tryReadLive: (out bool v) => { v = HdrService.GetState().Enabled; return true; },
            describe: v => v ? "On" : "Off",
            initialDefault: seedHdr);
        if (seedHdr != storedDefaultHdr) _profiles.Hdr.WriteDefault(seedHdr);

        // Must exist before the ApplyDisplayState call below: ApplyRate calls
        // UpdateTrayIcon whenever the live rate actually differs from the
        // seeded default (not always a no-op — e.g. hardware left at a rate
        // some other app's profile set, on a restart before it settles back),
        // and UpdateTrayIcon writes _tray.Icon. Everything BuildContextMenu's
        // click handlers capture (_rateSetting, _gsyncSetting, _gsyncAvailable,
        // etc.) is only read lazily when a menu item is actually clicked, long
        // after the rest of this constructor has run — safe to build now.
        _tray = new NotifyIcon
        {
            Text    = "RefreshRateOverlay",
            Visible = true,
        };
        UpdateTrayIcon(_currentRate);
        _tray.ContextMenuStrip = BuildContextMenu();
        _tray.MouseClick += TrayMouseClick;

        ApplyDisplayState(_rateSetting.Default, _hdrSetting.Default);

        // Same reseed-from-live-state-at-startup treatment as HDR just above,
        // now gated by the same ReconcilePlanner.PlanSeed decision — see its
        // remarks. NVIDIA's base profile is the "hardware" here. Confirmed by
        // testing that VRR_MODE only ever behaves as a true global switch — no
        // per-app NVIDIA write exists anywhere in this class, only
        // ApplyGsyncMode below pushing whatever our own INI resolves to for
        // the current app into that one base-profile setting.
        _gsyncAvailable = NvidiaGsyncService.IsAvailable;
        GsyncGlobalMode storedDefaultGsync = _profiles.GsyncMode.ReadDefault();
        GsyncGlobalMode seedGsync = storedDefaultGsync;
        if (_gsyncAvailable && NvidiaGsyncService.TryGetGlobalMode(out var liveGsyncMode) &&
            ReconcilePlanner.PlanSeed(_profiles.GsyncMode.HasDefault(), storedDefaultGsync, liveGsyncMode, _reconciliationEnabled)
                == SeedAction.AdoptLive)
        {
            seedGsync = liveGsyncMode;
        }
        _gsyncSetting = new SyncableSetting<GsyncGlobalMode>(
            "G-SYNC", _profiles.GsyncMode,
            isAvailable: () => _gsyncAvailable,
            tryReadLive: (out GsyncGlobalMode v) => { v = _gsyncCache; return true; },
            describe: v => v.ToString(),
            initialDefault: seedGsync);
        if (_gsyncAvailable)
        {
            if (seedGsync != storedDefaultGsync) _profiles.GsyncMode.WriteDefault(seedGsync);
            NvidiaGsyncService.SetGlobalMode(seedGsync);
        }
        _gsyncCache = seedGsync;

        _syncables = new List<ISyncableSetting> { _rateSetting, _hdrSetting, _gsyncSetting };

        // HardwareChange subscriber — see its own remarks. Adding a fourth
        // setting of this shape later means constructing one more
        // SyncableSetting and appending it to _syncables above, not writing a
        // new ReconcileXxx method or a new subscription here.
        HardwareChange += ReconcileAll;

        _tracker = new ForegroundTracker();
        _tracker.AppChanged += OnAppChanged;
        _tracker.Start();

        // Early-HDR path for games whose render pipeline queries HDR once at
        // startup, well before their window ever gets focus — see
        // OnProcessStarted remarks.
        _processStartWatcher = new ProcessStartWatcher();
        _processStartWatcher.ProcessStarted += OnProcessStarted;
        _processStartWatcher.Start();

        // StickyProfileTracker's sole pop mechanism — see StickyLivenessTick
        // remarks. 3s balances catching a closed app reasonably promptly
        // against the cost of a process-table scan every tick; always
        // running (cheap no-op when nothing's sticky-tracked) avoids a
        // start/stop dance every time the tray toggle flips.
        _stickyLivenessTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _stickyLivenessTimer.Tick += StickyLivenessTick;
        _stickyLivenessTimer.Start();

        _msgSink = new SystemMessageSink(OnDisplayChange);

        (uint hkMods, uint hkVk) = _profiles.ReadHotkey()
            ?? (HotkeyService.MOD_WIN | HotkeyService.MOD_ALT | HotkeyService.MOD_SHIFT, 0x52 /* R */);
        _hotkey = new HotkeyService(() => ShowOverlay(), hkMods, hkVk, Logger.Log);

        Logger.Log($"TrayApp started. Rate={_currentRate} HDR={_hdrSupported}");
    }

    // -------------------------------------------------------------------------
    // Foreground tracking
    // -------------------------------------------------------------------------
    private void OnAppChanged(object? sender, string app)
    {
        Logger.Log($"ForegroundTracker: app changed -> '{app}'");

        // ForegroundTracker only ever hands us the app being focused now,
        // never the one just left — captured here, before it's overwritten,
        // so RefreshGsyncCacheIfLeavingNvidiaApp can tell "we just left the
        // NVIDIA App" from every other transition.
        string previousApp = _previousForegroundApp;
        _previousForegroundApp = app;
        RefreshGsyncCacheIfLeavingNvidiaApp(previousApp);

        // A real, debounced focus change is always authoritative — supersedes
        // any outstanding guess about who's about to take over (see
        // AnticipatedAppTracker remarks), whether it confirms that guess or not.
        _anticipatedApp.RealFocusChanged();

        // See _seenFirstAppChange remarks: the very first callback only
        // reports pre-existing focus, not an observed open, so it's excluded
        // from sticky promotion below — captured before flipping the flag so
        // this specific call sees "was this the first."
        bool isFirstAppChange = !_seenFirstAppChange;
        _seenFirstAppChange = true;

        // Promote/register this app in the sticky stack BEFORE resolving —
        // if it has a profile, it becomes (or stays) the active app, so
        // ResolveStickyApp below naturally returns it unchanged. If the
        // newly focused app has no profile, nothing is pushed, and — if a
        // sticky app is still open underneath — that app keeps winning
        // instead of this focus change clobbering it.
        if (_stickyProfilesEnabled && !isFirstAppChange && HasAnyProfile(app))
        {
            Logger.Log($"StickyProfileTracker: '{app}' opened (has a profile) — now the active sticky app.");
            _stickyProfiles.AppOpened(app);
        }

        ApplyAllProfiles(ResolveStickyApp(app));
    }

    private readonly AnticipatedAppTracker _anticipatedApp = new();

    // Guards the very first OnAppChanged callback: ForegroundTracker's own
    // _lastApp starts empty, so its first-ever AppChanged just reports
    // whatever was ALREADY focused before Start() was called, not an "open"
    // this app actually witnessed happen. Sticky is meant to survive an
    // observed app losing focus temporarily (see StickyProfileTracker
    // remarks) — latching onto that first ambiguous sample would stick to a
    // guess, not something this app ever saw open. The profile still applies
    // normally either way (ApplyAllProfiles below runs unconditionally); only
    // the sticky promotion is skipped for this one callback.
    private bool _seenFirstAppChange;

    // What OnAppChanged's `app` parameter was on the PREVIOUS call — see its
    // own remarks and RefreshGsyncCacheIfLeavingNvidiaApp. Empty on the very
    // first callback, same as ForegroundTracker's own _lastApp; harmless,
    // since nothing will ever match NvidiaAppProcessName against "".
    private string _previousForegroundApp = string.Empty;

    // NVIDIA's DRS database has no change notification to hook the way
    // WM_DISPLAYCHANGE does for Rate/HDR, and a live read is far too
    // expensive to poll on a timer (~130ms — see _gsyncCache's remarks and
    // plans/2026-09-10-gsync-cache.md). Instead, refresh the cache exactly
    // when someone is most likely to have just changed G-SYNC by hand: the
    // moment focus leaves the NVIDIA App itself. Confirmed this actually
    // matters in practice — RefreshRateOverlay.WPF.log already has a real
    // instance of a G-SYNC change made this exact way, caught (at the time)
    // by the poll this replaces.
    private const string NvidiaAppProcessName = "NVIDIA App.exe";

    private void RefreshGsyncCacheIfLeavingNvidiaApp(string previousApp)
    {
        if (!_gsyncAvailable) return;
        if (!string.Equals(previousApp, NvidiaAppProcessName, StringComparison.OrdinalIgnoreCase)) return;
        if (!NvidiaGsyncService.TryGetGlobalMode(out var liveMode)) return;
        if (liveMode == _gsyncCache) return;

        _gsyncCache = liveMode;
        HardwareChange?.Invoke();
    }

    /// <summary>What TrayApp should actually treat as "the current app" for
    /// applying/reconciling profiles: the sticky-tracked app if the feature
    /// is on and one is open, otherwise whatever was passed in (typically
    /// real focus, or the anticipated-app resolution). See
    /// StickyProfileTracker remarks — this is the single place that decision
    /// gets made, so every call site (focus change, reconcile, the early-
    /// apply revert check, opening the overlay) agrees on the same app.</summary>
    private string ResolveStickyApp(string app) =>
        _stickyProfilesEnabled && _stickyProfiles.ActiveApp is { } sticky ? sticky : app;

    /// <summary>StickyProfileTracker's sole pop mechanism: on every tick,
    /// drop any sticky-tracked app that's no longer actually running.
    ///
    /// This used to be event-driven (a WMI Win32_ProcessStopTrace watcher,
    /// firing the instant a tracked process exited) — dropped after a real
    /// production bug: a closed game (Doom) stayed sticky-active
    /// indefinitely because the exit event never fired. This app's
    /// Win32_ProcessStartTrace watcher (ProcessStartWatcher, the exact same
    /// WMI trace mechanism, just for process START) has thrown intermittent
    /// "Access Denied" ManagementExceptions in production for weeks —
    /// concrete evidence this WMI event channel isn't reliable enough to be
    /// the ONLY way a sticky app ever gets released. Polling actual process
    /// existence (Process.GetProcessesByName) doesn't depend on any event
    /// ever being delivered, so a missed/failed WMI subscription can no
    /// longer strand a closed app as permanently "active".</summary>
    private void StickyLivenessTick(object? sender, EventArgs e)
    {
        if (!_stickyProfilesEnabled) return;

        bool changed = _stickyProfiles.PruneClosedApps(IsProcessRunning);
        if (!changed) return;

        Logger.Log("StickyProfileTracker: liveness check released a closed app.");
        ApplyAllProfiles(ResolveStickyApp(_tracker.LastApp));
    }

    private static bool IsProcessRunning(string exeName)
    {
        string name = Path.GetFileNameWithoutExtension(exeName);
        var procs = Process.GetProcessesByName(name);
        try { return procs.Length > 0; }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    /// <summary>Applies every per-app setting (rate/HDR, DSX, G-SYNC) for the
    /// given app in one call — the single "make hardware match this app's
    /// profile" entry point shared by focus-change (OnAppChanged), the
    /// speculative early-start push (OnProcessStarted), and that push's own
    /// revert (ScheduleEarlyApplyRevertCheck). One shared definition of "apply
    /// this app" instead of each caller picking its own subset.</summary>
    // The most recently resolved app ApplyAllProfiles actually applied a
    // profile for — lets it tell "genuinely switching to a different app"
    // (reset the enforcement backoff trackers — a fresh attempt) from
    // "Sticky Profiles resolving back to the SAME app again because
    // something else briefly took real focus" (leave backoff alone). Real
    // production bug this fixes: with Sticky Profiles on, WM_DISPLAYCHANGE/
    // ForegroundTracker noise from totally unrelated apps kept re-resolving
    // to the same sticky app and re-triggering this unconditional reset,
    // wiping out the backoff escalation against an ONGOING external fight
    // (WRC.exe repeatedly resetting its own display mode after a crash)
    // every single time — so it could never reach give-up no matter how long
    // the fight went on, as long as focus kept moving around elsewhere.
    private string? _lastAppliedProfileApp;

    private void ApplyAllProfiles(string app)
    {
        // A real, deliberate switch to a DIFFERENT app is a fresh attempt
        // regardless of what an unrelated background enforcement fight was
        // doing for the previous one — see EnforcementBackoffTracker
        // remarks. Reapplying the SAME app again (sticky resolving back to
        // it, an early-apply re-confirmation, etc.) is not a fresh
        // situation for THAT app's own ongoing fight, so it must not clear
        // escalation that's already in progress.
        if (app != _lastAppliedProfileApp)
        {
            _displayBackoff.Reset();
            _gsyncBackoff.Reset();
        }
        _lastAppliedProfileApp = app;

        ApplyProfile(app);
        ApplyDsxProfileForApp(app);
        ApplyGsyncMode(app);
    }

    private void ApplyProfile(string app, Action<bool>? onSettled = null)
    {
        // Re-sync from actual hardware before deciding whether a rate change is
        // needed: _currentRate is only ever updated by our own calls, so if
        // anything outside this app (the game's own fullscreen setup, a driver
        // renegotiation, etc.) changed the real rate, the cached value would be
        // stale and ApplyRate's no-op guard would wrongly skip re-applying it.
        _currentRate = DisplayService.GetCurrentRate();

        (int targetRate, bool targetHdr) = ResolveTarget(app);

        Logger.Log($"ApplyProfile: app='{app}' target={targetRate} hwRateBefore={_currentRate}");

        ApplyDisplayState(targetRate, targetHdr, onSettled);
    }

    /// <summary>What rate/HDR *should* be right now for the given app: its own
    /// profile if it has one, otherwise the shared default. Same resolution used
    /// both to apply (ApplyProfile) and to detect drift (OnDisplayChange) —
    /// one shared definition of "correct" instead of two that could disagree.</summary>
    private (int Rate, bool Hdr) ResolveTarget(string app) =>
        (_rateSetting.ResolveTarget(app), _hdrSupported && _hdrSetting.ResolveTarget(app));

    // -------------------------------------------------------------------------
    // Early apply (process start, ahead of focus)
    // -------------------------------------------------------------------------

    /// <summary>Front-runs focus for apps the user has configured a profile
    /// for — same ApplyAllProfiles call OnAppChanged makes, just triggered by
    /// the process existing rather than by it taking foreground focus. Exists
    /// because some games' render pipelines query HDR once at startup, well
    /// before their window ever shows, so waiting for focus is too late for
    /// those specifically — but once acting early at all, there's no reason
    /// to apply it narrowly: rate/DSX/G-SYNC all already go through this same
    /// per-app resolution on a normal focus change, so a second trigger that
    /// only forwarded a subset of them would just be a second, inconsistent
    /// application model to reason about. User-togglable via the tray menu
    /// (_earlyApplyOnStart) since it's a guess, not a certainty — see below.
    ///
    /// This is a speculative push — the process starting doesn't guarantee it
    /// ever takes foreground focus (a helper/updater process, a launcher that
    /// spawns a differently-named child, a crash on boot). ScheduleEarlyApplyRevertCheck
    /// is what corrects a wrong guess; nothing here needs to coordinate with
    /// OnAppChanged directly — both funnel through the same ApplyAllProfiles/
    /// ApplyDisplayState, which already treats a fresh call as superseding
    /// whatever came before.
    ///
    /// Also promotes to the sticky stack immediately, same as OnAppChanged does
    /// on real focus — not just on the apply below. Without this, a companion
    /// process that steals real OS focus before the profiled app's own window
    /// ever shows (e.g. an anti-cheat launcher racing the game's own process
    /// start) resolves with no sticky app to protect it yet, since sticky
    /// promotion used to wait for the profiled app's OWN focus event — which
    /// can't happen until after that companion process is done stealing focus.
    /// Safe to promote on a guess: StickyLivenessTick prunes it the moment the
    /// process exits, so a wrong guess (never takes focus) self-corrects within
    /// one liveness poll rather than sticking around indefinitely.</summary>
    private void OnProcessStarted(object? sender, string app)
    {
        if (!_earlyApplyOnStart || !HasAnyProfile(app)) return;

        Logger.Log($"ProcessStartWatcher: '{app}' started, early-applying its profile ahead of focus.");
        _anticipatedApp.Started(app);

        if (_stickyProfilesEnabled)
        {
            Logger.Log($"StickyProfileTracker: '{app}' started (has a profile) — now the active sticky app.");
            _stickyProfiles.AppOpened(app);
        }

        ApplyAllProfiles(app);

        ScheduleEarlyApplyRevertCheck(app);
    }

    private bool HasAnyProfile(string app) =>
        _rateSetting.HasProfile(app) ||
        _hdrSetting.HasProfile(app) ||
        _profiles.Dsx.HasProfile(app) ||
        _gsyncSetting.HasProfile(app);

    /// <summary>If the anticipated app never actually takes foreground focus
    /// within this window, the early push above was a wrong guess — re-resolve
    /// and reapply for whatever app is really focused (ForegroundTracker.LastApp,
    /// possibly "" for none yet/desktop) to undo it. If the anticipated app did
    /// take focus in the meantime, OnAppChanged already reapplied for it — this
    /// is then a no-op re-confirmation, not a correction.</summary>
    private void ScheduleEarlyApplyRevertCheck(string anticipatedApp)
    {
        const int windowMs = 9000; // generous for slow game boot, short enough not to strand a wrong guess

        var timer = new System.Windows.Forms.Timer { Interval = windowMs };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();

            _anticipatedApp.WindowExpired(anticipatedApp);

            string actualApp = _tracker.LastApp;
            if (actualApp == anticipatedApp) return;

            Logger.Log($"ProcessStartWatcher: '{anticipatedApp}' never took focus (actual='{actualApp}') — reverting early apply.");
            ApplyAllProfiles(ResolveStickyApp(actualApp));
        };
        timer.Start();
    }

    // -------------------------------------------------------------------------
    // DSX controller-profile application
    // -------------------------------------------------------------------------

    /// <summary>Falls back to the default controller profile the same way rate/HDR
    /// fall back to _rateSetting.Default/_hdrSetting.Default. An empty default
    /// means "leave the controller alone" (never configured).</summary>
    private void ApplyDsxProfileForApp(string app)
    {
        string profile = _profiles.Dsx.TryReadProfile(app, out string p) ? p : _profiles.Dsx.ReadDefault();
        if (profile.Length > 0) _ = ApplyDsxProfileToDevicesAsync(profile);
    }

    private static async Task ApplyDsxProfileToDevicesAsync(string profileName)
    {
        var devices = await DsxProfileService.ListDevicesAsync();
        if (devices.Count == 0)
        {
            Logger.Log($"ApplyDsxProfile: no DSX devices connected, wanted '{profileName}'.");
            return;
        }
        foreach (var device in devices)
            await DsxProfileService.ChangeProfileAsync(device.MacAddress, profileName);
    }

    // -------------------------------------------------------------------------
    // G-SYNC application — our INI is the source of truth (ProfileService),
    // NVIDIA's base-profile VRR_MODE is just the push target, exactly like
    // DisplayService/HdrService are for rate/HDR. There is no per-app NVIDIA
    // write: "per-app" behavior comes entirely from resolving (per-app override
    // ?? default) here and reasserting that single value into the one base
    // profile setting on every foreground switch, same as ApplyProfile does for
    // rate/HDR against actual hardware.
    //
    // Skips the push entirely when the resolved target already matches
    // _gsyncCache — NvidiaGsyncService.SetGlobalMode opens a DRS session and
    // Save()s unconditionally, with no "is this actually different" guard of
    // its own (unlike ApplyRate's _currentRate check), and re-saving the base
    // profile is enough to make the driver retrain/blank the display link
    // even when the value being written is identical. Every ApplyAllProfiles
    // call — including Sticky Profiles resolving back to the SAME app on
    // every alt-tab away and back — used to hit this unconditionally, which
    // was a real, self-inflicted cause of a black flash on every such switch
    // even though G-SYNC was never actually changing. _gsyncCache itself is
    // only ever refreshed from live NVIDIA state at startup and when leaving
    // the NVIDIA App (see RefreshGsyncCacheIfLeavingNvidiaApp) — an actual
    // external change made any other way is caught independently once one of
    // those refreshes happens, same as before.
    // -------------------------------------------------------------------------
    private void ApplyGsyncMode(string app)
    {
        if (!_gsyncAvailable) return;

        GsyncGlobalMode target = _gsyncSetting.ResolveTarget(app);
        if (_gsyncCache == target) return;

        if (NvidiaGsyncService.SetGlobalMode(target)) _gsyncCache = target;
    }

    /// <summary>
    /// The reconciliation-off enforcement push for G-SYNC — unlike the normal
    /// fire-and-forget ApplyGsyncMode above, this confirms the push actually
    /// stuck (via SettleLoop, same shape as Rate/HDR's SettleAsync but with
    /// a coarser window — see GsyncSettleWindowMs/GsyncSettlePollMs remarks)
    /// and reports the outcome to _gsyncBackoff. Only ever called from
    /// ReconcileAll's reconciliation-off branch, gated by _gsyncEnforceInFlight
    /// so a second HardwareChange can't start a second DRS session write
    /// while this one is still settling.
    /// </summary>
    private async Task EnforceGsyncAsync(GsyncGlobalMode target)
    {
        NvidiaGsyncService.SetGlobalMode(target);
        bool converged = await SettleLoop.RunAsync(
            isDrifted: () => !NvidiaGsyncService.TryGetGlobalMode(out var live) || live != target,
            push: () => NvidiaGsyncService.SetGlobalMode(target),
            CancellationToken.None,
            GsyncSettleWindowMs, GsyncSettlePollMs);

        // Only update _gsyncCache once the push actually landed — an
        // unconverged attempt means hardware may still be at something else,
        // so ApplyGsyncMode/tryReadLive must not be tricked into trusting a
        // value that was never actually confirmed.
        if (converged) _gsyncCache = target;

        _gsyncEnforceInFlight = false;
        HandleEnforcementResult(_gsyncBackoff, "G-SYNC", converged);
    }

    /// <summary>No drift observed this tick. Does NOT immediately clear the
    /// tracker on the strength of that alone — a single quiet tick can just be
    /// the calm between two flaps of the very same ongoing fight (the WRC.exe
    /// regression this guards against: a crashed game flapping its own display
    /// mode every few seconds, with each of our reverts individually
    /// succeeding in between). Only once IsRecentlyActive says enough calm
    /// time has actually passed does this reset the streak and balloon "back
    /// in sync" — and only if it was actually struggling, never for the
    /// common case where nothing was ever wrong.</summary>
    private void HandleEnforcementRecovery(EnforcementBackoffTracker tracker, string label)
    {
        if (tracker.IsRecentlyActive(DateTime.UtcNow)) return;

        bool wasStruggling = tracker.ConsecutiveFailures > 0;
        tracker.Reset();
        if (wasStruggling)
        {
            Logger.Log($"Reconcile (disabled): {label} back in sync — clearing backoff.");
            NotifyRecovered(label);
        }
        RefreshOverlayApplyWarning();
    }

    /// <summary>Records one enforcement attempt's settle outcome and surfaces
    /// the transition. Convergence is no longer what decides whether the fight
    /// is "over" — see EnforcementBackoffTracker's remarks — so this only ever
    /// balloons a one-time "gave up" the moment the tracker crosses its
    /// give-up threshold; "back in sync" is HandleEnforcementRecovery's job,
    /// once a real calm gap has actually passed.</summary>
    private void HandleEnforcementResult(EnforcementBackoffTracker tracker, string label, bool converged)
    {
        tracker.RecordResult(converged, DateTime.UtcNow);

        if (!converged)
            Logger.Log($"Reconcile (disabled): {label} push did not converge.");

        if (tracker.GaveUp)
        {
            Logger.Log($"Reconcile (disabled): {label} keeps needing to be reverted — giving up until Apply is clicked.");
            NotifyGaveUp(label);
        }
        RefreshOverlayApplyWarning();
    }

    /// <summary>Reflects current gave-up state into the open overlay, if any —
    /// so a warning shown while enforcement was already failing doesn't sit
    /// there stale, and a fresh give-up shows up without needing to reopen
    /// Settings. See OverlayWindow.SetApplyWarning.</summary>
    private void RefreshOverlayApplyWarning()
    {
        if (_overlay is not { } overlay) return;

        var failed = new List<string>();
        if (_displayBackoff.GaveUp) failed.Add("Refresh rate/HDR");
        if (_gsyncAvailable && _gsyncBackoff.GaveUp) failed.Add("G-SYNC");

        overlay.SetApplyWarning(failed.Count > 0 ? string.Join(", ", failed) : null);
    }

    // -------------------------------------------------------------------------
    // HardwareChange — a generic "something external changed, go check for
    // drift" signal, deliberately decoupled from what detected it. Two
    // raisers today — OnDisplayChange (below) for WM_DISPLAYCHANGE, and
    // RefreshGsyncCacheIfLeavingNvidiaApp for NVIDIA's DRS database, which
    // has no equivalent OS notification — both feed the single ReconcileAll
    // subscriber, which walks every registered SyncableSetting (see its
    // remarks). Adding
    // another detection source later (a poll for some future setting, say)
    // means adding its own detector that raises this event — never editing
    // ReconcileAll to know about a new source, and never writing a new
    // per-setting Reconcile method by hand.
    // -------------------------------------------------------------------------
    private event Action? HardwareChange;

    // -------------------------------------------------------------------------
    // Display change detection — while _settling is true, this is our own apply
    // still riding out the driver/TV's negotiation delay (see ApplyDisplayState/
    // SettleAsync, which own that flag), not an external change, so it's
    // ignored entirely. _settling is specific to the display (HDMI/TV settling
    // time) — a future raiser with its own settling semantics would own its own
    // guard rather than share this one.
    // -------------------------------------------------------------------------
    private void OnDisplayChange()
    {
        if (_settling)
        {
            Logger.Log("OnDisplayChange: ignored (still settling from our own apply).");
            return;
        }

        // Unlike _gsyncAvailable (a static driver-presence fact, checked once),
        // HDR support is live display-path negotiation state — a display that
        // hasn't finished negotiating with the GPU yet at startup can report
        // unsupported, and without this we'd hide the HDR control for the rest
        // of the session even once the display catches up. Only adopts a flip
        // false -> true; never re-hides an already-supported display off a
        // single transient read during a WM_DISPLAYCHANGE settling window.
        if (!_hdrSupported && HdrService.GetState().Supported)
        {
            _hdrSupported = true;
            Logger.Log("OnDisplayChange: HDR now reports supported (was unsupported at startup).");
        }

        Logger.Log("OnDisplayChange: not settling, treating as external — invoking HardwareChange.");
        HardwareChange?.Invoke();
    }

    /// <summary>
    /// Subscribed to HardwareChange. Any external change means something
    /// outside the overlay changed it — the user via Windows Settings/NVCP/the
    /// NVIDIA app, or the app itself re-asserting its own preference.
    ///
    /// With _reconciliationEnabled (the default), this doesn't fight it: each
    /// SyncableSetting records the observed value as the new truth, into
    /// whichever slot is currently active for the foreground app (its own
    /// profile if it has one, otherwise the shared default) — same rule for
    /// every setting, since both cases are "something changed outside the
    /// overlay" and get written back the same way. One loop over _syncables
    /// instead of a hand-written ReconcileXxx per setting — see
    /// SyncableSetting remarks.
    ///
    /// With it off, the INI is instead treated as the sole source of truth:
    /// nothing gets persisted from live state, and any drift is reverted by
    /// reasserting the INI's own resolved target back onto hardware — the
    /// same push ApplyProfile/ApplyGsyncMode already do on a normal focus
    /// change, just re-triggered here because something other than a focus
    /// change caused the drift.
    /// </summary>
    private void ReconcileAll()
    {
        _currentRate = DisplayService.GetCurrentRate();
        UpdateTrayIcon(_currentRate);

        // Prefers the anticipated app (if any) over LastApp — see
        // AnticipatedAppTracker remarks — then the sticky-tracked app (if
        // any) over that, since sticky is a deliberate, longer-lived user
        // choice that should win over both real and anticipated focus.
        string app = ResolveStickyApp(_anticipatedApp.ResolveApp(_tracker.LastApp));

        if (_reconciliationEnabled)
        {
            foreach (var setting in _syncables)
            {
                var result = setting.Reconcile(app);
                if (result is { Action: not ReconcileAction.None } r)
                {
                    Logger.Log($"Reconcile: {setting.Name} drifted to {r.Live} (target was {r.Target}) for app='{app}' -> {r.Action}.");
                    NotifyReconciled(setting.Name, r.Live, app, r.Action);
                }
            }
        }
        else
        {
            // Rate and HDR are pushed together via ApplyProfile (which itself
            // enforces HDR-before-rate ordering — see ApplyDisplayState) rather
            // than reverted independently, same as every other caller of this
            // pair. Each bucket's own EnforcementBackoffTracker decides whether
            // this tick is even allowed to attempt a push — see its remarks.
            const string displayLabel = "Refresh rate/HDR";
            if (!(_rateSetting.IsDrifted(app) || _hdrSetting.IsDrifted(app)))
            {
                HandleEnforcementRecovery(_displayBackoff, displayLabel);
            }
            else if (_displayBackoff.ShouldAttempt(DateTime.UtcNow))
            {
                bool freshStreak = _displayBackoff.ConsecutiveFailures == 0;
                Logger.Log($"Reconcile (disabled): rate/HDR changed externally for app='{app}' — reverting to INI target.");
                if (freshStreak) NotifyReverted(displayLabel);
                ApplyProfile(app, onSettled: converged => HandleEnforcementResult(_displayBackoff, displayLabel, converged));
            }

            if (_gsyncAvailable)
            {
                const string gsyncLabel = "G-SYNC";
                if (!_gsyncSetting.IsDrifted(app))
                {
                    HandleEnforcementRecovery(_gsyncBackoff, gsyncLabel);
                }
                else if (!_gsyncEnforceInFlight && _gsyncBackoff.ShouldAttempt(DateTime.UtcNow))
                {
                    bool freshStreak = _gsyncBackoff.ConsecutiveFailures == 0;
                    Logger.Log($"Reconcile (disabled): G-SYNC changed externally for app='{app}' — reverting to INI target.");
                    if (freshStreak) NotifyReverted(gsyncLabel);
                    _gsyncEnforceInFlight = true;
                    _ = EnforceGsyncAsync(_gsyncSetting.ResolveTarget(app));
                }
            }
        }

        // If the overlay is open, push it its OWN app's resolved target — not
        // gated on overlay.ActiveApp == app (the app that was foreground when
        // this reconcile ran). Reconciling can easily happen while a different
        // app is focused than the overlay's — e.g. a write above just landed
        // on the shared default, changed from within the NVIDIA app itself,
        // which is a real foreground switch away from whatever the overlay is
        // showing. A default-scope change is still relevant to the overlay's
        // app whenever that app has no profile of its own; re-resolving here
        // (rather than reusing app from above) is what makes that fall out
        // correctly instead of only handling the same-app-focused case.
        if (_overlay is { } overlay)
        {
            (int overlayRate, bool overlayHdr) = ResolveTarget(overlay.ActiveApp);
            overlay.RefreshLiveState(overlayRate, overlayHdr);
            if (_gsyncAvailable) overlay.RefreshGsyncLiveState(_gsyncSetting.ResolveTarget(overlay.ActiveApp));
        }
    }

    // -------------------------------------------------------------------------
    // Rate application
    // -------------------------------------------------------------------------
    private void ApplyRate(int rate)
    {
        if (_currentRate == rate) return;

        if (DisplayService.SetRate(rate))
        {
            _currentRate = rate;
            UpdateTrayIcon(_currentRate);
            Logger.Log($"Rate changed to {rate} Hz");
        }
        else
        {
            Logger.Log($"Rate change to {rate} Hz FAILED (ChangeDisplaySettingsW rejected it).");
        }
    }

    /// <summary>
    /// Applies HDR before refresh rate — never the other way around. On
    /// bandwidth-constrained links (e.g. a TV on HDMI 2.0) requesting a higher
    /// refresh rate while still in HDR/higher-bit-depth mode can silently fail
    /// or get clamped back down by the driver, so HDR must be settled first to
    /// free up whatever headroom the rate change needs. Then spends up to a few
    /// seconds confirming hardware actually landed on this target, re-applying
    /// if it drifts (see SettleAsync) — the initial apply can still lose a
    /// race against the driver/TV settling the HDMI link asynchronously.
    /// </summary>
    private void ApplyDisplayState(int rate, bool hdrEnabled, Action<bool>? onSettled = null)
    {
        _settleCts?.Cancel();
        var cts = new CancellationTokenSource();
        _settleCts = cts;
        _settling = true;

        if (_hdrSupported) HdrService.SetState(hdrEnabled);
        ApplyRate(rate);

        _ = SettleAsync(rate, hdrEnabled, cts.Token, onSettled);
    }

    /// <summary>
    /// Polls actual hardware state for up to DisplaySettleWindowMs after an apply
    /// (via SettleLoop — see its remarks) and re-applies (HDR then rate) if it
    /// ever finds a mismatch against the target — absorbs the driver/TV's
    /// asynchronous HDMI settling delay instead of hoping the first attempt
    /// landed. Stops as soon as hardware matches the target, or if superseded by
    /// a newer ApplyDisplayState call (via the passed token) — clears _settling
    /// on the way out (guarded by the token so a superseded pass can't clear it
    /// out from under the newer one that replaced it), which is what lets
    /// OnDisplayChange start treating further changes as external. onSettled
    /// (only ever passed by the reconciliation-off enforcement path) reports
    /// whether it ultimately converged, guarded by the same token check so a
    /// superseded attempt never reports a stale result.
    ///
    /// Named distinctly from ReconcileAll/"Reconcile (disabled)" on purpose:
    /// this is confirming OUR OWN just-issued apply actually landed (the
    /// target hasn't changed, hardware just hasn't caught up yet) — never
    /// triggered by an externally-caused change the way ReconcileAll is.
    /// Logs under "Settle:" so the two are never confused in the log file.
    /// </summary>
    private async Task SettleAsync(int targetRate, bool targetHdr, CancellationToken token, Action<bool>? onSettled)
    {
        int lastHwRate = 0;
        bool converged = await SettleLoop.RunAsync(
            isDrifted: () =>
            {
                lastHwRate = DisplayService.GetCurrentRate();
                bool hwHdr = !_hdrSupported || HdrService.GetState().Enabled == targetHdr;
                return lastHwRate != targetRate || !hwHdr;
            },
            push: () =>
            {
                Logger.Log($"Settle: mismatch (hwRate={lastHwRate} target={targetRate}) — re-applying.");
                if (_hdrSupported) HdrService.SetState(targetHdr);
                _currentRate = lastHwRate; // resync so ApplyRate's no-op guard doesn't skip the needed reapply
                ApplyRate(targetRate);
            },
            token, DisplaySettleWindowMs, DisplaySettlePollMs);

        if (!token.IsCancellationRequested)
        {
            _settling = false;
            onSettled?.Invoke(converged);
        }
    }

    // -------------------------------------------------------------------------
    // Tray icon
    // -------------------------------------------------------------------------
    private void UpdateTrayIcon(int rate)
    {
        var old = _currentIcon;
        _currentIcon = TrayIconRenderer.CreateTextIcon(rate.ToString());
        _tray.Icon = _currentIcon;
        old?.Dispose();
    }

    /// <summary>Surfaces a reconciliation write (hardware drifted, ini updated
    /// to match) as a tray balloon — the log lines at each ReconcileXxx call
    /// site already record the technical detail; this is the same event made
    /// visible without having to go dig through the log file.</summary>
    private void NotifyReconciled(string setting, string valueDescription, string app, ReconcileAction action)
    {
        string scope = action == ReconcileAction.WriteProfile ? $"'{app}' profile" : "default (no profile for this app)";
        _tray.BalloonTipTitle = "RefreshRateOverlay";
        _tray.BalloonTipText  = $"{setting} changed to {valueDescription} outside the overlay — saved to {scope}.";
        _tray.BalloonTipIcon  = ToolTipIcon.Info;
        _tray.ShowBalloonTip(4000);
    }

    /// <summary>Surfaces a reconciliation revert (hardware drifted, overlay
    /// pushed the INI target back onto it) as a tray balloon — the
    /// reconciliation-off counterpart to NotifyReconciled above.</summary>
    private void NotifyReverted(string setting)
    {
        _tray.BalloonTipTitle = "RefreshRateOverlay";
        _tray.BalloonTipText  = $"{setting} was changed outside the overlay — reverted (reconciliation is off).";
        _tray.BalloonTipIcon  = ToolTipIcon.Info;
        _tray.ShowBalloonTip(4000);
    }

    /// <summary>One-time balloon for when a bucket's EnforcementBackoffTracker
    /// crosses its give-up threshold — see HandleEnforcementResult. Warning
    /// icon (not Info, unlike the other balloons here) since this needs the
    /// user to actually do something about it.</summary>
    private void NotifyGaveUp(string setting)
    {
        _tray.BalloonTipTitle = "RefreshRateOverlay";
        _tray.BalloonTipText  = $"{setting} keeps getting changed externally — giving up on auto-reverting it. Open Settings and click Apply to retry.";
        _tray.BalloonTipIcon  = ToolTipIcon.Warning;
        _tray.ShowBalloonTip(6000);
    }

    /// <summary>Balloon for when a bucket that was actually struggling (had
    /// consecutive failures, whether or not it had fully given up) converges
    /// again on its own — the counterpart to NotifyGaveUp.</summary>
    private void NotifyRecovered(string setting)
    {
        _tray.BalloonTipTitle = "RefreshRateOverlay";
        _tray.BalloonTipText  = $"{setting} is back in sync.";
        _tray.BalloonTipIcon  = ToolTipIcon.Info;
        _tray.ShowBalloonTip(3000);
    }

    // -------------------------------------------------------------------------
    // Context menu
    // -------------------------------------------------------------------------
    private ContextMenuStrip BuildContextMenu()
    {
        const string space = "   ";
        const string check = "✓ ";

        var menu = new ContextMenuStrip
        {
            Renderer        = new DarkMenuRenderer(),
            ShowCheckMargin = false,
            ShowImageMargin = false,
        };

        menu.Items.Add(new ToolStripMenuItem(" Refresh Rate Overlay") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(space + "Settings…", null, (_, _) => ShowOverlay());
        menu.Items.Add(space + "Change Hotkey…", null, (_, _) => ShowHotkeySettings());
        menu.Items.Add(new ToolStripSeparator());

        // Start at Login
        bool installed = StartupTaskService.IsInstalled(TaskName);
        var startupItem = new ToolStripMenuItem((installed ? check : space) + "Start at Login")
        {
            CheckOnClick = true,
            Checked      = installed,
        };
        bool reverting = false;
        startupItem.CheckedChanged += (sender, _) =>
        {
            if (reverting || sender is not ToolStripMenuItem item) return;
            item.Text = (item.Checked ? check : space) + "Start at Login";
            bool ok = item.Checked
                // The app itself now requires admin (app.manifest), so the startup task must
                // request RunLevel=HighestAvailable too — an elevated scheduled task launches
                // silently at login (no UAC prompt), matching a manual elevated launch.
                ? StartupTaskService.Install(TaskName, ExePath, TaskDescription, Logger.Log, requireElevation: true)
                : StartupTaskService.Uninstall(TaskName, Logger.Log);
            if (!ok)
            {
                reverting = true;
                item.Checked = !item.Checked;
                reverting = false;
                System.Windows.Forms.MessageBox.Show(
                    "Failed to update the startup task.\nCheck the log file for details.",
                    "RefreshRateOverlay",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        };

        menu.Items.Add(startupItem);

        // Apply Settings on Program Start (speculative early apply — see
        // OnProcessStarted)
        var earlyApplyItem = new ToolStripMenuItem((_earlyApplyOnStart ? check : space) + "Apply Settings on Program Start")
        {
            CheckOnClick = true,
            Checked      = _earlyApplyOnStart,
        };
        earlyApplyItem.CheckedChanged += (sender, _) =>
        {
            if (sender is not ToolStripMenuItem item) return;
            _earlyApplyOnStart = item.Checked;
            item.Text = (item.Checked ? check : space) + "Apply Settings on Program Start";
            _profiles.WriteEarlyApplyOnStart(_earlyApplyOnStart);
        };
        menu.Items.Add(earlyApplyItem);

        // Sticky Profiles — see StickyProfileTracker/ResolveStickyApp remarks.
        var stickyProfilesItem = new ToolStripMenuItem((_stickyProfilesEnabled ? check : space) + "Sticky Profiles")
        {
            CheckOnClick = true,
            Checked      = _stickyProfilesEnabled,
        };
        stickyProfilesItem.CheckedChanged += (sender, _) =>
        {
            if (sender is not ToolStripMenuItem item) return;
            _stickyProfilesEnabled = item.Checked;
            item.Text = (item.Checked ? check : space) + "Sticky Profiles";
            _profiles.WriteStickyProfilesEnabled(_stickyProfilesEnabled);

            if (_stickyProfilesEnabled)
            {
                // Start tracking from whatever's focused right now, if it
                // has a profile — otherwise the feature would only kick in
                // after the NEXT focus change, doing nothing for an already-
                // focused profiled app until the user alt-tabs away and back.
                if (HasAnyProfile(_tracker.LastApp)) _stickyProfiles.AppOpened(_tracker.LastApp);
            }
            else
            {
                // Drop tracking and immediately reapply for real focus — the
                // override stops the instant the feature is turned off.
                _stickyProfiles.Clear();
                ApplyAllProfiles(_tracker.LastApp);
            }
        };
        menu.Items.Add(stickyProfilesItem);

        // Reconcile External Changes — see ReconcileAll remarks. On by default
        // (existing behavior); unticking makes the INI the sole source of
        // truth, so an external change gets reverted instead of learned.
        var reconciliationItem = new ToolStripMenuItem((_reconciliationEnabled ? check : space) + "Reconcile External Changes")
        {
            CheckOnClick = true,
            Checked      = _reconciliationEnabled,
        };
        reconciliationItem.CheckedChanged += (sender, _) =>
        {
            if (sender is not ToolStripMenuItem item) return;
            _reconciliationEnabled = item.Checked;
            item.Text = (item.Checked ? check : space) + "Reconcile External Changes";
            _profiles.WriteReconciliationEnabled(_reconciliationEnabled);

            // Stale enforcement state from before the flip (either direction)
            // no longer means anything — reconciliation-on doesn't consult it,
            // and reconciliation-off should start clean rather than resuming
            // mid-backoff or already given up.
            _displayBackoff.Reset();
            _gsyncBackoff.Reset();
            RefreshOverlayApplyWarning();
        };
        menu.Items.Add(reconciliationItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(space + "Exit", null, (_, _) => ExitApp());

        return menu;
    }

    // -------------------------------------------------------------------------
    // Tray click -> overlay
    // -------------------------------------------------------------------------
    private void TrayMouseClick(object? sender, System.Windows.Forms.MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            ShowOverlay();
    }

    // -------------------------------------------------------------------------
    // Overlay
    // -------------------------------------------------------------------------
    // Carries the ORIGINAL pre-overlay foreground window across an app-picker
    // switch (see ShowOverlay's forcedApp remarks) — a switch closes and
    // reopens a fresh OverlayWindow, and by then GetForegroundWindow() would
    // return the overlay itself, not whatever was focused before it ever
    // opened. Only meaningful while _overlay is non-null.
    private IntPtr _overlayPriorForeground;

    // True only for the brief Close()-then-reopen inside an app-picker
    // switch — guards the Closed handler so it doesn't hand focus back to
    // _overlayPriorForeground mid-switch, which would yank focus away from
    // the overlay the user is still actively using.
    private bool _switchingOverlayApp;

    /// <summary>Opens the overlay for whatever's actually focused, or — when
    /// forcedApp is given — for a specific app instead, regardless of focus.
    /// forcedApp exists for the app picker (see OverlayWindow.AppSwitchRequested):
    /// some games capture keyboard input exclusively while focused, blocking
    /// the global hotkey no matter which combo is bound (confirmed: Doom: The
    /// Dark Ages), so picking their settings from the dropdown while a
    /// different app is genuinely focused is the only way to reach them.
    ///
    /// A picker switch closes the current OverlayWindow and opens a fresh one
    /// for the new app rather than retargeting the same instance in place —
    /// simpler than threading a "reload for a different app" path through
    /// every field OverlayWindow's constructor already resolves once, and
    /// this method already recomputes everything fresh on every call.</summary>
    private void ShowOverlay(string? forcedApp = null)
    {
        bool wasOpen = _overlay is not null;
        if (wasOpen)
        {
            if (forcedApp is null) { _overlay!.Close(); return; }
            _switchingOverlayApp = true;
            _overlay!.Close();
            _switchingOverlayApp = false;
        }

        // A forced app (app picker) is an explicit user choice — bypasses
        // sticky resolution entirely, same as it bypasses real focus.
        string app = forcedApp ?? ResolveStickyApp(_tracker.LastApp);
        if (string.IsNullOrEmpty(app)) app = "Desktop";

        // Remember whatever currently owns foreground/focus (typically the game)
        // so we can hand it back explicitly once the overlay closes. A real click
        // into the overlay's controls will activate it at some point no matter
        // what — that's unavoidable for an interactive dialog — and a fullscreen
        // app losing activation can drop behind other windows (or get minimized,
        // for apps using an exclusive-fullscreen swapchain) as a result. Windows
        // won't restore it on its own, so we do it ourselves. On a picker switch
        // the overlay itself is already foreground, so the ORIGINAL prior window
        // carries over instead of being recomputed.
        IntPtr priorForeground = wasOpen ? _overlayPriorForeground : GetForegroundWindow();
        _overlayPriorForeground = priorForeground;

        // Must be detected now, before the overlay ever shows: SHQueryUserNotificationState
        // (inside WindowModeService) reports on whatever is currently the actual foreground
        // window system-wide, so it has to run while that's still the game, not our overlay —
        // and only means anything for the app priorForeground actually belongs to. During a
        // picker switch to a non-focused app, priorForeground is neither, so detecting against
        // it would describe the wrong app's window entirely; Unknown is the honest answer.
        WindowMode windowMode = ForegroundTracker.GetProcessName(priorForeground) == app
            ? WindowModeService.Detect(priorForeground)
            : WindowMode.Unknown;

        // Sync reality before opening
        _currentRate = DisplayService.GetCurrentRate();
        UpdateTrayIcon(_currentRate);

        (_, bool currHdr) = _hdrSupported ? HdrService.GetState() : (false, _hdrSetting.Default);

        // hasDsxProfile drives the Save-checkbox/delete-button state — only a
        // per-app entry counts as "this app has its own profile". dsxProfile
        // (what the dropdown preselects) additionally falls back to the default,
        // same as preselectRate/preselectHdr below, so an app with no profile of
        // its own still shows what's actually applied instead of "Not Managed".
        bool    hasDsxProfile = _profiles.Dsx.TryReadProfile(app, out string dsxVal);
        string  defaultDsx    = _profiles.Dsx.ReadDefault();
        string? dsxProfile    = hasDsxProfile ? dsxVal : (defaultDsx.Length > 0 ? defaultDsx : null);

        bool hasGsyncModeProfile = _gsyncAvailable && _profiles.GsyncMode.HasProfile(app);

        // See OverlayWindow's AppVrrRow remarks. Null hides the row entirely
        // rather than showing a misleading value if the read fails (NVAPI
        // unavailable, app unknown to the driver, etc).
        VrrAppState? appVrrState = _gsyncAvailable && NvidiaGsyncService.TryGetAppVrrState(app, out var vrrState)
            ? vrrState
            : null;

        // Same no-default/global-scope shape as App VRR above — see
        // NvidiaGsyncService's remarks on AppVrr/FrameCap.
        uint? frameCapFps = _gsyncAvailable && NvidiaGsyncService.TryGetAppFrameCap(app, out var capFps)
            ? capFps
            : null;

        bool hasProfile = _profiles.Rate.HasProfile(app) || hasDsxProfile || hasGsyncModeProfile;

        // Opening the overlay never writes the INI — that's OnDisplayChange's job
        // now (see its remarks). What's actually stored (profile-or-default, same
        // resolution ApplyProfile/OnDisplayChange use) is handed to the overlay
        // as the sync dots' baseline — it compares that against whatever's live
        // in each control itself, including your own edits, not just what got
        // preselected here.
        (int storedRate, bool storedHdr) = ResolveTarget(app);

        // Preselect from the saved profile when one exists, not live hardware state —
        // otherwise if the actual display has drifted from what the profile says
        // (e.g. mid-thrash, or anything external changed it), the overlay shows the
        // wrong thing and looks like the saved profile itself is wrong.
        int  preselectRate = (hasProfile && _profiles.Rate.TryReadProfile(app, out int prRate)) ? prRate : _currentRate;
        bool preselectHdr  = (hasProfile && _hdrSupported && _profiles.Hdr.TryReadProfile(app, out bool prHdr)) ? prHdr : currHdr;

        // Same fallback shape as rate/HDR above: this app's own VRR_MODE override
        // if it has one, otherwise the INI default. Null only when NVAPI isn't
        // available at all this session — hides the row.
        GsyncGlobalMode? gsyncMode = !_gsyncAvailable ? null
            : hasGsyncModeProfile && _profiles.GsyncMode.TryReadProfile(app, out var pgm) ? pgm
            : _gsyncSetting.Default;

        // Reflects whatever enforcement already gave up on before this dialog
        // was even opened — see RefreshOverlayApplyWarning, which keeps this in
        // sync afterward while the overlay stays open.
        var failedSettings = new List<string>();
        if (_displayBackoff.GaveUp) failedSettings.Add("Refresh rate/HDR");
        if (_gsyncAvailable && _gsyncBackoff.GaveUp) failedSettings.Add("G-SYNC");
        string? applyWarning = failedSettings.Count > 0 ? string.Join(", ", failedSettings) : null;

        _overlay = new OverlayWindow(
            activeApp:      app,
            availableRates: DisplayService.GetAvailableRates(),
            preselectRate:  preselectRate,
            hdrSupported:   _hdrSupported,
            hdrEnabled:     preselectHdr,
            hasProfile:     hasProfile,
            windowMode:     windowMode,
            preselectDsxProfile: dsxProfile,
            gsyncMode:      gsyncMode,
            appVrrState:    appVrrState,
            runningApps:    RunningAppsService.GetRunningApps(),
            storedRate:     storedRate,
            storedHdr:      storedHdr,
            applyWarning:   applyWarning,
            frameCapFps:    frameCapFps);

        _overlay.AppSwitchRequested += (_, newApp) => ShowOverlay(forcedApp: newApp);

        _overlay.ProfileDeleteRequested += (_, _) =>
        {
            Logger.Log($"Overlay: profile deleted for app='{app}'.");
            _displayBackoff.Reset();
            _gsyncBackoff.Reset();
            _overlay!.SetApplyWarning(null);
            _profiles.Rate.DeleteProfile(app);
            if (_hdrSupported) _profiles.Hdr.DeleteProfile(app);
            _profiles.Dsx.DeleteProfile(app);
            if (_gsyncAvailable) _profiles.GsyncMode.DeleteProfile(app);
            ApplyDisplayState(_rateSetting.Default, _hdrSetting.Default);
            if (_gsyncAvailable) ApplyGsyncMode(app);
        };

        _overlay.ApplyRequested += (_, _) =>
        {
            // A manual Apply is an explicit fresh attempt — "restart the loop"
            // regardless of whatever backoff/give-up state an unrelated
            // enforcement fight left behind. See EnforcementBackoffTracker.
            _displayBackoff.Reset();
            _gsyncBackoff.Reset();
            _overlay!.SetApplyWarning(null);

            int     selRate     = _overlay!.SelectedRate;
            bool    saveProfile = _overlay.SaveProfile;
            bool    hdrVal      = _overlay.HdrEnabled;
            string? selDsx      = _overlay.SelectedDsxProfile;

            Logger.Log($"Overlay: Apply clicked for app='{app}' rate={selRate} hdr={hdrVal} dsx='{selDsx}' gsync={_overlay.SelectedGsyncMode} frameCap={_overlay.SelectedFrameCapFps} saveProfile={saveProfile}.");

            _rateSetting.SaveOrClear(app, saveProfile, selRate);
            if (_hdrSupported)
                _hdrSetting.SaveOrClear(app, saveProfile, hdrVal);

            // Only touch DSX storage if the dropdown was actually live this session
            // (DSX was reachable) — otherwise it was a grayed-out echo of whatever's
            // saved, "nothing selected" doesn't mean the user chose to clear it, and
            // writing here would wipe the saved profile just because DSX wasn't up yet.
            if (_overlay.DsxProfileEditable)
            {
                // "— Not Managed —" (selDsx == null) always clears rather than saving/
                // defaulting an empty selection — there's nothing meaningful to persist.
                if (selDsx is not null) SaveOrClear(_profiles.Dsx, app, saveProfile, selDsx, _profiles.Dsx.ReadDefault());
                else _profiles.Dsx.DeleteProfile(app);
                _overlay.ReflectDsxApplied(selDsx ?? "");
            }

            ApplyDisplayState(selRate, hdrVal);
            if (selDsx is not null) _ = ApplyDsxProfileToDevicesAsync(selDsx);

            // Every write below is immediately followed by telling the
            // overlay's own dot about it — see OverlayWindow's Apply-
            // reflection remarks (RefreshLiveState/RefreshGsyncLiveState/
            // ReflectAppVrrApplied/ReflectFrameCapApplied) for why this was
            // missing before and what it fixes: without it, a dot the user
            // had just turned red by editing stayed red after a successful
            // Apply, since nothing here ever told it the shown value had
            // become the new truth — only closing and reopening the dialog
            // (which recomputes the baseline from scratch) ever showed green.
            _overlay.RefreshLiveState(selRate, hdrVal);

            // Same default/override split as rate/HDR above, and same "apply now
            // regardless of save scope" — except what gets applied is always a
            // push to NVIDIA's one base-profile VRR_MODE setting, never a per-app
            // NVIDIA write (see NvidiaGsyncService remarks for why).
            if (_overlay.SelectedGsyncMode is { } selGsync)
            {
                _gsyncSetting.SaveOrClear(app, saveProfile, selGsync);
                NvidiaGsyncService.SetGlobalMode(selGsync);
                _overlay.RefreshGsyncLiveState(selGsync);
            }

            // No default/global scope to fall back to (see NvidiaGsyncService.
            // SetAppVrrOverride remarks) — only ever written while the dropdown
            // was actually enabled, i.e. saveProfile ticked. Unlike every other
            // setting here, there's deliberately no "clear on untick" path:
            // unticking Save just stops offering this app's edits, it doesn't
            // erase whatever NVIDIA's own per-app profile already has.
            if (saveProfile && _overlay.SelectedAppVrrState is { } selAppVrr)
            {
                NvidiaGsyncService.SetAppVrrOverride(app, selAppVrr);
                _overlay.ReflectAppVrrApplied(selAppVrr);
            }

            // Same shape as App VRR just above — see NvidiaGsyncService's
            // AppVrr/FrameCap remarks for why there's no clear-on-untick path
            // here either.
            if (saveProfile && _overlay.SelectedFrameCapFps is { } selFrameCap)
            {
                NvidiaGsyncService.SetAppFrameCap(app, selFrameCap);
                _overlay.ReflectFrameCapApplied(selFrameCap);
            }

            _overlay.ReflectProfileState(saveProfile);
        };

        _overlay.Closed += (_, _) =>
        {
            _overlay = null;
            if (!_switchingOverlayApp) RestoreForeground(priorForeground);
        };

        _overlay.Show();
    }

    /// <summary>
    /// Same save-path shape as SyncableSetting&lt;T&gt;.SaveOrClear, for DSX —
    /// the one remaining setting still backed by a raw ProfileSetting&lt;T&gt;
    /// rather than a SyncableSetting, since it has no synchronous live
    /// readback to reconcile against (see OverlayWindow's DSX dot remarks).
    /// Kept as its own static method rather than duplicating the branch inline
    /// at its one call site.
    /// </summary>
    private static T SaveOrClear<T>(ProfileSetting<T> setting, string app, bool saveProfile, T value, T currentDefault)
    {
        if (saveProfile)
        {
            setting.WriteProfile(app, value);
            return currentDefault;
        }

        setting.DeleteProfile(app);
        setting.WriteDefault(value);
        return value;
    }

    /// <summary>Restores whatever window owned foreground before the overlay
    /// opened — un-minimizing it first if needed (exclusive-fullscreen apps
    /// commonly auto-minimize on focus loss).</summary>
    private static void RestoreForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    // -------------------------------------------------------------------------
    // Hotkey settings
    // -------------------------------------------------------------------------
    private void ShowHotkeySettings()
    {
        IntPtr priorForeground = GetForegroundWindow();

        var win = new HotkeySettingsWindow("Overlay Hotkey", _hotkey.Modifiers, _hotkey.Vk)
        {
            SaveRequested = (mods, vk) =>
            {
                bool ok = _hotkey.Rebind(mods, vk);
                if (ok) _profiles.WriteHotkey(mods, vk);
                return ok;
            },
        };
        win.Closed += (_, _) => RestoreForeground(priorForeground);
        win.Show();
    }

    // -------------------------------------------------------------------------
    // Exit
    // -------------------------------------------------------------------------
    private void ExitApp()
    {
        Logger.Log("Exit requested.");
        System.Windows.Application.Current.Shutdown();
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------
    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settleCts?.Cancel();
        _stickyLivenessTimer.Stop();
        _stickyLivenessTimer.Dispose();
        _tracker.Dispose();
        _processStartWatcher.Dispose();
        _msgSink.Dispose();
        _hotkey.Dispose();
        _ini.Dispose();
        _tray.Visible = false;
        _currentIcon?.Dispose();
        _tray.Dispose();
    }
}
