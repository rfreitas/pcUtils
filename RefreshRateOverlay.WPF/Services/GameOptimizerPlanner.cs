using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RefreshRateOverlay.WPF.Services;

/// <summary>Per-app optimizer setting. DryRun only shows decisions; Live applies
/// them through GameOptimizerEnforcer (plans/2026-10-07-game-optimizer-live.md).
/// Values are persisted by name and used as combo indexes: append only.</summary>
public enum OptimizerMode
{
    Off,
    DryRun,
    Live,
}

internal enum OptimizerRole
{
    Game,
    /// <summary>A process up the game's parent chain (launcher, anti-cheat
    /// bootstrapper, Explorer, a terminal). Never changed, like Protected, but
    /// shown under the game in the panel because it is part of the game's launch.</summary>
    GameChain,
    /// <summary>The VR runtime and streamer (active OpenXR runtime folder, or a known
    /// VR runtime name). Never changed; shown under the game because in VR the
    /// game's frame still has to pass through them.</summary>
    VrPipeline,
    WouldThrottle,
    Protected,
    LeftAlone,
}

/// <summary>One process as read from the system. Path and PriorityClass are null
/// when the process couldn't be opened (protected processes, anti-cheat).
/// CpuCores is null on the first sample, before there is a delta to measure.
/// ParentPid is null when unknown or when the pid was reused by a newer process
/// (ProcessSnapshotService checks creation times). MaxThreadPriority is the
/// highest thread base priority seen for this process so far (0-31).</summary>
internal sealed record ProcessSnapshot(
    string Name,
    int Pid,
    string? Path,
    int SessionId,
    System.Diagnostics.ProcessPriorityClass? PriorityClass,
    double? CpuCores,
    int? ParentPid = null)
{
    public int? MaxThreadPriority { get; init; }
}

/// <summary>ChainDepth is set for GameChain only: 1 = the game's direct parent.</summary>
internal sealed record OptimizerDecision(string Name, int Pid, OptimizerRole Role, string Reason, string? Action, double? CpuCores,
    int? ChainDepth = null);

internal sealed record OptimizerReport(string App, DateTime At, IReadOnlyList<OptimizerDecision> Decisions);

/// <summary>One panel row: same-name processes with the same role and reason
/// collapse into one row with a count and summed CPU.</summary>
internal sealed record OptimizerRow(string Name, int Count, string Reason, string? Action, double? CpuCores, int? ChainDepth = null);

/// <summary>ChainRows and PipelineRows are only set on the Game section: the launch
/// chain (nearest parent first) and the VR runtime/streamer processes.</summary>
internal sealed record OptimizerSection(OptimizerRole Role, string Title, IReadOnlyList<OptimizerRow> Rows, int ProcessCount,
    IReadOnlyList<OptimizerRow>? ChainRows = null, IReadOnlyList<OptimizerRow>? PipelineRows = null);

/// <summary>Rows of one section that share a reason (the rule that matched),
/// for the panel's collapsible Protected groups.</summary>
internal sealed record OptimizerGroup(string Reason, IReadOnlyList<OptimizerRow> Rows, int ProcessCount, double? CpuCores);

/// <summary>
/// Pure classification of processes for the game optimizer. Decides, for the
/// focused app, which process is the game, which ones would be throttled, which
/// are protected and which are left alone. Changes nothing itself. Rules are
/// first-match-wins, in the order of <see cref="Classify"/>; see the plan files
/// for why each exists. Throttling is the default: every ordinary user process at
/// Normal priority is throttled before it can spike, not after, and protection
/// comes from general signals (registered VR runtimes, what they start, real-time
/// threads) with the name lists only as a backstop.
/// </summary>
internal static class GameOptimizerPlanner
{
    /// <summary>The game keeps its own priority: raising it to High would put it above
    /// most VR streamer/runtime threads (Normal, base 8), which must keep up with it.</summary>
    public static readonly string GameAction = OptimizerActions.Default.GameDescription;
    private const string VrRuntimeReason = "VR runtime";
    public const string VrFolderReason = "Installed VR runtime";
    public const string VrChildReason = "Started by the VR runtime";
    public const string RealtimeReason = "Real-time threads (audio / VR / streaming)";
    public const string BusyReason = "Busy now";
    public const string BuildToolReason = "Build tool";
    public const string BackgroundReason = "Background";

    /// <summary>Thread base priority 16+ only exists in the real-time range: MMCSS
    /// (audio, VR compositors and streamers) or a Realtime class. Ordinary UI threads
    /// top out at 15, so this marks latency-critical work without a name list.</summary>
    public const int RealtimeThreadPriority = 16;

    public static readonly string ThrottleAction = OptimizerActions.Default.ThrottleDescription;

    /// <summary>Busy enough to throttle: average cores over the sample. In cores,
    /// not % of total: on 28 threads one busy thread is only 3.6%, and one busy
    /// thread is exactly what preempts a game thread.</summary>
    public const double DefaultCpuThresholdCores = 0.5;

    private static readonly (string Reason, string[] Names)[] ProtectedNames =
    {
        (VrRuntimeReason, new[]
        {
            "vrserver", "vrcompositor", "vrmonitor", "vrdashboard", "vrwebhelper", "vrstartup", "vrserverhelper",
            "OVRServer_x64", "OVRRedir", "OVRServiceLauncher", "OculusClient", "OculusDash",
            "VirtualDesktop.Streamer", "VirtualDesktop.Service",
        }),
        ("GPU / audio / input helper", new[]
        {
            "dwm", "audiodg", "nvcontainer", "NVIDIA Overlay", "nvsphelper64", "NVDisplay.Container",
            "DSX", "RTSS", "RTSSHooksLoader64",
            // Microsoft GameInput: the game's controller/keyboard input goes through it.
            "GameInputSvc", "GameInputRedistService",
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

    /// <summary>Folders that hold many unrelated installs. A runtime folder's parent
    /// is never added when it is one of these: SteamVR's parent is steamapps\common,
    /// which would protect every installed game.</summary>
    private static readonly HashSet<string> ContainerFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Program Files", "Program Files (x86)", "common", "steamapps", "SteamLibrary", "Games",
        "Users", "AppData", "Local", "LocalLow", "Roaming", "Programs", "WindowsApps",
    };

    /// <summary>
    /// Folders whose processes belong to a VR runtime: each registered runtime or
    /// driver folder, plus its parent unless the parent is a container folder.
    /// Runtimes keep their manifest or driver in a subfolder with the exes nearby
    /// (Virtual Desktop: OpenXR\ and OpenVRDriver\ under the streamer folder;
    /// Oculus: Support\oculus-runtime next to oculus-client and oculus-dash).
    /// </summary>
    public static IReadOnlyList<string> RuntimeRoots(IEnumerable<string?> folders)
    {
        var roots = new List<string>();
        void Add(string dir)
        {
            string d = WithSlash(dir);
            if (!roots.Contains(d, StringComparer.OrdinalIgnoreCase)) roots.Add(d);
        }

        foreach (string? folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            string dir = folder.Trim().TrimEnd('\\');
            if (dir.Length <= 3) continue; // a drive root is never a runtime folder
            Add(dir);
            if (System.IO.Path.GetDirectoryName(dir) is { Length: > 3 } parent
                && !ContainerFolders.Contains(System.IO.Path.GetFileName(parent)))
            {
                Add(parent);
            }
        }
        return roots;
    }

    /// <summary>Roots for a single OpenXR runtime manifest (see RuntimeRoots).</summary>
    public static IReadOnlyList<string> OpenXrRoots(string? runtimeManifest) =>
        string.IsNullOrWhiteSpace(runtimeManifest)
            ? Array.Empty<string>()
            : RuntimeRoots(new[] { System.IO.Path.GetDirectoryName(runtimeManifest.Trim()) });

    private static string WithSlash(string dir) =>
        dir.EndsWith('\\') ? dir : dir + "\\";

    public static OptimizerReport Plan(
        string app, DateTime at, IEnumerable<ProcessSnapshot> processes, int ownPid,
        IReadOnlyList<string> runtimeRoots, double cpuThresholdCores = DefaultCpuThresholdCores, OptimizerActions? actions = null)
    {
        string game = BaseName(app);
        var list = processes as IReadOnlyCollection<ProcessSnapshot> ?? processes.ToList();
        var ancestors = GameAncestors(list, game);
        var decisions = list
            .Select(p => Classify(p, game, ownPid, runtimeRoots, cpuThresholdCores, ancestors, actions))
            .ToList();
        MarkVrDescendants(list, decisions);
        return new OptimizerReport(app, at, decisions);
    }

    /// <summary>
    /// Whatever a VR pipeline process starts (helpers, encoders, a driver host) is
    /// part of the pipeline too, whatever its name or folder. The walk stops at the
    /// game, so a game launched from a VR dashboard and its own helpers stay the game's.
    /// </summary>
    private static void MarkVrDescendants(IReadOnlyCollection<ProcessSnapshot> processes, List<OptimizerDecision> decisions)
    {
        var vr = decisions.Where(d => d.Role == OptimizerRole.VrPipeline).Select(d => d.Pid).ToHashSet();
        if (vr.Count == 0) return;
        var game = decisions.Where(d => d.Role == OptimizerRole.Game).Select(d => d.Pid).ToHashSet();
        var parentOf = new Dictionary<int, int>();
        foreach (var p in processes)
            if (p.ParentPid is { } pp) parentOf.TryAdd(p.Pid, pp);

        bool FromVr(int pid)
        {
            var seen = new HashSet<int> { pid };
            for (int depth = 0; depth < 16 && parentOf.TryGetValue(pid, out int parent) && seen.Add(parent); depth++)
            {
                if (game.Contains(parent)) return false;
                if (vr.Contains(parent)) return true;
                pid = parent;
            }
            return false;
        }

        for (int i = 0; i < decisions.Count; i++)
        {
            var d = decisions[i];
            if (d.Role is OptimizerRole.WouldThrottle or OptimizerRole.LeftAlone && FromVr(d.Pid))
                decisions[i] = d with { Role = OptimizerRole.VrPipeline, Reason = VrChildReason, Action = null };
        }
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
        IReadOnlyDictionary<int, int>? gameAncestors = null, OptimizerActions? actions = null)
    {
        string gameAction = (actions ?? OptimizerActions.Default).GameDescription;
        string throttleAction = (actions ?? OptimizerActions.Default).ThrottleDescription;
        OptimizerDecision D(OptimizerRole role, string reason, string? action = null) =>
            new(p.Name, p.Pid, role, reason, action, p.CpuCores);

        if (p.Pid == ownPid)
            return D(OptimizerRole.Protected, "Overlay app");

        if (gameBaseName.Length > 0 && string.Equals(p.Name, gameBaseName, StringComparison.OrdinalIgnoreCase))
        {
            return p.Path is null || p.PriorityClass is null
                ? D(OptimizerRole.Game, "Focused app — no access: a live boost would fail (anti-cheat?)", gameAction)
                : D(OptimizerRole.Game, "Focused app", gameAction);
        }

        if (gameAncestors is not null && gameAncestors.TryGetValue(p.Pid, out int depth))
            return D(OptimizerRole.GameChain, "Started the game") with { ChainDepth = depth };

        if (p.SessionId == 0 || p.Path is null || p.PriorityClass is null)
            return D(OptimizerRole.Protected, "Windows / service");

        foreach (string root in runtimeRoots)
            if (p.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return D(OptimizerRole.VrPipeline, VrFolderReason);

        if (ProtectedReasonByName.TryGetValue(p.Name, out string? reason))
            return D(reason == VrRuntimeReason ? OptimizerRole.VrPipeline : OptimizerRole.Protected, reason);

        if (p.MaxThreadPriority >= RealtimeThreadPriority)
            return D(OptimizerRole.Protected, RealtimeReason);

        if (p.PriorityClass != System.Diagnostics.ProcessPriorityClass.Normal)
            return D(OptimizerRole.LeftAlone, AlreadyReason(p.PriorityClass.Value));

        // Everything else is throttled up front, so a spike lands on throttled cores
        // instead of waiting to be caught. The reason only says how busy it is now.
        if (DevTools.Contains(p.Name))
            return D(OptimizerRole.WouldThrottle, BuildToolReason, throttleAction);

        if (p.CpuCores is { } cores && cores >= cpuThresholdCores)
            return D(OptimizerRole.WouldThrottle, BusyReason, throttleAction);

        return D(OptimizerRole.WouldThrottle, BackgroundReason, throttleAction);
    }

    /// <summary>Left alone reason for a priority someone else set: the user, the app
    /// itself, or a tool such as Process Lasso.</summary>
    public static string AlreadyReason(System.Diagnostics.ProcessPriorityClass priority) => priority switch
    {
        System.Diagnostics.ProcessPriorityClass.Idle => "Already Idle",
        System.Diagnostics.ProcessPriorityClass.BelowNormal => "Already Below normal",
        System.Diagnostics.ProcessPriorityClass.AboveNormal => "Already Above normal",
        System.Diagnostics.ProcessPriorityClass.High => "Already High",
        System.Diagnostics.ProcessPriorityClass.RealTime => "Already Realtime",
        _ => $"Already {priority}",
    };

    /// <summary>Group reasons in rule order, so groups keep a stable position
    /// across refreshes instead of jumping around with CPU use.</summary>
    private static readonly string[] ReasonOrder =
    {
        BusyReason,
        BuildToolReason,
        BackgroundReason,
        RealtimeReason,
        "Input hook / hotkeys",
        "GPU / audio / input helper",
        "Anti-cheat",
        "Launcher",
        "Capture",
        "Windows core",
        "Windows / service",
        "Overlay app",
        "Already High",
        "Already Above normal",
        "Already Below normal",
        "Already Idle",
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
            IReadOnlyList<OptimizerRow>? chain = null, pipeline = null;
            if (role == OptimizerRole.Game)
            {
                chain = Rows(report.Decisions.Where(d => d.Role == OptimizerRole.GameChain))
                    .OrderBy(r => r.ChainDepth ?? int.MaxValue)
                    .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                pipeline = Rows(report.Decisions.Where(d => d.Role == OptimizerRole.VrPipeline));
            }
            sections.Add(new OptimizerSection(role, title, rows, inRole.Count, chain, pipeline));
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
