using System.IO;

namespace SteamDxvk;

/// <summary>Everything the app persists lives under %LOCALAPPDATA%\SteamDxvk (never beside the exe, which dotnet clean wipes).</summary>
internal static class AppPaths
{
    public static string Dir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamDxvk");
}
