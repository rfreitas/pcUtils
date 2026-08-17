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

    private readonly IniStore          _ini;
    private readonly ProfileService    _profiles;
    private readonly ForegroundTracker _tracker;
    private readonly SystemMessageSink _msgSink;
    private readonly HotkeyService     _hotkey;

    private int  _defaultRate;
    private bool _defaultHdr;
    private bool _hdrSupported;
    private int  _currentRate;

    // True from the moment ApplyDisplayState issues a change until ReconcileAsync
    // confirms hardware landed on the target (or times out) — see ReconcileAsync
    // and OnDisplayChange remarks. While true, OnDisplayChange treats any
    // WM_DISPLAYCHANGE as expected settling noise from our own apply, not an
    // external change.
    private bool _settling;

    private GsyncGlobalMode _defaultGsyncMode;
    private bool            _gsyncAvailable;

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
            _profiles.Rate.TryReadProfile(app, out int profileRate) ? profileRate : _defaultRate,
            _hdrSupported && _profiles.Hdr.TryReadProfile(app, out bool profileHdr) ? profileHdr : _defaultHdr
        );

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

        GsyncGlobalMode target = _profiles.GsyncMode.TryReadProfile(app, out var profileMode)
            ? profileMode
            : _defaultGsyncMode;

        NvidiaGsyncService.SetGlobalMode(target);
    }

    // -------------------------------------------------------------------------
    // Display change sync — while _settling is true, this is our own apply
    // still riding out the driver/TV's negotiation delay (see ApplyDisplayState/
    // ReconcileAsync, which own that flag), not an external change, so it's
    // ignored entirely. Once settled, any further display change means
    // something outside the overlay changed it — the user via Windows Settings,
    // or the app itself re-asserting its own preference. Either way this
    // doesn't fight it: it records the observed value as the new truth, into
    // whichever slot is currently active for the foreground app (its own
    // profile if it has one, otherwise the shared default) — same rule
    // regardless of which one applies, since both cases are "something changed
    // outside the overlay" and get written back the same way.
    // -------------------------------------------------------------------------
    private void OnDisplayChange()
    {
        if (_settling) return;

        _currentRate = DisplayService.GetCurrentRate();
        UpdateTrayIcon(_currentRate);

        string app = _tracker.LastApp;
        (int targetRate, bool targetHdr) = ResolveTarget(app);

        if (_currentRate != targetRate)
        {
            if (_profiles.Rate.HasProfile(app))
            {
                _profiles.Rate.WriteProfile(app, _currentRate);
            }
            else
            {
                _defaultRate = _currentRate;
                _profiles.Rate.WriteDefault(_defaultRate);
            }
        }

        bool currHdr = _hdrSupported && HdrService.GetState().Enabled;
        if (_hdrSupported && currHdr != targetHdr)
        {
            if (_profiles.Hdr.HasProfile(app))
            {
                _profiles.Hdr.WriteProfile(app, currHdr);
            }
            else
            {
                _defaultHdr = currHdr;
                _profiles.Hdr.WriteDefault(_defaultHdr);
            }
        }

        // Whatever branch ran above, the app's rate/HDR now match reality by
        // construction (either they already did, or we just made them) — so if
        // the overlay is open and showing this exact app, push the refreshed
        // state in rather than leaving it silently stale for as long as the
        // window stays open.
        if (_overlay is { } overlay && overlay.ActiveApp == app)
            overlay.RefreshLiveState(_currentRate, rateSynced: true, currHdr, hdrSynced: true);
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

        bool hasProfile = _profiles.Rate.HasProfile(app) || hasDsxProfile || hasGsyncModeProfile;

        // Opening the overlay never writes the INI — that's OnDisplayChange's job
        // now (see its remarks). But the preselect below can still legitimately
        // disagree with what's actually stored (hardware drifted and no display
        // event has reconciled it yet — most likely still mid-settling), so flag
        // that rather than silently showing a value Apply would treat as already
        // saved. Checked per-setting (not the bundled hasProfile above, which
        // also covers Dsx/GsyncMode) since preselectRate only falls back to live
        // hardware when Rate specifically has no profile — an app with only a
        // Dsx profile would otherwise be wrongly marked synced.
        bool rateSynced = _profiles.Rate.HasProfile(app) || _defaultRate == _currentRate;
        bool hdrSynced  = _profiles.Hdr.HasProfile(app) || !_hdrSupported || _defaultHdr == currHdr;

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
            rateSynced:     rateSynced,
            hdrSynced:      hdrSynced);

        _overlay.ProfileDeleteRequested += (_, _) =>
        {
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
        _tracker.Dispose();
        _msgSink.Dispose();
        _hotkey.Dispose();
        _ini.Dispose();
        _tray.Visible = false;
        _currentIcon?.Dispose();
        _tray.Dispose();
    }
}
