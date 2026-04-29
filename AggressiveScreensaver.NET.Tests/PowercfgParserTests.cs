using AggressiveScreensaver.Parsing;

namespace AggressiveScreensaver.NET.Tests;

/// <summary>
/// Ports test_parsing.ahk cases 1-6 (parser) and adds BlockingFormatter coverage.
/// </summary>
public class PowercfgParserTests
{
    // -------------------------------------------------------------------------
    // Section 1: Happy Path
    // -------------------------------------------------------------------------
    private const string MockOutput1 = """
        DISPLAY:
        [PROCESS] \Device\HarddiskVolume3\Program Files\VLC\vlc.exe
        [PROCESS] \Device\HarddiskVolume3\Windows\System32\DisplayApp.exe

        SYSTEM:
        [DRIVER] Realtek Audio (HDAUDIO\FUNC_01)

        AWAYMODE:
        None.
        """;

    [Fact] public void HappyPath_FoundTwoDisplayApps()   => Assert.Equal(2, PowercfgParser.Parse(MockOutput1).Screen.Count);
    [Fact] public void HappyPath_FoundOneSystemApp()     => Assert.Equal(1, PowercfgParser.Parse(MockOutput1).Sleep.Count);
    [Fact] public void HappyPath_FirstAppIsVlc()         => Assert.Equal("vlc.exe", PowercfgParser.Parse(MockOutput1).Screen[0].Filename);
    [Fact] public void HappyPath_SecondAppIsDisplayApp() => Assert.Equal("DisplayApp.exe", PowercfgParser.Parse(MockOutput1).Screen[1].Filename);
    [Fact] public void HappyPath_SleepAppContainsRealtek()
    {
        var result = PowercfgParser.Parse(MockOutput1).Sleep[0];
        Assert.True(result.Filename.Contains("Realtek") || result.Text.Contains("Realtek"));
    }

    // -------------------------------------------------------------------------
    // Section 2: Empty sections
    // -------------------------------------------------------------------------
    private const string MockOutput2 = """
        DISPLAY:
        None.

        SYSTEM:
        None.

        AWAYMODE:
        None.
        """;

    [Fact] public void EmptySections_ZeroDisplayApps() => Assert.Equal(0, PowercfgParser.Parse(MockOutput2).Screen.Count);
    [Fact] public void EmptySections_ZeroSleepApps()   => Assert.Equal(0, PowercfgParser.Parse(MockOutput2).Sleep.Count);

    // -------------------------------------------------------------------------
    // Section 3: Indented lines (reason: sub-entries)
    // -------------------------------------------------------------------------
    private const string MockOutput3 = """
        DISPLAY:
        [PROCESS] C:\Program Files\VLC\vlc.exe
          Display is required by:
          Something something
        [PROCESS] C:\Windows\System32\notepad.exe

        SYSTEM:
        None.

        AWAYMODE:
        None.
        """;

    [Fact] public void IndentedLines_FoundTwoDisplayApps()        => Assert.Equal(2, PowercfgParser.Parse(MockOutput3).Screen.Count);
    [Fact] public void IndentedLines_FirstAppIsVlc()              => Assert.Equal("vlc.exe", PowercfgParser.Parse(MockOutput3).Screen[0].Filename);
    [Fact] public void IndentedLines_SecondAppIsNotepad()         => Assert.Equal("notepad.exe", PowercfgParser.Parse(MockOutput3).Screen[1].Filename);

    // -------------------------------------------------------------------------
    // Section 4: DRIVER tags
    // -------------------------------------------------------------------------
    private const string MockOutput4 = """
        DISPLAY:
        [DRIVER] NVIDIA Graphics (PCI\VEN_10DE)

        SYSTEM:
        [DRIVER] Realtek Audio (HDAUDIO\FUNC_01)
        [DRIVER] Windows Search (WSearchTrigger)

        AWAYMODE:
        None.
        """;

    [Fact] public void Drivers_OneDisplayDriver()       => Assert.Equal(1, PowercfgParser.Parse(MockOutput4).Screen.Count);
    [Fact] public void Drivers_TextContainsDriverTag()  => Assert.Contains("[DRIVER]", PowercfgParser.Parse(MockOutput4).Screen[0].Text);
    [Fact] public void Drivers_TwoSleepDrivers()        => Assert.Equal(2, PowercfgParser.Parse(MockOutput4).Sleep.Count);

    // -------------------------------------------------------------------------
    // Section 5: Path extraction
    // -------------------------------------------------------------------------
    private const string MockOutput5 = """
        DISPLAY:
        [PROCESS] C:\Program Files (x86)\Some App\app.exe
        [PROCESS] \Device\HarddiskVolume3\Deep\Nested\Path\file.exe

        SYSTEM:
        None.

        AWAYMODE:
        None.
        """;

    [Fact] public void PathExtraction_AppWithParenthesesInDir()   => Assert.Equal("app.exe", PowercfgParser.Parse(MockOutput5).Screen[0].Filename);
    [Fact] public void PathExtraction_DevicePathFile()            => Assert.Equal("file.exe", PowercfgParser.Parse(MockOutput5).Screen[1].Filename);

    // -------------------------------------------------------------------------
    // Section 6: Long filename truncation
    // -------------------------------------------------------------------------
    private const string MockOutput6 = """
        DISPLAY:
        [PROCESS] C:\Program Files\SomeVeryLongApplicationName.exe

        SYSTEM:
        None.

        AWAYMODE:
        None.
        """;

    [Fact] public void LongFilename_EntryFound()
    {
        var result = PowercfgParser.Parse(MockOutput6);
        Assert.Single(result.Screen);
    }

    [Fact] public void LongFilename_TextContainsEllipsis()
    {
        var text = PowercfgParser.Parse(MockOutput6).Screen[0].Text;
        Assert.Contains("...", text);
    }

    [Fact] public void LongFilename_DisplayPartMaxLength()
    {
        // display part is at most 15 chars (12 + "..."), tag is appended separately
        var text = PowercfgParser.Parse(MockOutput6).Screen[0].Text;
        // text = "SomeVeryLong... [PROCESS]"
        int tagIdx = text.LastIndexOf('[');
        string displayPart = text[..tagIdx].Trim();
        Assert.True(displayPart.Length <= 16); // "SomeVeryLong..." = 15 chars + space
    }

    // -------------------------------------------------------------------------
    // AWAYMODE counted as Sleep
    // -------------------------------------------------------------------------
    private const string MockOutputAway = """
        DISPLAY:
        None.

        SYSTEM:
        None.

        AWAYMODE:
        [PROCESS] C:\Windows\System32\svchost.exe
        """;

    [Fact] public void AwayMode_CountedAsSleep() => Assert.Equal(1, PowercfgParser.Parse(MockOutputAway).Sleep.Count);
}
