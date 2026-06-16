using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RefreshRateOverlay.Rendering;

/// <summary>
/// Renders a refresh-rate number (e.g. "60", "144") as a tray icon.
/// Mirrors the AHK TrayIconRenderer.ahk: white text, transparent background,
/// alpha = luminance of each pixel so white glyphs are opaque and the black
/// background becomes fully transparent.
/// </summary>
internal static class TrayIconRenderer
{
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

    private const int SM_CXSMICON = 49;
    private const int SM_CYSMICON = 50;

    private static readonly string[] FontFamilies =
        ["Segoe UI Variable Text", "Segoe UI", "Arial"];

    /// <summary>
    /// Creates an Icon with the given text rendered as white on transparent.
    /// The caller owns the returned Icon and must dispose it.
    /// </summary>
    public static Icon CreateTextIcon(string text)
    {
        int w = GetSystemMetrics(SM_CXSMICON);
        int h = GetSystemMetrics(SM_CYSMICON);
        if (w <= 0) w = 16;
        if (h <= 0) h = 16;

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            using var font  = SolveFontToFit(text, w, h);
            using var brush = new SolidBrush(Color.White);
            var bounds = new RectangleF(0, 0, w, h);
            using var sf = new StringFormat
            {
                Alignment     = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString(text, font, brush, bounds, sf);
        }

        // Set alpha = red channel so white glyphs are opaque, black → transparent
        ApplyLuminanceAlpha(bmp);

        IntPtr hIcon = bmp.GetHicon();
        // Icon.FromHandle creates an Icon that wraps (but doesn't own) the HICON.
        // Clone it so we control lifetime, then destroy the raw handle.
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    /// <summary>
    /// Finds the largest bold font size where <paramref name="text"/> fits within
    /// <paramref name="targetWidth"/> pixels. Mirrors AHK SolveFontToFit.
    /// </summary>
    internal static Font SolveFontToFit(string text, int targetWidth, int targetHeight)
    {
        string family = ResolveFontFamily();
        float size = targetHeight * 0.90f;

        while (size >= 4f)
        {
            var font = new Font(family, size, FontStyle.Bold, GraphicsUnit.Pixel);
            using var tmp = new Bitmap(1, 1);
            using var g = Graphics.FromImage(tmp);
            SizeF measured = g.MeasureString(text, font);
            if (measured.Width <= targetWidth - 1)
                return font;
            font.Dispose();
            size -= 1f;
        }

        return new Font(family, 4f, FontStyle.Bold, GraphicsUnit.Pixel);
    }

    private static void ApplyLuminanceAlpha(Bitmap bmp)
    {
        var data = bmp.LockBits(
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadWrite,
            PixelFormat.Format32bppArgb);

        try
        {
            int bytes = Math.Abs(data.Stride) * bmp.Height;
            var buf = new byte[bytes];
            Marshal.Copy(data.Scan0, buf, 0, bytes);

            // ARGB in memory layout: B G R A
            for (int i = 0; i < bytes; i += 4)
                buf[i + 3] = buf[i + 2]; // alpha = red channel

            Marshal.Copy(buf, 0, data.Scan0, bytes);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static string? _resolvedFamily;
    private static string ResolveFontFamily()
    {
        if (_resolvedFamily is not null) return _resolvedFamily;
        using var tmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(tmp);
        foreach (var name in FontFamilies)
        {
            try
            {
                using var f = new Font(name, 10f);
                if (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    _resolvedFamily = name;
                    return _resolvedFamily;
                }
            }
            catch { }
        }
        _resolvedFamily = "Arial";
        return _resolvedFamily;
    }
}
