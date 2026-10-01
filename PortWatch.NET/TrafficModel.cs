namespace PortWatch;

internal enum TrafficDirection { In, Out }

/// <summary>One kernel network event: bytes sent or received by <see cref="Pid"/> on a socket with these two ports.</summary>
internal readonly record struct TrafficEvent(string Protocol, TrafficDirection Direction, int Pid, int SourcePort, int DestPort, int Bytes);

/// <summary>Throughput in bytes per second.</summary>
internal readonly record struct Rate(double InBps, double OutBps)
{
    public bool Active => InBps > 0 || OutBps > 0;
    public static Rate operator +(Rate a, Rate b) => new(a.InBps + b.InBps, a.OutBps + b.OutBps);
}

/// <summary>
/// Keeps an arrow lit for a while after its traffic stops, so brief gaps in a stream don't flicker and a
/// burst is still visible a moment later. One latch per arrow target; in and out are held independently.
/// </summary>
internal sealed class ActivityLatch
{
    private long _inMs  = -1_000_000_000;
    private long _outMs = -1_000_000_000;

    /// <summary>Records the current rate and reports which directions are lit (active now, or within <paramref name="holdMs"/>).</summary>
    public (bool In, bool Out) Observe(Rate rate, long nowMs, long holdMs)
    {
        if (rate.InBps  > 0) _inMs  = nowMs;
        if (rate.OutBps > 0) _outMs = nowMs;
        return (nowMs - _inMs < holdMs, nowMs - _outMs < holdMs);
    }
}

/// <summary>An immutable view of current throughput, per process and per (protocol, process, port).</summary>
internal sealed class TrafficSnapshot
{
    public static readonly TrafficSnapshot Empty = new(new(), new());

    private readonly Dictionary<int, Rate> _byPid;
    private readonly Dictionary<(string Protocol, int Pid, int Port), Rate> _byPidPort;

    public TrafficSnapshot(Dictionary<int, Rate> byPid, Dictionary<(string, int, int), Rate> byPidPort)
    {
        _byPid = byPid;
        _byPidPort = byPidPort;
    }

    /// <summary>All traffic of these processes, including sockets with ephemeral (unlisted) ports.</summary>
    public Rate ForProcess(IEnumerable<int> pids)
    {
        var total = default(Rate);
        foreach (int pid in pids)
            if (_byPid.TryGetValue(pid, out var r)) total += r;
        return total;
    }

    /// <summary>Traffic these processes have on exactly these local ports.</summary>
    public Rate ForPorts(string protocol, IEnumerable<int> pids, IEnumerable<int> ports)
    {
        var total = default(Rate);
        var portList = ports as IReadOnlyCollection<int> ?? ports.ToList();
        foreach (int pid in pids)
            foreach (int port in portList)
                if (_byPidPort.TryGetValue((protocol, pid, port), out var r)) total += r;
        return total;
    }
}

/// <summary>
/// Accumulates kernel network events and turns the last couple of complete seconds into rates.
///
/// An event carries both socket ports but not which one is local, so the local port is the first
/// candidate (sender port for Out, destination port for In, then the other) that the process really has
/// bound. Traffic on unbound sockets (outgoing connections use ephemeral ports) still counts toward the
/// process total, just not toward any port.
/// </summary>
internal sealed class TrafficTracker
{
    private const int RetainSeconds = 6;
    private const int WindowSeconds = 2;

    private readonly object _gate = new();
    private readonly Func<long> _clockMs;
    private readonly long _startSec;
    private readonly SortedDictionary<long, Dictionary<Key, long>> _seconds = new();

    private readonly record struct Key(string Protocol, TrafficDirection Direction, int Pid, int SourcePort, int DestPort);

    public TrafficTracker(Func<long>? clockMs = null)
    {
        _clockMs  = clockMs ?? (() => Environment.TickCount64);
        _startSec = _clockMs() / 1000;
    }

    /// <param name="atMs">
    /// When the traffic happened, in this tracker's clock. ETW delivers in bursts about a second late, so
    /// bucketing by arrival would pile a whole burst into the still-filling current second. Defaults to now.
    /// </param>
    public void Record(TrafficEvent e, long? atMs = null)
    {
        if (e.Bytes <= 0) return;

        long now = _clockMs();
        long sec = (atMs ?? now) / 1000;
        var key = new Key(e.Protocol, e.Direction, e.Pid, e.SourcePort, e.DestPort);
        lock (_gate)
        {
            if (!_seconds.TryGetValue(sec, out var bucket)) _seconds[sec] = bucket = new Dictionary<Key, long>();
            bucket[key] = bucket.GetValueOrDefault(key) + e.Bytes;

            long newest = now / 1000;
            while (_seconds.Count > 0 && _seconds.Keys.First() < newest - RetainSeconds)
                _seconds.Remove(_seconds.Keys.First());
        }
    }

    /// <summary>
    /// Rates over the last <see cref="WindowSeconds"/> COMPLETE seconds (the current second is still filling),
    /// resolved against the sockets that are bound right now.
    /// </summary>
    public TrafficSnapshot Snapshot(IEnumerable<PortEntry> bound)
    {
        long nowSec = _clockMs() / 1000;
        long from = nowSec - WindowSeconds, to = nowSec - 1;
        // Until a full window has elapsed since start, divide by the time actually observed.
        double divisor = Math.Clamp(nowSec - _startSec, 1, WindowSeconds);

        var boundPorts = bound
            .GroupBy(b => (b.Protocol, b.Pid))
            .ToDictionary(g => g.Key, g => g.Select(b => b.Port).ToHashSet());

        Dictionary<Key, long> sums = new();
        lock (_gate)
        {
            foreach (var (sec, bucket) in _seconds)
            {
                if (sec < from || sec > to) continue;
                foreach (var (key, bytes) in bucket) sums[key] = sums.GetValueOrDefault(key) + bytes;
            }
        }

        var byPid = new Dictionary<int, Rate>();
        var byPidPort = new Dictionary<(string, int, int), Rate>();
        foreach (var (key, bytes) in sums)
        {
            var rate = key.Direction == TrafficDirection.In ? new Rate(bytes / divisor, 0) : new Rate(0, bytes / divisor);
            byPid[key.Pid] = byPid.GetValueOrDefault(key.Pid) + rate;

            int? port = LocalPort(key, boundPorts);
            if (port is int p)
            {
                var k = (key.Protocol, key.Pid, p);
                byPidPort[k] = byPidPort.GetValueOrDefault(k) + rate;
            }
        }
        return new TrafficSnapshot(byPid, byPidPort);
    }

    private static int? LocalPort(Key key, Dictionary<(string, int), HashSet<int>> boundPorts)
    {
        if (!boundPorts.TryGetValue((key.Protocol, key.Pid), out var ports)) return null;

        (int first, int second) = key.Direction == TrafficDirection.Out
            ? (key.SourcePort, key.DestPort)
            : (key.DestPort, key.SourcePort);

        if (ports.Contains(first)) return first;
        if (ports.Contains(second)) return second;
        return null;
    }
}
