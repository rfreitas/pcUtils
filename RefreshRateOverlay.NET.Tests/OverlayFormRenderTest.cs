using System.Collections.Generic;
using RefreshRateOverlay.NET.Tests;

namespace RefreshRateOverlay.NET.Tests;

/// <summary>
/// Render tests for OverlayForm — captures PNGs for visual comparison against the AHK screenshot.
/// Run: dotnet test --filter OverlayFormRenderTest
/// Output: RefreshRateOverlay.NET.Tests/bin/.../overlay_*.png
/// </summary>
public class OverlayFormRenderTest
{
    private static readonly List<int> TestRates = [240, 144, 120, 60];

    [Theory]
    [MemberData(nameof(RenderHelper.ScaleFactors), MemberType = typeof(RenderHelper))]
    public void Render_WithHdr_NoProfile_SavesToPng(float scale, string dpiLabel)
    {
        string path = RenderHelper.CaptureForm(
            () => new OverlayForm(
                activeApp:      "Code.exe",
                availableRates: TestRates,
                currentRate:    60,
                hdrSupported:   true,
                hdrEnabled:     false,
                hasProfile:     false),
            $"overlay_hdr_{dpiLabel}",
            scaleFactor: scale);

        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 0);
        Console.WriteLine($"View: {path}");
    }

    [Theory]
    [MemberData(nameof(RenderHelper.ScaleFactors), MemberType = typeof(RenderHelper))]
    public void Render_NoHdr_WithProfile_SavesToPng(float scale, string dpiLabel)
    {
        string path = RenderHelper.CaptureForm(
            () => new OverlayForm(
                activeApp:      "Cairn.exe",
                availableRates: TestRates,
                currentRate:    144,
                hdrSupported:   false,
                hdrEnabled:     false,
                hasProfile:     true),
            $"overlay_profile_{dpiLabel}",
            scaleFactor: scale);

        Assert.True(File.Exists(path));
        Console.WriteLine($"View: {path}");
    }

    [Theory]
    [MemberData(nameof(RenderHelper.ScaleFactors), MemberType = typeof(RenderHelper))]
    public void Render_HdrEnabled_WithProfile_SavesToPng(float scale, string dpiLabel)
    {
        string path = RenderHelper.CaptureForm(
            () => new OverlayForm(
                activeApp:      "game.exe",
                availableRates: TestRates,
                currentRate:    240,
                hdrSupported:   true,
                hdrEnabled:     true,
                hasProfile:     true),
            $"overlay_hdr_on_profile_{dpiLabel}",
            scaleFactor: scale);

        Assert.True(File.Exists(path));
        Console.WriteLine($"View: {path}");
    }
}
