using System;
using System.Collections.Generic;
using System.IO;

namespace AggressiveScreensaver.Parsing;

/// <summary>
/// Pure filename matching between DISPLAY power-request blockers and either the
/// current foreground window's process image or the set of processes owning a
/// visible top-level window, used to decide whether an unfocused/invisible
/// blocker should be ignored.
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
            if (IsSameFilename(f, foregroundFilename))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True if any of <paramref name="visibleFilenames"/> (process images owning a
    /// currently visible, non-minimized top-level window) matches one of the
    /// blocker filenames.
    /// </summary>
    public static bool IsBlockerVisible(IReadOnlyList<string> blockerFilenames, IEnumerable<string?> visibleFilenames)
    {
        if (blockerFilenames.Count == 0)
            return false;

        foreach (var visible in visibleFilenames)
        {
            if (visible is null) continue;
            foreach (var f in blockerFilenames)
            {
                if (IsSameFilename(f, visible))
                    return true;
            }
        }
        return false;
    }

    private static bool IsSameFilename(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetFileNameWithoutExtension(a), Path.GetFileNameWithoutExtension(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Removes any filename the user has put on the Ignore List. Unlike
    /// <see cref="IsBlockerFocused"/>/<see cref="IsBlockerVisible"/>, this is an
    /// exact case-insensitive match with no extension normalization — Ignore
    /// List entries and <paramref name="blockerFilenames"/> both come from the
    /// same powercfg identity filenames, so there's no cross-source ambiguity
    /// to account for.
    /// </summary>
    public static IReadOnlyList<string> ExcludeIgnored(IReadOnlyList<string> blockerFilenames, IReadOnlyDictionary<string, bool> ignoredApps)
    {
        if (ignoredApps.Count == 0)
            return blockerFilenames;

        var result = new List<string>();
        foreach (var f in blockerFilenames)
        {
            if (!ignoredApps.ContainsKey(f))
                result.Add(f);
        }
        return result;
    }
}
