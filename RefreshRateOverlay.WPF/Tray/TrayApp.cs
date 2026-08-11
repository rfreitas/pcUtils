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
    private int  _ignoreDisplayChangeUntil; // Environment.TickCount64 threshold

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
        _defaultRate = _profiles.ReadDefaultRate();
        _defaultHdr  = _hdrSupported && _profiles.ReadDefaultHdr();

        if (_defaultRate == 60 && _currentRate > 0)
        {
            _defaultRate = _currentRate;
            _profiles.WriteDefaultRate(_defaultRate);
        }
        if (_hdrSupported)
        {
            _defaultHdr = hdrEnabled;
            _profiles.WriteDefaultHdr(_defaultHdr);
        }

        ApplyDisplayState(_defaultRate, _defaultHdr);

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
    }

    private void ApplyProfile(string app)
    {
        // Re-sync from actual hardware before deciding whether a rate change is
        // needed: _currentRate is only ever updated by our own calls, so if
        // anything outside this app (the game's own fullscreen setup, a driver
        // renegotiation, etc.) changed the real rate, the cached value would be
        // stale and ApplyRate's no-op guard would wrongly skip re-applying it.
        _currentRate = DisplayService.GetCurrentRate();

        int? profileRate = _profiles.ReadRateProfile(app);
        int  targetRate  = profileRate ?? _defaultRate;
        bool targetHdr   = _hdrSupported ? (_profiles.ReadHdrProfile(app) ?? _defaultHdr) : _defaultHdr;

        Logger.Log($"ApplyProfile: app='{app}' savedProfileRate={(profileRate?.ToString() ?? "none")} defaultRate={_defaultRate} target={targetRate} hwRateBefore={_currentRate}");

        ApplyDisplayState(targetRate, targetHdr);
    }

    // -------------------------------------------------------------------------
    // Display change sync
    // -------------------------------------------------------------------------
    private void OnDisplayChange()
    {
        if (Environment.TickCount64 < _ignoreDisplayChangeUntil)
            return;

        _currentRate = DisplayService.GetCurrentRate();
        UpdateTrayIcon(_currentRate);

        string lastApp = _tracker.LastApp;

        if (_profiles.ReadRateProfile(lastApp) is null)
        {
            if (_defaultRate != _currentRate)
            {
                _defaultRate = _currentRate;
                _profiles.WriteDefaultRate(_defaultRate);
            }
        }

        if (_hdrSupported && _profiles.ReadHdrProfile(lastApp) is null)
        {
            (_, bool currHdr) = HdrService.GetState();
            if (_defaultHdr != currHdr)
            {
                _defaultHdr = currHdr;
                _profiles.WriteDefaultHdr(_defaultHdr);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Rate application
    // -------------------------------------------------------------------------
    private void ApplyRate(int rate)
    {
        if (_currentRate == rate) return;

        _ignoreDisplayChangeUntil = (int)(Environment.TickCount64 + 2000);
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

        if (_hdrSupported) HdrService.SetState(hdrEnabled);
        ApplyRate(rate);

        _ = ReconcileAsync(rate, hdrEnabled, cts.Token);
    }

    /// <summary>
    /// Polls actual hardware state for up to ReconcileWindowMs after an apply and
    /// re-applies (HDR then rate) if it ever finds a mismatch against the target —
    /// absorbs the driver/TV's asynchronous HDMI settling delay instead of hoping
    /// the first attempt landed. Stops as soon as hardware matches the target, or
    /// if superseded by a newer ApplyDisplayState call (via the passed token).
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
                return; // converged

            Logger.Log($"Reconcile: mismatch (hwRate={hwRate} target={targetRate}, hdrOk={hwHdr}) — re-applying.");
            if (_hdrSupported) HdrService.SetState(targetHdr);
            _currentRate = hwRate; // resync so ApplyRate's no-op guard doesn't skip the needed reapply
            ApplyRate(targetRate);
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

        bool hasProfile = _profiles.HasRateProfile(app);

        // If not in a profile, ensure defaults match reality
        if (!hasProfile)
        {
            if (_defaultRate != _currentRate)
            {
                _defaultRate = _currentRate;
                _profiles.WriteDefaultRate(_defaultRate);
            }
            if (_hdrSupported && _defaultHdr != currHdr)
            {
                _defaultHdr = currHdr;
                _profiles.WriteDefaultHdr(_defaultHdr);
            }
        }

        // Preselect from the saved profile when one exists, not live hardware state —
        // otherwise if the actual display has drifted from what the profile says
        // (e.g. mid-thrash, or anything external changed it), the overlay shows the
        // wrong thing and looks like the saved profile itself is wrong.
        int  preselectRate = (hasProfile ? _profiles.ReadRateProfile(app) : null) ?? _currentRate;
        bool preselectHdr  = (hasProfile && _hdrSupported ? _profiles.ReadHdrProfile(app) : null) ?? currHdr;

        _overlay = new OverlayWindow(
            activeApp:      app,
            availableRates: DisplayService.GetAvailableRates(),
            preselectRate:  preselectRate,
            hdrSupported:   _hdrSupported,
            hdrEnabled:     preselectHdr,
            hasProfile:     hasProfile,
            windowMode:     windowMode);

        _overlay.ProfileDeleteRequested += (_, _) =>
        {
            _profiles.DeleteRateProfile(app);
            if (_hdrSupported) _profiles.DeleteHdrProfile(app);
            ApplyDisplayState(_defaultRate, _defaultHdr);
        };

        _overlay.ApplyRequested += (_, _) =>
        {
            int  selRate     = _overlay!.SelectedRate;
            bool saveProfile = _overlay.SaveProfile;
            bool hdrVal      = _overlay.HdrEnabled;

            if (saveProfile)
            {
                _profiles.WriteRateProfile(app, selRate);
                if (_hdrSupported) _profiles.WriteHdrProfile(app, hdrVal);
            }
            else
            {
                _profiles.DeleteRateProfile(app);
                if (_hdrSupported) _profiles.DeleteHdrProfile(app);
                _defaultRate = selRate;
                _profiles.WriteDefaultRate(selRate);
                if (_hdrSupported)
                {
                    _defaultHdr = hdrVal;
                    _profiles.WriteDefaultHdr(hdrVal);
                }
            }

            ApplyDisplayState(selRate, hdrVal);
            _overlay.ReflectProfileState(saveProfile);
        };

        _overlay.Closed += (_, _) =>
        {
            _overlay = null;
            RestoreForeground(priorForeground);
        };

        _overlay.Show();
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
