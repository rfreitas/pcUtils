using System.IO;

namespace ClaudeFleet;

/// <summary>Pure formatting and bucketing used by the flyout and the tray icon.</summary>
internal static class FleetFormat
{
    /// <summary>Segments in every CPU meter (tray icon and flyout rows).</summary>
    public const int MeterSteps = 5;

    /// <summary>Upper bound (exclusive, % of the whole machine) for meter steps 0..4; anything above lights all 5.</summary>
    private static readonly double[] StepLimits = [1, 5, 15, 30, 50];

    /// <summary>0 (idle) .. 5 (heavy). Percent is of the whole machine, like Task Manager's total.</summary>
    public static int MeterLevel(double cpuPercent)
    {
        for (int i = 0; i < StepLimits.Length; i++)
            if (cpuPercent < StepLimits[i]) return i;
        return MeterSteps;
    }

    /// <summary>Lit-segment colour for a level: grey idle, accent light, amber busy, red heavy.</summary>
    public static string MeterColor(int level) => level switch
    {
        0     => "#555555",
        1 or 2 => "#7fc8ff",
        3 or 4 => "#ffb454",
        _     => "#ff6b6b",
    };

    /// <summary>"12s", "40m", "19h", "8d".</summary>
    public static string Age(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{Math.Max(0, (int)t.TotalSeconds)}s"
      : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes}m"
      : t.TotalHours   < 24 ? $"{(int)t.TotalHours}h"
      :                       $"{(int)t.TotalDays}d";

    /// <summary>"840 MB", "1.2 GB".</summary>
    public static string Memory(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes >> 20} MB";

    /// <summary>
    /// Short folder for a session: relative to <paramref name="reposRoot"/> when under it ("boardvr-bevy"),
    /// with Claude worktrees shown as "repo ⎇ worktree".
    /// </summary>
    public static string ShortCwd(string cwd, string reposRoot)
    {
        string root = reposRoot.TrimEnd('\\', '/');
        string path = cwd.TrimEnd('\\', '/');
        string rel = path.Equals(root, StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(root)
                   : path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ? path[(root.Length + 1)..]
                   : path;
        const string Worktrees = "\\.claude\\worktrees\\";
        int i = rel.IndexOf(Worktrees, StringComparison.OrdinalIgnoreCase);
        return i < 0 ? rel : $"{rel[..i]} ⎇ {rel[(i + Worktrees.Length)..]}";
    }

    /// <summary>Tray tooltip, at most 127 characters (NotifyIcon limit).</summary>
    public static string Tooltip(FleetSnapshot s)
    {
        var top = s.All().Where(x => x.TreeCpu >= 1).OrderByDescending(x => x.TreeCpu).FirstOrDefault();
        string text = $"Claude: {s.TotalCpu:0}% CPU · {s.Busy} busy / {s.Count} · {s.RunningAgents} agents"
                    + (top is null ? "" : $" · top {top.Name}")
                    + (s.IsMock ? " (mock)" : "");
        return text.Length <= 127 ? text : text[..126] + "…";
    }

    /// <summary>Tooltip for a session or process row: exact numbers live here, never in the row text.</summary>
    public static string Detail(double cpu, long memory, int pid) => $"CPU {cpu:0.0}% · {Memory(memory)} · PID {pid}";
}
