using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Windows.Interop;
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

        ApplyRate(_defaultRate);
        if (_hdrSupported) HdrService.SetState(_defaultHdr);

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
        _hotkey  = new HotkeyService(ShowOverlay);

        Logger.Log($"TrayApp started. Rate={_currentRate} HDR={_hdrSupported}");
    }

    // -------------------------------------------------------------------------
    // Foreground tracking
    // -------------------------------------------------------------------------
    private void OnAppChanged(object? sender, string app) =>
        ApplyProfile(app);

    private void ApplyProfile(string app)
    {
        int? profileRate = _profiles.ReadRateProfile(app);
        ApplyRate(profileRate ?? _defaultRate);

        if (_hdrSupported)
        {
            bool? profileHdr = _profiles.ReadHdrProfile(app);
            HdrService.SetState(profileHdr ?? _defaultHdr);
        }
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
                // This app runs asInvoker (no admin needed), so the startup task must not
                // request RunLevel=HighestAvailable — registering that requires the calling
                // process to already be elevated, which this one deliberately isn't.
                ? StartupTaskService.Install(TaskName, ExePath, TaskDescription, Logger.Log, requireElevation: false)
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

        _overlay = new OverlayWindow(
            activeApp:      app,
            availableRates: DisplayService.GetAvailableRates(),
            currentRate:    _currentRate,
            hdrSupported:   _hdrSupported,
            hdrEnabled:     currHdr,
            hasProfile:     hasProfile);

        var helper = new WindowInteropHelper(_overlay);
        helper.EnsureHandle();
        _tracker.OverlayHandle = helper.Handle;

        _overlay.ProfileDeleteRequested += (_, _) =>
        {
            _profiles.DeleteRateProfile(app);
            if (_hdrSupported) _profiles.DeleteHdrProfile(app);
            ApplyRate(_defaultRate);
            if (_hdrSupported) HdrService.SetState(_defaultHdr);
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

            ApplyRate(selRate);
            if (_hdrSupported) HdrService.SetState(hdrVal);
            _overlay.ReflectProfileState(saveProfile);
        };

        _overlay.Closed += (_, _) =>
        {
            _tracker.OverlayHandle = IntPtr.Zero;
            _overlay = null;
        };

        _overlay.Show();
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
        _tracker.Dispose();
        _msgSink.Dispose();
        _hotkey.Dispose();
        _ini.Dispose();
        _tray.Visible = false;
        _currentIcon?.Dispose();
        _tray.Dispose();
    }
}
