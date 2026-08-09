using AggressiveScreensaver.Forms;
using Xunit;

namespace AggressiveScreensaver.NET.Tests;

/// <summary>
/// Captures TimeoutSliderWindow via RenderHelper.CaptureWpfWindow.
/// Run with: dotnet test --filter SliderRenderTest
/// </summary>
public class SliderRenderTest
{
    private static readonly int[] Steps = [15, 30, 60, 120, 180, 300, 600, 900, 1200, 1800];

    [Theory]
    [MemberData(nameof(RenderHelper.ScaleFactors), MemberType = typeof(RenderHelper))]
    public void RenderSlider_ShortTimeout_SavesToPng(float scale, string dpiLabel)
    {
        string path = RenderHelper.CaptureWpfWindow(
            () => new TimeoutSliderWindow(30, Steps, _ => { }),
            $"slider_render_short_{dpiLabel}",
            scaleFactor: scale);

        Assert.True(File.Exists(path));
        Console.WriteLine($"View: {path}");
    }

    [Theory]
    [MemberData(nameof(RenderHelper.ScaleFactors), MemberType = typeof(RenderHelper))]
    public void RenderSlider_LongTimeout_SavesToPng(float scale, string dpiLabel)
    {
        string path = RenderHelper.CaptureWpfWindow(
            () => new TimeoutSliderWindow(1800, Steps, _ => { }),
            $"slider_render_long_{dpiLabel}",
            scaleFactor: scale);

        Assert.True(File.Exists(path));
        Console.WriteLine($"View: {path}");
    }
}
