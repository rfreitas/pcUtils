using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Shared;

/// <summary>
/// Where to put a popup opened from a tray icon so it sits above the taskbar instead of
/// under it. Everything is in physical pixels (WPF's DIU space is not a simple scale across
/// mixed-DPI monitors, so callers position with SetWindowPos).
///
/// The anchor is the top edge of a bottom taskbar (which also covers auto-hide taskbars,
/// whose WorkingArea reservation is only 1 px), or the cursor if that is higher — when the
/// icon lives in the overflow flyout the cursor is already well above the taskbar.
/// </summary>
internal static class TrayPopupPlacement
{
    /// <summary>Physical-pixel top-left for a popup of the given size, opened from the current cursor.</summary>
    public static Point AboveTaskbar(Point mouse, Size sizePx, int marginPx)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(mouse);
        return Compute(mouse, sizePx, marginPx, screen.WorkingArea, TaskbarTop(screen));
    }

    /// <summary>Pure placement: centred on the cursor, bottom edge just above the anchor, kept inside <paramref name="bounds"/>.</summary>
    public static Point Compute(Point mouse, Size size, int margin, Rectangle bounds, int taskbarTop)
    {
        int anchor = Math.Min(taskbarTop, mouse.Y);
        int x = mouse.X - size.Width / 2;
        int y = anchor - size.Height - margin;

        x = Math.Clamp(x, bounds.Left, Math.Max(bounds.Left, bounds.Right - size.Width));
        y = Math.Max(y, bounds.Top);
        return new Point(x, y);
    }

    /// <summary>DPI scale (1.0 = 96 dpi) of the monitor under <paramref name="pt"/>.</summary>
    public static float ScaleAt(Point pt)
    {
        IntPtr mon = MonitorFromPoint(new NativePoint { X = pt.X, Y = pt.Y }, MONITOR_DEFAULTTONEAREST);
        return GetDpiForMonitor(mon, 0 /* MDT_EFFECTIVE_DPI */, out uint dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96f : 1f;
    }

    /// <summary>Top edge of the bottom taskbar on this screen, else the work-area bottom.</summary>
    internal static int TaskbarTop(System.Windows.Forms.Screen screen)
    {
        var abd = new AppBarData { cbSize = (uint)Marshal.SizeOf<AppBarData>() };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref abd) != IntPtr.Zero &&
            abd.rc.Bottom == screen.Bounds.Bottom &&
            abd.rc.Right  >  screen.Bounds.Left   &&
            abd.rc.Left   <  screen.Bounds.Right)
            return abd.rc.Top;

        return screen.WorkingArea.Bottom;
    }

    private const uint ABM_GETTASKBARPOS        = 0x00000005;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("shell32.dll")] private static extern IntPtr SHAppBarMessage(uint dwMessage, ref AppBarData pData);
    [DllImport("user32.dll")]  private static extern IntPtr MonitorFromPoint(NativePoint pt, uint dwFlags);
    [DllImport("Shcore.dll")]  private static extern int    GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect  { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint      cbSize;
        public IntPtr    hWnd;
        public uint      uCallbackMessage;
        public uint      uEdge;
        public NativeRect rc;
        public int       lParam;
    }
}
