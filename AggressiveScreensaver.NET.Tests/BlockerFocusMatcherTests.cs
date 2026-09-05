using System;
using System.Collections.Generic;
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

    // -------------------------------------------------------------------------
    // IsBlockerVisible
    // -------------------------------------------------------------------------

    [Fact]
    public void Visible_NoBlockers_ReturnsFalse()
    {
        Assert.False(BlockerFocusMatcher.IsBlockerVisible(Array.Empty<string>(), new[] { "vlc.exe" }));
    }

    [Fact]
    public void Visible_NoVisibleWindows_ReturnsFalse()
    {
        Assert.False(BlockerFocusMatcher.IsBlockerVisible(new[] { "vlc.exe" }, Array.Empty<string?>()));
    }

    [Fact]
    public void Visible_MatchAmongMultipleVisibleWindows_ReturnsTrue()
    {
        Assert.True(BlockerFocusMatcher.IsBlockerVisible(new[] { "vlc.exe" }, new[] { "explorer.exe", "vlc.exe", "notepad.exe" }));
    }

    [Fact]
    public void Visible_NullEntriesInList_AreSkipped()
    {
        Assert.True(BlockerFocusMatcher.IsBlockerVisible(new[] { "vlc.exe" }, new string?[] { null, "vlc.exe" }));
    }

    [Fact]
    public void Visible_NotFocusedButVisible_ReturnsTrue()
    {
        // The whole point of this mode: a blocker not in front still counts as visible.
        Assert.True(BlockerFocusMatcher.IsBlockerVisible(new[] { "vlc.exe" }, new[] { "notepad.exe", "vlc.exe" }));
    }

    [Fact]
    public void Visible_NoMatch_ReturnsFalse()
    {
        Assert.False(BlockerFocusMatcher.IsBlockerVisible(new[] { "vlc.exe" }, new[] { "notepad.exe", "explorer.exe" }));
    }

    // -------------------------------------------------------------------------
    // ExcludeIgnored
    // -------------------------------------------------------------------------

    private static Dictionary<string, bool> IgnoredApps(params string[] names)
    {
        var dict = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
            dict[name] = true;
        return dict;
    }

    [Fact]
    public void ExcludeIgnored_NoIgnoredApps_ReturnsAllBlockers()
    {
        var result = BlockerFocusMatcher.ExcludeIgnored(new[] { "vlc.exe", "spotify.exe" }, IgnoredApps());
        Assert.Equal(new[] { "vlc.exe", "spotify.exe" }, result);
    }

    [Fact]
    public void ExcludeIgnored_RemovesIgnoredFilename()
    {
        var result = BlockerFocusMatcher.ExcludeIgnored(new[] { "vlc.exe", "spotify.exe" }, IgnoredApps("spotify.exe"));
        Assert.Equal(new[] { "vlc.exe" }, result);
    }

    [Fact]
    public void ExcludeIgnored_CaseInsensitiveMatch_Removes()
    {
        var result = BlockerFocusMatcher.ExcludeIgnored(new[] { "SPOTIFY.EXE" }, IgnoredApps("spotify.exe"));
        Assert.Empty(result);
    }

    [Fact]
    public void ExcludeIgnored_AllBlockersIgnored_ReturnsEmpty()
    {
        var result = BlockerFocusMatcher.ExcludeIgnored(new[] { "vlc.exe" }, IgnoredApps("vlc.exe"));
        Assert.Empty(result);
    }

    [Fact]
    public void ExcludeIgnored_UnrelatedIgnoredApp_DoesNotAffectOthers()
    {
        // An app on the Ignore List that isn't currently blocking shouldn't affect the result.
        var result = BlockerFocusMatcher.ExcludeIgnored(new[] { "vlc.exe" }, IgnoredApps("notepad.exe"));
        Assert.Equal(new[] { "vlc.exe" }, result);
    }

    [Fact]
    public void ExcludeIgnored_NoExtensionNormalization_DoesNotMatch()
    {
        // Unlike IsBlockerFocused/IsBlockerVisible, this is an exact match only —
        // both sides come from the same powercfg identity filenames.
        var result = BlockerFocusMatcher.ExcludeIgnored(new[] { "vlc.exe" }, IgnoredApps("vlc"));
        Assert.Equal(new[] { "vlc.exe" }, result);
    }
}
