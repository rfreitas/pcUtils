using System.IO;

namespace SteamDxvk;

internal sealed record ModuleInfo(string Name, string Path);

/// <param name="Api">The API in use (a single value), or None when it can't be told.</param>
/// <param name="ViaDxvk">True when DXVK is translating it to Vulkan.</param>
/// <param name="Confirmed">True when the evidence is conclusive; false when it is a best guess.</param>
/// <param name="Evidence">Why we think so, for a tooltip.</param>
internal sealed record RunningApi(GfxApi Api, bool ViaDxvk, bool Confirmed, string Evidence)
{
    public static readonly RunningApi Unknown = new(GfxApi.None, false, false, "no graphics DLL evidence yet");

    public string Label => Api == GfxApi.None ? "unknown API" : (ViaDxvk ? $"{Api} via DXVK" : Api.ToString()) + (Confirmed ? "" : " (likely)");
}

/// <summary>
/// Works out which API a running game is really using from the DLLs it has loaded.
/// A DLL being loaded is NOT proof it is in use: games often link several renderers statically, so d3d11.dll (even DXVK's)
/// is loaded in a Vulkan or D3D12 run too (seen with Session). So conclusive evidence is a device actually having been
/// created: D3D12Core.dll loads only when a D3D12 device is created, and DXVK writes its log when it creates one.
/// </summary>
internal static class ApiDetector
{
    /// <param name="dxvkDevice">The API DXVK served in this run (from its fresh log), or None.</param>
    /// <param name="windowsDir">Where the system's own DLLs live (so a game-folder d3d11.dll is recognised as not the system one).</param>
    public static RunningApi Detect(IReadOnlyList<ModuleInfo> modules, GfxApi dxvkDevice, string windowsDir)
    {
        bool Has(string name) => modules.Any(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        bool HasSystem(string name) => modules.Any(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            m.Path.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase));

        if (Has("d3d12core.dll"))
            return new(GfxApi.D3D12, false, true, "D3D12Core.dll is loaded, which only happens once a D3D12 device is created");

        if (dxvkDevice != GfxApi.None)
            return new(dxvkDevice, true, true, $"DXVK wrote a fresh log for a {dxvkDevice} device in this run");

        // Best guesses from here: no device-creation proof is available for native APIs.
        if (Has("vulkan-1.dll"))
            return new(GfxApi.Vulkan, false, false, "vulkan-1.dll is loaded and neither D3D12Core.dll nor a DXVK device exists");
        if (HasSystem("d3d11.dll"))
            return new(GfxApi.D3D11, false, false, "the system d3d11.dll is loaded, with no Vulkan or D3D12 device evidence");
        if (HasSystem("d3d9.dll"))
            return new(GfxApi.D3D9, false, false, "the system d3d9.dll is loaded, with no newer API evidence");
        if (Has("opengl32.dll"))
            return new(GfxApi.OpenGL, false, false, "opengl32.dll is loaded, with no other graphics API evidence");
        return RunningApi.Unknown;
    }

    /// <summary>Empty when the running API is what was asked for (or nothing was asked / can't be told), else the warning.</summary>
    public static string? Mismatch(RunMode requested, RunningApi running)
    {
        if (requested == RunMode.Default || running.Api == GfxApi.None) return null;
        bool matches = requested switch
        {
            RunMode.D3D11 => running.Api == GfxApi.D3D11,
            RunMode.D3D12 => running.Api == GfxApi.D3D12,
            RunMode.Vulkan => running.Api == GfxApi.Vulkan && !running.ViaDxvk,   // DXVK is Vulkan underneath, but not the game's own Vulkan renderer
            _ => true,
        };
        return matches ? null : $"Requested {requested}, but the game is running {running.Label}.";
    }
}
