using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace SteamDxvk;

/// <summary>What to run to start a game: a program and its arguments, or a steam:// URL (empty <see cref="Arguments"/>).</summary>
internal sealed record LaunchCommand(string FileName, string Arguments)
{
    public override string ToString() => Arguments.Length == 0 ? FileName : $"{Path.GetFileName(FileName)} {Arguments}";
}

/// <summary>
/// Starts a game through the Steam client, so overlay, cloud saves and DRM all work.
/// With a launch flag it runs <c>steam.exe -applaunch &lt;id&gt; &lt;flag&gt;</c>. That avoids Steam's "launch with command line"
/// confirmation, which Steam shows (its log says "waiting for user response to ShowGameArgs") for any steam://run/&lt;id&gt;//&lt;args&gt;/
/// URL but not for -applaunch (checked against Steam's own log with Session and -dx11). Without a flag the plain
/// <c>steam://rungameid/&lt;id&gt;</c> URL is used. The URL form is also the fallback if steam.exe can't be found.
/// </summary>
internal static partial class SteamLaunch
{
    /// <summary>The only shape of argument ever passed: one engine flag from <see cref="EngineFlags"/>. It goes on a real command line, so be strict.</summary>
    [GeneratedRegex("^-[A-Za-z0-9-]+$")]
    private static partial Regex SafeFlag();

    public static string Url(int appId, string? args = null) =>
        string.IsNullOrWhiteSpace(args) ? $"steam://rungameid/{appId}" : $"steam://run/{appId}//{Uri.EscapeDataString(args)}/";

    public static LaunchCommand Command(int appId, string? args, string? steamExe)
    {
        if (string.IsNullOrWhiteSpace(args)) return new(Url(appId), "");
        if (!SafeFlag().IsMatch(args)) throw new ArgumentException($"refusing to pass '{args}' to Steam: only a single -flag is allowed", nameof(args));
        return steamExe is null ? new(Url(appId, args), "") : new(steamExe, $"-applaunch {appId} {args}");
    }

    public static void Start(int appId, string? args)
    {
        var command = Command(appId, args, SteamExe());
        Process.Start(new ProcessStartInfo(command.FileName, command.Arguments) { UseShellExecute = true });
    }

    private static string? SteamExe()
    {
        string? root = SteamLibrary.FindSteamPath();
        string? exe = root is null ? null : Path.Combine(root, "steam.exe");
        return exe is not null && File.Exists(exe) ? exe : null;
    }
}
