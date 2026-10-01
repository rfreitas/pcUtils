using System.Net;
using System.Net.Sockets;
using PortWatch;
using Xunit;

public class TrafficTrackerTests
{
    // A controllable clock so "seconds" can be advanced without sleeping.
    private sealed class Clock { public long Ms = 1_000_000; public long Now() => Ms; public void Advance(int seconds) => Ms += seconds * 1000L; }

    private static PortEntry Bound(string proto, int port, int pid, string name = "app") => new(proto, port, pid, name);

    private static (TrafficTracker tracker, Clock clock) Make()
    {
        var clock = new Clock();
        return (new TrafficTracker(clock.Now), clock);
    }

    [Fact]
    public void Nothing_recorded_is_idle()
    {
        var (t, _) = Make();
        Assert.False(t.Snapshot([]).ForProcess([1]).Active);
    }

    [Fact]
    public void Received_bytes_become_a_rate_over_the_completed_window()
    {
        var (t, clock) = Make();
        clock.Advance(5);                       // past the start-up divisor ramp: window is a full 2 s
        t.Record(new("UDP", TrafficDirection.In, 7, 50000, 5353, 2000));
        t.Record(new("UDP", TrafficDirection.In, 7, 50000, 5353, 2000));
        clock.Advance(1);                       // second completes

        var rate = t.Snapshot([Bound("UDP", 5353, 7)]).ForProcess([7]);

        Assert.Equal(2000, rate.InBps);         // 4000 bytes / 2 s
        Assert.Equal(0, rate.OutBps);
    }

    [Fact]
    public void The_second_still_filling_is_not_counted()
    {
        var (t, clock) = Make();
        clock.Advance(5);
        t.Record(new("TCP", TrafficDirection.Out, 7, 80, 51000, 5000));

        Assert.False(t.Snapshot([Bound("TCP", 80, 7)]).ForProcess([7]).Active);
    }

    [Fact]
    public void Old_traffic_falls_out_of_the_window()
    {
        var (t, clock) = Make();
        clock.Advance(5);
        t.Record(new("TCP", TrafficDirection.Out, 7, 80, 51000, 5000));
        clock.Advance(10);

        Assert.False(t.Snapshot([Bound("TCP", 80, 7)]).ForProcess([7]).Active);
    }

    [Fact]
    public void Send_is_attributed_to_the_source_port_when_it_is_bound()
    {
        var (t, clock) = Make();
        clock.Advance(5);
        t.Record(new("TCP", TrafficDirection.Out, 7, SourcePort: 8080, DestPort: 51000, Bytes: 1000));
        clock.Advance(1);

        var snap = t.Snapshot([Bound("TCP", 8080, 7)]);

        Assert.True(snap.ForPorts("TCP", [7], [8080]).OutBps > 0);
        Assert.False(snap.ForPorts("TCP", [7], [51000]).Active);
    }

    [Fact]
    public void Receive_is_attributed_to_the_destination_port_when_it_is_bound()
    {
        var (t, clock) = Make();
        clock.Advance(5);
        t.Record(new("TCP", TrafficDirection.In, 7, SourcePort: 51000, DestPort: 8080, Bytes: 1000));
        clock.Advance(1);

        Assert.True(t.Snapshot([Bound("TCP", 8080, 7)]).ForPorts("TCP", [7], [8080]).InBps > 0);
    }

    [Fact]
    public void Swapped_port_order_still_finds_the_bound_port()
    {
        // If the kernel reports the local port on the "wrong" side, the other candidate is tried.
        var (t, clock) = Make();
        clock.Advance(5);
        t.Record(new("UDP", TrafficDirection.In, 7, SourcePort: 5353, DestPort: 61000, Bytes: 1000));
        clock.Advance(1);

        Assert.True(t.Snapshot([Bound("UDP", 5353, 7)]).ForPorts("UDP", [7], [5353]).InBps > 0);
    }

    [Fact]
    public void Traffic_on_unbound_sockets_counts_for_the_process_but_no_port()
    {
        var (t, clock) = Make();
        clock.Advance(5);
        t.Record(new("TCP", TrafficDirection.Out, 7, 52000, 443, 4000));   // outgoing connection, ephemeral port
        clock.Advance(1);

        var snap = t.Snapshot([Bound("TCP", 8080, 7)]);

        Assert.True(snap.ForProcess([7]).OutBps > 0);
        Assert.False(snap.ForPorts("TCP", [7], [8080, 52000, 443]).Active);
    }

    [Fact]
    public void A_port_is_only_credited_to_the_process_that_has_it_bound()
    {
        var (t, clock) = Make();
        clock.Advance(5);
        t.Record(new("UDP", TrafficDirection.In, 7, 50000, 5353, 1000));    // pid 7 has 5353
        t.Record(new("UDP", TrafficDirection.In, 8, 50001, 5353, 1000));    // pid 8 also has 5353
        clock.Advance(1);

        var snap = t.Snapshot([Bound("UDP", 5353, 7), Bound("UDP", 5353, 8)]);

        Assert.True(snap.ForPorts("UDP", [7], [5353]).InBps > 0);
        Assert.True(snap.ForPorts("UDP", [8], [5353]).InBps > 0);
        Assert.False(snap.ForPorts("UDP", [9], [5353]).Active);
    }

    [Fact]
    public void Process_rate_sums_all_pids_of_the_row()
    {
        var (t, clock) = Make();
        clock.Advance(5);
        t.Record(new("TCP", TrafficDirection.In, 1, 1, 80, 2000));
        t.Record(new("TCP", TrafficDirection.In, 2, 1, 80, 2000));
        clock.Advance(1);

        Assert.Equal(2000, t.Snapshot([]).ForProcess([1, 2]).InBps);   // 4000 bytes / 2 s
    }

    [Fact]
    public void Start_up_divides_by_the_time_actually_observed()
    {
        var (t, clock) = Make();
        t.Record(new("TCP", TrafficDirection.In, 1, 1, 80, 3000));
        clock.Advance(1);                                              // only one second since start

        Assert.Equal(3000, t.Snapshot([]).ForProcess([1]).InBps);      // not 1500
    }

    [Fact]
    public void Empty_or_negative_events_are_ignored()
    {
        var (t, clock) = Make();
        clock.Advance(5);
        t.Record(new("TCP", TrafficDirection.In, 1, 1, 80, 0));
        t.Record(new("TCP", TrafficDirection.In, 1, 1, 80, -10));
        clock.Advance(1);

        Assert.False(t.Snapshot([]).ForProcess([1]).Active);
    }
}

public class TrafficFormatTests
{
    [Theory]
    [InlineData(0,        "0 B/s")]
    [InlineData(340,      "340 B/s")]
    [InlineData(1024,     "1.0 KB/s")]
    [InlineData(1536,     "1.5 KB/s")]
    [InlineData(40960,    "40 KB/s")]
    [InlineData(1258291,  "1.2 MB/s")]
    [InlineData(58720256, "56 MB/s")]
    public void Formats_rates(double bps, string expected) => Assert.Equal(expected, TrafficFormat.Bytes(bps));

    [Theory]
    [InlineData(0,  0,  "")]
    [InlineData(10, 0,  "↓")]
    [InlineData(0,  10, "↑")]
    [InlineData(10, 10, "↕")]
    public void Picks_an_arrow(double inBps, double outBps, string expected) =>
        Assert.Equal(expected, TrafficFormat.Arrow(new Rate(inBps, outBps)));

    [Fact]
    public void Summary_lists_only_active_directions()
    {
        Assert.Equal("", TrafficFormat.Summary(default));
        Assert.Equal("↓ 1.0 KB/s", TrafficFormat.Summary(new Rate(1024, 0)));
        Assert.Equal("↓ 1.0 KB/s  ↑ 340 B/s", TrafficFormat.Summary(new Rate(1024, 340)));
    }
}

/// <summary>Real ETW capture. Needs an elevated test process (kernel traces do); returns early otherwise.</summary>
public class TrafficCollectorLiveTests
{
    [Fact]
    public void Sees_real_loopback_udp_traffic_on_a_bound_port()
    {
        if (!TrafficCollector.IsElevated) return;   // cannot start a kernel session without elevation

        var log = new List<string>();
        using var collector = TrafficCollector.TryStart(log.Add, out var error);
        Assert.True(collector is not null, error);

        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        using var sender = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, port);
        byte[] payload = new byte[1000];

        // Drain on a thread so the socket buffer never fills; send ~100 KB/s for just over 3 seconds.
        var cts = new CancellationTokenSource();
        var drain = Task.Run(() =>
        {
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (!cts.IsCancellationRequested) { try { receiver.Receive(ref any); } catch { break; } }
        });
        var until = DateTime.UtcNow.AddMilliseconds(3300);
        while (DateTime.UtcNow < until) { sender.Send(payload, payload.Length, target); Thread.Sleep(10); }

        int pid = Environment.ProcessId;
        var snap = collector!.Tracker.Snapshot([new PortEntry("UDP", port, pid, "testhost")]);
        cts.Cancel(); receiver.Close();

        string diag = $"events={collector.EventCount}; log=[{string.Join(" | ", log)}]";
        Assert.True(collector.EventCount > 0, "no kernel network events were delivered. " + diag);
        Assert.True(snap.ForProcess([pid]).Active, "no traffic attributed to this process. " + diag);
        Assert.True(snap.ForPorts("UDP", [pid], [port]).InBps > 10_000, "receive rate on the bound port too low. " + diag);
    }
}
