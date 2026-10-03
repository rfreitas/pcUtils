namespace SteamDxvk;

/// <param name="Args">The engine flag to pass to Steam, or null to launch with no extra arguments.</param>
internal sealed record LaunchPlan(bool CanLaunch, string? Args, IReadOnlyList<string> Warnings, string? Blocked);

/// <summary>Pure decisions behind the Launch button: what to pass to Steam, and what would make the launch pointless or crash.</summary>
internal static class LaunchPlanner
{
    public static LaunchPlan Plan(GameScan scan, RunMode mode, bool dxvkInstalled)
    {
        string? flag = null;
        if (mode != RunMode.Default)
        {
            flag = EngineFlags.Flag(scan.Engine, mode);
            if (flag is null)
            {
                string engine = scan.Engine.Length > 0 ? scan.Engine : "this engine";
                return new(false, null, [], $"No launch flag is known for {engine} games, so Run as {mode} can't be applied. Set Run as to Default.");
            }
        }

        // DXVK's dxgi.dll can't create a D3D12 swap chain, so the game dies at startup (seen with Session).
        if (mode == RunMode.D3D12 && dxvkInstalled)
            return new(false, null, [], "DXVK is installed and replaces dxgi.dll, which breaks D3D12 (the game crashes at startup). " +
                                        "Uninstall DXVK, or set Run as to D3D11.");

        var warnings = new List<string>();
        if (mode == RunMode.Default && dxvkInstalled && scan.Primary?.Apis.HasFlag(GfxApi.D3D12) == true)
            warnings.Add("DXVK is installed but this game also has D3D12 and may start in it, which crashes with DXVK. " +
                         "Set Run as to D3D11.");
        return new(true, flag, warnings, null);
    }
}
