using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SteamDxvk;

internal enum DxvkVariant { Official, Async }

/// <param name="Async">Use dxvk-gplasync and set dxvk.enableAsync.</param>
/// <param name="Gpl">dxvk.enableGraphicsPipelineLibrary: Auto | True | False.</param>
/// <param name="Hud">Show DXVK's "compiler" HUD item so shader compiles are visible.</param>
internal sealed record DxvkOptions(bool Async = false, string Gpl = "Auto", bool Hud = false)
{
    public DxvkVariant Variant => Async ? DxvkVariant.Async : DxvkVariant.Official;
}

/// <summary>What a folder currently has: a manager-installed DXVK (with its options) or a manual one.</summary>
internal sealed record DxvkStatus(bool Managed, string Label, DxvkOptions? Options);

internal sealed class DxvkManifest
{
    [JsonPropertyName("variant")] public string Variant { get; set; } = "official";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("arch")] public string Arch { get; set; } = "";
    [JsonPropertyName("files")] public Dictionary<string, string> Files { get; set; } = [];       // name -> sha256 as installed
    [JsonPropertyName("backups")] public List<string> Backups { get; set; } = [];                 // originals we moved aside
    [JsonPropertyName("conf_dirs")] public Dictionary<string, bool> ConfDirs { get; set; } = [];  // folder -> we created dxvk.conf
    [JsonPropertyName("options")] public ManifestOptions Options { get; set; } = new();
}

internal sealed class ManifestOptions
{
    [JsonPropertyName("async")] public bool Async { get; set; }
    [JsonPropertyName("gpl")] public string Gpl { get; set; } = "Auto";
    [JsonPropertyName("hud")] public bool Hud { get; set; }
}

/// <summary>
/// Installs/uninstalls DXVK DLLs next to a game exe. Pure file operations (the build to copy from is passed in),
/// so it is fully testable against temp folders. Existing files are backed up and restored; files changed since
/// install are never deleted; dxvk.conf edits live in a marked block so the user's own lines survive.
/// </summary>
internal static class DxvkInstaller
{
    public static readonly string[] AllDlls = ["d3d8.dll", "d3d9.dll", "d3d10core.dll", "d3d11.dll", "dxgi.dll"];
    public const string ManifestName = "dxvk-manager.json";
    public const string BackupDir = ".dxvk-manager-backup";
    public const string ConfName = "dxvk.conf";
    private const string BlockBegin = "# >>> steam-dxvk-manager (managed block, edits inside are overwritten)";
    private const string BlockEnd = "# <<< steam-dxvk-manager";
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string Label(DxvkVariant v) => v == DxvkVariant.Async ? "DXVK-gplasync" : "DXVK";

    public static List<string> DllsFor(GfxApi apis)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        if (apis.HasFlag(GfxApi.D3D8)) { names.Add("d3d8.dll"); names.Add("d3d9.dll"); }   // d3d8 layer sits on DXVK's d3d9
        if (apis.HasFlag(GfxApi.D3D9)) names.Add("d3d9.dll");
        if (apis.Overlaps(GfxApi.D3D10 | GfxApi.D3D11)) { names.Add("d3d10core.dll"); names.Add("d3d11.dll"); names.Add("dxgi.dll"); }
        return [.. names];
    }

    public static List<string> ConfLines(DxvkOptions o)
    {
        var lines = new List<string>();
        if (o.Async) lines.Add("dxvk.enableAsync = True");
        if (o.Gpl != "Auto") lines.Add($"dxvk.enableGraphicsPipelineLibrary = {o.Gpl}");
        if (o.Hud) lines.Add("dxvk.hud = compiler");
        return lines;
    }

    // ----------------------------------------------------------------- status

    public static DxvkManifest? ReadManifest(string exeDir)
    {
        try { return JsonSerializer.Deserialize<DxvkManifest>(File.ReadAllText(Path.Combine(exeDir, ManifestName))); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static DxvkStatus? Status(string exeDir)
    {
        if (ReadManifest(exeDir) is { } mf)
        {
            var variant = mf.Variant == "async" ? DxvkVariant.Async : DxvkVariant.Official;
            return new DxvkStatus(true, $"{Label(variant)} {mf.Version}", new DxvkOptions(mf.Options.Async, mf.Options.Gpl, mf.Options.Hud));
        }
        foreach (var name in AllDlls)
        {
            string p = Path.Combine(exeDir, name);
            if (File.Exists(p) && PeInspector.ContainsAscii(p, "dxvk")) return new DxvkStatus(false, "DXVK (manual)", null);
        }
        return null;
    }

    public static DxvkStatus? StatusOf(GameScan scan) =>
        scan.Exes.Select(e => Status(e.Dir)).FirstOrDefault(s => s is not null);

    // ---------------------------------------------------------------- install

    public static string Install(string exeDir, string rootDir, int bits, GfxApi apis, DxvkOptions opts,
        string buildDir, string version, Action<string>? log = null)
    {
        var names = DllsFor(apis);
        if (names.Count == 0) throw new ArgumentException("no DXVK-supported API (D3D8-D3D11) selected");
        string arch = bits == 64 ? "x64" : "x32";
        var mf = ReadManifest(exeDir) ?? new DxvkManifest();

        foreach (var old in mf.Files.Keys.Where(n => !names.Contains(n)).ToList())
            RemoveDll(exeDir, old, mf, log);

        // Files this call adds for the first time. If a later one fails (typically a DLL locked by the running game) they are
        // taken back out, because a half-installed set with no manifest would look like a hand-made install and never be cleaned up.
        var placed = new List<string>();
        foreach (var name in names)
        {
            string dest = Path.Combine(exeDir, name);
            bool isNew = !mf.Files.ContainsKey(name);
            try
            {
                if (isNew && File.Exists(dest))
                {
                    Directory.CreateDirectory(Path.Combine(exeDir, BackupDir));
                    File.Copy(dest, Path.Combine(exeDir, BackupDir, name), overwrite: true);
                    if (!mf.Backups.Contains(name)) mf.Backups.Add(name);
                    log?.Invoke($"  backed up existing {name}");
                }
                File.Copy(Path.Combine(buildDir, arch, name), dest, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                RollBack(exeDir, placed, mf, log);
                throw new InvalidOperationException($"cannot write {dest} - is the game running?", e);
            }
            mf.Files[name] = Sha256(dest);
            if (isNew) placed.Add(name);
            log?.Invoke($"  installed {arch}/{name}");
        }

        // DXVK reads dxvk.conf from the process working directory: the exe folder or the game root depending on
        // how the game is launched, so write the config to both.
        foreach (var folder in new[] { exeDir, rootDir }.Distinct(StringComparer.OrdinalIgnoreCase))
            WriteConf(folder, ConfLines(opts), mf);

        mf.Variant = opts.Async ? "async" : "official";
        mf.Version = version;
        mf.Arch = arch;
        mf.Options = new ManifestOptions { Async = opts.Async, Gpl = opts.Gpl, Hud = opts.Hud };
        File.WriteAllText(Path.Combine(exeDir, ManifestName), JsonSerializer.Serialize(mf, JsonOpts));
        return $"{Label(opts.Variant)} {version} ({arch}) -> {exeDir}";
    }

    public static void Uninstall(string exeDir, Action<string>? log = null)
    {
        var mf = ReadManifest(exeDir) ?? throw new InvalidOperationException("no manager-installed DXVK in this folder");
        foreach (var name in mf.Files.Keys.ToList())
        {
            try { RemoveDll(exeDir, name, mf, log); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"cannot modify {Path.Combine(exeDir, name)} - is the game running?", e);
            }
        }
        foreach (var folder in mf.ConfDirs.Keys.ToList()) WriteConf(folder, [], mf);

        string backup = Path.Combine(exeDir, BackupDir);
        if (Directory.Exists(backup) && !Directory.EnumerateFileSystemEntries(backup).Any()) Directory.Delete(backup);
        File.Delete(Path.Combine(exeDir, ManifestName));
        log?.Invoke($"Removed DXVK from {exeDir}");
    }

    /// <summary>
    /// What no longer matches the install: DLLs gone or replaced (Steam verify, a game update, another tool) and
    /// dxvk.conf blocks missing or stale. Empty means the install is exactly as the manifest says.
    /// </summary>
    public static IReadOnlyList<string> Verify(string exeDir)
    {
        var mf = ReadManifest(exeDir) ?? throw new InvalidOperationException("no manager-installed DXVK in this folder");
        var problems = new List<string>();
        foreach (var (name, expected) in mf.Files)
        {
            string path = Path.Combine(exeDir, name);
            if (!File.Exists(path)) problems.Add($"{name} is missing");
            else if (Sha256(path) != expected) problems.Add($"{name} was replaced or modified");
        }

        var lines = ConfLines(new DxvkOptions(mf.Options.Async, mf.Options.Gpl, mf.Options.Hud));
        if (lines.Count > 0)
        {
            foreach (var folder in mf.ConfDirs.Keys)
            {
                string conf = Path.Combine(folder, ConfName);
                string text = File.Exists(conf) ? File.ReadAllText(conf) : "";
                if (!text.Contains(BlockBegin) || lines.Any(l => !text.Contains(l)))
                    problems.Add($"{ConfName} in {folder} is missing or out of date");
            }
        }
        return problems;
    }

    /// <summary>The APIs an install was made for, recovered from the DLLs it put down.</summary>
    public static GfxApi ApisFromFiles(IEnumerable<string> dllNames)
    {
        var apis = GfxApi.None;
        foreach (var name in dllNames)
        {
            apis |= name switch
            {
                "d3d8.dll" => GfxApi.D3D8,
                "d3d9.dll" => GfxApi.D3D9,
                "d3d10core.dll" or "d3d11.dll" or "dxgi.dll" => GfxApi.D3D11,
                _ => GfxApi.None,
            };
        }
        return apis;
    }

    /// <summary>Re-applies an existing install exactly as it was configured (same API set, arch and options).</summary>
    public static string Repair(string exeDir, string rootDir, string buildDir, string version, Action<string>? log = null)
    {
        var mf = ReadManifest(exeDir) ?? throw new InvalidOperationException("no manager-installed DXVK in this folder");
        var options = new DxvkOptions(mf.Options.Async, mf.Options.Gpl, mf.Options.Hud);
        return Install(exeDir, rootDir, mf.Arch == "x64" ? 64 : 32, ApisFromFiles(mf.Files.Keys), options, buildDir, version, log);
    }

    private static void RollBack(string dir, List<string> placed, DxvkManifest mf, Action<string>? log)
    {
        foreach (var name in placed)
        {
            try { RemoveDll(dir, name, mf, log); }   // deletes our copy and puts the backed-up original back
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log?.Invoke($"  could not undo {name}: {e.Message}"); }
        }
        string backup = Path.Combine(dir, BackupDir);
        if (Directory.Exists(backup) && !Directory.EnumerateFileSystemEntries(backup).Any()) Directory.Delete(backup);
    }

    private static void RemoveDll(string dir, string name, DxvkManifest mf, Action<string>? log)
    {
        string dest = Path.Combine(dir, name), backup = Path.Combine(dir, BackupDir, name);
        if (File.Exists(dest))
        {
            if (Sha256(dest) != mf.Files.GetValueOrDefault(name))
            {
                log?.Invoke($"  {name} was changed since install (Steam verify?) - leaving it in place");
                mf.Files.Remove(name);
                return;
            }
            File.Delete(dest);
        }
        if (mf.Backups.Contains(name) && File.Exists(backup))
        {
            File.Move(backup, dest, overwrite: true);
            mf.Backups.Remove(name);
            log?.Invoke($"  restored original {name}");
        }
        mf.Files.Remove(name);
    }

    // ------------------------------------------------------------ dxvk.conf

    /// <summary>Removes our block, and the blank separator line we put before it, leaving the user's text as it was.</summary>
    public static string StripBlock(string text) => Regex.Replace(text,
        $@"(?:\r?\n)?{Regex.Escape(BlockBegin)}.*?{Regex.Escape(BlockEnd)}(?:\r?\n)?", "", RegexOptions.Singleline);

    private static void WriteConf(string folder, List<string> lines, DxvkManifest mf)
    {
        string path = Path.Combine(folder, ConfName);
        string? existing = File.Exists(path) ? File.ReadAllText(path) : null;
        string rest = StripBlock(existing ?? "");
        string nl = existing?.Contains("\r\n") == true ? "\r\n" : "\n";
        if (!mf.ConfDirs.TryGetValue(folder, out bool created)) mf.ConfDirs[folder] = created = existing is null;

        if (lines.Count > 0)
        {
            string block = $"{BlockBegin}{nl}{string.Join(nl, lines)}{nl}{BlockEnd}{nl}";
            string head = rest.Trim().Length > 0 ? rest.TrimEnd('\r', '\n') + nl + nl : "";
            File.WriteAllText(path, head + block);
        }
        else if (rest.Trim().Length == 0 && created)
        {
            File.Delete(path);
            mf.ConfDirs.Remove(folder);
        }
        else if (existing is not null)
        {
            File.WriteAllText(path, rest);
        }
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
