using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AggressiveScreensaver.Forms;

/// <summary>
/// Fully custom-drawn vertical slider with modern dark styling.
/// Circular thumb, rounded track, filled active section, subtle step dots.
/// </summary>
internal sealed class DarkSlider : Control
{
    public int Minimum { get; init; } = 1;
    public int Maximum { get; init; } = 10;

    private int _value = 1;
    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, Minimum, Maximum);
            if (_value == v) return;
            _value = v;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ValueChanged;

    private bool _dragging;
    private bool _hover;

    // Colors
    private static readonly Color BgColor        = Color.FromArgb(0x2D, 0x2D, 0x2D);
    private static readonly Color TrackInactive   = Color.FromArgb(0x55, 0x55, 0x55);
    private static readonly Color TrackActive     = Color.FromArgb(0x88, 0xBB, 0xFF);
    private static readonly Color ThumbNormal     = Color.FromArgb(0xCC, 0xCC, 0xCC);
    private static readonly Color ThumbHover      = Color.FromArgb(0xFF, 0xFF, 0xFF);
    private static readonly Color StepDot         = Color.FromArgb(0x44, 0x44, 0x44);

    // Base sizes at 96 DPI — multiplied by DpiScale at runtime
    private const int TrackW   = 4;   // track width in logical pixels
    private const int ThumbR   = 8;   // thumb radius in logical pixels
    private const int TrackPad = 14;  // top/bottom padding in logical pixels
    private const int DotR     = 1;   // step-dot radius in logical pixels

    private float DpiScale => DeviceDpi / 96f;

    public DarkSlider()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        BackColor = BgColor;
    }

    private int TrackTop    => (int)(TrackPad * DpiScale);
    private int TrackBottom => Height - (int)(TrackPad * DpiScale);
    private int CenterX     => Width / 2;

    private int ValueToY(int v)
    {
        if (Maximum == Minimum) return TrackTop;
        double t = (double)(v - Minimum) / (Maximum - Minimum);
        // High value = top (low y), low value = bottom (high y)
        return TrackBottom - (int)(t * (TrackBottom - TrackTop));
    }

    private int YToValue(int y)
    {
        double t = 1.0 - Math.Clamp((double)(y - TrackTop) / (TrackBottom - TrackTop), 0.0, 1.0);
        return Minimum + (int)Math.Round(t * (Maximum - Minimum));
    }

    private static void FillRoundedRect(Graphics g, Brush b, RectangleF r, float radius)
    {
        if (r.Width < 1 || r.Height < 1) return; // degenerate — GraphicsPath would crash
        float d = radius * 2;
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(b, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (TrackBottom <= TrackTop || Width < 4) return; // too small to draw

        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(BgColor);

        float s    = DpiScale;
        float trkW = TrackW * s;
        float thmR = ThumbR * s;
        float dotR = DotR   * s;
        float hw   = trkW / 2f;

        int cx  = CenterX;
        int top = TrackTop;
        int bot = TrackBottom;
        int ty  = ValueToY(_value);

        // Inactive track (below thumb = lower values)
        using (var b = new SolidBrush(TrackInactive))
            FillRoundedRect(g, b, new RectangleF(cx - hw, top, trkW, bot - top), hw);

        // Active track (above thumb = higher/selected value and above)
        if (ty > top)
            using (var b = new SolidBrush(TrackActive))
                FillRoundedRect(g, b, new RectangleF(cx - hw, top, trkW, ty - top), hw);

        // Step dots
        for (int i = Minimum; i <= Maximum; i++)
        {
            int dy = ValueToY(i);
            using var dotB = new SolidBrush(StepDot);
            g.FillEllipse(dotB, cx - dotR, dy - dotR, dotR * 2, dotR * 2);
        }

        // Thumb circle
        var thumbRect = new RectangleF(cx - thmR, ty - thmR, thmR * 2, thmR * 2);
        using (var b = new SolidBrush(_hover ? ThumbHover : ThumbNormal))
            g.FillEllipse(b, thumbRect);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { _dragging = true; Value = YToValue(e.Y); }
    }

    private bool IsOverThumb(int x, int y)
    {
        int ty    = ValueToY(_value);
        float thmR = ThumbR * DpiScale;
        float dx  = x - CenterX;
        float dy  = y - ty;
        return dx * dx + dy * dy <= thmR * thmR;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            Value = YToValue(e.Y);
            return;
        }
        bool over = IsOverThumb(e.X, e.Y);
        if (over != _hover)
        {
            _hover  = over;
            Cursor  = over ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) _dragging = false;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        Value += e.Delta > 0 ? 1 : -1;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Cursor = Cursors.Default;
        Invalidate();
    }
}

/// <summary>
/// Vertical timeout slider — dark borderless window, opens above the mouse cursor,
/// dismisses on click-away or Escape.
/// Mirrors VerticalSlider module from AHK (60×210 DPI-scaled, 10 fixed timeout steps).
/// </summary>
internal sealed class TimeoutSliderForm : Form
{
    private readonly int[]      _steps;
    private readonly Action<int> _onChanged;
    private readonly Label      _label;
    private readonly DarkSlider  _track;

    private const int GuiW         = 60;
    private const int GuiH         = 210;
    private const int LabelH       = 26;
    private const int TaskbarMargin = 6;  // logical px gap above taskbar

    public TimeoutSliderForm(int currentThresholdSec, int[] timeoutSteps, Action<int> onChanged)
    {
        _steps     = timeoutSteps;
        _onChanged = onChanged;

        // ---------- Form style ----------
        FormBorderStyle = FormBorderStyle.None;
        BackColor       = Color.FromArgb(0x2D, 0x2D, 0x2D);
        TopMost         = true;
        ShowInTaskbar   = false;
        StartPosition   = FormStartPosition.Manual;
        ClientSize      = new Size(GuiW, GuiH);
        KeyPreview      = true;

        // ---------- Label ----------
        _label = new Label
        {
            ForeColor = Color.FromArgb(0xCC, 0xCC, 0xCC),
            BackColor = Color.FromArgb(0x2D, 0x2D, 0x2D),
            Font      = new Font("Segoe UI", 9f),
            TextAlign = ContentAlignment.MiddleCenter,
            AutoSize  = false,
        };

        // ---------- Slider ----------
        // Raw 1 (top) = longest timeout step; Raw 10 (bottom) = shortest.
        // Mirrors AHK inversion: FindStepIndex uses (11 - step) so bottom=short.
        _track = new DarkSlider
        {
            Minimum = 1,
            Maximum = _steps.Length,
        };
        _track.ValueChanged += TrackValueChanged;

        Controls.Add(_label);
        Controls.Add(_track);

        // ---------- Set initial position ----------
        _track.Value = StepToRaw(FindStepIndex(currentThresholdSec));
        _label.Text  = FormatTimeout(currentThresholdSec);

        // ---------- Explicit layout (no Dock — native TrackBar ignores Dock bounds) ----------
        SizeChanged += (_, _) => LayoutControls();
        LayoutControls();

        // ---------- Dismiss on click-away ----------
        Deactivate += (_, _) => Hide();
        KeyDown    += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
    }

    // -------------------------------------------------------------------------
    // Public
    // -------------------------------------------------------------------------

    // -------------------------------------------------------------------------
    // Taskbar position (handles auto-hide — WorkingArea gives ~full screen in that case)
    // -------------------------------------------------------------------------

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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref AppBarData pData);

    private const uint ABM_GETTASKBARPOS = 0x00000005;

    /// <summary>
    /// Returns the y bottom-boundary to position the slider above,
    /// correctly handling auto-hiding taskbars whose WorkingArea reservation is only 1 px.
    /// </summary>
    private static int GetAvailableBottom(Point nearPoint)
    {
        var screen = Screen.FromPoint(nearPoint);

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

    public void ShowAboveMouse()
    {
        // Prevent re-opening immediately after a hide from a click
        if (Visible)
        {
            Hide();
            return;
        }

        float dpiScale = DeviceDpi / 96f;
        int w = (int)(GuiW * dpiScale);
        int h = (int)(GuiH * dpiScale);

        Point mouse = Cursor.Position;
        int bottom  = GetAvailableBottom(mouse);
        int margin  = (int)(TaskbarMargin * dpiScale);

        int x = mouse.X - w / 2;
        int y = bottom - h - margin;

        Location   = new Point(x, y);
        ClientSize = new Size(w, h);
        Show();
        Activate(); // needed so Deactivate fires on click-away
    }

    // -------------------------------------------------------------------------
    // Layout
    // -------------------------------------------------------------------------
    private void LayoutControls()
    {
        int labelH = (int)(LabelH * DeviceDpi / 96f);
        _label.SetBounds(0, 0, ClientSize.Width, labelH);
        _track.SetBounds(0, labelH, ClientSize.Width, ClientSize.Height - labelH);
    }

    // -------------------------------------------------------------------------
    // TrackBar change
    // -------------------------------------------------------------------------
    private void TrackValueChanged(object? sender, EventArgs e)
    {
        int sec = RawToSec(_track.Value);
        _label.Text = FormatTimeout(sec);
        _onChanged(sec);
    }

    // -------------------------------------------------------------------------
    // Step mapping (mirrors AHK inversion)
    // -------------------------------------------------------------------------

    /// <summary>Raw TrackBar value 1–N where 1=top=longest step, N=bottom=shortest.</summary>
    private int StepToRaw(int stepIdx) => _steps.Length + 1 - stepIdx;
    private int RawToStepIdx(int raw)  => _steps.Length + 1 - raw;
    private int RawToSec(int raw)      => _steps[RawToStepIdx(raw) - 1];

    private int FindStepIndex(int sec)
    {
        for (int i = 0; i < _steps.Length; i++)
            if (sec <= _steps[i])
                return i + 1;
        return _steps.Length;
    }

    // -------------------------------------------------------------------------
    // Formatting (mirrors FormatTimeout in AHK)
    // -------------------------------------------------------------------------
    public static string FormatTimeout(int s)
    {
        if (s < 60) return $"{s}s";
        int m   = s / 60;
        int rem = s % 60;
        return rem > 0 ? $"{m}m {rem}s" : $"{m}m";
    }
}
