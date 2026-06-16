using System.Drawing;
using RefreshRateOverlay.NET.Tests;

namespace RefreshRateOverlay.NET.Tests;

/// <summary>
/// Ports test_icon.ahk and adds .NET-specific cases.
/// </summary>
public class TrayIconRendererTests
{
    // ---- ports of test_icon.ahk ---------------------------------------------

    [Fact]
    public void CreateTextIcon_ThreeDigits_ReturnsValidIcon()
    {
        using var icon = TrayIconRenderer.CreateTextIcon("144");
        Assert.NotNull(icon);
        Assert.NotEqual(IntPtr.Zero, icon.Handle);
    }

    [Fact]
    public void CreateTextIcon_TwoDigits_ReturnsValidIcon()
    {
        using var icon = TrayIconRenderer.CreateTextIcon("60");
        Assert.NotNull(icon);
        Assert.NotEqual(IntPtr.Zero, icon.Handle);
    }

    [Theory]
    [InlineData("144", 40, 40)]
    public void SolveFontToFit_ThreeDigits_FitsWithin40pxContainer(string text, int w, int h)
    {
        using var font = TrayIconRenderer.SolveFontToFit(text, w, h);
        using var tmp  = new Bitmap(1, 1);
        using var g    = Graphics.FromImage(tmp);
        float measuredW = g.MeasureString(text, font).Width;
        Assert.True(measuredW <= w - 1, $"Width {measuredW} should fit in {w - 1}");
    }

    [Theory]
    [InlineData("60", 16, 16)]
    public void SolveFontToFit_TwoDigits_FitsWithin16pxContainer(string text, int w, int h)
    {
        using var font = TrayIconRenderer.SolveFontToFit(text, w, h);
        using var tmp  = new Bitmap(1, 1);
        using var g    = Graphics.FromImage(tmp);
        float measuredW = g.MeasureString(text, font).Width;
        Assert.True(measuredW <= w - 1, $"Width {measuredW} should fit in {w - 1}");
    }

    [Theory]
    [InlineData("1000", 24, 24)]
    public void SolveFontToFit_FourDigits_FitsWithin24pxContainer(string text, int w, int h)
    {
        using var font = TrayIconRenderer.SolveFontToFit(text, w, h);
        using var tmp  = new Bitmap(1, 1);
        using var g    = Graphics.FromImage(tmp);
        float measuredW = g.MeasureString(text, font).Width;
        Assert.True(measuredW <= w - 1, $"Width {measuredW} should fit in {w - 1}");
    }

    // ---- new .NET-specific tests --------------------------------------------

    [Fact]
    public void CreateTextIcon_ProducesCorrectIconSize()
    {
        using var icon = TrayIconRenderer.CreateTextIcon("60");
        // Icon size should be reasonable (between 8 and 64 px)
        Assert.InRange(icon.Width, 8, 64);
        Assert.InRange(icon.Height, 8, 64);
    }

    [Fact]
    public void CreateTextIcon_HasNonZeroAlpha()
    {
        // The rendered icon should have some opaque pixels (the text glyphs)
        using var icon = TrayIconRenderer.CreateTextIcon("60");
        using var bmp  = icon.ToBitmap();
        bool foundOpaque = false;
        for (int x = 0; x < bmp.Width && !foundOpaque; x++)
        for (int y = 0; y < bmp.Height && !foundOpaque; y++)
            if (bmp.GetPixel(x, y).A > 0)
                foundOpaque = true;
        Assert.True(foundOpaque, "Icon should contain at least one opaque pixel");
    }

    [Theory]
    [InlineData("60")]
    [InlineData("144")]
    [InlineData("240")]
    [InlineData("120")]
    public void CreateTextIcon_CommonRates_AllSucceed(string rate)
    {
        using var icon = TrayIconRenderer.CreateTextIcon(rate);
        Assert.NotEqual(IntPtr.Zero, icon.Handle);
    }

    [Fact]
    public void SolveFontToFit_SmallContainer_ReturnsMinimumFontSize()
    {
        // Should not throw even for extremely small containers
        using var font = TrayIconRenderer.SolveFontToFit("144", 4, 4);
        Assert.NotNull(font);
        Assert.True(font.Size >= 1f);
    }
}
