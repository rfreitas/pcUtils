using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamDxvk;

/// <summary>Per-game choices the app remembers (Run as, and the DXVK options last used), in %LOCALAPPDATA%\SteamDxvk\settings.json.</summary>
internal sealed class GameSettings
{
    private sealed class Entry
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public RunMode RunMode { get; set; }

        // Whether the user wants DXVK for this game. Kept even while DXVK can't be used (Run as D3D12), so the tick stays put
        // and DXVK comes back by itself when it can be used again. Null = never decided: follow what is installed.
        public bool? UseDxvk { get; set; }

        // The DXVK options last used for this game, so ticking "Use DXVK" again brings the same ones back.
        public bool? Async { get; set; }
        public string? Gpl { get; set; }
        public bool? Hud { get; set; }

        public bool IsDefault => RunMode == RunMode.Default && UseDxvk is null && Async is null && Gpl is null && Hud is null;
    }

    private readonly string _path;
    private Dictionary<string, Entry> _games = [];

    public static string DefaultPath => Path.Combine(AppPaths.Dir, "settings.json");

    public GameSettings(string? path = null)
    {
        _path = path ?? DefaultPath;
        try { _games = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path)) ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Missing is normal; damaged or hand-edited with a bad value falls back to defaults rather than failing.
            if (e is not FileNotFoundException and not DirectoryNotFoundException) Logger.Log($"Could not read settings, using defaults: {e.Message}");
        }
    }

    public RunMode GetRunMode(int appId) => _games.TryGetValue(appId.ToString(), out var e) ? e.RunMode : RunMode.Default;

    public void SetRunMode(int appId, RunMode mode) => Update(appId, e => e.RunMode = mode);

    /// <summary>The remembered "Use DXVK" choice, or null if the user never made one.</summary>
    public bool? GetUseDxvk(int appId) => _games.TryGetValue(appId.ToString(), out var e) ? e.UseDxvk : null;

    public void SetUseDxvk(int appId, bool use) => Update(appId, e => e.UseDxvk = use);

    /// <summary>The DXVK options last used for this game, or null if none were ever saved.</summary>
    public DxvkOptions? GetDxvkOptions(int appId) =>
        _games.TryGetValue(appId.ToString(), out var e) && e.Async is not null
            ? new DxvkOptions(e.Async.Value, e.Gpl ?? "Auto", e.Hud ?? false)
            : null;

    public void SetDxvkOptions(int appId, DxvkOptions options) => Update(appId, e =>
    {
        e.Async = options.Async;
        e.Gpl = options.Gpl;
        e.Hud = options.Hud;
    });

    private void Update(int appId, Action<Entry> change)
    {
        string key = appId.ToString();
        var entry = _games.TryGetValue(key, out var existing) ? existing : new Entry();
        change(entry);
        if (entry.IsDefault) _games.Remove(key);
        else _games[key] = entry;
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_games, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logger.Log($"Could not save settings: {e.Message}");
        }
    }
}
