using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace RefreshRateOverlay.WPF.Services;

/// <summary>
/// Polls the foreground window every 500 ms and fires AppChanged when the active
/// process name changes.
/// </summary>
internal sealed class ForegroundTracker : IDisposable
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint   GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenProcess(uint dwAccess, bool bInherit, uint dwProcessId);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, StringBuilder name, ref uint size);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // Window classes that represent the taskbar / system tray, not real apps
    private static readonly string[] IgnoredClasses =
        ["Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow"];

    // Our own process id: any window we own (the settings overlay, the hotkey-
    // rebind window, any future dialog) must never be treated as "the active
    // app" for profile purposes. Excluding by PID rather than tracking a single
    // window handle (the old OverlayHandle approach) covers every window this
    // process ever creates, not just the one we remembered to wire up.
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    public event EventHandler<string>? AppChanged;

    private readonly System.Windows.Forms.Timer _timer;
    private string _lastApp = string.Empty;

    // Debounce: a candidate app must be seen on this many consecutive ticks
    // before we treat it as a real switch. Filters out transient foreground
    // hand-offs (e.g. a display-mode change's brief blackout, or the settings
    // overlay opening/closing) that would otherwise falsely trigger a profile
    // re-application and clobber a value that was just correctly set.
    private const int RequiredStableTicks = 2;
    private string? _pendingApp;
    private int _pendingTicks;

    public ForegroundTracker()
    {
        _timer = new System.Windows.Forms.Timer { Interval = 500 };
        _timer.Tick += OnTick;
    }

    public void Start() => _timer.Start();
    public void Stop()  => _timer.Stop();

    private void OnTick(object? sender, EventArgs e)
    {
        IntPtr hwnd = GetForegroundWindow();

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == OwnProcessId)
        {
            _pendingApp = null;
            return;
        }

        string? app = GetProcessName(hwnd);
        if (app is null)
        {
            _pendingApp = null;
            return;
        }

        if (app == _lastApp)
        {
            _pendingApp = null;
            return;
        }

        if (app != _pendingApp)
        {
            _pendingApp = app;
            _pendingTicks = 1;
            return;
        }

        if (++_pendingTicks < RequiredStableTicks)
            return;

        _lastApp = app;
        _pendingApp = null;
        AppChanged?.Invoke(this, app);
    }

    /// <summary>
    /// Returns the process filename (e.g. "chrome.exe") of the given window,
    /// or null if the window should be ignored.
    /// </summary>
    internal static string? GetProcessName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;

        var cls = new StringBuilder(256);
        GetClassName(hwnd, cls, cls.Capacity);
        string className = cls.ToString();

        foreach (var ignored in IgnoredClasses)
            if (className.Equals(ignored, StringComparison.OrdinalIgnoreCase))
                return null;

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;

        IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc == IntPtr.Zero) return null;

        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            if (!QueryFullProcessImageName(hProc, 0, sb, ref size))
                return null;
            return Path.GetFileName(sb.ToString());
        }
        finally
        {
            CloseHandle(hProc);
        }
    }

    public string LastApp => _lastApp;

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
