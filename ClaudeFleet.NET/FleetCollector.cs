using System.Text.RegularExpressions;

namespace ClaudeFleet;

/// <summary>
/// Produces a <see cref="FleetSnapshot"/> from the real machine. Two modes (plan "Cost model"):
/// <list type="bullet">
/// <item><c>detail: false</c> (tray meter): sessions + one process snapshot; each session carries the CPU/memory of its
/// whole process subtree and no process rows, no command lines, no agent files.</item>
/// <item><c>detail: true</c> (flyout open): adds process trees with command lines, agents and the model of headless runs.</item>
/// </list>
/// All inputs are injectable, so tests drive it with fabricated snapshots and a temp folder.
/// </summary>
internal sealed class FleetCollector
{
    private readonly string _sessionsDir;
    private readonly string _projectsDir;
    private readonly Func<IReadOnlyList<ProcEntry>> _capture;
    private readonly Func<int, string?> _commandLineOf;
    private readonly Func<DateTime> _utcNow;
    private readonly CpuSampler _sampler;
    private readonly Dictionary<(int, long), string?> _cmdCache = new();

    public FleetCollector(
        string? sessionsDir = null, string? projectsDir = null, Func<IReadOnlyList<ProcEntry>>? capture = null,
        Func<int, string?>? commandLineOf = null, Func<DateTime>? utcNow = null, int? logicalProcessors = null)
    {
        _sessionsDir = sessionsDir ?? SessionRegistry.DefaultDir;
        _projectsDir = projectsDir ?? AgentReader.DefaultProjectsDir;
        _capture = capture ?? ProcessSnapshot.Capture;
        _commandLineOf = commandLineOf ?? ProcessSnapshot.CommandLine;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _sampler = new CpuSampler(logicalProcessors ?? Environment.ProcessorCount);
    }

    public FleetSnapshot Collect(bool detail)
    {
        DateTime nowUtc = _utcNow();
        var entries = _capture();
        var cpu = _sampler.Update(entries, nowUtc.Ticks);

        var created = new Dictionary<int, long>(entries.Count);
        var byPid = new Dictionary<int, ProcEntry>(entries.Count);
        foreach (var e in entries) { created.TryAdd(e.Pid, e.CreateTime); byPid.TryAdd(e.Pid, e); }

        var sessions = SessionRegistry.ReadAll(_sessionsDir, pid => created.TryGetValue(pid, out long t) ? t : null);
        var sessionPids = sessions.Select(s => s.Pid).ToHashSet();
        var kids = ProcessTree.ChildMap(entries);
        if (detail) PruneCommandLines(entries); else _cmdCache.Clear();

        // A headless run belongs to the nearest registered session above it in the process tree.
        var childrenOf = new Dictionary<int, List<SessionInfo>>();
        var topLevel = new List<SessionInfo>();
        foreach (var s in sessions)
        {
            int? parent = s.Headless ? NearestSessionAbove(s.Pid, byPid, sessionPids) : null;
            if (parent is int p) (childrenOf.TryGetValue(p, out var l) ? l : childrenOf[p] = new()).Add(s);
            else topLevel.Add(s);
        }

        SessionView View(SessionInfo s)
        {
            byPid.TryGetValue(s.Pid, out var self);
            double ownCpu = cpu.GetValueOrDefault(s.Pid);
            long ownMem = self?.WorkingSet ?? 0;
            IReadOnlyList<ProcView> procs = [];
            IReadOnlyList<AgentView> agents = [];
            string? model = null;

            if (detail)
            {
                procs = ProcessTree.Build(s.Pid, entries, cpu, CommandLine, sessionPids);
                agents = AgentReader.Read(_projectsDir, s.Cwd, s.SessionId, s.Status, nowUtc);
                if (s.Headless && self is not null)
                    model = Regex.Match(CommandLine(self) ?? "", @"--model[ =](\S+)") is { Success: true } m ? m.Groups[1].Value : null;
            }
            else
            {
                // Tray meter: fold the whole subtree into the session so the total is right without building rows.
                foreach (var d in ProcessTree.Subtree(s.Pid, kids, sessionPids))
                {
                    ownCpu += cpu.GetValueOrDefault(d.Pid);
                    ownMem += d.WorkingSet;
                }
            }

            var children = (childrenOf.TryGetValue(s.Pid, out var cs) ? cs : []).OrderBy(c => c.StartedAt).Select(View).ToList();
            return new SessionView(
                s.Pid, s.SessionId, s.Name, s.Cwd, s.Status, nowUtc - s.StartedAt.UtcDateTime, s.Headless, model,
                ownCpu, ownMem, agents, procs, children);
        }

        // Interactive sessions, busiest first, then by name: stable order while the flyout is open.
        var views = topLevel.Select(View).OrderByDescending(v => v.Status).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new FleetSnapshot(views, IsMock: false);
    }

    private string? CommandLine(ProcEntry e)
    {
        var key = (e.Pid, e.CreateTime);
        if (!_cmdCache.TryGetValue(key, out var cmd)) _cmdCache[key] = cmd = _commandLineOf(e.Pid);
        return cmd;
    }

    /// <summary>Forget cached command lines of processes that are gone (or whose PID was reused).</summary>
    private void PruneCommandLines(IReadOnlyList<ProcEntry> entries)
    {
        if (_cmdCache.Count == 0) return;
        var live = entries.Select(e => (e.Pid, e.CreateTime)).ToHashSet();
        foreach (var k in _cmdCache.Keys.Where(k => !live.Contains(k)).ToList()) _cmdCache.Remove(k);
    }

    private static int? NearestSessionAbove(int pid, Dictionary<int, ProcEntry> byPid, HashSet<int> sessionPids)
    {
        int guard = 0;
        int? cur = byPid.TryGetValue(pid, out var e) ? e.ParentPid : null;
        while (cur is int c && guard++ < 64 && byPid.TryGetValue(c, out var parent))
        {
            if (sessionPids.Contains(c)) return c;
            cur = parent.ParentPid == c ? null : parent.ParentPid;
        }
        return null;
    }
}
