using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RefreshRateOverlay.WPF.Services;

/// <summary>Per-app optimizer setting. Only Off and DryRun exist: a Live value
/// is added when enforcement is built (plans/2026-10-07-game-optimizer-dry-run.md).</summary>
public enum OptimizerMode
{
    Off,
    DryRun,
}

internal enum OptimizerRole
{
    Game,
    /// <summary>A process up the game's parent chain (launcher, anti-cheat
    /// bootstrapper, Explorer, a terminal). Never changed, like Protected, but
    /// shown under the game in the panel because it is part of the game's launch.</summary>
    GameChain,
    WouldThrottle,
    Protected,
    LeftAlone,
}

/// <summary>One process as read from the system. Path and PriorityClass are null
/// when the process couldn't be opened (protected processes, anti-cheat).
/// CpuCores is null on the first sample, before there is a delta to measure.
/// ParentPid is null when unknown or when the pid was reused by a newer process
/// (ProcessSnapshotService checks creation times).</summary>
internal sealed record ProcessSnapshot(
    string Name,
    int Pid,
    string? Path,
    int SessionId,
    System.Diagnostics.ProcessPriorityClass? PriorityClass,
    double? CpuCores,
    int? ParentPid = null);

/// <summary>ChainDepth is set for GameChain only: 1 = the game's direct parent.</summary>
internal sealed record OptimizerDecision(string Name, int Pid, OptimizerRole Role, string Reason, string? Action, double? CpuCores,
    int? ChainDepth = null);

internal sealed record OptimizerReport(string App, DateTime At, IReadOnlyList<OptimizerDecision> Decisions);

/// <summary>One panel row: same-name processes with the same role and reason
/// collapse into one row with a count and summed CPU.</summary>
internal sealed record OptimizerRow(string Name, int Count, string Reason, string? Action, double? CpuCores, int? ChainDepth = null);

/// <summary>ChainRows is only set on the Game section: the launch chain, nearest parent first.</summary>
internal sealed record OptimizerSection(OptimizerRole Role, string Title, IReadOnlyList<OptimizerRow> Rows, int ProcessCount,
    IReadOnlyList<OptimizerRow>? ChainRows = null);

/// <summary>Rows of one section that share a reason (the rule that matched),
/// for the panel's collapsible Protected groups.</summary>
internal sealed record OptimizerGroup(string Reason, IReadOnlyList<OptimizerRow> Rows, int ProcessCount, double? CpuCores);

/// <summary>
/// Pure classification of processes for the game optimizer. Decides, for the
/// focused app, which process is the game, which ones would be throttled, which
/// are protected and which are left alone. Changes nothing; TrayApp only shows
/// the result (dry run). Rules are first-match-wins, in the order of
/// <see cref="Classify"/>; see the plan file for why each exists.
/// </summary>
internal static class GameOptimizerPlanner
{
    public const string GameAction = "High priority, P-cores";
    public const string ThrottleAction = "Below normal, E-cores, low I/O";

    /// <summary>Busy enough to throttle: average cores over the sample. In cores,
    /// not % of total: on 28 threads one busy thread is only 3.6%, and one busy
    /// thread is exactly what preempts a game thread.</summary>
    public const double DefaultCpuThresholdCores = 0.5;

    private static readonly (string Reason, string[] Names)[] ProtectedNames =
    {
        ("VR runtime", new[]
        {
            "vrserver", "vrcompositor", "vrmonitor", "vrdashboard", "vrwebhelper", "vrstartup", "vrserverhelper",
            "OVRServer_x64", "OVRRedir", "OVRServiceLauncher", "OculusClient", "OculusDash",
            "VirtualDesktop.Streamer", "VirtualDesktop.Service",
        }),
        ("GPU / audio / input helper", new[]
        {
            "dwm", "audiodg", "nvcontainer", "NVIDIA Overlay", "nvsphelper64", "NVDisplay.Container",
            "DSX", "RTSS", "RTSSHooksLoader64",
        }),
        // A process holding a global keyboard/mouse hook that gets starved of CPU
        // delays every keystroke and mouse move system-wide, not just its own.
        ("Input hook / hotkeys", new[]
        {
            "AutoHotkey", "AutoHotkey64", "AutoHotkey32", "AutoHotkeyUX",
            "AggressiveScreensaver", "LGTV_brightness.NET",
        }),
        ("Anti-cheat", new[]
        {
            "EasyAntiCheat", "EasyAntiCheat_EOS", "start_protected_game", "BEService", "BEService_x64",
            "vgc", "vgtray", "EAAntiCheat.GameServiceLauncher", "faceit", "FACEITService",
        }),
        ("Launcher", new[]
        {
            "steam", "steamwebhelper", "steamservice", "EpicGamesLauncher", "EADesktop", "GalaxyClient", "Battle.net",
            "XboxPcApp", "gamingservices",
        }),
        ("Capture", new[] { "obs64", "obs32", "obs-ffmpeg-mux", "Discord" }),
        ("Windows core", new[]
        {
            "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "fontdrvhost",
            "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "ctfmon", "sihost",
        }),
    };

    private static readonly string[] DevToolNames =
    {
        "rustc", "cargo", "link", "lld", "rust-lld", "lld-link", "cl", "msbuild", "clang", "clang-cl", "ninja",
    };

    private static readonly Dictionary<string, string> ProtectedReasonByName = BuildProtectedIndex();
    private static readonly HashSet<string> DevTools = new(DevToolNames, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> BuildProtectedIndex()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (reason, names) in ProtectedNames)
            foreach (string n in names) map[n] = reason;
        return map;
    }

    /// <summary>Exe name without ".exe", the form Process.ProcessName uses.</summary>
    public static string BaseName(string app) =>
        app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;

    /// <summary>
    /// Folders whose processes belong to the active OpenXR runtime. The runtime
    /// manifest's own folder, plus its parent only when that folder is named
    /// "OpenXR": Virtual Desktop keeps its manifest in an OpenXR subfolder with the
    /// exes one level up, while SteamVR's parent is steamapps\common, which would
    /// protect every installed game.
    /// </summary>
    public static IReadOnlyList<string> OpenXrRoots(string? activeRuntimeManifest)
    {
        if (string.IsNullOrWhiteSpace(activeRuntimeManifest)) return Array.Empty<string>();
        string? dir = System.IO.Path.GetDirectoryName(activeRuntimeManifest.Trim());
        if (string.IsNullOrEmpty(dir)) return Array.Empty<string>();

        var roots = new List<string> { WithSlash(dir) };
        if (string.Equals(System.IO.Path.GetFileName(dir), "OpenXR", StringComparison.OrdinalIgnoreCase)
            && System.IO.Path.GetDirectoryName(dir) is { Length: > 3 } parent)
        {
            roots.Add(WithSlash(parent));
        }
        return roots;
    }

    private static string WithSlash(string dir) =>
        dir.EndsWith('\\') ? dir : dir + "\\";

    public static OptimizerReport Plan(
        string app, DateTime at, IEnumerable<ProcessSnapshot> processes, int ownPid,
        IReadOnlyList<string> runtimeRoots, double cpuThresholdCores = DefaultCpuThresholdCores)
    {
        string game = BaseName(app);
        var list = processes as IReadOnlyCollection<ProcessSnapshot> ?? processes.ToList();
        var ancestors = GameAncestors(list, game);
        var decisions = list
            .Select(p => Classify(p, game, ownPid, runtimeRoots, cpuThresholdCores, ancestors))
            .ToList();
        return new OptimizerReport(app, at, decisions);
    }

    /// <summary>
    /// Pids of every process up the parent chain of the game's processes. Windows
    /// starts a child at the parent's priority class when that is Idle or Below
    /// normal, and children inherit the parent's CPU affinity, so throttling a
    /// launcher just before it starts the game would start the game throttled.
    /// Covers launchers, anti-cheat bootstrappers, Explorer, a terminal or a script,
    /// whatever actually started it. Stops at a missing parent, a cycle, or depth 16.
    /// </summary>
    public static Dictionary<int, int> GameAncestors(IEnumerable<ProcessSnapshot> processes, string gameBaseName)
    {
        var byPid = new Dictionary<int, ProcessSnapshot>();
        foreach (var p in processes) byPid.TryAdd(p.Pid, p);

        // pid -> depth (1 = direct parent). With several game processes the
        // shallowest depth wins.
        var result = new Dictionary<int, int>();
        if (gameBaseName.Length == 0) return result;

        foreach (var game in byPid.Values.Where(p => string.Equals(p.Name, gameBaseName, StringComparison.OrdinalIgnoreCase)))
        {
            var seen = new HashSet<int>();
            int? parent = game.ParentPid;
            for (int depth = 1; depth <= 16 && parent is { } pid && pid > 4; depth++)
            {
                if (!byPid.TryGetValue(pid, out var proc) || !seen.Add(pid)) break;
                if (string.Equals(proc.Name, gameBaseName, StringComparison.OrdinalIgnoreCase)) break; // the game's own helper, already Game
                result[pid] = result.TryGetValue(pid, out int d) ? Math.Min(d, depth) : depth;
                parent = proc.ParentPid;
            }
        }
        return result;
    }

    public static OptimizerDecision Classify(
        ProcessSnapshot p, string gameBaseName, int ownPid, IReadOnlyList<string> runtimeRoots, double cpuThresholdCores,
        IReadOnlyDictionary<int, int>? gameAncestors = null)
    {
        OptimizerDecision D(OptimizerRole role, string reason, string? action = null) =>
            new(p.Name, p.Pid, role, reason, action, p.CpuCores);

        if (p.Pid == ownPid)
            return D(OptimizerRole.Protected, "Overlay app");

        if (gameBaseName.Length > 0 && string.Equals(p.Name, gameBaseName, StringComparison.OrdinalIgnoreCase))
        {
            return p.Path is null || p.PriorityClass is null
                ? D(OptimizerRole.Game, "Focused app — no access: a live boost would fail (anti-cheat?)", GameAction)
                : D(OptimizerRole.Game, "Focused app", GameAction);
        }

        if (gameAncestors is not null && gameAncestors.TryGetValue(p.Pid, out int depth))
            return D(OptimizerRole.GameChain, "Started the game") with { ChainDepth = depth };

        if (p.SessionId == 0 || p.Path is null || p.PriorityClass is null)
            return D(OptimizerRole.Protected, "Windows / service");

        foreach (string root in runtimeRoots)
            if (p.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return D(OptimizerRole.Protected, "OpenXR runtime");

        if (ProtectedReasonByName.TryGetValue(p.Name, out string? reason))
            return D(OptimizerRole.Protected, reason);

        if (p.PriorityClass != System.Diagnostics.ProcessPriorityClass.Normal)
            return D(OptimizerRole.LeftAlone, $"Priority already {p.PriorityClass} (you, or a tool such as Process Lasso)");

        if (DevTools.Contains(p.Name))
            return D(OptimizerRole.WouldThrottle, "Build tool", ThrottleAction);

        if (p.CpuCores is { } cores && cores >= cpuThresholdCores)
            return D(OptimizerRole.WouldThrottle, "High CPU", ThrottleAction);

        return D(OptimizerRole.LeftAlone, p.CpuCores is null ? "CPU not measured yet" : "Low CPU");
    }

    /// <summary>Protected reasons in rule order, so groups keep a stable position
    /// across refreshes instead of jumping around with CPU use.</summary>
    private static readonly string[] ReasonOrder =
    {
        "OpenXR runtime",
        "VR runtime",
        "Input hook / hotkeys",
        "GPU / audio / input helper",
        "Anti-cheat",
        "Launcher",
        "Capture",
        "Windows core",
        "Windows / service",
        "Overlay app",
    };

    /// <summary>Groups a section's rows by reason: known reasons in rule order,
    /// any other reason after them alphabetically. Rows keep their CPU order.</summary>
    public static IReadOnlyList<OptimizerGroup> GroupByReason(OptimizerSection section)
    {
        int Rank(string reason)
        {
            int i = Array.IndexOf(ReasonOrder, reason);
            return i >= 0 ? i : ReasonOrder.Length;
        }

        return section.Rows
            .GroupBy(r => r.Reason)
            .OrderBy(g => Rank(g.Key))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var rows = g.ToList();
                double? cpu = rows.Any(r => r.CpuCores is not null) ? rows.Sum(r => r.CpuCores ?? 0) : null;
                return new OptimizerGroup(g.Key, rows, rows.Sum(r => r.Count), cpu);
            })
            .ToList();
    }

    private static readonly (OptimizerRole Role, string Title)[] SectionOrder =
    {
        (OptimizerRole.Game, "Game"),
        (OptimizerRole.WouldThrottle, "Would throttle"),
        (OptimizerRole.Protected, "Protected"),
        (OptimizerRole.LeftAlone, "Left alone"),
    };

    /// <summary>Panel view of a report: sections in a fixed order, rows collapsed by
    /// (name, reason), sorted by CPU then name. Empty sections are kept so the
    /// panel layout doesn't jump.</summary>
    public static IReadOnlyList<OptimizerSection> Summarize(OptimizerReport report)
    {
        var sections = new List<OptimizerSection>();
        foreach (var (role, title) in SectionOrder)
        {
            var inRole = report.Decisions.Where(d => d.Role == role).ToList();
            var rows = Rows(inRole);
            IReadOnlyList<OptimizerRow>? chain = null;
            if (role == OptimizerRole.Game)
            {
                chain = Rows(report.Decisions.Where(d => d.Role == OptimizerRole.GameChain))
                    .OrderBy(r => r.ChainDepth ?? int.MaxValue)
                    .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            sections.Add(new OptimizerSection(role, title, rows, inRole.Count, chain));
        }
        return sections;
    }

    private static List<OptimizerRow> Rows(IEnumerable<OptimizerDecision> decisions) =>
        decisions
            .GroupBy(d => (Name: d.Name.ToLowerInvariant(), d.Reason, d.Action))
            .Select(g =>
            {
                double? cpu = g.Any(d => d.CpuCores is not null) ? g.Sum(d => d.CpuCores ?? 0) : null;
                int? depth = g.Any(d => d.ChainDepth is not null) ? g.Min(d => d.ChainDepth ?? int.MaxValue) : null;
                return new OptimizerRow(g.First().Name, g.Count(), g.Key.Reason, g.Key.Action, cpu, depth);
            })
            .OrderByDescending(r => r.CpuCores ?? -1)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
