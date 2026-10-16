namespace ClaudeFleet;

/// <summary>
/// Turns successive process snapshots into CPU percentages of the whole machine (like Task Manager's total:
/// 100 % = every logical processor fully busy). Pure: time and processor count are passed in.
/// A process is keyed by PID + creation time, so a reused PID never inherits another process's history.
/// </summary>
internal sealed class CpuSampler
{
    private readonly int _cpus;
    private Dictionary<(int Pid, long Create), long> _previous = new();
    private long _previousAtTicks = -1;

    /// <summary>A gap longer than this makes the next sample a fresh baseline (reads 0) instead of a long average.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    public CpuSampler(int logicalProcessors) => _cpus = Math.Max(1, logicalProcessors);

    /// <summary>CPU percent per PID since the previous call (all 0 on the first call or after a stale gap).</summary>
    public Dictionary<int, double> Update(IReadOnlyList<ProcEntry> entries, long nowTicks)
    {
        var result = new Dictionary<int, double>(entries.Count);
        long elapsed = nowTicks - _previousAtTicks;
        bool usable = _previousAtTicks >= 0 && elapsed > 0 && elapsed <= StaleAfter.Ticks;

        var next = new Dictionary<(int, long), long>(entries.Count);
        foreach (var e in entries)
        {
            var key = (e.Pid, e.CreateTime);
            next[key] = e.CpuTime;
            double pct = 0;
            if (usable && _previous.TryGetValue(key, out long before) && e.CpuTime >= before)
                pct = (e.CpuTime - before) * 100.0 / (elapsed * (double)_cpus);
            result[e.Pid] = pct;
        }
        _previous = next;
        _previousAtTicks = nowTicks;
        return result;
    }
}
