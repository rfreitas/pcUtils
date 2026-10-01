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
    private const string SessionName = "PortWatch-Network";

    private TraceEventSession? _session;
    private Task? _pump;

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

    private void Start()
    {
        // A session left behind by a crashed run keeps the name; the default Create option replaces it.
        _session = new TraceEventSession(SessionName) { StopOnDispose = true };
        _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

        var k = _session.Source.Kernel;
        k.TcpIpSend     += d => Add("TCP", TrafficDirection.Out, d.ProcessID, d.sport, d.dport, d.size);
        k.TcpIpRecv     += d => Add("TCP", TrafficDirection.In,  d.ProcessID, d.sport, d.dport, d.size);
        k.TcpIpSendIPV6 += d => Add("TCP", TrafficDirection.Out, d.ProcessID, d.sport, d.dport, d.size);
        k.TcpIpRecvIPV6 += d => Add("TCP", TrafficDirection.In,  d.ProcessID, d.sport, d.dport, d.size);
        k.UdpIpSend     += d => Add("UDP", TrafficDirection.Out, d.ProcessID, d.sport, d.dport, d.size);
        k.UdpIpRecv     += d => Add("UDP", TrafficDirection.In,  d.ProcessID, d.sport, d.dport, d.size);
        k.UdpIpSendIPV6 += d => Add("UDP", TrafficDirection.Out, d.ProcessID, d.sport, d.dport, d.size);
        k.UdpIpRecvIPV6 += d => Add("UDP", TrafficDirection.In,  d.ProcessID, d.sport, d.dport, d.size);

        _pump = Task.Run(() => _session.Source.Process());
    }

    private void Add(string protocol, TrafficDirection dir, int pid, int sport, int dport, int size)
    {
        Interlocked.Increment(ref _events);
        Tracker.Record(new TrafficEvent(protocol, dir, pid, sport, dport, size));
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { /* already stopped */ }
        try { _pump?.Wait(2000); } catch { /* the pump faults when the session is torn down */ }
        _session = null;
        _pump = null;
    }
}
