using System.Drawing;
using System.Windows.Forms;

namespace RefreshRateOverlay.Forms;

internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color BgColor       = Color.FromArgb(0x2D, 0x2D, 0x2D);
    private static readonly Color HoverColor    = Color.FromArgb(0x44, 0x44, 0x44);
    private static readonly Color TextColor     = Color.FromArgb(0xCC, 0xCC, 0xCC);
    private static readonly Color DisabledColor = Color.FromArgb(0x77, 0x77, 0x77);
    private static readonly Color SepColor      = Color.FromArgb(0x55, 0x55, 0x55);

    public DarkMenuRenderer() : base(new DarkColorTable()) { }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(BgColor);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var color = (e.Item.Selected && e.Item.Enabled) ? HoverColor : BgColor;
        using var brush = new SolidBrush(color);
        e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? TextColor : DisabledColor;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Pen(SepColor);
        e.Graphics.DrawLine(pen, 30, y, e.Item.Width - 4, y);
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        private static readonly Color Bg     = Color.FromArgb(0x2D, 0x2D, 0x2D);
        private static readonly Color Border = Color.FromArgb(0x55, 0x55, 0x55);

        public override Color MenuBorder                    => Border;
        public override Color MenuItemBorder                => Color.Transparent;
        public override Color MenuItemSelected              => Color.FromArgb(0x44, 0x44, 0x44);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(0x44, 0x44, 0x44);
        public override Color MenuItemSelectedGradientEnd   => Color.FromArgb(0x44, 0x44, 0x44);
        public override Color MenuItemPressedGradientBegin  => Color.FromArgb(0x44, 0x44, 0x44);
        public override Color MenuItemPressedGradientEnd    => Color.FromArgb(0x44, 0x44, 0x44);
        public override Color ToolStripDropDownBackground   => Bg;
        public override Color ImageMarginGradientBegin      => Bg;
        public override Color ImageMarginGradientMiddle     => Bg;
        public override Color ImageMarginGradientEnd        => Bg;
    }
}
