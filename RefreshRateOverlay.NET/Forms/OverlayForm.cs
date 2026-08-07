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

        const int ctrlW  = 280;
        const int pad    = 14;
        const int rowGap = 8;
        const int btnGap = 8;

        var grid = new TableLayoutPanel
        {
            BackColor    = BgColor,
            AutoSize     = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount  = 1,
            GrowStyle    = TableLayoutPanelGrowStyle.AddRows,
            Padding      = new Padding(pad),
            Margin       = Padding.Empty,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ctrlW));

        void AddRow(Control c)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(c, 0, grid.RowCount++);
        }

        AddRow(MakeLabel("Refresh Rate Overlay", Ui10, TextWhite, ctrlW));

        var subtitle = MakeLabel($"Active: {activeApp}", Ui9, TextGray, ctrlW);
        subtitle.Margin = new Padding(0, 4, 0, 0);
        AddRow(subtitle);

        _rateDropDown = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width         = ctrlW,
            Font          = Ui10,
            BackColor     = Color.White,
            Margin        = new Padding(0, rowGap, 0, 0),
        };
        int preSelect = 0;
        for (int i = 0; i < availableRates.Count; i++)
        {
            _rateDropDown.Items.Add($"{availableRates[i]} Hz");
            if (availableRates[i] == currentRate) preSelect = i;
        }
        _rateDropDown.SelectedIndex = preSelect;
        AddRow(_rateDropDown);

        if (hdrSupported)
        {
            _hdrCheckBox = MakeCheckBox("Enable HDR", hdrEnabled, ctrlW);
            _hdrCheckBox.Margin = new Padding(0, rowGap, 0, 0);
            AddRow(_hdrCheckBox);
        }

        _saveCheckBox = MakeCheckBox($"Save for {activeApp}", hasProfile, ctrlW);
        _saveCheckBox.Margin = new Padding(0, hdrSupported ? 4 : rowGap, 0, 0);
        AddRow(_saveCheckBox);

        int btnW = (ctrlW - btnGap) / 2;
        var applyBtn  = MakeButton("Apply",  btnW, new Padding(0, 0, btnGap, 0));
        var cancelBtn = MakeButton("Cancel", btnW, Padding.Empty);

        var btnRow = new TableLayoutPanel
        {
            ColumnCount  = 2,
            RowCount     = 1,
            BackColor    = BgColor,
            AutoSize     = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin       = new Padding(0, rowGap, 0, 0),
        };
        btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, btnW + btnGap));
        btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, btnW));
        btnRow.Controls.Add(applyBtn,  0, 0);
        btnRow.Controls.Add(cancelBtn, 1, 0);
        AddRow(btnRow);

        Controls.Add(grid);

        applyBtn.Click  += (_, _) => { Applied = true;  Close(); };
        cancelBtn.Click += (_, _) => { Applied = false; Close(); };
        AcceptButton = applyBtn;
        CancelButton = cancelBtn;
    }

    // Size is computed up front from measured, word-wrapped text rather than left to
    // AutoSize + MaximumSize: nested AutoSize containers (TableLayoutPanel wrapping a
    // wrapped-text Label) can finalize their preferred size before the wrap height
    // settles, which clips content. Explicit sizing sidesteps that timing bug entirely.
    private static Label MakeLabel(string text, Font font, Color fore, int width)
    {
        int height = TextRenderer.MeasureText(
            text, font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height;
        return new Label
        {
            Text      = text,
            Font      = font,
            ForeColor = fore,
            BackColor = BgColor,
            AutoSize  = false,
            Size      = new Size(width, height),
            TextAlign = ContentAlignment.MiddleCenter,
            Margin    = Padding.Empty,
        };
    }

    private const int CheckGlyphW = 24; // checkbox tick + spacing eaten from the text area

    private static CheckBox MakeCheckBox(string text, bool isChecked, int width)
    {
        int height = TextRenderer.MeasureText(
            text, Ui10, new Size(width - CheckGlyphW, int.MaxValue), TextFormatFlags.WordBreak).Height;
        return new CheckBox
        {
            Text      = text,
            Checked   = isChecked,
            Font      = Ui10,
            ForeColor = TextWhite,
            BackColor = BgColor,
            AutoSize  = false,
            Size      = new Size(width, Math.Max(height, 20) + 4),
            FlatStyle = FlatStyle.Standard,
            Margin    = Padding.Empty,
        };
    }

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
