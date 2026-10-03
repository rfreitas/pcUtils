namespace SteamDxvk;

/// <summary>Public only so xunit theories (public methods) can take it as a parameter.</summary>
[Flags]
public enum GfxApi
{
    None = 0,
    D3D8 = 1,
    D3D9 = 2,
    D3D10 = 4,
    D3D11 = 8,
    D3D12 = 16,
    Vulkan = 32,
    OpenGL = 64,
}

internal static class GfxApis
{
    /// <summary>What DXVK can translate to Vulkan.</summary>
    public const GfxApi Supported = GfxApi.D3D8 | GfxApi.D3D9 | GfxApi.D3D10 | GfxApi.D3D11;

    public const GfxApi DirectX = Supported | GfxApi.D3D12;

    public const GfxApi Any = DirectX | GfxApi.Vulkan | GfxApi.OpenGL;

    /// <summary>Graphics DLL names (lowercase) and the API each one means.</summary>
    public static readonly (string Dll, GfxApi Api)[] Dlls =
    [
        ("d3d8.dll", GfxApi.D3D8),
        ("d3d9.dll", GfxApi.D3D9),
        ("d3d10.dll", GfxApi.D3D10),
        ("d3d10_1.dll", GfxApi.D3D10),
        ("d3d10core.dll", GfxApi.D3D10),
        ("d3d11.dll", GfxApi.D3D11),
        ("d3d12.dll", GfxApi.D3D12),
        ("vulkan-1.dll", GfxApi.Vulkan),
        ("opengl32.dll", GfxApi.OpenGL),
    ];

    public static GfxApi FromDll(string name)
    {
        foreach (var (dll, api) in Dlls)
            if (string.Equals(dll, name, StringComparison.OrdinalIgnoreCase)) return api;
        return GfxApi.None;
    }

    public static IEnumerable<GfxApi> Each(GfxApi apis)
    {
        foreach (var api in Enum.GetValues<GfxApi>())
            if (api != GfxApi.None && apis.HasFlag(api)) yield return api;
    }

    public static string Join(GfxApi apis) => string.Join(", ", Each(apis));

    public static bool Overlaps(this GfxApi apis, GfxApi mask) => (apis & mask) != 0;
}
