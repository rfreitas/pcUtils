using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace AggressiveScreensaver.Forms;

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
    private readonly TrackBar   _track;

    private const int GuiW  = 45;
    private const int GuiH  = 210;
    private const int LabelH = 26;

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

        // ---------- TrackBar ----------
        // Raw 1 (top) = longest timeout step; Raw 10 (bottom) = shortest.
        // Mirrors AHK inversion: FindStepIndex uses (11 - step) so bottom=short.
        _track = new TrackBar
        {
            Orientation   = Orientation.Vertical,
            Minimum       = 1,
            Maximum       = _steps.Length,
            TickFrequency = 1,
            LargeChange   = 1,
            SmallChange   = 1,
            TickStyle     = TickStyle.Both,
            BackColor     = Color.FromArgb(0x2D, 0x2D, 0x2D),
        };
        _track.ValueChanged += TrackValueChanged;
        _track.AutoSize = false;

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

        // Position above taskbar
        var workArea = Screen.GetWorkingArea(mouse);
        int x = mouse.X - w / 2;
        int y = workArea.Bottom - h;

        Location  = new Point(x, y);
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
