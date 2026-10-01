using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace PortWatch;

/// <summary>
/// Live per-socket traffic from the Windows kernel network ETW provider (the source Resource Monitor uses):
/// every TCP/UDP send and receive with its PID, ports and byte count. Starting a kernel trace requires an
/// elevated process, which is why PortWatch's manifest is requireAdministrator.
///
/// Meant to run only while the flyout is open (<see cref="TryStart"/> on open, <see cref="Dispose"/> on close),
/// so there is no background tracing cost.
/// </summary>
internal sealed class TrafficCollector : IDisposable
{
    // ETW session names are system-wide and creating one with an existing name STOPS the old session, so every
    // collector gets its own name: PortWatch-Network-<pid>-<n>. (Two collectors in one process happen in tests.)
    private const string SessionPrefix = "PortWatch-Network-";
    private static int _sessionCounter;

    private TraceEventSession? _session;
    private Thread? _pump;

    public TrafficTracker Tracker { get; } = new();
    public long EventCount => Interlocked.Read(ref _events);
    private long _events;

    public static bool IsElevated
    {
        get { try { return TraceEventSession.IsElevated() == true; } catch { return false; } }
    }

    /// <summary>Starts a session. Returns null and the reason in <paramref name="error"/> if tracing is unavailable.</summary>
    public static TrafficCollector? TryStart(Action<string>? log, out string? error)
    {
        error = null;
        if (!IsElevated)
        {
            error = "Traffic needs an elevated PortWatch.";
            log?.Invoke("Traffic: not elevated, kernel network tracing unavailable.");
            return null;
        }

        var collector = new TrafficCollector();
        try
        {
            StopOrphanedSessions(log);
            collector.Start();
            log?.Invoke("Traffic: kernel network session started.");
            return collector;
        }
        catch (Exception ex)
        {
            error = "Traffic unavailable: " + ex.Message;
            log?.Invoke($"Traffic: could not start session: {ex}");
            collector.Dispose();
            return null;
        }
    }

    /// <summary>
    /// A PortWatch that was killed (not closed) leaves its kernel session running, buffers and all, until reboot.
    /// Stop any session of ours whose owning process no longer exists.
    /// </summary>
    private static void StopOrphanedSessions(Action<string>? log)
    {
        try
        {
            foreach (string name in TraceEventSession.GetActiveSessionNames())
            {
                if (!name.StartsWith(SessionPrefix, StringComparison.Ordinal)) continue;

                string[] parts = name[SessionPrefix.Length..].Split('-');
                if (parts.Length < 1 || !int.TryParse(parts[0], out int pid)) continue;
                if (pid == Environment.ProcessId || IsRunning(pid)) continue;

                using var orphan = new TraceEventSession(name, TraceEventSessionOptions.Attach);
                orphan.Stop();
                log?.Invoke($"Traffic: stopped orphaned session {name}.");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Traffic: orphan cleanup failed: {ex.Message}");
        }
    }

    private static bool IsRunning(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    private void Start()
    {
        string name = $"{SessionPrefix}{Environment.ProcessId}-{Interlocked.Increment(ref _sessionCounter)}";
        _session = new TraceEventSession(name) { StopOnDispose = true };
        _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

        var k = _session.Source.Kernel;
        k.TcpIpSend     += d => Add("TCP", TrafficDirection.Out, d.ProcessID, d.sport, d.dport, d.size, d.TimeStamp);
        k.TcpIpRecv     += d => Add("TCP", TrafficDirection.In,  d.ProcessID, d.sport, d.dport, d.size, d.TimeStamp);
        k.TcpIpSendIPV6 += d => Add("TCP", TrafficDirection.Out, d.ProcessID, d.sport, d.dport, d.size, d.TimeStamp);
        k.TcpIpRecvIPV6 += d => Add("TCP", TrafficDirection.In,  d.ProcessID, d.sport, d.dport, d.size, d.TimeStamp);
        k.UdpIpSend     += d => Add("UDP", TrafficDirection.Out, d.ProcessID, d.sport, d.dport, d.size, d.TimeStamp);
        k.UdpIpRecv     += d => Add("UDP", TrafficDirection.In,  d.ProcessID, d.sport, d.dport, d.size, d.TimeStamp);
        k.UdpIpSendIPV6 += d => Add("UDP", TrafficDirection.Out, d.ProcessID, d.sport, d.dport, d.size, d.TimeStamp);
        k.UdpIpRecvIPV6 += d => Add("UDP", TrafficDirection.In,  d.ProcessID, d.sport, d.dport, d.size, d.TimeStamp);

        // A dedicated thread: Process() blocks for the life of the session, and parking it on the shared
        // thread pool can delay its start (and so event delivery) when the pool is busy.
        var session = _session;
        _pump = new Thread(() => { try { session.Source.Process(); } catch { /* torn down by Dispose */ } })
        {
            IsBackground = true,
            Name = "PortWatch ETW",
        };
        _pump.Start();
    }

    private void Add(string protocol, TrafficDirection dir, int pid, int sport, int dport, int size, DateTime eventTime)
    {
        Interlocked.Increment(ref _events);
        // Place the event where it happened: now, minus how late it was delivered (never in the future).
        long age = Math.Max(0, (long)(DateTime.Now - eventTime).TotalMilliseconds);
        Tracker.Record(new TrafficEvent(protocol, dir, pid, sport, dport, size), Environment.TickCount64 - age);
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { /* already stopped */ }
        _pump?.Join(2000);
        _session = null;
        _pump = null;
    }
}
