using RefreshRateOverlay.WPF.Services;

namespace RefreshRateOverlay.WPF.Tests;

/// <summary>
/// Covers OverlayWindow's DescribeFrameCap/ParseFrameCapText — the display
/// and parse halves of the Frame Cap text box (a plain numeric field, not a
/// dropdown — see OverlayWindow.xaml's FrameCapRow comment). The app's own
/// writable range is 0-FrameCapMaxFps (1000), narrower than FRL_FPS's real
/// driver range (0-1023, see NvidiaGsyncService.FrlFpsId remarks).
/// </summary>
public class FrameCapParsingTests
{
    [Theory]
    [InlineData(NvidiaGsyncService.FrameCapNotSet, "Not Set")]
    [InlineData(0u, "0")]
    [InlineData(60u, "60")]
    [InlineData(1000u, "1000")]
    public void DescribeFrameCap_FormatsEachCase(uint capFps, string expected) =>
        Assert.Equal(expected, OverlayWindow.DescribeFrameCap(capFps));

    [Theory]
    [InlineData("Not Set", NvidiaGsyncService.FrameCapNotSet)]
    [InlineData("not set", NvidiaGsyncService.FrameCapNotSet)]
    [InlineData("0", 0u)]
    [InlineData("60", 60u)]
    [InlineData(" 144  ", 144u)]
    [InlineData("1000", 1000u)]
    public void ParseFrameCapText_ParsesEachCase(string text, uint expected) =>
        Assert.Equal(expected, OverlayWindow.ParseFrameCapText(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a number")]
    [InlineData("1001")]  // past the app's own 0-1000 writable range
    [InlineData("60 FPS")] // no unit suffix accepted — plain numbers only
    [InlineData("-1")]
    public void ParseFrameCapText_UnparseableOrOutOfRange_FallsBackToNotSet(string? text) =>
        Assert.Equal(NvidiaGsyncService.FrameCapNotSet, OverlayWindow.ParseFrameCapText(text));

    [Theory]
    [InlineData(NvidiaGsyncService.FrameCapNotSet)]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(60u)]
    [InlineData(1000u)]
    public void RoundTrip_DescribeThenParse_ReturnsOriginalValue(uint capFps) =>
        Assert.Equal(capFps, OverlayWindow.ParseFrameCapText(OverlayWindow.DescribeFrameCap(capFps)));
}
