namespace ClaudeFleet;

internal enum SessionStatus { Idle, Busy }

internal enum AgentState { Running, Done }

/// <summary>A process in a session's tree. <see cref="Label"/> is the command with shell wrappers removed.</summary>
internal sealed record ProcView(int Pid, string Label, double CpuPercent, long WorkingSetBytes, bool IsMcp, IReadOnlyList<ProcView> Children)
{
    public double TreeCpu => CpuPercent + Children.Sum(c => c.TreeCpu);
    public long TreeMemory => WorkingSetBytes + Children.Sum(c => c.TreeMemory);
}

/// <summary>An Agent-tool subagent of a session (runs inside the session's own claude.exe).</summary>
internal sealed record AgentView(string AgentId, string Type, string Description, string Model, bool Background, AgentState State, TimeSpan Elapsed);

/// <summary>
/// One registered Claude Code session. <see cref="Children"/> are headless <c>claude -p</c> sessions it started
/// (they register their own file; the parent PID chain ties them here).
/// </summary>
internal sealed record SessionView(
    int Pid, string SessionId, string Name, string Cwd, SessionStatus Status, TimeSpan Age, bool Headless, string? Model,
    double OwnCpuPercent, long OwnWorkingSetBytes,
    IReadOnlyList<AgentView> Agents, IReadOnlyList<ProcView> Processes, IReadOnlyList<SessionView> Children)
{
    /// <summary>CPU of claude.exe itself, its processes and its headless child sessions.</summary>
    public double TreeCpu => OwnCpuPercent + Processes.Sum(p => p.TreeCpu) + Children.Sum(c => c.TreeCpu);
    public long TreeMemory => OwnWorkingSetBytes + Processes.Sum(p => p.TreeMemory) + Children.Sum(c => c.TreeMemory);
    /// <summary>Running subagents plus busy headless child sessions (each of those is an agent too).</summary>
    public int RunningAgents => Agents.Count(a => a.State == AgentState.Running)
                              + Children.Sum(c => c.RunningAgents + (c.Status == SessionStatus.Busy ? 1 : 0));
}

/// <summary>Everything the flyout and the tray icon show. CPU is percent of the whole machine.</summary>
internal sealed record FleetSnapshot(IReadOnlyList<SessionView> Sessions, bool IsMock)
{
    public double TotalCpu => Sessions.Sum(s => s.TreeCpu);
    public int Busy => All().Count(s => s.Status == SessionStatus.Busy);
    public int Count => All().Count();
    public int RunningAgents => Sessions.Sum(s => s.RunningAgents);

    /// <summary>Every session, headless children included.</summary>
    public IEnumerable<SessionView> All() => Sessions.SelectMany(Flatten);

    private static IEnumerable<SessionView> Flatten(SessionView s) => s.Children.SelectMany(Flatten).Prepend(s);
}
