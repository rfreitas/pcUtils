using AggressiveScreensaver.Parsing;

namespace AggressiveScreensaver.NET.Tests;

public class BlockerFocusMatcherTests
{
    [Fact]
    public void NoBlockers_ReturnsFalse()
    {
        Assert.False(BlockerFocusMatcher.IsBlockerFocused(Array.Empty<string>(), "vlc.exe"));
    }

    [Fact]
    public void NoForegroundWindow_ReturnsFalse()
    {
        Assert.False(BlockerFocusMatcher.IsBlockerFocused(new[] { "vlc.exe" }, null));
    }

    [Fact]
    public void ExactFilenameMatch_ReturnsTrue()
    {
        Assert.True(BlockerFocusMatcher.IsBlockerFocused(new[] { "vlc.exe" }, "vlc.exe"));
    }

    [Fact]
    public void CaseInsensitiveMatch_ReturnsTrue()
    {
        Assert.True(BlockerFocusMatcher.IsBlockerFocused(new[] { "VLC.EXE" }, "vlc.exe"));
    }

    [Fact]
    public void ExtensionlessBlockerFilename_MatchesByNameWithoutExtension()
    {
        // powercfg strips ".exe" for DRIVER-tagged entries.
        Assert.True(BlockerFocusMatcher.IsBlockerFocused(new[] { "vlc" }, "vlc.exe"));
    }

    [Fact]
    public void DifferentApp_ReturnsFalse()
    {
        Assert.False(BlockerFocusMatcher.IsBlockerFocused(new[] { "vlc.exe" }, "notepad.exe"));
    }

    [Fact]
    public void MatchesAnyBlockerInList()
    {
        Assert.True(BlockerFocusMatcher.IsBlockerFocused(new[] { "notepad.exe", "vlc.exe" }, "vlc.exe"));
    }
}
