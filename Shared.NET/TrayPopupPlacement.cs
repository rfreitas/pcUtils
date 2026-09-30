using System;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

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

    // ---- Window-level API: what a flyout actually calls ---------------------------------------------

    private sealed class Anchor { public System.Drawing.Point Cursor; }
    private static readonly ConditionalWeakTable<System.Windows.Window, Anchor> Anchors = new();

    /// <summary>
    /// Shows <paramref name="window"/> (if hidden) and places it above the taskbar, anchored to the cursor.
    /// Sizes from the window's REAL pixel rectangle (WPF's DIP size is wrong while the window is still on
    /// another monitor/DPI), re-places once layout has settled, and again whenever the DPI changes.
    /// The window should start at Left = Top = -10000 so it never flashes at WPF's default spot.
    /// </summary>
    public static void ShowAbove(System.Windows.Window window, Point cursorPx)
    {
        bool first = !Anchors.TryGetValue(window, out var anchor);
        if (first)
        {
            anchor = new Anchor();
            Anchors.Add(window, anchor);
            window.DpiChanged += (_, _) =>
            {
                if (window.IsVisible)
                    window.Dispatcher.BeginInvoke(new Action(() => Place(window, anchor!)), DispatcherPriority.Loaded);
            };
        }
        anchor!.Cursor = cursorPx;

        if (!window.IsVisible) window.Show();
        Place(window, anchor);
        window.Dispatcher.BeginInvoke(new Action(() => Place(window, anchor)), DispatcherPriority.Loaded);
    }

    /// <summary>The window's live rectangle in physical pixels.</summary>
    public static Rectangle WindowRect(System.Windows.Window window)
    {
        GetWindowRect(new WindowInteropHelper(window).Handle, out var r);
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    private static void Place(System.Windows.Window window, Anchor anchor)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        window.UpdateLayout();
        for (int pass = 0; pass < 2; pass++)   // 2nd pass corrects a size change caused by the 1st move
        {
            if (!GetWindowRect(hwnd, out var r)) return;
            var size   = new System.Drawing.Size(r.Right - r.Left, r.Bottom - r.Top);
            int margin = (int)(6 * ScaleAt(anchor.Cursor));
            var pos    = AboveTaskbar(anchor.Cursor, size, margin);
            if (pos.X == r.Left && pos.Y == r.Top) return;
            SetWindowPos(hwnd, new IntPtr(-1), pos.X, pos.Y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);   // HWND_TOPMOST
            window.UpdateLayout();
        }
    }

    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);


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
