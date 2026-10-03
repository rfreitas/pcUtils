using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamDxvk;

internal sealed record ExeInfo(string Path, int Bits, long Size, GfxApi Apis, GfxApi Imports)
{
    public string Dir => System.IO.Path.GetDirectoryName(Path)!;
}

internal sealed class GameScan
{
    public GameScan(Game game)
    {
        AppId = game.AppId;
        Name = game.Name;
        Root = game.Root;
    }

    public int AppId { get; }
    public string Name { get; }
    public string Root { get; }
    public List<ExeInfo> Exes { get; set; } = [];
    public string Engine { get; set; } = "";
    public List<string> AntiCheat { get; set; } = [];
    public DxvkStatus? Status { get; set; }
    public string Error { get; set; } = "";

    public ExeInfo? Primary => Exes.Count > 0 ? Exes[0] : null;
}

/// <summary>Remembers per-file results keyed by path+size+mtime so rescans are near-instant.</summary>
internal sealed class ScanCache
{
    private sealed record Entry(int Apis, int Imports);

    private readonly string _path;
    private readonly object _gate = new();
    private readonly HashSet<string> _touched = [];
    private Dictionary<string, Entry> _data = [];

    public ScanCache(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(AppPaths.Dir, "scan-cache.json");
        try { _data = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path)) ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
    }

    /// <summary>(all APIs referenced, APIs linked through import tables) for one binary.</summary>
    public (GfxApi Apis, GfxApi Imports) Info(string path)
    {
        FileInfo fi;
        try { fi = new FileInfo(path); if (!fi.Exists) return default; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return default; }

        string key = $"{path}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        lock (_gate)
        {
            _touched.Add(key);
            if (_data.TryGetValue(key, out var hit)) return ((GfxApi)hit.Apis, (GfxApi)hit.Imports);
        }
        var imports = PeInspector.Imports(path);
        var apis = PeInspector.ScanStrings(path) | imports;
        lock (_gate) _data[key] = new Entry((int)apis, (int)imports);
        return (apis, imports);
    }

    /// <summary>Writes the cache, dropping entries no scan touched this session.</summary>
    public void Save()
    {
        lock (_gate)
        {
            _data = _data.Where(kv => _touched.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(_data));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Finds a game's real executables and works out which graphics APIs they use.</summary>
internal static class GameScanner
{
    public const long MinExeBytes = 1_000_000;
    public const long UnityMinExeBytes = 100_000;   // Unity's launcher is a ~600 KB stub
    private const int MaxDepth = 4;
    private const int MaxExes = 6;
    private const int MaxSiblingDlls = 4;

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "_commonredist", "commonredist", "redist", "redistributables", "directx", "vcredist",
        "__installer", "installer", "dotnet", "_redist", "support", "prerequisites", "streamingassets",
    };

    private const RegexOptions Rx = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    private static readonly Regex ExeExclude = new(
        @"(unins|crash|report|redist|vcredist|vc_redist|dxsetup|dotnet|setup|install|prereq|" +
        @"easyanticheat|^eac|beservice|battleye|start_protected_game|cefsubprocess|webhelper|" +
        @"notification_helper|python|updater|bugsplat|ffmpeg|ffprobe)", Rx);

    private static readonly Regex DllExclude = new(
        @"^(d3d|dxgi|vulkan|opengl|steam_api|steamclient|xinput|msvc|vcruntime|api-ms|ucrtbase|" +
        @"libcef|concrt|vcomp|mfc|openvr|oculus|openxr|nvngx|amd_|ffx_|libcurl|libssl|libcrypto|" +
        @"zlib|physx|fmod|wwise|bink|discord|galaxy|eos|steamwebrtc|libegl|libgles)", Rx);

    private static readonly (Regex Pattern, string Label)[] AntiCheat =
    [
        (new(@"^(easyanticheat.*|eaclauncher.*)$", Rx), "EasyAntiCheat"),
        (new(@"^(battleye|be(service|client).*|start_protected_game\.exe)$", Rx), "BattlEye"),
        (new(@"^(xigncode.*|nprotect.*|gameguard.*|punkbuster.*|vgc.*|equ8.*)$", Rx), "other anti-cheat"),
    ];

    public static GameScan Scan(Game game, ScanCache cache)
    {
        var scan = new GameScan(game);
        var candidates = new List<(long Size, string Path)>();
        var anticheat = new SortedSet<string>();

        foreach (var (dir, subdirs, files) in Walk(game.Root))
        {
            foreach (var name in subdirs.Concat(files))
                foreach (var (pattern, label) in AntiCheat)
                    if (pattern.IsMatch(name)) anticheat.Add(label);

            bool isUnity = files.Any(f => f.Equals("unityplayer.dll", StringComparison.OrdinalIgnoreCase));
            foreach (var name in files)
            {
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || ExeExclude.IsMatch(name)) continue;
                string path = System.IO.Path.Combine(dir, name);
                long size;
                try { size = new FileInfo(path).Length; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                if (size >= (isUnity ? UnityMinExeBytes : MinExeBytes)) candidates.Add((size, path));
            }
        }
        scan.AntiCheat = [.. anticheat];

        var exes = new List<(int Priority, ExeInfo Info)>();
        foreach (var (size, exe) in candidates.OrderByDescending(c => c.Size).Take(MaxExes))
        {
            if (PeInspector.Bits(exe) is not int bits) continue;
            var (apis, imports) = cache.Info(exe);
            string dir = System.IO.Path.GetDirectoryName(exe)!;

            string unity = System.IO.Path.Combine(dir, "UnityPlayer.dll");
            if (File.Exists(unity))
            {
                if (scan.Engine.Length == 0) scan.Engine = "Unity";
                var (a, i) = cache.Info(unity);
                apis |= a; imports |= i;
            }
            else if (!apis.Overlaps(GfxApis.Any))
            {
                // Launcher-style exe: the renderer usually lives in a sibling DLL.
                foreach (var dll in SiblingDlls(dir))
                {
                    var (a, i) = cache.Info(dll);
                    apis |= a; imports |= i;
                }
            }

            bool shipping = exe.EndsWith("-win64-shipping.exe", StringComparison.OrdinalIgnoreCase)
                         || exe.EndsWith("-wingdk-shipping.exe", StringComparison.OrdinalIgnoreCase);
            if (shipping && scan.Engine.Length == 0) scan.Engine = "Unreal";
            exes.Add((File.Exists(unity) || shipping ? 1 : 0, new ExeInfo(exe, bits, size, apis, imports)));
        }

        // Engine exes (Unity stub, UE -Shipping) first, then biggest first (OrderBy is stable). Ranking by
        // "mentions D3D" would put launchers (EAC, idTech) on top.
        scan.Exes = exes.OrderByDescending(e => e.Priority).Select(e => e.Info).ToList();
        if (scan.Engine.Length == 0 && Directory.Exists(System.IO.Path.Combine(game.Root, "Engine", "Binaries")))
            scan.Engine = "Unreal";
        return scan;
    }

    /// <summary>'D3D11 (+D3D12, Vulkan)': linked APIs plain, string-only mentions after the plus.</summary>
    public static string ApiSummary(ExeInfo? exe)
    {
        if (exe is null || exe.Apis == GfxApi.None) return "?";
        string mentioned = GfxApis.Join(exe.Apis & ~exe.Imports);
        string linked = GfxApis.Join(exe.Imports);
        if (mentioned.Length == 0) return linked;
        return linked.Length > 0 ? $"{linked} (+{mentioned})" : $"({mentioned})";
    }

    private static IEnumerable<string> SiblingDlls(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.dll")
                .Where(p => !DllExclude.IsMatch(System.IO.Path.GetFileName(p)))
                .Select(p => (Path: p, Size: new FileInfo(p).Length))
                .Where(x => x.Size >= MinExeBytes)
                .OrderByDescending(x => x.Size)
                .Take(MaxSiblingDlls)
                .Select(x => x.Path)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Depth-limited directory walk yielding names only (so no filesystem call per file).</summary>
    private static IEnumerable<(string Dir, List<string> Subdirs, List<string> Files)> Walk(string root)
    {
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            List<string> subdirs, files;
            try
            {
                subdirs = Directory.EnumerateDirectories(dir).Select(p => System.IO.Path.GetFileName(p)!).ToList();
                files = Directory.EnumerateFiles(dir).Select(p => System.IO.Path.GetFileName(p)!).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            yield return (dir, subdirs, files);
            if (depth >= MaxDepth) continue;
            foreach (var d in subdirs)
                if (!SkipDirs.Contains(d)) stack.Push((System.IO.Path.Combine(dir, d), depth + 1));
        }
    }
}
