using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarReveal;

/// <summary>
/// Manages taskbar visibility via cursor-edge detection.
/// Show/hide never alters the system auto-hide setting.
///
/// Bottom edge  → show taskbar.
/// Bottom-right corner → hide taskbar (only when AutoHide is on).
/// </summary>
internal sealed class TaskbarService : IDisposable
{
    [DllImport("user32.dll")]  private static extern IntPtr FindWindow(string cls, string? name);
    [DllImport("user32.dll")]  private static extern bool   ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("shell32.dll")] private static extern uint   SHAppBarMessage(uint msg, ref APPBARDATA data);

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint   cbSize;
        public IntPtr hWnd;
        public uint   uCallbackMessage;
        public uint   uEdge;
        public RECT   rc;
        public int    lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const int  SW_HIDE      = 0;
    private const int  SW_SHOW      = 5;
    private const uint ABM_GETSTATE = 0x00000004;
    private const uint ABM_SETSTATE = 0x0000000A;
    private const int  ABS_AUTOHIDE = 0x00000001;

    private static readonly string[] TaskbarClasses = ["Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    // Pixels from bottom edge to trigger show.
    private const int BottomTriggerPx = 2;
    // Pixels from both right and bottom edges to trigger hide.
    private const int CornerTriggerPx = 20;

    private enum Zone { None, Bottom, BottomRight }

    private readonly System.Windows.Forms.Timer _pollTimer;
    private Zone _lastZone = Zone.None;

    public bool AutoHide
    {
        get => GetAutoHideSystem();
        set => SetAutoHideSystem(value);
    }

    public TaskbarService()
    {
        _pollTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _pollTimer.Tick += Poll;
        _pollTimer.Start();
    }

    public void HideTaskbar()
    {
        SetTaskbarsVisible(false);
        _lastZone = Zone.None; // reset so next bottom-edge visit re-shows
    }

    private void Poll(object? sender, EventArgs e)
    {
        Zone zone = GetZone(Control.MousePosition);
        if (zone == _lastZone) return;
        _lastZone = zone;

        switch (zone)
        {
            case Zone.Bottom:
                SetTaskbarsVisible(true);
                break;
            case Zone.BottomRight when AutoHide:
                SetTaskbarsVisible(false);
                break;
        }
    }

    private static Zone GetZone(Point cursor)
    {
        foreach (Screen screen in Screen.AllScreens)
        {
            Rectangle b = screen.Bounds;
            if (cursor.X < b.Left || cursor.X >= b.Right)  continue;
            if (cursor.Y < b.Top  || cursor.Y >= b.Bottom) continue;

            bool nearBottom = cursor.Y >= b.Bottom - CornerTriggerPx;
            bool atRight    = cursor.X >= b.Right  - CornerTriggerPx;
            bool atBottom   = cursor.Y >= b.Bottom - BottomTriggerPx;

            if (nearBottom && atRight) return Zone.BottomRight;
            if (atBottom)             return Zone.Bottom;
        }
        return Zone.None;
    }

    private static void SetTaskbarsVisible(bool visible)
    {
        int cmd = visible ? SW_SHOW : SW_HIDE;
        foreach (string cls in TaskbarClasses)
        {
            IntPtr hwnd = FindWindow(cls, null);
            if (hwnd != IntPtr.Zero)
                ShowWindow(hwnd, cmd);
        }
    }

    private static bool GetAutoHideSystem()
    {
        var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        uint state = SHAppBarMessage(ABM_GETSTATE, ref data);
        return (state & ABS_AUTOHIDE) != 0;
    }

    private static void SetAutoHideSystem(bool enable)
    {
        var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        data.hWnd = FindWindow("Shell_TrayWnd", null);
        uint current = SHAppBarMessage(ABM_GETSTATE, ref data);
        data.lParam = enable
            ? (int)(current |  (uint)ABS_AUTOHIDE)
            : (int)(current & ~(uint)ABS_AUTOHIDE);
        SHAppBarMessage(ABM_SETSTATE, ref data);
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        _pollTimer.Dispose();
    }
}
