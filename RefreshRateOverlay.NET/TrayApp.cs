using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using RefreshRateOverlay.Forms;
using RefreshRateOverlay.Rendering;
using RefreshRateOverlay.Services;
using Shared;

namespace RefreshRateOverlay;

/// <summary>
/// Application context. Owns all services and the NotifyIcon.
/// Mirrors the AHK index.ahk main block + TrayIconRenderer + TrackForegroundApp.
/// </summary>
internal sealed class TrayApp : ApplicationContext, IDisposable
{
    // -------------------------------------------------------------------------
    // Services
    // -------------------------------------------------------------------------
    private readonly IniStore          _ini;
    private readonly ProfileService    _profiles;
    private readonly ForegroundTracker _tracker;
    private readonly SystemMessageSink _msgSink;

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------
    private int  _defaultRate;
    private bool _defaultHdr;
    private bool _hdrSupported;
    private int  _currentRate;
    private int  _ignoreDisplayChangeUntil; // Environment.TickCount64 threshold

    // -------------------------------------------------------------------------
    // Tray
    // -------------------------------------------------------------------------
    private readonly NotifyIcon _tray;
    private Icon? _currentIcon;

    // -------------------------------------------------------------------------
    // Overlay
    // -------------------------------------------------------------------------
    private OverlayForm? _overlay;

    // -------------------------------------------------------------------------
    // Startup
    // -------------------------------------------------------------------------
    private const string TaskName        = "RefreshRateOverlay";
    private static readonly string ExePath = Path.Combine(AppContext.BaseDirectory, "RefreshRateOverlay.exe");
    private const string TaskDescription = "Launches RefreshRateOverlay at logon.";

    // -------------------------------------------------------------------------
    // Ctor
    // -------------------------------------------------------------------------
    public TrayApp()
    {
        string iniPath = Path.Combine(AppContext.BaseDirectory, "RefreshSettings.ini");
        _ini      = new IniStore(iniPath);
        _profiles = new ProfileService(_ini);

        // Read HDR support once at startup
        (_hdrSupported, bool hdrEnabled) = HdrService.GetState();

        // Read defaults from INI, falling back to current system state
        _currentRate = DisplayService.GetCurrentRate();
        _defaultRate = _profiles.ReadDefaultRate();
        _defaultHdr  = _hdrSupported ? _profiles.ReadDefaultHdr() : false;

        // If INI had no value, bootstrap from current system state
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

        // Apply defaults on startup (mirrors AHK SetMonitorRefreshRate on startup)
        ApplyRate(_defaultRate);
        if (_hdrSupported) HdrService.SetState(_defaultHdr);

        // Tray icon
        _tray = new NotifyIcon
        {
            Text    = "RefreshRateOverlay",
            Visible = true,
        };
        UpdateTrayIcon(_currentRate);
        _tray.ContextMenuStrip = BuildContextMenu();
        _tray.MouseClick += TrayMouseClick;

        // Foreground tracker (mirrors SetTimer(TrackForegroundApp, 500))
        _tracker = new ForegroundTracker();
        _tracker.AppChanged += OnAppChanged;
        _tracker.Start();

        // Display change listener
        _msgSink = new SystemMessageSink(OnDisplayChange);

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
    // Display change sync (mirrors SyncSettingsWithSystem)
    // -------------------------------------------------------------------------
    private void OnDisplayChange()
    {
        if (Environment.TickCount64 < _ignoreDisplayChangeUntil)
            return;

        _currentRate = DisplayService.GetCurrentRate();
        UpdateTrayIcon(_currentRate);

        string lastApp = _tracker.LastApp;

        // Only update defaults when not inside an app profile
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
                ? StartupTaskService.Install(TaskName, ExePath, TaskDescription, Logger.Log)
                : StartupTaskService.Uninstall(TaskName, Logger.Log);
            if (!ok)
            {
                reverting = true;
                item.Checked = !item.Checked;
                reverting = false;
                MessageBox.Show(
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
    // Tray click → overlay
    // -------------------------------------------------------------------------
    private void TrayMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            ShowOverlay();
    }

    // -------------------------------------------------------------------------
    // Overlay (mirrors ToggleOverlay)
    // -------------------------------------------------------------------------
    private void ShowOverlay()
    {
        if (_overlay is not null && !_overlay.IsDisposed)
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

        _overlay = new OverlayForm(
            activeApp:      app,
            availableRates: DisplayService.GetAvailableRates(),
            currentRate:    _currentRate,
            hdrSupported:   _hdrSupported,
            hdrEnabled:     currHdr,
            hasProfile:     hasProfile);

        _tracker.OverlayHandle = _overlay.Handle;

        _overlay.FormClosed += (_, _) =>
        {
            _tracker.OverlayHandle = IntPtr.Zero;

            if (!_overlay.Applied) return;

            int selRate = _overlay.SelectedRate;
            bool saveProfile = _overlay.SaveProfile;
            bool hdrVal = _overlay.HdrEnabled;

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
        };

        _overlay.Show();
    }

    // -------------------------------------------------------------------------
    // Exit
    // -------------------------------------------------------------------------
    private void ExitApp()
    {
        Logger.Log("Exit requested.");
        Application.Exit();
    }

    // -------------------------------------------------------------------------
    // Icon loading (fallback for initial state before renderer fires)
    // -------------------------------------------------------------------------
    private static Icon LoadTrayIcon()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("RefreshRateOverlay.Resources.tray.ico");
            if (stream is not null) return new Icon(stream);
        }
        catch { }
        return SystemIcons.Application;
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------
    private bool _disposed;
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            _tracker.Dispose();
            _msgSink.Dispose();
            _ini.Dispose();
            _tray.Visible = false;
            _currentIcon?.Dispose();
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
