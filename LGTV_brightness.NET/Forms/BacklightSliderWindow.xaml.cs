using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Shared;

namespace LgtvBrightness.Forms;

/// <summary>
/// Vertical backlight slider — dark borderless window, opens above the mouse cursor.
/// Click-away/Escape dismissal and tray-icon toggle-safety come from TrayFlyoutWindow.
/// Positioning logic is a straight port of AggressiveScreensaver.NET's TimeoutSliderWindow.
/// </summary>
internal partial class BacklightSliderWindow : TrayFlyoutWindow
{
    private readonly Action<int> _onChanged;
    private bool _suppressChange;

    private const int GuiW          = 60;
    private const int GuiH          = 210;
    private const int TaskbarMargin = 6; // logical px gap above taskbar

    public BacklightSliderWindow(int currentValue, Action<int> onChanged)
    {
        InitializeComponent();

        // Off-screen until the first ShowAboveMouse() repositions it via SetWindowPos,
        // so there's no flash at WPF's default location on first show.
        Left = -10000;
        Top  = -10000;

        _onChanged = onChanged;

        BacklightSlider.Minimum = 0;
        BacklightSlider.Maximum = 100;
        BacklightSlider.ValueChanged += TrackValueChanged;

        SetValue(currentValue);
    }

    /// <summary>Reflects an external value change (periodic TV sync) without re-firing onChanged.</summary>
    public void SetValue(int value)
    {
        _suppressChange = true;
        BacklightSlider.Value = Math.Clamp(value, 0, 100);
        BacklightLabel.Text = value.ToString();
        _suppressChange = false;
    }

    // -------------------------------------------------------------------------
    // Positioning — done via raw Win32 SetWindowPos in physical pixels, same
    // approach as AggressiveScreensaver.NET's TimeoutSliderWindow.
    // -------------------------------------------------------------------------
    [DllImport("shell32.dll")] private static extern IntPtr SHAppBarMessage(uint dwMessage, ref AppBarData pData);
    [DllImport("user32.dll")]  private static extern bool   SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")]  private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    [DllImport("Shcore.dll")]  private static extern int    GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint   cbSize;
        public IntPtr hWnd;
        public uint   uCallbackMessage;
        public uint   uEdge;
        public NativeRect rc;
        public int    lParam;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }

    private const uint ABM_GETTASKBARPOS        = 0x00000005;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOZORDER   = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>
    /// Returns the y bottom-boundary to position the slider above, correctly
    /// handling auto-hiding taskbars whose WorkingArea reservation is only 1 px.
    /// </summary>
    private static int GetAvailableBottom(System.Drawing.Point mouse)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(mouse);

        var abd = new AppBarData { cbSize = (uint)Marshal.SizeOf<AppBarData>() };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref abd) != IntPtr.Zero)
        {
            if (abd.rc.Bottom == screen.Bounds.Bottom &&
                abd.rc.Right  >  screen.Bounds.Left  &&
                abd.rc.Left   <  screen.Bounds.Right)
                return abd.rc.Top;
        }

        return screen.WorkingArea.Bottom;
    }

    public void ShowAboveMouse() => ToggleShow(() =>
    {
        System.Drawing.Point mouse = System.Windows.Forms.Cursor.Position; // physical px

        uint dpi = 96;
        IntPtr hMon = MonitorFromPoint(new POINT { X = mouse.X, Y = mouse.Y }, MONITOR_DEFAULTTONEAREST);
        if (GetDpiForMonitor(hMon, 0 /* MDT_EFFECTIVE_DPI */, out uint dpiX, out _) == 0 && dpiX > 0)
            dpi = dpiX;
        float scale = dpi / 96f;

        int w      = (int)(GuiW * scale);
        int h      = (int)(GuiH * scale);
        int margin = (int)(TaskbarMargin * scale);

        int anchor = Math.Min(GetAvailableBottom(mouse), mouse.Y);

        int x = mouse.X - w / 2;
        int y = anchor - h - margin;

        Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        Activate();
    });

    // -------------------------------------------------------------------------
    // Slider change
    // -------------------------------------------------------------------------
    private void TrackValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int val = (int)Math.Round(e.NewValue);
        BacklightLabel.Text = val.ToString();
        if (!_suppressChange)
            _onChanged(val);
    }
}
