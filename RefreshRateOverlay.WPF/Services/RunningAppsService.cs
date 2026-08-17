using System.Runtime.InteropServices;
using System.Text;

namespace RefreshRateOverlay.WPF.Services;

/// <summary>
/// Enumerates distinct apps that currently have at least one visible, titled
/// top-level window — the overlay's app-picker list. Exists because a game's
/// own exclusive input capture can block this app's global hotkey while that
/// game has focus, independent of which key combo is bound (confirmed:
/// Doom: The Dark Ages does this, other games don't) — Windows' hotkey API
/// has no way to override that, so the only workaround is letting the
/// overlay be opened some other way (tray icon, or while a different app is
/// focused) and explicitly retargeted to the app that can't be focused.
/// </summary>
internal static class RunningAppsService
{
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    /// <summary>Distinct exe names (e.g. "acs.exe"), one per app with at
    /// least one visible, titled top-level window — same "not a real app
    /// window" exclusions ForegroundTracker already applies (shell windows),
    /// plus this app's own windows.</summary>
    public static List<string> GetRunningApps()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var apps = new List<string>();

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (GetWindowTextLength(hwnd) == 0) return true; // untitled windows are never real app windows

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == OwnProcessId) return true;

            string? app = ForegroundTracker.GetProcessName(hwnd);
            if (app is not null && seen.Add(app)) apps.Add(app);

            return true;
        }, IntPtr.Zero);

        apps.Sort(StringComparer.OrdinalIgnoreCase);
        return apps;
    }
}
