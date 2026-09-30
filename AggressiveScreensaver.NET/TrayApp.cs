using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AggressiveScreensaver.Forms;
using AggressiveScreensaver.Input;
using AggressiveScreensaver.Parsing;
using AggressiveScreensaver.Services;
using Shared;

namespace AggressiveScreensaver;

/// <summary>
/// Owns all services and the NotifyIcon. WPF has no native tray-icon control,
/// so this uses System.Windows.Forms.NotifyIcon/ContextMenuStrip, same as
/// LGTV_brightness.NET and RefreshRateOverlay.WPF — both ride on the host
/// WPF Application's message loop (App.xaml.cs), not a WinForms one.
/// </summary>
internal sealed class TrayApp : IDisposable
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
    private bool _ignoreUnfocusedBlockers;
    private bool _ignoreNonvisibleBlockers;
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

        _blankThresholdSec       = _ini.ReadInt("Settings", "BlankThreshold",    30);
        _suppressFullscreen      = _ini.ReadInt("Settings", "SuppressFullscreen", 0) != 0;
        _ignoreUnfocusedBlockers  = _ini.ReadInt("Settings", "IgnoreUnfocusedBlockers", 1) != 0;
        _ignoreNonvisibleBlockers = _ini.ReadInt("Settings", "IgnoreNonvisibleBlockers", 0) != 0;

        // Input
        var idleTimers  = new IdleTimers();
        var controllers = new GameControllerMonitor();

        _agentIdle = new AgentIdleService(idleTimers, controllers);
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
        bool screenBlocked = IsScreenBlocked();

        // A screen-blocking app (e.g. video player) counts as activity: reset the
        // idle counter while it is present so the full threshold must elapse again
        // after it stops before we blank.
        if (screenBlocked)
            _agentIdle.ResetActivity();

        bool fullscreenSuppressed = _suppressFullscreen && IsFullscreenAppForeground();

        bool shouldBlank =
            _agentIdle.AgentIdleSeconds >= _blankThresholdSec &&
            !screenBlocked &&
            !fullscreenSuppressed;

        if (shouldBlank && !_overlay.IsBlanked)
        {
            Logger.Log($"Blank triggered: idle={_agentIdle.AgentIdleSeconds}s/{_blankThresholdSec}s, screenBlocked={screenBlocked}, fullscreenSuppressed={fullscreenSuppressed}, blockers=[{string.Join(",", GetActiveBlockerFilenames())}]");
            _overlay.Show();
        }
        else if (!shouldBlank && _overlay.IsBlanked)
        {
            Logger.Log($"Blank cleared: idle={_agentIdle.AgentIdleSeconds}s/{_blankThresholdSec}s, screenBlocked={screenBlocked}, fullscreenSuppressed={fullscreenSuppressed}, blockers=[{string.Join(",", GetActiveBlockerFilenames())}]");
            _overlay.Remove();
        }
    }

    /// <summary>
    /// Whether a DISPLAY power request should currently hold off blanking.
    /// Apps on the Ignore List never count, regardless of focus or visibility —
    /// same as an unfocused/invisible blocker, they're excluded before either
    /// check runs. Of what's left: when <see cref="_ignoreUnfocusedBlockers"/> is
    /// set, a blocking app only counts while it is the foreground window — a
    /// blocker running unfocused in the background is ignored, so the idle timer
    /// keeps ticking and blanking still happens. <see cref="_ignoreNonvisibleBlockers"/>
    /// only has an effect while that's set: it relaxes the requirement from
    /// "focused" to merely having a visible, non-minimized window — the blocker
    /// need not be focused, just on screen. (The tray menu grays it out
    /// otherwise, since it would be a no-op.)
    /// </summary>
    private bool IsScreenBlocked()
    {
        var activeFilenames = GetActiveBlockerFilenames();
        if (activeFilenames.Count == 0)
            return false;

        if (!_ignoreUnfocusedBlockers)
            return true;

        return _ignoreNonvisibleBlockers ? IsBlockingAppVisible(activeFilenames) : IsBlockingAppForeground(activeFilenames);
    }

    /// <summary>
    /// Filenames of current DISPLAY blockers, minus anything on the Ignore List.
    /// </summary>
    private IReadOnlyList<string> GetActiveBlockerFilenames() =>
        BlockerFocusMatcher.ExcludeIgnored(_powercfg.BlockingScreenAppFilenames, _powercfg.IgnoredApps);

    /// <summary>
    /// True if the current foreground window belongs to one of <paramref name="blockerFilenames"/>.
    /// </summary>
    private bool IsBlockingAppForeground(IReadOnlyList<string> blockerFilenames) =>
        BlockerFocusMatcher.IsBlockerFocused(blockerFilenames, GetForegroundProcessFilename());

    /// <summary>
    /// True if any of <paramref name="blockerFilenames"/> owns a visible,
    /// non-minimized top-level window (it need not be focused).
    /// </summary>
    private bool IsBlockingAppVisible(IReadOnlyList<string> blockerFilenames) =>
        BlockerFocusMatcher.IsBlockerVisible(blockerFilenames, GetVisibleProcessFilenames());

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private const int DWMWA_CLOAKED = 14;

    /// <summary>
    /// True if the window is DWM-cloaked — invisible to the user despite having
    /// WS_VISIBLE set. This covers windows on an inactive virtual desktop (Task
    /// View moves them to another desktop without hiding the window itself) and
    /// suspended UWP apps, both of which <see cref="IsWindowVisible"/> alone
    /// cannot detect.
    /// </summary>
    private static bool IsWindowCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static string? GetForegroundProcessFilename() => GetProcessFilename(GetForegroundWindow());

    private static string? GetProcessFilename(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return null;

            GetWindowThreadProcessId(hwnd, out uint pid);
            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            return Path.GetFileName(proc.MainModule?.FileName) is { Length: > 0 } name ? name : proc.ProcessName + ".exe";
        }
        catch { return null; }
    }

    /// <summary>
    /// Process filenames owning a currently visible, non-minimized top-level window
    /// on the active virtual desktop.
    /// </summary>
    private static System.Collections.Generic.IEnumerable<string?> GetVisibleProcessFilenames()
    {
        var filenames = new System.Collections.Generic.List<string?>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && !IsIconic(hwnd) && !IsWindowCloaked(hwnd))
                filenames.Add(GetProcessFilename(hwnd));
            return true;
        }, IntPtr.Zero);
        return filenames;
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
        string screenLine = string.IsNullOrEmpty(_powercfg.BlockingScreenApps)
            ? "None"
            : IsScreenBlocked()
                ? _powercfg.BlockingScreenApps
                : $"{_powercfg.BlockingScreenApps} (ignoring)";

        string tip =
            $"True Idle: {_agentIdle?.AgentIdleSeconds ?? 0}s (Goal: {_blankThresholdSec}s)\n" +
            $"SCREEN: {screenLine}\n" +
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
            startAtLogin:                     StartupTaskService.IsInstalled(TaskName),
            suppressFullscreen:               _suppressFullscreen,
            ignoreUnfocusedBlockers:          _ignoreUnfocusedBlockers,
            ignoreNonvisibleBlockers:         _ignoreNonvisibleBlockers,
            nativeScreensaverActive:          NativeScreensaverService.IsActive(),
            onIgnoreList:                     ShowIgnoreListForm,
            onDebug:                          ShowDebugForm,
            onStartupChanged:                 HandleStartupToggle,
            onSuppressFullscreenChanged:      HandleSuppressFullscreenToggle,
            onIgnoreUnfocusedBlockersChanged: HandleIgnoreUnfocusedBlockersToggle,
            onIgnoreNonvisibleBlockersChanged: HandleIgnoreNonvisibleBlockersToggle,
            onNativeScreensaverChanged:       HandleNativeScreensaverToggle,
            onExit:                           ExitApp);

    private bool HandleSuppressFullscreenToggle(bool wantEnabled)
    {
        _suppressFullscreen = wantEnabled;
        _ini.DebouncedSave(() => _ini.WriteInt("Settings", "SuppressFullscreen", _suppressFullscreen ? 1 : 0));
        return true;
    }

    private bool HandleIgnoreUnfocusedBlockersToggle(bool wantEnabled)
    {
        _ignoreUnfocusedBlockers = wantEnabled;
        _ini.DebouncedSave(() => _ini.WriteInt("Settings", "IgnoreUnfocusedBlockers", _ignoreUnfocusedBlockers ? 1 : 0));
        return true;
    }

    private bool HandleIgnoreNonvisibleBlockersToggle(bool wantEnabled)
    {
        _ignoreNonvisibleBlockers = wantEnabled;
        _ini.DebouncedSave(() => _ini.WriteInt("Settings", "IgnoreNonvisibleBlockers", _ignoreNonvisibleBlockers ? 1 : 0));
        return true;
    }

    private bool HandleNativeScreensaverToggle(bool wantEnabled)
    {
        bool ok = NativeScreensaverService.SetActive(wantEnabled);
        Logger.Log($"Native screensaver {(wantEnabled ? "enabled" : "disabled")} via tray menu.{(ok ? "" : " (failed)")}");
        return ok;
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
    private void ShowIgnoreListForm()
    {
        var w = new IgnoreListWindow(_powercfg, _ini);
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
        System.Windows.Application.Current.Shutdown();
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
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tooltipTimer.Dispose();
        _agentIdle.Dispose();
        _powercfg.Dispose();
        _msgSink.Dispose();
        _ini.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
    }
}
