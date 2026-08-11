using System;
using System.Runtime.InteropServices;

namespace RefreshRateOverlay.WPF.Services;

public enum WindowMode
{
    Unknown,
    Windowed,
    BorderlessFullscreen,
    ExclusiveFullscreen,
}

/// <summary>
/// Classifies how a window is currently presenting: true DXGI exclusive
/// fullscreen, a borderless window sized to cover its monitor, or a normal
/// windowed app. Uses only public, non-elevated APIs — no ETW, no injection.
/// </summary>
internal static class WindowModeService
{
    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE state);

    private enum QUERY_USER_NOTIFICATION_STATE
    {
        QUNS_NOT_PRESENT = 1,
        QUNS_BUSY = 2,
        QUNS_RUNNING_D3D_FULL_SCREEN = 3,
        QUNS_PRESENTATION_MODE = 4,
        QUNS_ACCEPTS_NOTIFICATIONS = 5,
        QUNS_QUIET_TIME = 6,
        QUNS_APP = 7,
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

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

    /// <summary>
    /// Classifies whatever owns <paramref name="hwnd"/>. Must be called while
    /// that window is still the actual foreground window — SHQueryUserNotificationState
    /// reports on "whatever is in front right now" system-wide, not a specific hwnd,
    /// so this cannot be called after our own overlay has taken foreground.
    /// </summary>
    public static WindowMode Detect(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return WindowMode.Unknown;

        if (SHQueryUserNotificationState(out var state) == 0 &&
            state == QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN)
            return WindowMode.ExclusiveFullscreen;

        if (!GetWindowRect(hwnd, out var rect)) return WindowMode.Unknown;

        IntPtr hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (hMon == IntPtr.Zero || !GetMonitorInfo(hMon, ref mi)) return WindowMode.Unknown;

        // Allow a 1px slop for DPI/scaling rounding.
        bool coversMonitor =
            rect.Left <= mi.rcMonitor.Left + 1 && rect.Top <= mi.rcMonitor.Top + 1 &&
            rect.Right >= mi.rcMonitor.Right - 1 && rect.Bottom >= mi.rcMonitor.Bottom - 1;

        return coversMonitor ? WindowMode.BorderlessFullscreen : WindowMode.Windowed;
    }

    public static string Describe(WindowMode mode) => mode switch
    {
        WindowMode.ExclusiveFullscreen  => "Exclusive Fullscreen",
        WindowMode.BorderlessFullscreen => "Borderless (fullscreen-sized window)",
        WindowMode.Windowed             => "Windowed",
        _                                => "Unknown",
    };
}
