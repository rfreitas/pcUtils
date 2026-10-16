using System.IO;
using System.Text.Json;

namespace ClaudeFleet;

/// <summary>One <c>~/.claude/sessions/&lt;pid&gt;.json</c> file, parsed tolerantly (the format is undocumented).</summary>
internal sealed record SessionInfo(
    int Pid, string SessionId, string Name, string Cwd, DateTimeOffset StartedAt, long ProcStart,
    string Entrypoint, string RawStatus, string? Version)
{
    /// <summary>Headless runs (<c>claude -p</c>, SDK) register with entrypoint "sdk-cli"; their kind is still "interactive".</summary>
    public bool Headless => Entrypoint.StartsWith("sdk", StringComparison.OrdinalIgnoreCase);

    /// <summary>"busy" and "shell" (waiting on a shell command) both mean work is in progress.</summary>
    public SessionStatus Status => RawStatus is "busy" or "shell" ? SessionStatus.Busy : SessionStatus.Idle;
}

/// <summary>Reads the session registry. Read-only; the <c>.key</c> files next to the json files are never opened.</summary>
internal static class SessionRegistry
{
    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");

    /// <summary>Parses one file's text; null when it is not a usable session record.</summary>
    public static SessionInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("pid", out var pid) || !pid.TryGetInt32(out int p)) return null;
            string Str(string name, string fallback = "") => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
            long Num(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;

            // procStart is a FILETIME serialised as a string.
            long.TryParse(Str("procStart"), out long procStart);
            string id = Str("sessionId");
            return new SessionInfo(
                p, id, Str("name", id.Length >= 8 ? id[..8] : $"pid {p}"), Str("cwd"),
                DateTimeOffset.FromUnixTimeMilliseconds(Num("startedAt")), procStart,
                Str("entrypoint", "cli"), Str("status", "idle"), r.TryGetProperty("version", out var ver) ? ver.GetString() : null);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Every registered session whose process is still the one that wrote the file: the PID must exist and, when the
    /// file records procStart, the process creation time must match (rejects a reused PID after a crash).
    /// </summary>
    public static IReadOnlyList<SessionInfo> ReadAll(string dir, Func<int, long?> processCreateTime)
    {
        var result = new List<SessionInfo>();
        if (!Directory.Exists(dir)) return result;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            SessionInfo? s;
            try { s = Parse(File.ReadAllText(file)); }
            catch (IOException) { continue; }          // being rewritten; next event picks it up
            catch (UnauthorizedAccessException) { continue; }
            if (s is null) continue;

            long? created = processCreateTime(s.Pid);
            if (created is null) continue;
            if (s.ProcStart != 0 && Math.Abs(created.Value - s.ProcStart) > TimeSpan.TicksPerSecond) continue;
            result.Add(s);
        }
        return result;
    }
}
