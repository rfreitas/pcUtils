namespace SteamDxvk;

/// <param name="CanInstall">False means the "Use DXVK" tick is greyed out; <paramref name="Blocked"/> says why (shown as its tooltip only).</param>
/// <param name="Apis">What the DLLs will be installed for. Chosen automatically: the user never picks DLLs.</param>
/// <param name="Warnings">Risks the UI can't prevent by itself, shown as a line. Constraints the UI does enforce (greyed controls) are not repeated here.</param>
/// <param name="Risk">Anti-cheat text, for the confirmation asked when DXVK is ticked (the list already flags anti-cheat per game).</param>
/// <param name="Guessed">Nothing was detected, so both common DLL sets go in; the tick's tooltip says so.</param>
internal sealed record InstallPlan(bool CanInstall, GfxApi Apis, IReadOnlyList<string> Warnings, string? Blocked,
    string? Risk = null, bool Guessed = false);

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

        string? risk = scan.AntiCheat.Count > 0
            ? $"Anti-cheat detected ({string.Join(", ", scan.AntiCheat)}). Modified DLLs, and async compilation in particular, can get you banned in online play."
            : null;

        var warnings = new List<string>();
        // Engines with a launch flag get Run as D3D11 set automatically when DXVK is ticked, so only others need the warning.
        if (mode != RunMode.D3D11 && scan.Status is null && exe.Apis.HasFlag(GfxApi.D3D12) && EngineFlags.Flag(scan.Engine, RunMode.D3D11) is null)
            warnings.Add("This game may start in D3D12, which crashes with DXVK, and this app has no launch flag for its engine to prevent it. " +
                         "Untick DXVK if it fails to start.");
        return new(true, apis, warnings, null, risk, guessed);
    }
}

/// <summary>Small UI decisions that don't depend on a window.</summary>
internal static class PlanSummary
{
    /// <summary>Run as only means something where the engine has launch flags.</summary>
    public static bool ShowRunAs(GameScan scan) => EngineFlags.Available(scan.Engine).Count > 1;
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
