using System;
using System.Collections.Generic;
using System.IO;

namespace AggressiveScreensaver.Parsing;

/// <summary>
/// Pure filename matching between DISPLAY power-request blockers and the current
/// foreground window's process image, used to decide whether an unfocused blocker
/// should be ignored.
/// </summary>
public static class BlockerFocusMatcher
{
    /// <summary>
    /// True if <paramref name="foregroundFilename"/> matches one of the blocker
    /// filenames, either exactly or with extensions ignored (powercfg strips
    /// ".exe" for DRIVER-tagged entries).
    /// </summary>
    public static bool IsBlockerFocused(IReadOnlyList<string> blockerFilenames, string? foregroundFilename)
    {
        if (blockerFilenames.Count == 0 || foregroundFilename is null)
            return false;

        foreach (var f in blockerFilenames)
        {
            if (string.Equals(f, foregroundFilename, StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(Path.GetFileNameWithoutExtension(f), Path.GetFileNameWithoutExtension(foregroundFilename), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
