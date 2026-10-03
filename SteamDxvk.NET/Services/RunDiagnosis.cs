using System.IO;

namespace SteamDxvk;

/// <param name="DxvkActive">True when DXVK created a device for the game's last run.</param>
/// <param name="Api">Which API DXVK served (D3D8/9/10/11) when it did.</param>
/// <param name="D3D12Device">True when the game tried to create a D3D12 device, which DXVK's dxgi can't serve.</param>
internal sealed record RunReport(string Verdict, bool DxvkActive, GfxApi Api = GfxApi.None, bool D3D12Device = false);

/// <summary>
/// Works out from DXVK's own log files (written next to the exe, e.g. Game-Win64-Shipping_d3d11.log) which API the
/// game's last run actually used. This is the ground truth the static scan can't give.
/// </summary>
internal static class RunDiagnosis
{
    private static readonly string[] ApiSuffixes = ["d3d11", "d3d10core", "d3d9", "d3d8"];
    private const string RunMarker = "Game: ";   // DXVK prints "info:  Game: <exe>" at the start of every process

    /// <param name="sinceUtc">Ignore logs not written since then (typically the launch time); null accepts any.</param>
    public static RunReport Check(string exeDir, DateTime? sinceUtc)
    {
        string? Fresh(string suffix) => Directory.EnumerateFiles(exeDir, $"*_{suffix}.log")
            .Where(f => sinceUtc is null || File.GetLastWriteTimeUtc(f) >= sinceUtc)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        string? dxgiLog = Fresh("dxgi");
        string dxgiText = dxgiLog is null ? "" : LastRun(ReadShared(dxgiLog));

        if (dxgiText.Contains("Unsupported device type"))
            return new("The game created a D3D12 device, which DXVK can't serve (DXVK replaced dxgi.dll). " +
                       "Set Run as to D3D11, or uninstall DXVK.", false, GfxApi.None, D3D12Device: true);

        foreach (var suffix in ApiSuffixes)
        {
            if (Fresh(suffix) is not { } log) continue;
            string text = LastRun(ReadShared(log));
            var lines = text.Split('\n').Select(l => l.Trim()).ToList();
            string device = DeviceName(lines) ?? "the GPU";
            int errors = lines.Count(l => l.StartsWith("err:", StringComparison.Ordinal));
            string api = suffix == "d3d10core" ? "D3D10" : suffix.ToUpperInvariant();
            return new($"DXVK is rendering {api} on {device}" + (errors > 0 ? $" ({errors} error line(s) in {Path.GetFileName(log)})." : "."),
                true, Enum.Parse<GfxApi>(api));
        }

        if (dxgiLog is not null)
            return new("DXVK's dxgi loaded but no D3D9/10/11 device was created, so the game isn't rendering with D3D11 " +
                       "(it may be using D3D12 or Vulkan).", false);

        string since = sinceUtc is { } t ? $" since {t.ToLocalTime():HH:mm:ss}" : "";
        return new($"No DXVK log was written{since}: DXVK wasn't loaded. The game may not have started yet, or it renders " +
                   "with an API DXVK doesn't serve.", false);
    }

    /// <summary>
    /// The GPU name. The dxgi log says "Found device: NAME"; the d3d11 log instead has "Creating device:" followed by "NAME:".
    /// </summary>
    private static string? DeviceName(List<string> lines)
    {
        const string found = "info:  Found device:";
        if (lines.FirstOrDefault(l => l.StartsWith(found, StringComparison.Ordinal)) is { } f) return f[found.Length..].Trim();

        int creating = lines.FindIndex(l => l.StartsWith("info:  Creating device:", StringComparison.Ordinal));
        if (creating < 0 || creating + 1 >= lines.Count) return null;
        string name = lines[creating + 1]["info:".Length..].Trim().TrimEnd(':').Trim();
        return name.Length > 0 ? name : null;
    }

    /// <summary>Only the last process's section: DXVK appends, so older runs' errors would otherwise be misread as current.</summary>
    internal static string LastRun(string text)
    {
        int at = text.LastIndexOf(RunMarker, StringComparison.Ordinal);
        return at < 0 ? text : text[at..];
    }

    // The game may still hold the log open for writing.
    private static string ReadShared(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }
}
