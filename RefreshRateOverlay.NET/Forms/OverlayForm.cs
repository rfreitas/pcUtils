using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace RefreshRateOverlay.Forms;

internal sealed class OverlayForm : Form
{
    // Thin border without caption: WS_BORDER only, no WS_CAPTION
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style &= ~0x00C00000;
            cp.Style |=  0x00800000;
            return cp;
        }
    }

    private static readonly Color BgColor    = Color.FromArgb(0x1E, 0x1E, 0x1E);
    private static readonly Color TextWhite  = Color.White;
    private static readonly Color TextGray   = Color.FromArgb(0x88, 0x88, 0x88);
    private static readonly Color AccentBlue = Color.FromArgb(0x00, 0x78, 0xD4);

    private static Font Ui10 => new("Segoe UI", 10f);
    private static Font Ui9  => new("Segoe UI",  9f);

    private readonly ComboBox  _rateDropDown;
    private readonly CheckBox? _hdrCheckBox;
    private readonly CheckBox  _saveCheckBox;

    public string ActiveApp   { get; }
    public int    SelectedRate =>
        int.TryParse((_rateDropDown.SelectedItem as string)?.Replace(" Hz", ""), out int r) ? r : 60;
    public bool SaveProfile   => _saveCheckBox.Checked;
    public bool HdrEnabled    => _hdrCheckBox?.Checked ?? false;
    public bool Applied       { get; private set; }

    public OverlayForm(
        string    activeApp,
        List<int> availableRates,
        int       currentRate,
        bool      hdrSupported,
        bool      hdrEnabled,
        bool      hasProfile)
    {
        ActiveApp = activeApp;

        Text                = string.Empty;
        BackColor           = BgColor;
        ForeColor           = TextWhite;
        Font                = Ui10;
        FormBorderStyle     = FormBorderStyle.None;
        TopMost             = true;
        ShowInTaskbar       = false;
        StartPosition       = FormStartPosition.CenterScreen;
        AutoSize            = true;
        AutoSizeMode        = AutoSizeMode.GrowAndShrink;
        // All pixel values below are 96-DPI design units; WinForms scales them at load time
        AutoScaleMode       = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);

        const int ctrlW  = 250;
        const int pad    = 12;
        const int btnGap = 10;
        const int btnW   = (ctrlW - btnGap) / 2;

        var outer = new Panel
        {
            BackColor    = BgColor,
            AutoSize     = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding      = new Padding(pad),
        };

        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents  = false,
            BackColor     = BgColor,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            Margin        = Padding.Empty,
            Padding       = Padding.Empty,
        };

        flow.Controls.Add(MakeLabel("Refresh Rate Overlay", Ui10, TextWhite, ctrlW));
        flow.Controls.Add(MakeSpacer(ctrlW, 4));
        flow.Controls.Add(MakeLabel($"Active: {activeApp}", Ui9, TextGray, ctrlW));
        flow.Controls.Add(MakeSpacer(ctrlW, 8));

        _rateDropDown = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width         = ctrlW,
            Font          = Ui10,
            BackColor     = Color.White,
            Margin        = new Padding(0, 0, 0, 6),
        };
        int preSelect = 0;
        for (int i = 0; i < availableRates.Count; i++)
        {
            _rateDropDown.Items.Add($"{availableRates[i]} Hz");
            if (availableRates[i] == currentRate) preSelect = i;
        }
        _rateDropDown.SelectedIndex = preSelect;
        flow.Controls.Add(_rateDropDown);

        if (hdrSupported)
        {
            _hdrCheckBox = MakeCheckBox("Enable HDR", hdrEnabled, ctrlW);
            flow.Controls.Add(_hdrCheckBox);
        }

        _saveCheckBox = MakeCheckBox($"Save for {activeApp}", hasProfile, ctrlW);
        flow.Controls.Add(_saveCheckBox);

        flow.Controls.Add(MakeSpacer(ctrlW, 8));

        var applyBtn  = MakeButton("Apply",  btnW, new Padding(0, 0, btnGap, 0));
        var cancelBtn = MakeButton("Cancel", btnW, Padding.Empty);

        var btnRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents  = false,
            BackColor     = BgColor,
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            Margin        = Padding.Empty,
            Padding       = Padding.Empty,
        };
        btnRow.Controls.Add(applyBtn);
        btnRow.Controls.Add(cancelBtn);
        flow.Controls.Add(btnRow);

        outer.Controls.Add(flow);
        Controls.Add(outer);

        applyBtn.Click  += (_, _) => { Applied = true;  Close(); };
        cancelBtn.Click += (_, _) => { Applied = false; Close(); };
        AcceptButton = applyBtn;
        CancelButton = cancelBtn;
    }

    // AutoSize = true + MinimumSize/MaximumSize keeps the label exactly ctrlW wide
    // while letting height grow with the font — no font.Height arithmetic needed.
    private static Label MakeLabel(string text, Font font, Color fore, int width) =>
        new()
        {
            Text        = text,
            Font        = font,
            ForeColor   = fore,
            BackColor   = BgColor,
            AutoSize    = true,
            MinimumSize = new Size(width, 0),
            MaximumSize = new Size(width, 0),
            TextAlign   = ContentAlignment.MiddleCenter,
            Margin      = Padding.Empty,
        };

    private static Panel MakeSpacer(int width, int height) =>
        new() { Width = width, Height = height, BackColor = BgColor, Margin = Padding.Empty };

    private static CheckBox MakeCheckBox(string text, bool isChecked, int width) =>
        new()
        {
            Text        = text,
            Checked     = isChecked,
            ForeColor   = TextWhite,
            BackColor   = BgColor,
            AutoSize    = true,
            MinimumSize = new Size(width, 0), // forces full-width; height auto-fits the font
            FlatStyle   = FlatStyle.Standard,
            Margin      = new Padding(0, 0, 0, 6),
        };

    private static Button MakeButton(string text, int width, Padding margin)
    {
        var btn = new Button
        {
            Text      = text,
            Width     = width,
            Height    = 30,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Color.Black,
            Font      = Ui10,
            Margin    = margin,
        };
        btn.FlatAppearance.BorderColor = AccentBlue;
        btn.FlatAppearance.BorderSize  = 1;
        return btn;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(0x55, 0x55, 0x55));
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}
