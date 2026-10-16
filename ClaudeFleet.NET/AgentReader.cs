using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeFleet;

/// <summary>
/// Reads a session's Agent-tool subagents from <c>projects\&lt;cwd-slug&gt;\&lt;sessionId&gt;\subagents\agent-*.meta.json</c>.
/// Only called while the flyout is open. Read-only; transcripts are never opened, only their timestamps.
/// </summary>
internal static class AgentReader
{
    /// <summary>Finished agents stay listed this long after their last write.</summary>
    public static readonly TimeSpan KeepFinished = TimeSpan.FromMinutes(10);
    /// <summary>An agent without a "finished" flag (older versions) counts as running only while its transcript is this fresh.</summary>
    public static readonly TimeSpan RunningFreshness = TimeSpan.FromMinutes(10);

    public static string DefaultProjectsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    /// <summary>Claude's folder name for a working directory: every non-alphanumeric character becomes '-'.</summary>
    public static string Slug(string cwd) => Regex.Replace(cwd, "[^A-Za-z0-9]", "-");

    public static IReadOnlyList<AgentView> Read(string projectsDir, string cwd, string sessionId, SessionStatus sessionStatus, DateTime nowUtc)
    {
        string dir = Path.Combine(projectsDir, Slug(cwd), sessionId, "subagents");
        if (!Directory.Exists(dir)) return [];

        var result = new List<AgentView>();
        foreach (var metaPath in Directory.EnumerateFiles(dir, "agent-*.meta.json"))
        {
            try
            {
                string transcript = metaPath[..^".meta.json".Length] + ".jsonl";
                DateTime last = File.Exists(transcript) ? File.GetLastWriteTimeUtc(transcript) : File.GetLastWriteTimeUtc(metaPath);

                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                var r = doc.RootElement;
                string Str(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                bool Flag(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;

                bool finished = Flag("finished");
                bool running = !finished && sessionStatus == SessionStatus.Busy && nowUtc - last < RunningFreshness;
                if (!running && nowUtc - last > KeepFinished) continue;

                string id = Path.GetFileName(metaPath)["agent-".Length..^".meta.json".Length];
                string type = Str("agentType");
                result.Add(new AgentView(
                    id, type.Length == 0 ? "agent" : type, Str("description"), Str("model"), Str("requestShape") == "background",
                    running ? AgentState.Running : AgentState.Done,
                    running ? nowUtc - StartTime(transcript, last) : nowUtc - last));
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { /* half-written: next refresh */ }
        }
        return result.OrderByDescending(a => a.State == AgentState.Running).ThenBy(a => a.Elapsed).ToList();
    }

    /// <summary>Start of an agent: its transcript's first record timestamp (one line is read), else the last write.</summary>
    private static DateTime StartTime(string transcript, DateTime fallback)
    {
        try
        {
            using var sr = new StreamReader(transcript);
            string? first = sr.ReadLine();
            if (first is null) return fallback;
            using var doc = JsonDocument.Parse(first);
            if (doc.RootElement.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String
                && DateTime.TryParse(ts.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t))
                return t;
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        return fallback;
    }
}
