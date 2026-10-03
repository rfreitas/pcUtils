namespace SteamDxvk;

/// <param name="CanInstall">False means the "Use DXVK" tick is greyed out; <paramref name="Blocked"/> says why.</param>
/// <param name="Apis">What the DLLs will be installed for. Chosen automatically: the user never picks DLLs.</param>
internal sealed record InstallPlan(bool CanInstall, GfxApi Apis, IReadOnlyList<string> Warnings, string? Blocked);

/// <summary>Pure decisions behind the UI: can DXVK be used for this game as configured, which DLLs that takes, and what to warn about.</summary>
internal static class InstallPlanner
{
    /// <param name="mode">The user's Run as choice: D3D11 means the game will be launched in D3D11, so that is what DXVK serves.</param>
    public static InstallPlan Plan(GameScan scan, ExeInfo? exe, RunMode mode = RunMode.Default)
    {
        static InstallPlan Blocked(string why) => new(false, GfxApi.None, [], why);

        if (exe is null) return Blocked("No game executable found.");
        if (mode == RunMode.D3D12) return Blocked("Run as D3D12 can't use DXVK, which only translates D3D8-D3D11.");
        if (mode == RunMode.Vulkan) return Blocked("Run as Vulkan uses the game's own renderer, so DXVK isn't involved.");

        var apis = mode == RunMode.D3D11 ? GfxApi.D3D11 : exe.Apis & GfxApis.Supported;
        bool guessed = false;
        if (apis == GfxApi.None)
        {
            if (exe.Apis.HasFlag(GfxApi.D3D12)) return Blocked("This game only uses D3D12, which DXVK can't translate.");
            if (exe.Apis.HasFlag(GfxApi.Vulkan)) return Blocked("This game already uses Vulkan.");
            if (exe.Apis.HasFlag(GfxApi.OpenGL)) return Blocked("This game uses OpenGL, which DXVK doesn't translate.");
            // Nothing detected at all: install both common sets. Whichever API the game really uses picks up its own DLLs.
            apis = GfxApi.D3D9 | GfxApi.D3D11;
            guessed = true;
        }

        var warnings = new List<string>();
        if (scan.AntiCheat.Count > 0)
            warnings.Add($"Anti-cheat detected ({string.Join(", ", scan.AntiCheat)}). Modified DLLs, and async compilation in " +
                         "particular, can get you banned in online play.");
        if (guessed)
            warnings.Add("Couldn't tell which API this game uses, so both the D3D9 and D3D11 DXVK files are installed.");
        // Engines with a launch flag get Run as D3D11 set automatically when DXVK is ticked, so only others need the warning.
        if (mode != RunMode.D3D11 && scan.Status is null && exe.Apis.HasFlag(GfxApi.D3D12) && EngineFlags.Flag(scan.Engine, RunMode.D3D11) is null)
            warnings.Add("This game also contains D3D12. If it starts in D3D12 it will crash with DXVK installed, and this app " +
                         "has no launch flag for its engine to prevent that. Untick DXVK if it fails to start.");
        return new(true, apis, warnings, null);
    }
}

/// <summary>The one plain sentence that says what the app will do for the selected game and what DXVK's part in it is.</summary>
internal static class PlanSummary
{
    /// <summary>Run as only means something where the engine has launch flags.</summary>
    public static bool ShowRunAs(GameScan scan) => EngineFlags.Available(scan.Engine).Count > 1;

    public static string? Describe(GameScan scan, ExeInfo? exe, RunMode mode, bool dxvkInstalled)
    {
        if (exe is null) return null;
        if (mode == RunMode.D3D12) return "Launches in D3D12 on the driver directly. DXVK can't translate D3D12.";
        if (mode == RunMode.Vulkan) return "Launches with the game's own Vulkan renderer. DXVK isn't involved.";

        var plan = InstallPlanner.Plan(scan, exe, mode);
        if (!plan.CanInstall) return null;

        if (mode != RunMode.D3D11 && (exe.Apis & GfxApis.Supported) == GfxApi.None)
            return dxvkInstalled
                ? "The game's API wasn't detected, so DXVK's D3D9 and D3D11 files are both installed; whichever the game uses is translated to Vulkan."
                : "The game's API wasn't detected. Using DXVK installs its D3D9 and D3D11 files, and whichever the game uses is translated to Vulkan.";

        // D3D10 and D3D11 are one DLL set; name the newest the game has so the sentence reads naturally.
        var served = plan.Apis;
        string api = served.HasFlag(GfxApi.D3D11) ? "D3D11" : served.HasFlag(GfxApi.D3D10) ? "D3D10" : served.HasFlag(GfxApi.D3D9) ? "D3D9" : "D3D8";
        return dxvkInstalled
            ? $"The game uses {api}; DXVK translates it to Vulkan."
            : $"The game uses {api}. Using DXVK translates it to Vulkan.";
    }
}

internal static class GameFilter
{
    public static readonly string[] Options =
        ["All", "D3D12", "D3D11", "D3D10", "D3D9", "D3D8", "Vulkan", "OpenGL", "DXVK installed", "No API detected"];

    public static bool Matches(GameScan scan, string filter, string search)
    {
        if (search.Length > 0 && !scan.Name.Contains(search, StringComparison.OrdinalIgnoreCase)) return false;
        var apis = scan.Primary?.Apis ?? GfxApi.None;
        return filter switch
        {
            "All" => true,
            "DXVK installed" => scan.Status is not null,
            "No API detected" => apis == GfxApi.None,
            _ => Enum.TryParse<GfxApi>(filter, out var wanted) && apis.HasFlag(wanted),
        };
    }
}
