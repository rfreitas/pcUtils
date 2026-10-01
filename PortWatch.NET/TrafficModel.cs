using System.Globalization;

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

    public void Record(TrafficEvent e)
    {
        if (e.Bytes <= 0) return;

        long sec = _clockMs() / 1000;
        var key = new Key(e.Protocol, e.Direction, e.Pid, e.SourcePort, e.DestPort);
        lock (_gate)
        {
            if (!_seconds.TryGetValue(sec, out var bucket)) _seconds[sec] = bucket = new Dictionary<Key, long>();
            bucket[key] = bucket.GetValueOrDefault(key) + e.Bytes;

            while (_seconds.Count > 0 && _seconds.Keys.First() < sec - RetainSeconds)
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

internal static class TrafficFormat
{
    /// <summary>"340 B/s", "1.2 KB/s", "56 MB/s" (1024-based, one decimal below 10).</summary>
    public static string Bytes(double bytesPerSecond)
    {
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        double v = bytesPerSecond;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }

        string number = i > 0 && v < 10 ? v.ToString("0.0", CultureInfo.InvariantCulture) : v.ToString("0", CultureInfo.InvariantCulture);
        return $"{number} {units[i]}";
    }

    /// <summary>↓ receiving, ↑ sending, ↕ both, empty when idle.</summary>
    public static string Arrow(Rate r) =>
        r.InBps > 0 && r.OutBps > 0 ? "↕" : r.InBps > 0 ? "↓" : r.OutBps > 0 ? "↑" : "";

    /// <summary>"↓ 1.2 MB/s  ↑ 40 KB/s" with only the active directions; empty when idle.</summary>
    public static string Summary(Rate r)
    {
        var parts = new List<string>();
        if (r.InBps > 0)  parts.Add($"↓ {Bytes(r.InBps)}");
        if (r.OutBps > 0) parts.Add($"↑ {Bytes(r.OutBps)}");
        return string.Join("  ", parts);
    }
}
