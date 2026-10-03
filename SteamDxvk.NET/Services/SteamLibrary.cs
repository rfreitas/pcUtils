using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SteamDxvk;

internal sealed record Game(int AppId, string Name, string Root);

/// <summary>Locates the Steam install and enumerates installed games. Parsing is separate from file access so it can be tested with mock text.</summary>
internal static class SteamLibrary
{
    private static readonly Regex NotGames = new(
        @"^(Proton|Steam Linux Runtime|Steamworks Common Redistributables|Steam Controller Configs)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? FindSteamPath()
    {
        foreach (var (hive, sub, value) in new[]
        {
            (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
        })
        {
            try
            {
                using var key = hive.OpenSubKey(sub);
                if (key?.GetValue(value) is string path && Directory.Exists(path)) return Path.GetFullPath(path);
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        foreach (var guess in new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" })
            if (Directory.Exists(guess)) return guess;
        return null;
    }

    /// <summary>Library folder roots listed in libraryfolders.vdf (new format: numbered blocks with "path"; old: numbered plain strings).</summary>
    public static IReadOnlyList<string> ParseLibraryFolders(string vdfText)
    {
        var paths = new List<string>();
        var root = Vdf.Parse(vdfText);
        if (Vdf.Object(root.GetValueOrDefault("libraryfolders")) is not { } folders) return paths;
        foreach (var (key, node) in folders)
        {
            string? path = node is Dictionary<string, object> block ? Vdf.Str(block, "path")
                : key.All(char.IsDigit) ? node as string   // old format; skips "ContentStatsID" style entries
                : null;
            if (!string.IsNullOrWhiteSpace(path)) paths.Add(path);
        }
        return paths;
    }

    public static (int AppId, string Name, string InstallDir)? ParseAppManifest(string acfText)
    {
        if (Vdf.Object(Vdf.Parse(acfText).GetValueOrDefault("AppState")) is not { } state) return null;
        if (!int.TryParse(Vdf.Str(state, "appid"), out int appId)) return null;
        string? name = Vdf.Str(state, "name"), dir = Vdf.Str(state, "installdir");
        return name is null || dir is null ? null : (appId, name, dir);
    }

    public static IReadOnlyList<string> SteamAppsDirs(string steamPath)
    {
        var dirs = new List<string> { Path.Combine(steamPath, "steamapps") };
        string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (var library in ParseLibraryFolders(File.ReadAllText(vdf)))
                dirs.Add(Path.Combine(library, "steamapps"));
        return dirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<Game> ListGames(string steamPath)
    {
        var games = new Dictionary<int, Game>();
        foreach (var steamapps in SteamAppsDirs(steamPath))
        {
            foreach (var manifest in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                try
                {
                    if (ParseAppManifest(File.ReadAllText(manifest)) is not var (appId, name, installDir)) continue;
                    string root = Path.Combine(steamapps, "common", installDir);
                    if (Directory.Exists(root) && !NotGames.IsMatch(name)) games.TryAdd(appId, new Game(appId, name, root));
                }
                catch (IOException) { }
            }
        }
        return games.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
