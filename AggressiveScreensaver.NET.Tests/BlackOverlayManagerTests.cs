using AggressiveScreensaver.Services;

namespace AggressiveScreensaver.NET.Tests;

public class BlackOverlayManagerTests
{
    [Fact]
    public void ShownDurationMs_WhenNotBlanked_ReturnsMaxValue()
    {
        var overlay = new BlackOverlayManager(() => { });
        Assert.Equal(long.MaxValue, overlay.ShownDurationMs);
    }
}
