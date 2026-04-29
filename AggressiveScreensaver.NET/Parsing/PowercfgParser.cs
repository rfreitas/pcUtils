using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AggressiveScreensaver.Parsing;

/// <summary>
/// One blocking app entry.
/// </summary>
/// <param name="Text">Display string, e.g. "vlc [PROCESS]"</param>
/// <param name="Filename">Identity key, e.g. "vlc.exe"</param>
public record AppEntry(string Text, string Filename);

/// <summary>
/// Result of parsing powercfg /requests output.
/// </summary>
public record ParseResult(
    IReadOnlyList<AppEntry> Screen,
    IReadOnlyList<AppEntry> Sleep);

/// <summary>
/// Pure, stateless parser for `powercfg /requests` output.
/// Ported line-for-line from AggressiveScreensaver/index.ahk ParsePowercfgOutput().
/// </summary>
public static class PowercfgParser
{
    // Matches section headers: must be at col 0, all-caps, followed by ':'
    private static readonly Regex SectionRx = new(@"^([A-Z]+):$", RegexOptions.Compiled);
    // Matches entry lines:  [TAG] payload
    private static readonly Regex EntryRx = new(@"^\[([^\]]+)\]\s*(.*)$", RegexOptions.Compiled);

    public static ParseResult Parse(string output)
    {
        var screenApps = new List<AppEntry>();
        var sleepApps = new List<AppEntry>();
        string currentSection = "";

        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            // Detect section header (must be at column 0)
            var sectionMatch = SectionRx.Match(line);
            if (sectionMatch.Success)
            {
                currentSection = sectionMatch.Groups[1].Value;
                continue;
            }

            line = line.Trim();
            if (line.Length == 0 || line == "None.")
                continue;

            // Parse entries starting with '['
            if (line[0] == '[')
            {
                var entryMatch = EntryRx.Match(line);
                if (!entryMatch.Success)
                    continue;

                string tag = entryMatch.Groups[1].Value;
                string rest = entryMatch.Groups[2].Value.Trim();

                string entry = rest;
                string filename = rest;

                if (tag == "DRIVER")
                {
                    // Extract the name before the '(' parenthesis
                    int paren = rest.IndexOf('(');
                    if (paren >= 0)
                    {
                        entry = rest[..paren].Trim();
                        filename = entry;
                    }
                }
                else if (rest.Contains('\\'))
                {
                    // Use the last path segment as the filename
                    int lastSlash = rest.LastIndexOf('\\');
                    entry = rest[(lastSlash + 1)..];
                    filename = entry;
                }

                // Strip .exe extension for display
                string displayEntry = Regex.Replace(entry, @"(?i)\.exe$", "");

                // Truncate long names: >15 chars → first 12 + "..."
                if (displayEntry.Length > 15)
                    displayEntry = displayEntry[..12] + "...";

                var appData = new AppEntry(
                    Text: $"{displayEntry} [{tag}]",
                    Filename: filename);

                switch (currentSection)
                {
                    case "DISPLAY":
                        screenApps.Add(appData);
                        break;
                    case "SYSTEM":
                    case "AWAYMODE":
                        sleepApps.Add(appData);
                        break;
                }
            }
        }

        return new ParseResult(screenApps, sleepApps);
    }
}
