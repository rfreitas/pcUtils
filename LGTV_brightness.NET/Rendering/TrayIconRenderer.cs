using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LgtvBrightness.Rendering;

/// <summary>
/// Renders a vertical backlight-level bar (0-100) as a tray icon: white outline
/// and fill on a transparent background. Replaces the AHK version's hand-rolled
/// per-value .ico file cache with an in-memory GDI+ render on each change.
/// </summary>
internal static class TrayIconRenderer
{
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

    private const int SM_CXSMICON = 49;
    private const int SM_CYSMICON = 50;

    public static Icon CreateBarIcon(int value)
    {
        int w = GetSystemMetrics(SM_CXSMICON);
        int h = GetSystemMetrics(SM_CYSMICON);
        if (w <= 0) w = 16;
        if (h <= 0) h = 16;

        value = Math.Clamp(value, 0, 100);

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.None;

            float margin = w * 0.12f;
            float top    = h * 0.06f;
            float bottom = h * 0.94f;
            var outline  = new RectangleF(margin, top, w - 2 * margin, bottom - top);

            using (var outlinePen = new Pen(Color.FromArgb(220, 220, 220), 1f))
                g.DrawRectangle(outlinePen, outline.X, outline.Y, outline.Width, outline.Height);

            if (value > 0)
            {
                float fillH = outline.Height * (value / 100f);
                var fillRect = new RectangleF(outline.X + 1, outline.Bottom - fillH, outline.Width - 2, fillH - 1);
                if (fillRect.Height > 0)
                {
                    using var fillBrush = new SolidBrush(Color.White);
                    g.FillRectangle(fillBrush, fillRect);
                }
            }
        }

        IntPtr hIcon = bmp.GetHicon();
        // Icon.FromHandle wraps (doesn't own) the HICON — clone to control lifetime,
        // then destroy the raw handle.
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }
}
