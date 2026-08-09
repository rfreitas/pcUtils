using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AggressiveScreensaver.Forms;
using AggressiveScreensaver.Input;
using AggressiveScreensaver.Services;
using Shared;

namespace AggressiveScreensaver;

/// <summary>
/// Main application context. Owns all services and the NotifyIcon.
/// Wires everything together and runs the WinForms message loop.
/// </summary>
internal sealed class TrayApp : ApplicationContext, IDisposable
{
    // -------------------------------------------------------------------------
    // Services
    // -------------------------------------------------------------------------
    private readonly IniStore            _ini;
    private readonly PowercfgService     _powercfg;
    private readonly AgentIdleService    _agentIdle;
    private readonly BlackOverlayManager _overlay;
    private readonly SystemMessageSink   _msgSink;

    // -------------------------------------------------------------------------
    // Tray
    // -------------------------------------------------------------------------
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _tooltipTimer;

    // -------------------------------------------------------------------------
    // Settings
    // -------------------------------------------------------------------------
    private int  _blankThresholdSec;
    private bool _suppressFullscreen;
    private static readonly int[] TimeoutSteps = [15, 30, 60, 120, 180, 300, 600, 900, 1200, 1800];

    // -------------------------------------------------------------------------
    // Forms (lazy)
    // -------------------------------------------------------------------------
    private TimeoutSliderWindow? _sliderWindow;

    // -------------------------------------------------------------------------
    // Ctor
    // -------------------------------------------------------------------------
    public TrayApp()
    {
        string iniPath = System.IO.Path.Combine(AppContext.BaseDirectory, "AggressiveScreensaver.ini");
        _ini = new IniStore(iniPath);

        _blankThresholdSec  = _ini.ReadInt("Settings", "BlankThreshold",    30);
        _suppressFullscreen = _ini.ReadInt("Settings", "SuppressFullscreen", 0) != 0;

        // Input
        var idleTimers = new IdleTimers();
        var xinput     = new XInputMonitor();
        var joy        = new JoystickMonitor();

        xinput.Initialize();

        _agentIdle = new AgentIdleService(idleTimers, xinput, joy);
        _agentIdle.Ticked += OnAgentIdleTicked;

        // Overlay
        _overlay = new BlackOverlayManager(_agentIdle.ResetActivity);

        // Message sink for display/power events.
        // Ignore WM_DISPLAYCHANGE if the overlay just appeared — exclusive-fullscreen
        // games trigger a mode switch when they lose focus, which would otherwise
        // self-remove the overlay in ~10 ms (flicker).
        _msgSink = new SystemMessageSink(
            onDisplayChange: () => { if (_overlay.ShownDurationMs > 1000) _overlay.Remove(); },
            onWake:          _overlay.Remove);

        // Powercfg
        _powercfg = new PowercfgService(_ini);
        _powercfg.Updated += (_, _) => UpdateTooltip();

        // Tray icon
        _tray = new NotifyIcon
        {
            Icon    = LoadTrayIcon(),
            Text    = "AggressiveScreensaver",
            Visible = true,
        };
        _tray.ContextMenuStrip = BuildContextMenu();
        _tray.MouseClick += TrayMouseClick;

        // Tooltip refresh every 1 s
        _tooltipTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _tooltipTimer.Tick += (_, _) => UpdateTooltip();
        _tooltipTimer.Start();

        // Start services
        _powercfg.Start();
        _agentIdle.Start();

        // Initial balloon
        _tray.ShowBalloonTip(3000, "Power Monitor", "Hover tray icon to see idle timers and blocking apps.", ToolTipIcon.Info);
        Logger.Log("TrayApp started.");
    }

    // -------------------------------------------------------------------------
    // Idle tick handler
    // -------------------------------------------------------------------------
    private void OnAgentIdleTicked(object? sender, EventArgs e)
    {
        // A screen-blocking app (e.g. video player) counts as activity: reset the
        // idle counter while it is present so the full threshold must elapse again
        // after it stops before we blank.
        if (!string.IsNullOrEmpty(_powercfg.BlockingScreenApps))
            _agentIdle.ResetActivity();

        bool shouldBlank =
            _agentIdle.AgentIdleSeconds >= _blankThresholdSec &&
            string.IsNullOrEmpty(_powercfg.BlockingScreenApps) &&
            (!_suppressFullscreen || !IsFullscreenAppForeground());

        if (shouldBlank && !_overlay.IsBlanked)
            _overlay.Show();
        else if (!shouldBlank && _overlay.IsBlanked)
            _overlay.Remove();
    }

    // -------------------------------------------------------------------------
    // Fullscreen detection
    // -------------------------------------------------------------------------
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>
    /// Returns true when the foreground window covers an entire monitor (games,
    /// fullscreen video). Maximized windowed apps don't reach the taskbar band,
    /// so they are not matched. The desktop and shell windows are excluded.
    /// </summary>
    private static bool IsFullscreenAppForeground()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            if (hwnd == GetDesktopWindow() || hwnd == GetShellWindow()) return false;

            if (!GetWindowRect(hwnd, out RECT wr)) return false;

            IntPtr hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hMon, ref mi)) return false;

            return wr.Left  <= mi.rcMonitor.Left  &&
                   wr.Top   <= mi.rcMonitor.Top    &&
                   wr.Right >= mi.rcMonitor.Right  &&
                   wr.Bottom >= mi.rcMonitor.Bottom;
        }
        catch { return false; }
    }

    // -------------------------------------------------------------------------
    // Tooltip
    // -------------------------------------------------------------------------
    private void UpdateTooltip()
    {
        string tip =
            $"True Idle: {_agentIdle?.AgentIdleSeconds ?? 0}s (Goal: {_blankThresholdSec}s)\n" +
            $"SCREEN: {(string.IsNullOrEmpty(_powercfg.BlockingScreenApps) ? "None" : _powercfg.BlockingScreenApps)}\n" +
            $"SLEEP: {(string.IsNullOrEmpty(_powercfg.BlockingSleepApps) ? "None" : _powercfg.BlockingSleepApps)}";

        // Win32 tray tip limit is 127 chars
        if (tip.Length > 127)
            tip = tip[..127];

        _tray.Text = tip;
    }

    // -------------------------------------------------------------------------
    // Context menu
    // -------------------------------------------------------------------------
    private const string TaskName = "AggressiveScreensaver";
    private static readonly string ExePath = Path.Combine(AppContext.BaseDirectory, "AggressiveScreensaver.exe");
    private const string TaskDescription = "Launches AggressiveScreensaver at logon with administrator privileges.";

    private ContextMenuStrip BuildContextMenu() =>
        TrayMenuFactory.Build(
            startAtLogin:                StartupTaskService.IsInstalled(TaskName),
            suppressFullscreen:          _suppressFullscreen,
            onBlacklist:                 ShowBlacklistForm,
            onDebug:                     ShowDebugForm,
            onStartupChanged:            HandleStartupToggle,
            onSuppressFullscreenChanged: HandleSuppressFullscreenToggle,
            onExit:                      ExitApp);

    private bool HandleSuppressFullscreenToggle(bool wantEnabled)
    {
        _suppressFullscreen = wantEnabled;
        _ini.DebouncedSave(() => _ini.WriteInt("Settings", "SuppressFullscreen", _suppressFullscreen ? 1 : 0));
        return true;
    }

    private bool HandleStartupToggle(bool wantEnabled)
    {
        bool success = wantEnabled
            ? StartupTaskService.Install(TaskName, ExePath, TaskDescription, Logger.Log)
            : StartupTaskService.Uninstall(TaskName, Logger.Log);

        if (!success)
            MessageBox.Show(
                "Failed to update the startup task.\nCheck the log file for details.",
                "AggressiveScreensaver",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

        return success;
    }

    // -------------------------------------------------------------------------
    // Left-click → slider
    // -------------------------------------------------------------------------
    private void TrayMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            ShowSlider();
    }

    private void ShowSlider()
    {
        // WPF window shown from a WinForms host: Show()/ShowDialog() pump their
        // own Dispatcher via a nested message loop, so no System.Windows.Application
        // instance is needed (this app is WinForms end to end otherwise).
        if (_sliderWindow is null)
        {
            _sliderWindow = new TimeoutSliderWindow(
                currentThresholdSec: _blankThresholdSec,
                timeoutSteps: TimeoutSteps,
                onChanged: sec =>
                {
                    _blankThresholdSec = sec;
                    _ini.DebouncedSave(() => _ini.WriteInt("Settings", "BlankThreshold", _blankThresholdSec));
                    UpdateTooltip();
                    Logger.Log($"BlankThreshold changed to {sec}s");
                });
        }
        _sliderWindow.ShowAboveMouse();
    }

    // -------------------------------------------------------------------------
    // Sub-dialogs
    // -------------------------------------------------------------------------
    private void ShowBlacklistForm()
    {
        var w = new BlacklistWindow(_powercfg, _ini);
        w.ShowDialog();
    }

    private void ShowDebugForm()
    {
        var w = new DebugWindow(_powercfg);
        w.Show();
    }

    // -------------------------------------------------------------------------
    // Exit
    // -------------------------------------------------------------------------
    private void ExitApp()
    {
        Logger.Log("Exit requested via tray menu.");
        _overlay.Remove();   // restore taskbar + cursor on clean exit
        Application.Exit();
    }

    // -------------------------------------------------------------------------
    // Icon loading
    // -------------------------------------------------------------------------
    private static Icon LoadTrayIcon()
    {
        try
        {
            // Prefer the embedded resource
            var asm = typeof(TrayApp).Assembly;
            using var stream = asm.GetManifestResourceStream("AggressiveScreensaver.Resources.tray.ico");
            if (stream is not null)
                return new Icon(stream);
        }
        catch { /* fall through */ }

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
            _tooltipTimer.Dispose();
            _agentIdle.Dispose();
            _powercfg.Dispose();
            _msgSink.Dispose();
            _ini.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
