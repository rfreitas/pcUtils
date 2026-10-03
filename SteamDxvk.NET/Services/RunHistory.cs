using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamDxvk;

/// <summary>Public only so xunit theories (public methods) can take it as a parameter.</summary>
public enum RunOutcome { Ok, Crashed, ExitedEarly }

/// <param name="Config">The setup the run used: see <see cref="ConfigKey"/>.</param>
internal sealed record RunRecord(
    int AppId, string Config,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] RunOutcome Outcome,
    string Detail, DateTime WhenUtc);

/// <summary>A game's setup, as a comparable string: what it was asked to run as, and the DXVK state. Crashes belong to a setup, not a game.</summary>
internal static class ConfigKey
{
    public static string Of(RunMode requested, DxvkStatus? dxvk) => $"{requested}|{Dxvk(dxvk)}";

    public static string Dxvk(DxvkStatus? status) => status switch
    {
        null => "no-dxvk",
        { Managed: false } => "dxvk-manual",
        { Options: { } o } => $"dxvk-{(o.Async ? "async" : "official")}-gpl{o.Gpl}",
        _ => "dxvk",
    };

    /// <summary>Human wording for messages: "Run as D3D12 + DXVK async".</summary>
    public static string Describe(string config)
    {
        var parts = config.Split('|');
        string mode = parts[0] == "Default" ? "Run as Default" : $"Run as {parts[0]}";
        return parts.Length < 2 || parts[1] == "no-dxvk" ? mode : $"{mode} + {(parts[1].Contains("async") ? "DXVK async" : "DXVK")}";
    }
}

/// <summary>What happened the last times each setup was run, in %LOCALAPPDATA%\SteamDxvk\history.json. The latest result for a setup wins.</summary>
internal sealed class RunHistory
{
    private const int MaxEntries = 300;
    private readonly string _path;
    private readonly object _gate = new();
    private List<RunRecord> _records = [];

    public static string DefaultPath => Path.Combine(AppPaths.Dir, "history.json");

    public RunHistory(string? path = null)
    {
        _path = path ?? DefaultPath;
        try { _records = JsonSerializer.Deserialize<List<RunRecord>>(File.ReadAllText(_path)) ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            if (e is not FileNotFoundException and not DirectoryNotFoundException) Logger.Log($"Could not read run history, starting empty: {e.Message}");
        }
    }

    public void Add(RunRecord record)
    {
        lock (_gate)
        {
            _records.Add(record);
            if (_records.Count > MaxEntries) _records = _records.Skip(_records.Count - MaxEntries).ToList();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(_records, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Logger.Log($"Could not save run history: {e.Message}");
            }
        }
    }

    /// <summary>The most recent run of this game with this setup, or null.</summary>
    public RunRecord? Last(int appId, string config)
    {
        lock (_gate) return _records.LastOrDefault(r => r.AppId == appId && r.Config == config);
    }

    /// <summary>The latest run of this setup if it ended badly (a later good run clears it), else null.</summary>
    public RunRecord? Problem(int appId, string config) => Last(appId, config) is { Outcome: not RunOutcome.Ok } bad ? bad : null;
}
