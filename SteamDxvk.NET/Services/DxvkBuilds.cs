using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace SteamDxvk;

/// <summary>
/// Downloads and caches DXVK builds under %LOCALAPPDATA%\SteamDxvk\builds\{official|async}-{tag}\{x32,x64}.
///   official: doitsujin/dxvk (GitHub)           - Vulkan translation + graphics-pipeline-library.
///   async:    Ph42oN/dxvk-gplasync (GitLab)     - same plus dxvk.enableAsync.
/// </summary>
internal static class DxvkBuilds
{
    private static readonly HttpClient Http = CreateClient();

    public static string BuildsDir { get; } = Path.Combine(AppPaths.Dir, "builds");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SteamDxvk");
        return client;
    }

    private static string Prefix(DxvkVariant v) => v == DxvkVariant.Async ? "async" : "official";

    public static async Task<(string Tag, string Url)> LatestReleaseAsync(DxvkVariant variant, CancellationToken ct = default)
    {
        if (variant == DxvkVariant.Official)
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://api.github.com/repos/doitsujin/dxvk/releases/latest", ct));
            var root = doc.RootElement;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".tar.gz") && !name.StartsWith("dxvk-native"))
                    return (root.GetProperty("tag_name").GetString()!, asset.GetProperty("browser_download_url").GetString()!);
            }
        }
        else
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(
                "https://gitlab.com/api/v4/projects/Ph42oN%2Fdxvk-gplasync/releases?per_page=5", ct));
            foreach (var release in doc.RootElement.EnumerateArray())   // newest first
                foreach (var link in release.GetProperty("assets").GetProperty("links").EnumerateArray())
                    if ((link.GetProperty("name").GetString() ?? "").EndsWith(".tar.gz"))
                        return (release.GetProperty("tag_name").GetString()!, link.GetProperty("url").GetString()!);
        }
        throw new InvalidOperationException($"no tarball found in the latest {variant} release");
    }

    /// <summary>Cached builds of a variant, newest first (a build is complete only if it has the .ok marker).</summary>
    public static IReadOnlyList<(string Dir, string Version)> Cached(DxvkVariant variant)
    {
        if (!Directory.Exists(BuildsDir)) return [];
        string prefix = Prefix(variant) + "-";
        return Directory.EnumerateDirectories(BuildsDir, prefix + "*")
            .Where(d => File.Exists(Path.Combine(d, ".ok")))
            .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
            .Select(d => (d, Path.GetFileName(d)[prefix.Length..]))
            .ToList();
    }

    /// <summary>The cached build with exactly this version (what an install was made from), else the latest.</summary>
    public static async Task<(string Dir, string Version)> ResolveAsync(
        DxvkVariant variant, string version, Action<string>? log = null, CancellationToken ct = default)
    {
        foreach (var cached in Cached(variant))
            if (cached.Version == version) return cached;
        log?.Invoke($"{DxvkInstaller.Label(variant)} {version} is not cached; using the latest build instead");
        return await EnsureBuildAsync(variant, log, ct);
    }

    /// <summary>Extracts only the known DLL members (never the whole archive) so a hostile tarball can't write elsewhere.</summary>
    public static void Extract(byte[] tarGz, string dest)
    {
        var wanted = new HashSet<string>(
            from arch in new[] { "x32", "x64" } from dll in DxvkInstaller.AllDlls select $"{arch}/{dll}");
        string tmp = dest + ".tmp";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);

        int got = 0;
        var seen = new HashSet<string>();
        using (var gz = new GZipStream(new MemoryStream(tarGz), CompressionMode.Decompress))
        using (var tar = new TarReader(gz))
        {
            while (tar.GetNextEntry() is { } entry)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null) continue;
                // Real archives are exactly <top folder>/<x32|x64>/<dll>. Anything else (extra depth, "..", duplicates) is ignored.
                var parts = entry.Name.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToArray();
                if (parts.Length != 3 || !wanted.Contains($"{parts[1]}/{parts[2]}") || !seen.Add($"{parts[1]}/{parts[2]}")) continue;
                string outPath = Path.Combine(tmp, parts[1], parts[2]);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                using var fs = File.Create(outPath);
                entry.DataStream.CopyTo(fs);
                got++;
            }
        }
        if (got != wanted.Count)
        {
            Directory.Delete(tmp, recursive: true);
            throw new InvalidDataException($"archive had {got}/{wanted.Count} expected DLLs");
        }
        File.WriteAllText(Path.Combine(tmp, ".ok"), "");
        if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        Directory.Move(tmp, dest);
    }

    /// <summary>The newest build of a variant: downloads it if not cached, falls back to a cached one when offline.</summary>
    public static async Task<(string Dir, string Version)> EnsureBuildAsync(
        DxvkVariant variant, Action<string>? log = null, CancellationToken ct = default)
    {
        string tag, url;
        try { (tag, url) = await LatestReleaseAsync(variant, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            var cached = Cached(variant);
            if (cached.Count == 0) throw new InvalidOperationException($"cannot reach the release server and no cached {variant} build: {e.Message}", e);
            log?.Invoke($"Could not check for updates ({e.Message}); using cached {cached[0].Version}");
            return cached[0];
        }

        string dest = Path.Combine(BuildsDir, $"{Prefix(variant)}-{tag}");
        if (File.Exists(Path.Combine(dest, ".ok"))) return (dest, tag);

        log?.Invoke($"Downloading {DxvkInstaller.Label(variant)} {tag} ...");
        Directory.CreateDirectory(BuildsDir);
        byte[] data = await Http.GetByteArrayAsync(url, ct);
        await Task.Run(() => Extract(data, dest), ct);
        log?.Invoke($"Downloaded {DxvkInstaller.Label(variant)} {tag} ({data.Length / 1e6:F1} MB)");
        return (dest, tag);
    }
}
