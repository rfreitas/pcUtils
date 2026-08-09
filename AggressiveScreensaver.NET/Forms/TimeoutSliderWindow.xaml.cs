using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Shared;

namespace AggressiveScreensaver.Forms;

/// <summary>
/// Vertical timeout slider — dark borderless window, opens above the mouse cursor.
/// Click-away/Escape dismissal and tray-icon toggle-safety come from TrayFlyoutWindow.
/// </summary>
internal partial class TimeoutSliderWindow : TrayFlyoutWindow
{
    private readonly int[]      _steps;
    private readonly Action<int> _onChanged;

    private const int GuiW          = 60;
    private const int GuiH          = 210;
    private const int TaskbarMargin = 6; // logical px gap above taskbar

    public TimeoutSliderWindow(int currentThresholdSec, int[] timeoutSteps, Action<int> onChanged)
    {
        InitializeComponent();

        // Off-screen until the first ShowAboveMouse() repositions it via SetWindowPos,
        // so there's no flash at WPF's default location on first show.
        Left = -10000;
        Top  = -10000;

        _steps     = timeoutSteps;
        _onChanged = onChanged;

        TimeoutSlider.Minimum = 1;
        TimeoutSlider.Maximum = _steps.Length;
        TimeoutSlider.ValueChanged += TrackValueChanged;

        TimeoutSlider.Value = StepToRaw(FindStepIndex(currentThresholdSec));
        TimeoutLabel.Text   = FormatTimeout(currentThresholdSec);
    }

    // -------------------------------------------------------------------------
    // Positioning — done via raw Win32 SetWindowPos in physical pixels rather
    // than WPF's device-independent Left/Top. WPF's DIU coordinate space isn't
    // a simple physical/scale conversion across monitors with different DPI,
    // so computing an absolute screen position by hand is more reliable done
    // in physical pixels (same approach the DPI work on RefreshRateOverlay
    // settled on).
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
    /// Returns the y bottom-boundary to position the slider above,
    /// correctly handling auto-hiding taskbars whose WorkingArea reservation is only 1 px.
    /// </summary>
    private static int GetAvailableBottom(System.Drawing.Point mouse)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(mouse);

        var abd = new AppBarData { cbSize = (uint)Marshal.SizeOf<AppBarData>() };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref abd) != IntPtr.Zero)
        {
            // Only apply when taskbar is on the bottom edge of this screen
            if (abd.rc.Bottom == screen.Bounds.Bottom &&
                abd.rc.Right  >  screen.Bounds.Left  &&
                abd.rc.Left   <  screen.Bounds.Right)
                return abd.rc.Top;
        }

        // Fallback: work area bottom (correct for docked non-auto-hide taskbar)
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

        // When the tray icon is in the overflow (hidden icons) menu the cursor
        // is already above the taskbar, so anchor to the cursor rather than the
        // taskbar edge — whichever is higher wins.
        int anchor = Math.Min(GetAvailableBottom(mouse), mouse.Y);

        int x = mouse.X - w / 2;
        int y = anchor - h - margin;

        Show(); // ensures the HWND exists so SetWindowPos has a target
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        Activate(); // needed so Deactivated fires on click-away
    });

    // -------------------------------------------------------------------------
    // Slider change
    // -------------------------------------------------------------------------
    private void TrackValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int sec = RawToSec((int)Math.Round(e.NewValue));
        TimeoutLabel.Text = FormatTimeout(sec);
        _onChanged(sec);
    }

    // -------------------------------------------------------------------------
    // Step mapping (mirrors the original AHK/WinForms inversion)
    // -------------------------------------------------------------------------

    /// <summary>Raw slider value 1..N where 1=bottom(Minimum)=longest step, N=top(Maximum)=shortest.</summary>
    private int StepToRaw(int stepIdx) => _steps.Length + 1 - stepIdx;
    private int RawToStepIdx(int raw)  => _steps.Length + 1 - raw;
    private int RawToSec(int raw)      => _steps[Math.Clamp(RawToStepIdx(raw) - 1, 0, _steps.Length - 1)];

    private int FindStepIndex(int sec)
    {
        for (int i = 0; i < _steps.Length; i++)
            if (sec <= _steps[i])
                return i + 1;
        return _steps.Length;
    }

    // -------------------------------------------------------------------------
    // Formatting
    // -------------------------------------------------------------------------
    public static string FormatTimeout(int s)
    {
        if (s < 60) return $"{s}s";
        int m   = s / 60;
        int rem = s % 60;
        return rem > 0 ? $"{m}m {rem}s" : $"{m}m";
    }
}
