using System;
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

    private int  _defaultRate;
    private bool _defaultHdr;
    private bool _hdrSupported;
    private int  _currentRate;

    // Tray-menu-togglable: whether OnProcessStarted speculatively applies a
    // profile ahead of focus at all. Persisted so the user's choice survives
    // a restart.
    private bool _earlyApplyOnStart;

    // True from the moment ApplyDisplayState issues a change until ReconcileAsync
    // confirms hardware landed on the target (or times out) — see ReconcileAsync
    // and OnDisplayChange remarks. While true, OnDisplayChange treats any
    // WM_DISPLAYCHANGE as expected settling noise from our own apply, not an
    // external change.
    private bool _settling;

    private GsyncGlobalMode  _defaultGsyncMode;
    private bool             _gsyncAvailable;
    private GsyncGlobalMode  _lastPolledGsyncMode;
    private readonly System.Windows.Forms.Timer _gsyncPollTimer;

    private readonly NotifyIcon _tray;
    private Icon? _currentIcon;

    private OverlayWindow? _overlay;

    // Reconciles hardware state against the last intended (rate, HDR) target for a
    // short window after every apply, since SetDisplayConfig's HDR call can return
    // before the driver/TV actually finish renegotiating the HDMI link — a rate
    // change issued right after can get silently clamped by the still-settling
    // link. A fresh ApplyDisplayState call cancels whatever reconciliation was
    // still running for the previous target.
    private CancellationTokenSource? _reconcileCts;

    private const string TaskName        = "RefreshRateOverlay.WPF";
    private static readonly string ExePath = Path.Combine(AppContext.BaseDirectory, "RefreshRateOverlay.WPF.exe");
    private const string TaskDescription = "Launches RefreshRateOverlay (WPF) at logon.";

    public TrayApp()
    {
        string iniPath = Path.Combine(AppContext.BaseDirectory, "RefreshSettings.ini");
        _ini      = new IniStore(iniPath);
        _profiles = new ProfileService(_ini);

        _earlyApplyOnStart = _profiles.ReadEarlyApplyOnStart();

        (_hdrSupported, bool hdrEnabled) = HdrService.GetState();

        _currentRate = DisplayService.GetCurrentRate();
        _defaultRate = _profiles.Rate.ReadDefault();
        _defaultHdr  = _hdrSupported && _profiles.Hdr.ReadDefault();

        if (_defaultRate == 60 && _currentRate > 0)
        {
            _defaultRate = _currentRate;
            _profiles.Rate.WriteDefault(_defaultRate);
        }
        if (_hdrSupported)
        {
            _defaultHdr = hdrEnabled;
            _profiles.Hdr.WriteDefault(_defaultHdr);
        }

        ApplyDisplayState(_defaultRate, _defaultHdr);

        // Same reseed-from-live-state-at-startup treatment as HDR just above:
        // NVIDIA's base profile is the "hardware" here. Confirmed by testing that
        // VRR_MODE only ever behaves as a true global switch — no per-app NVIDIA
        // write exists anywhere in this class, only ApplyGsyncMode below pushing
        // whatever our own INI resolves to for the current app into that one
        // base-profile setting.
        _gsyncAvailable = NvidiaGsyncService.IsAvailable;
        _defaultGsyncMode = _profiles.GsyncMode.ReadDefault();
        if (_gsyncAvailable && NvidiaGsyncService.TryGetGlobalMode(out var liveGsyncMode))
        {
            _defaultGsyncMode = liveGsyncMode;
            _profiles.GsyncMode.WriteDefault(_defaultGsyncMode);
        }
        if (_gsyncAvailable) NvidiaGsyncService.SetGlobalMode(_defaultGsyncMode);
        _lastPolledGsyncMode = _defaultGsyncMode;

        // HardwareChange subscribers — see its own remarks. Each reconciler owns
        // its own setting; wiring a new integration in later means adding its
        // own detector + subscriber pair here, not editing these.
        HardwareChange += ReconcileRateAndHdr;
        HardwareChange += ReconcileGsyncMode;

        // NVIDIA's DRS database has no change notification to hook, unlike
        // WM_DISPLAYCHANGE — 2s balances catching an external change reasonably
        // promptly against the cost of a DRS session open/read every tick.
        _gsyncPollTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _gsyncPollTimer.Tick += PollGsyncMode;
        if (_gsyncAvailable) _gsyncPollTimer.Start();

        _tray = new NotifyIcon
        {
            Text    = "RefreshRateOverlay",
            Visible = true,
        };
        UpdateTrayIcon(_currentRate);
        _tray.ContextMenuStrip = BuildContextMenu();
        _tray.MouseClick += TrayMouseClick;

        _tracker = new ForegroundTracker();
        _tracker.AppChanged += OnAppChanged;
        _tracker.Start();

        // Early-HDR path for games whose render pipeline queries HDR once at
        // startup, well before their window ever gets focus — see
        // OnProcessStarted remarks.
        _processStartWatcher = new ProcessStartWatcher();
        _processStartWatcher.ProcessStarted += OnProcessStarted;
        _processStartWatcher.Start();

        _msgSink = new SystemMessageSink(OnDisplayChange);

        (uint hkMods, uint hkVk) = _profiles.ReadHotkey()
            ?? (HotkeyService.MOD_WIN | HotkeyService.MOD_ALT | HotkeyService.MOD_SHIFT, 0x52 /* R */);
        _hotkey = new HotkeyService(ShowOverlay, hkMods, hkVk, Logger.Log);

        Logger.Log($"TrayApp started. Rate={_currentRate} HDR={_hdrSupported}");
    }

    // -------------------------------------------------------------------------
    // Foreground tracking
    // -------------------------------------------------------------------------
    private void OnAppChanged(object? sender, string app)
    {
        Logger.Log($"ForegroundTracker: app changed -> '{app}'");
        // A real, debounced focus change is always authoritative — supersedes
        // any outstanding guess about who's about to take over (see
        // _anticipatedApp remarks), whether it confirms that guess or not.
        _anticipatedApp = null;
        ApplyAllProfiles(app);
    }

    /// <summary>Set while OnProcessStarted's speculative push for an app is
    /// still outstanding (cleared by ScheduleEarlyApplyRevertCheck's timeout,
    /// or superseded by a real focus change in OnAppChanged, whichever comes
    /// first). ReconcileRateAndHdr/ReconcileGsyncMode prefer this over
    /// ForegroundTracker.LastApp — see their remarks for why: LastApp is
    /// stale during this window (ForegroundTracker needs ~1s of stable focus
    /// before it updates), so a hardware change that's actually the
    /// anticipated app's own doing (its pipeline reasserting HDR at init,
    /// exactly the scenario the early-apply feature front-runs) would
    /// otherwise get attributed to whichever app used to be focused —
    /// falling through to the shared default if that app has no profile of
    /// its own, corrupting it for every app. Confirmed via log: launching
    /// Doom pushed a default-HDR write attributed to 'explorer.exe' (the
    /// stale LastApp) 4 seconds before ForegroundTracker caught up to Doom.</summary>
    private string? _anticipatedApp;

    /// <summary>Applies every per-app setting (rate/HDR, DSX, G-SYNC) for the
    /// given app in one call — the single "make hardware match this app's
    /// profile" entry point shared by focus-change (OnAppChanged), the
    /// speculative early-start push (OnProcessStarted), and that push's own
    /// revert (ScheduleEarlyApplyRevertCheck). One shared definition of "apply
    /// this app" instead of each caller picking its own subset.</summary>
    private void ApplyAllProfiles(string app)
    {
        ApplyProfile(app);
        ApplyDsxProfileForApp(app);
        ApplyGsyncMode(app);
    }

    private void ApplyProfile(string app)
    {
        // Re-sync from actual hardware before deciding whether a rate change is
        // needed: _currentRate is only ever updated by our own calls, so if
        // anything outside this app (the game's own fullscreen setup, a driver
        // renegotiation, etc.) changed the real rate, the cached value would be
        // stale and ApplyRate's no-op guard would wrongly skip re-applying it.
        _currentRate = DisplayService.GetCurrentRate();

        (int targetRate, bool targetHdr) = ResolveTarget(app);

        Logger.Log($"ApplyProfile: app='{app}' target={targetRate} hwRateBefore={_currentRate}");

        ApplyDisplayState(targetRate, targetHdr);
    }

    /// <summary>What rate/HDR *should* be right now for the given app: its own
    /// profile if it has one, otherwise the shared default. Same resolution used
    /// both to apply (ApplyProfile) and to detect drift (OnDisplayChange) —
    /// one shared definition of "correct" instead of two that could disagree.</summary>
    private (int Rate, bool Hdr) ResolveTarget(string app) =>
        (
            ReconcilePlanner.Resolve(_profiles.Rate.TryReadProfile(app, out int profileRate), profileRate, _defaultRate),
            _hdrSupported && ReconcilePlanner.Resolve(_profiles.Hdr.TryReadProfile(app, out bool profileHdr), profileHdr, _defaultHdr)
        );

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
    /// whatever came before.</summary>
    private void OnProcessStarted(object? sender, string app)
    {
        if (!_earlyApplyOnStart || !HasAnyProfile(app)) return;

        Logger.Log($"ProcessStartWatcher: '{app}' started, early-applying its profile ahead of focus.");
        _anticipatedApp = app;
        ApplyAllProfiles(app);

        ScheduleEarlyApplyRevertCheck(app);
    }

    private bool HasAnyProfile(string app) =>
        _profiles.Rate.HasProfile(app) ||
        _profiles.Hdr.HasProfile(app) ||
        _profiles.Dsx.HasProfile(app) ||
        _profiles.GsyncMode.HasProfile(app);

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

            // Window's over either way — stop preferring this guess over
            // LastApp, guarded so a newer anticipation (a second process
            // started before this one's window closed) isn't clobbered.
            if (_anticipatedApp == anticipatedApp) _anticipatedApp = null;

            string actualApp = _tracker.LastApp;
            if (actualApp == anticipatedApp) return;

            Logger.Log($"ProcessStartWatcher: '{anticipatedApp}' never took focus (actual='{actualApp}') — reverting early apply.");
            ApplyAllProfiles(actualApp);
        };
        timer.Start();
    }

    // -------------------------------------------------------------------------
    // DSX controller-profile application
    // -------------------------------------------------------------------------

    /// <summary>Falls back to the default controller profile the same way rate/HDR
    /// fall back to _defaultRate/_defaultHdr. An empty default means "leave the
    /// controller alone" (never configured).</summary>
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
    // -------------------------------------------------------------------------
    private void ApplyGsyncMode(string app)
    {
        if (!_gsyncAvailable) return;
        NvidiaGsyncService.SetGlobalMode(ResolveGsyncTarget(app));
    }

    // -------------------------------------------------------------------------
    // HardwareChange — a generic "something external changed, go check for
    // drift" signal, deliberately decoupled from what detected it. Today it has
    // one raiser (OnDisplayChange, below) and one subscriber
    // (ReconcileRateAndHdr) for the display, plus a second raiser/subscriber
    // pair (PollGsyncMode/ReconcileGsyncMode) proving a second, unrelated
    // integration can plug into the same signal without touching this one.
    // Adding another integration later (DSX, say) means adding its own
    // detector that raises this event and its own subscriber — never editing
    // an existing reconciler to know about a new source.
    // -------------------------------------------------------------------------
    private event Action? HardwareChange;

    // -------------------------------------------------------------------------
    // Display change detection — while _settling is true, this is our own apply
    // still riding out the driver/TV's negotiation delay (see ApplyDisplayState/
    // ReconcileAsync, which own that flag), not an external change, so it's
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
        Logger.Log("OnDisplayChange: not settling, treating as external — invoking HardwareChange.");
        HardwareChange?.Invoke();
    }

    /// <summary>
    /// Subscribed to HardwareChange. Any external display change means
    /// something outside the overlay changed it — the user via Windows
    /// Settings, or the app itself re-asserting its own preference. Either way
    /// this doesn't fight it: it records the observed value as the new truth,
    /// into whichever slot is currently active for the foreground app (its own
    /// profile if it has one, otherwise the shared default) — same rule
    /// regardless of which one applies, since both cases are "something changed
    /// outside the overlay" and get written back the same way.
    /// </summary>
    private void ReconcileRateAndHdr()
    {
        _currentRate = DisplayService.GetCurrentRate();
        UpdateTrayIcon(_currentRate);

        // Prefers the anticipated app (if any) over LastApp — see
        // _anticipatedApp remarks: LastApp can be stale for a few seconds
        // after a process-start early-apply, and attributing drift to the
        // wrong app during that window is how a game's own HDR pipeline
        // init once got written into the shared default instead of nothing.
        string app = _anticipatedApp ?? _tracker.LastApp;
        (int targetRate, bool targetHdr) = ResolveTarget(app);

        var ratePlan = ReconcilePlanner.Plan(_currentRate, targetRate, _profiles.Rate.HasProfile(app));
        if (ratePlan != ReconcileAction.None)
        {
            Logger.Log($"ReconcileRateAndHdr: rate drifted to {_currentRate} (target was {targetRate}) for app='{app}' -> {ratePlan}.");
            NotifyReconciled("Refresh rate", $"{_currentRate} Hz", app, ratePlan);
        }
        switch (ratePlan)
        {
            case ReconcileAction.WriteProfile:
                _profiles.Rate.WriteProfile(app, _currentRate);
                break;
            case ReconcileAction.WriteDefault:
                _defaultRate = _currentRate;
                _profiles.Rate.WriteDefault(_defaultRate);
                break;
        }

        bool currHdr = _hdrSupported && HdrService.GetState().Enabled;
        if (_hdrSupported)
        {
            var hdrPlan = ReconcilePlanner.Plan(currHdr, targetHdr, _profiles.Hdr.HasProfile(app));
            if (hdrPlan != ReconcileAction.None)
            {
                Logger.Log($"ReconcileRateAndHdr: HDR drifted to {currHdr} (target was {targetHdr}) for app='{app}' -> {hdrPlan}.");
                NotifyReconciled("HDR", currHdr ? "On" : "Off", app, hdrPlan);
            }
            switch (hdrPlan)
            {
                case ReconcileAction.WriteProfile:
                    _profiles.Hdr.WriteProfile(app, currHdr);
                    break;
                case ReconcileAction.WriteDefault:
                    _defaultHdr = currHdr;
                    _profiles.Hdr.WriteDefault(_defaultHdr);
                    break;
            }
        }

        // If the overlay is open, push it its OWN app's resolved target — not
        // gated on overlay.ActiveApp == app (the app that was foreground when
        // this reconcile ran). Reconciling can easily happen while a different
        // app is focused than the overlay's — e.g. the write here just landed
        // on the shared default, changed from within the NVIDIA app itself,
        // which is a real foreground switch away from whatever the overlay is
        // showing. A default-scope change is still relevant to the overlay's
        // app whenever that app has no profile of its own; re-resolving here
        // (rather than reusing app/_currentRate/currHdr from above) is what
        // makes that fall out correctly instead of only handling the
        // same-app-focused case.
        if (_overlay is { } overlay)
        {
            (int overlayRate, bool overlayHdr) = ResolveTarget(overlay.ActiveApp);
            overlay.RefreshLiveState(overlayRate, overlayHdr);
        }
    }

    // -------------------------------------------------------------------------
    // G-SYNC change detection — NVIDIA's DRS database has no equivalent of
    // WM_DISPLAYCHANGE to hook, so this polls the base profile instead. Only
    // raises HardwareChange when the polled value actually differs from the
    // last poll, same "detect, then let the shared reconciler re-derive truth
    // independently" split OnDisplayChange/ReconcileRateAndHdr use — this
    // method never writes anything itself.
    // -------------------------------------------------------------------------
    private void PollGsyncMode(object? sender, EventArgs e)
    {
        if (!_gsyncAvailable) return;
        if (!NvidiaGsyncService.TryGetGlobalMode(out var liveMode)) return;
        if (liveMode == _lastPolledGsyncMode) return;

        _lastPolledGsyncMode = liveMode;
        HardwareChange?.Invoke();
    }

    /// <summary>What VRR_MODE *should* be right now for the given app — same
    /// resolution shape as ResolveTarget, kept separate since it's a different
    /// setting with a different storage type (GsyncMode isn't Rate/Hdr).</summary>
    private GsyncGlobalMode ResolveGsyncTarget(string app) =>
        ReconcilePlanner.Resolve(_profiles.GsyncMode.TryReadProfile(app, out var profileMode), profileMode, _defaultGsyncMode);

    /// <summary>Subscribed to HardwareChange, same shape and reasoning as
    /// ReconcileRateAndHdr: an external base-profile change (the user via NVCP,
    /// or the NVIDIA app) gets recorded as the new truth into whichever slot is
    /// active for the foreground app, rather than fought.</summary>
    private void ReconcileGsyncMode()
    {
        if (!_gsyncAvailable) return;
        if (!NvidiaGsyncService.TryGetGlobalMode(out var liveMode)) return;

        // See ReconcileRateAndHdr's remarks on _anticipatedApp — same reasoning.
        string app = _anticipatedApp ?? _tracker.LastApp;
        GsyncGlobalMode target = ResolveGsyncTarget(app);

        var plan = ReconcilePlanner.Plan(liveMode, target, _profiles.GsyncMode.HasProfile(app));
        if (plan != ReconcileAction.None)
        {
            Logger.Log($"ReconcileGsyncMode: VRR_MODE drifted to {liveMode} (target was {target}) for app='{app}' -> {plan}.");
            NotifyReconciled("G-SYNC", liveMode.ToString(), app, plan);
        }
        switch (plan)
        {
            case ReconcileAction.WriteProfile:
                _profiles.GsyncMode.WriteProfile(app, liveMode);
                break;
            case ReconcileAction.WriteDefault:
                _defaultGsyncMode = liveMode;
                _profiles.GsyncMode.WriteDefault(_defaultGsyncMode);
                break;
        }

        // Same "push the overlay's own resolved target, not gated on app
        // identity" reasoning as ReconcileRateAndHdr — see its remarks. Changing
        // G-SYNC through the NVIDIA app is the clearest case this matters for:
        // that's a real foreground switch away from whatever the overlay is
        // showing, so app == "NVIDIA app.exe" here almost always, never the
        // overlay's own app.
        if (_overlay is { } overlay)
            overlay.RefreshGsyncLiveState(ResolveGsyncTarget(overlay.ActiveApp));
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
    /// if it drifts (see ReconcileAsync) — the initial apply can still lose a
    /// race against the driver/TV settling the HDMI link asynchronously.
    /// </summary>
    private void ApplyDisplayState(int rate, bool hdrEnabled)
    {
        _reconcileCts?.Cancel();
        var cts = new CancellationTokenSource();
        _reconcileCts = cts;
        _settling = true;

        if (_hdrSupported) HdrService.SetState(hdrEnabled);
        ApplyRate(rate);

        _ = ReconcileAsync(rate, hdrEnabled, cts.Token);
    }

    /// <summary>
    /// Polls actual hardware state for up to ReconcileWindowMs after an apply and
    /// re-applies (HDR then rate) if it ever finds a mismatch against the target —
    /// absorbs the driver/TV's asynchronous HDMI settling delay instead of hoping
    /// the first attempt landed. Stops as soon as hardware matches the target, or
    /// if superseded by a newer ApplyDisplayState call (via the passed token) —
    /// clears _settling on the way out (guarded by the token so a superseded pass
    /// can't clear it out from under the newer one that replaced it), which is
    /// what lets OnDisplayChange start treating further changes as external.
    /// </summary>
    private async Task ReconcileAsync(int targetRate, bool targetHdr, CancellationToken token)
    {
        const int windowMs = 5000;
        const int pollMs = 400;
        int elapsed = 0;

        while (elapsed < windowMs)
        {
            try { await Task.Delay(pollMs, token); }
            catch (OperationCanceledException) { return; }
            if (token.IsCancellationRequested) return;
            elapsed += pollMs;

            int  hwRate = DisplayService.GetCurrentRate();
            bool hwHdr  = !_hdrSupported || HdrService.GetState().Enabled == targetHdr;
            bool rateOk = hwRate == targetRate;

            if (rateOk && hwHdr)
            {
                if (!token.IsCancellationRequested) _settling = false;
                return; // converged
            }

            Logger.Log($"Reconcile: mismatch (hwRate={hwRate} target={targetRate}, hdrOk={hwHdr}) — re-applying.");
            if (_hdrSupported) HdrService.SetState(targetHdr);
            _currentRate = hwRate; // resync so ApplyRate's no-op guard doesn't skip the needed reapply
            ApplyRate(targetRate);
        }

        if (!token.IsCancellationRequested) _settling = false;
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
    private void ShowOverlay()
    {
        if (_overlay is not null)
        {
            _overlay.Close();
            return;
        }

        string app = _tracker.LastApp;
        if (string.IsNullOrEmpty(app)) app = "Desktop";

        // Remember whatever currently owns foreground/focus (typically the game)
        // so we can hand it back explicitly once the overlay closes. A real click
        // into the overlay's controls will activate it at some point no matter
        // what — that's unavoidable for an interactive dialog — and a fullscreen
        // app losing activation can drop behind other windows (or get minimized,
        // for apps using an exclusive-fullscreen swapchain) as a result. Windows
        // won't restore it on its own, so we do it ourselves.
        IntPtr priorForeground = GetForegroundWindow();

        // Must be detected now, before the overlay ever shows: SHQueryUserNotificationState
        // (inside WindowModeService) reports on whatever is currently the actual foreground
        // window system-wide, so it has to run while that's still the game, not our overlay.
        WindowMode windowMode = WindowModeService.Detect(priorForeground);

        // Sync reality before opening
        _currentRate = DisplayService.GetCurrentRate();
        UpdateTrayIcon(_currentRate);

        (_, bool currHdr) = _hdrSupported ? HdrService.GetState() : (false, _defaultHdr);

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
            : _defaultGsyncMode;

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
            storedRate:     storedRate,
            storedHdr:      storedHdr);

        _overlay.ProfileDeleteRequested += (_, _) =>
        {
            Logger.Log($"Overlay: profile deleted for app='{app}'.");
            _profiles.Rate.DeleteProfile(app);
            if (_hdrSupported) _profiles.Hdr.DeleteProfile(app);
            _profiles.Dsx.DeleteProfile(app);
            if (_gsyncAvailable) _profiles.GsyncMode.DeleteProfile(app);
            ApplyDisplayState(_defaultRate, _defaultHdr);
            if (_gsyncAvailable) ApplyGsyncMode(app);
        };

        _overlay.ApplyRequested += (_, _) =>
        {
            int     selRate     = _overlay!.SelectedRate;
            bool    saveProfile = _overlay.SaveProfile;
            bool    hdrVal      = _overlay.HdrEnabled;
            string? selDsx      = _overlay.SelectedDsxProfile;

            Logger.Log($"Overlay: Apply clicked for app='{app}' rate={selRate} hdr={hdrVal} dsx='{selDsx}' gsync={_overlay.SelectedGsyncMode} saveProfile={saveProfile}.");

            _defaultRate = SaveOrClear(_profiles.Rate, app, saveProfile, selRate, _defaultRate);
            if (_hdrSupported)
                _defaultHdr = SaveOrClear(_profiles.Hdr, app, saveProfile, hdrVal, _defaultHdr);

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
            }

            ApplyDisplayState(selRate, hdrVal);
            if (selDsx is not null) _ = ApplyDsxProfileToDevicesAsync(selDsx);

            // Same default/override split as rate/HDR above, and same "apply now
            // regardless of save scope" — except what gets applied is always a
            // push to NVIDIA's one base-profile VRR_MODE setting, never a per-app
            // NVIDIA write (see NvidiaGsyncService remarks for why).
            if (_overlay.SelectedGsyncMode is { } selGsync)
            {
                _defaultGsyncMode = SaveOrClear(_profiles.GsyncMode, app, saveProfile, selGsync, _defaultGsyncMode);
                NvidiaGsyncService.SetGlobalMode(selGsync);
            }

            // No default/global scope to fall back to (see NvidiaGsyncService.
            // SetAppVrrOverride remarks) — only ever written while the dropdown
            // was actually enabled, i.e. saveProfile ticked. Unlike every other
            // setting here, there's deliberately no "clear on untick" path:
            // unticking Save just stops offering this app's edits, it doesn't
            // erase whatever NVIDIA's own per-app profile already has.
            if (saveProfile && _overlay.SelectedAppVrrState is { } selAppVrr)
                NvidiaGsyncService.SetAppVrrOverride(app, selAppVrr);

            _overlay.ReflectProfileState(saveProfile);
        };

        _overlay.Closed += (_, _) =>
        {
            _overlay = null;
            RestoreForeground(priorForeground);
        };

        _overlay.Show();
    }

    /// <summary>
    /// Shared save path for every setting type (rate, HDR, DSX profile): saved
    /// per-app when saveProfile is set, otherwise persisted as the app-agnostic
    /// default. Routing all settings through this one branch — instead of each
    /// getting its own hand-written if/else — is what keeps "save as general"
    /// from silently having no effect for a setting type that never wired up
    /// its default half.
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
        _reconcileCts?.Cancel();
        _gsyncPollTimer.Stop();
        _gsyncPollTimer.Dispose();
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
