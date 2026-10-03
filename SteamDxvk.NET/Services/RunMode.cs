namespace SteamDxvk;

/// <summary>
/// Which graphics API the user wants a game to render with. Default leaves the choice to the game; the others are
/// applied by passing the engine's own launch flag. Public only so xunit theories can take it as a parameter.
/// </summary>
public enum RunMode { Default, D3D11, D3D12, Vulkan }

/// <summary>
/// The launch flag each engine uses to pick its renderer. Only fixed strings from this table are ever passed to Steam
/// (never user-typed text): the steam:// protocol hands its arguments straight to the game.
/// </summary>
internal static class EngineFlags
{
    private static readonly Dictionary<string, Dictionary<RunMode, string>> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Unreal"] = new() { [RunMode.D3D11] = "-dx11", [RunMode.D3D12] = "-dx12", [RunMode.Vulkan] = "-vulkan" },
        ["Unity"] = new() { [RunMode.D3D11] = "-force-d3d11", [RunMode.D3D12] = "-force-d3d12", [RunMode.Vulkan] = "-force-vulkan" },
    };

    /// <summary>The flag that makes <paramref name="engine"/> use <paramref name="mode"/>, or null if none is known.</summary>
    public static string? Flag(string engine, RunMode mode) =>
        mode != RunMode.Default && Table.TryGetValue(engine, out var flags) && flags.TryGetValue(mode, out var flag) ? flag : null;

    /// <summary>Modes the app can actually apply for this engine (always includes Default).</summary>
    public static IReadOnlyList<RunMode> Available(string engine)
    {
        var modes = new List<RunMode> { RunMode.Default };
        if (Table.TryGetValue(engine, out var flags)) modes.AddRange(flags.Keys);
        return modes;
    }
}
