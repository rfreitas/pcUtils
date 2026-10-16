using System.Drawing;
using ClaudeFleet;
using Xunit;

public class TrayIconRendererTests
{
    /// <summary>Counts lit meter segments by sampling the centre of each segment.</summary>
    private static int LitSegments(Bitmap bmp)
    {
        var unlit = ColorTranslator.FromHtml("#4a4a4a");
        int lit = 0;
        for (int i = 0; i < FleetFormat.MeterSteps; i++)
        {
            var (x, y) = TrayIconRenderer.SegmentCentre(i, bmp.Width);
            var c = bmp.GetPixel(x, y);
            if (c.A > 0 && c.ToArgb() != unlit.ToArgb()) lit++;
        }
        return lit;
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void Meter_lights_one_segment_per_level(int size)
    {
        for (int level = 0; level <= FleetFormat.MeterSteps; level++)
        {
            using var bmp = TrayIconRenderer.Render(level, size);
            Assert.Equal(level, LitSegments(bmp));
        }
    }

    [Fact]
    public void CreateIcon_returns_an_icon_of_the_requested_size()
    {
        using var icon = TrayIconRenderer.CreateIcon(3, 16);
        Assert.Equal(16, icon.Width);
    }
}
