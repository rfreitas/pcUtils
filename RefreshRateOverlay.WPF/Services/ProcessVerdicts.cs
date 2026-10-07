using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RefreshRateOverlay.WPF.Services;

/// <summary>What the AI said about one program. Confident is false when it was
/// still guessing after a web search.</summary>
internal sealed record AiVerdict(bool Protect, string Category, string Reason, DateTime At, bool Confident = true);

/// <summary>
/// Everything known about one program (by full exe path) beyond the hard rules:
/// the user's own choice from the panel, and the AI's answer. The user's choice
/// always wins; clearing it falls back to the AI's answer, then to the rules.
/// </summary>
internal sealed record ProcessVerdict
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>true = always protect, false = always throttle, null = not set.</summary>
    public bool? UserProtect { get; init; }
    public AiVerdict? Ai { get; init; }
}

internal interface IProcessVerdictStore
{
    List<ProcessVerdict> Load();
    void Save(IReadOnlyCollection<ProcessVerdict> verdicts);
}

/// <summary>JSON next to the log; written atomically.</summary>
internal sealed class JsonProcessVerdictStore : IProcessVerdictStore
{
    private readonly string _path;

    public JsonProcessVerdictStore(string path) => _path = path;

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RefreshRateOverlay.WPF", "process-verdicts.json");

    public List<ProcessVerdict> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<List<ProcessVerdict>>(File.ReadAllText(_path)) ?? new()
                : new();
        }
        catch (Exception ex)
        {
            Logger.Log($"Process verdicts unreadable ({ex.Message}); starting empty.");
            return new();
        }
    }

    public void Save(IReadOnlyCollection<ProcessVerdict> verdicts)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        string tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(verdicts, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }
}

/// <summary>
/// Cache of per-program verdicts, keyed by full exe path (case-insensitive).
/// Asked once per program, not per process run. The planner reads it through
/// <see cref="All"/>; the panel's right-click menu and the AI classifier write it.
/// </summary>
internal sealed class ProcessVerdictCache
{
    private readonly IProcessVerdictStore _store;
    private readonly Dictionary<string, ProcessVerdict> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public ProcessVerdictCache(IProcessVerdictStore store)
    {
        _store = store;
        foreach (var v in store.Load())
            if (v.Path.Length > 0) _byPath[v.Path] = v;
    }

    public IReadOnlyDictionary<string, ProcessVerdict> All => _byPath;

    public bool Contains(string path) => _byPath.ContainsKey(path);

    /// <summary>protect: true = always protect, false = always throttle, null = back to automatic.</summary>
    public void SetUser(string path, string name, bool? protect)
    {
        var v = _byPath.TryGetValue(path, out var old) ? old : new ProcessVerdict { Path = path, Name = name };
        v = v with { UserProtect = protect };
        if (v.UserProtect is null && v.Ai is null) _byPath.Remove(path);
        else _byPath[path] = v;
        Save();
    }

    public void SetAi(IEnumerable<(string Path, string Name, AiVerdict Verdict)> verdicts)
    {
        foreach (var (path, name, verdict) in verdicts)
        {
            var v = _byPath.TryGetValue(path, out var old) ? old : new ProcessVerdict { Path = path, Name = name };
            _byPath[path] = v with { Ai = verdict };
        }
        Save();
    }

    private void Save()
    {
        try { _store.Save(_byPath.Values.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList()); }
        catch (Exception ex) { Logger.Log($"Process verdicts: could not save: {ex.Message}"); }
    }
}
