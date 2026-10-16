using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ClaudeFleet;

/// <summary>
/// Draws the tray icon: a small orange spark on the left and a vertical <see cref="FleetFormat.MeterSteps"/>-step
/// CPU meter on the right. Everything is laid out on a 16-unit grid and scaled to the requested pixel size.
/// </summary>
internal static class TrayIconRenderer
{
    private static readonly Color Spark = ColorTranslator.FromHtml("#d97757");
    private static readonly Color Unlit = ColorTranslator.FromHtml("#4a4a4a");

    public static Bitmap Render(int level, int sizePx)
    {
        level = Math.Clamp(level, 0, FleetFormat.MeterSteps);
        var bmp = new Bitmap(sizePx, sizePx, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        float u = sizePx / 16f;

        // Spark: 4 strokes through the centre of the left 10x10 area.
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(Spark, Math.Max(1.5f, 2f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            float cx = 5.5f * u, cy = 8f * u, r = 4.5f * u, d = r * 0.7071f;
            g.DrawLine(pen, cx, cy - r, cx, cy + r);
            g.DrawLine(pen, cx - r, cy, cx + r, cy);
            g.DrawLine(pen, cx - d, cy - d, cx + d, cy + d);
            g.DrawLine(pen, cx - d, cy + d, cx + d, cy - d);
        }

        // Meter: 5 equal segments bottom-up in the right quarter, whole pixels so every segment has the same height.
        g.SmoothingMode = SmoothingMode.None;
        using var lit = new SolidBrush(ColorTranslator.FromHtml(FleetFormat.MeterColor(level)));
        using var off = new SolidBrush(Unlit);
        int gap = Math.Max(1, sizePx / 16);
        int seg = Math.Max(1, (sizePx - 1 - (FleetFormat.MeterSteps - 1) * gap) / FleetFormat.MeterSteps);
        int width = Math.Max(2, sizePx / 4);
        int x = sizePx - width;
        int bottom = sizePx - 1;
        for (int i = 0; i < FleetFormat.MeterSteps; i++)
        {
            int y = bottom - (i + 1) * seg - i * gap + 1;
            g.FillRectangle(i < level ? lit : off, new Rectangle(x, y, width, seg));
        }
        return bmp;
    }

    /// <summary>Pixel row inside meter segment <paramref name="index"/> (0 = bottom) at <paramref name="sizePx"/> (tests).</summary>
    public static (int X, int Y) SegmentCentre(int index, int sizePx)
    {
        int gap = Math.Max(1, sizePx / 16);
        int seg = Math.Max(1, (sizePx - 1 - (FleetFormat.MeterSteps - 1) * gap) / FleetFormat.MeterSteps);
        int width = Math.Max(2, sizePx / 4);
        int y = sizePx - 1 - (index + 1) * seg - index * gap + 1;
        return (sizePx - width / 2 - 1, y + seg / 2);
    }

    /// <summary>An owned <see cref="Icon"/> for the NotifyIcon; dispose the previous one after swapping.</summary>
    public static Icon CreateIcon(int level, int sizePx)
    {
        using var bmp = Render(level, sizePx);
        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    /// <summary>Every level at 16/24/32 px, enlarged 4x on a taskbar-grey strip, so the icon can be checked by eye.</summary>
    public static void RenderPreview(string path)
    {
        int[] sizes = [16, 24, 32];
        const int Zoom = 4, Pad = 8;
        int cell = 32 * Zoom + Pad;
        using var sheet = new Bitmap(Pad + (FleetFormat.MeterSteps + 1) * cell, Pad + sizes.Length * cell);
        using var g = Graphics.FromImage(sheet);
        g.Clear(ColorTranslator.FromHtml("#202020"));
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        for (int row = 0; row < sizes.Length; row++)
            for (int level = 0; level <= FleetFormat.MeterSteps; level++)
            {
                using var icon = Render(level, sizes[row]);
                g.DrawImage(icon, Pad + level * cell, Pad + row * cell, sizes[row] * Zoom, sizes[row] * Zoom);
            }
        sheet.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
}
