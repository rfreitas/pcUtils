using System.IO;
using PortWatch;
using Xunit;

public class PortPopupTrafficTests
{
    private static readonly PortEntry[] Entries =
    [
        new("UDP", 5353, 11, "chrome"),
        new("UDP", 5353, 12, "mDNSResponder"),
        new("TCP", 38810, 21, "VirtualDesktop.Streamer"),
        new("UDP", 38850, 21, "VirtualDesktop.Streamer"),
        new("TCP", 8080, 31, "idle-server"),
        new("TCP", 135, 4, "svchost", IsSystem: true),
        new("TCP", 49664, 5, "lsass", IsSystem: true),
    ];

    /// <summary>chrome receives, Virtual Desktop sends and receives, a server only sends, the rest idle.</summary>
    private static TrafficSnapshot Busy()
    {
        long ms = 10_000_000;
        var tracker = new TrafficTracker(() => ms);
        ms += 5_000;
        tracker.Record(new("UDP", TrafficDirection.In, 11, 55000, 5353, 8192));              // chrome: in on 5353
        tracker.Record(new("UDP", TrafficDirection.Out, 21, 38850, 60000, 3_000_000));        // VD: out on UDP 38850
        tracker.Record(new("UDP", TrafficDirection.In,  21, 60000, 38850, 40_000));           //     and in on UDP 38850
        tracker.Record(new("TCP", TrafficDirection.In,  21, 51000, 38810, 2048));             //     in on TCP 38810
        tracker.Record(new("TCP", TrafficDirection.Out, 31, 8080, 52000, 1000));              // server: out on 8080
        ms += 1_000;
        return tracker.Snapshot(Entries);
    }

    private static void OnSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        Assert.True(error is null, error?.ToString());
    }

    private static PortPopup Make()
    {
        var popup = new PortPopup();
        popup.SetRows(PortGrouper.Group(Entries));
        return popup;
    }

    [Fact]
    public void Processes_light_their_arrows_by_direction()
    {
        OnSta(() =>
        {
            var popup = Make();
            popup.UpdateTraffic(Busy());

            Assert.Equal("in",   popup.ProcessActivity("chrome"));
            Assert.Equal("both", popup.ProcessActivity("VirtualDesktop.Streamer"));
            Assert.Equal("out",  popup.ProcessActivity("idle-server"));
            Assert.Equal("idle", popup.ProcessActivity("svchost"));
            popup.Close();
        });
    }

    [Fact]
    public void Ports_light_their_arrows_and_their_number_changes_colour()
    {
        OnSta(() =>
        {
            var popup = Make();
            popup.UpdateTraffic(Busy());

            Assert.Equal("in",   popup.PortActivity("VirtualDesktop.Streamer", "TCP", 38810));
            Assert.Equal("both", popup.PortActivity("VirtualDesktop.Streamer", "UDP", 38850));
            Assert.Equal("out",  popup.PortActivity("idle-server", "TCP", 8080));
            Assert.Equal("idle", popup.PortActivity("svchost", "TCP", 135));

            Assert.Same(PortPopup.InBrush,   popup.PortBrush("VirtualDesktop.Streamer", "TCP", 38810));
            Assert.Same(PortPopup.BothBrush, popup.PortBrush("VirtualDesktop.Streamer", "UDP", 38850));
            Assert.Same(PortPopup.OutBrush,  popup.PortBrush("idle-server", "TCP", 8080));
            Assert.Same(PortPopup.IdleBrush, popup.PortBrush("svchost", "TCP", 135));
            popup.Close();
        });
    }

    [Fact]
    public void Traffic_changes_never_change_the_layout()
    {
        OnSta(() =>
        {
            var popup = Make();
            popup.UpdateLayout();
            popup.Show();
            var before = (popup.ActualWidth, popup.ActualHeight);
            string idleText = popup.AllText();

            popup.UpdateTraffic(Busy());
            popup.UpdateLayout();
            var busy = (popup.ActualWidth, popup.ActualHeight);

            popup.UpdateTraffic(TrafficSnapshot.Empty);
            popup.UpdateLayout();
            var after = (popup.ActualWidth, popup.ActualHeight);

            Assert.Equal(before, busy);
            Assert.Equal(before, after);
            Assert.Equal(idleText, popup.AllText());   // not one character of text changed either
            popup.Close();
        });
    }

    [Fact]
    public void Traffic_that_stops_returns_everything_to_idle()
    {
        OnSta(() =>
        {
            var popup = Make();
            popup.UpdateTraffic(Busy());
            popup.UpdateTraffic(TrafficSnapshot.Empty);

            Assert.Equal("idle", popup.ProcessActivity("chrome"));
            Assert.Equal("idle", popup.PortActivity("VirtualDesktop.Streamer", "UDP", 38850));
            Assert.Same(PortPopup.IdleBrush, popup.PortBrush("VirtualDesktop.Streamer", "UDP", 38850));
            popup.Close();
        });
    }

    [Fact]
    public void Unavailable_traffic_is_explained_in_the_footer()
    {
        OnSta(() =>
        {
            var popup = Make();
            popup.SetTrafficStatus("Traffic needs an elevated PortWatch.");
            string path = Path.Combine(AppContext.BaseDirectory, "portwatch_traffic_unavailable.png");
            popup.RenderToPng(path);
            Assert.True(new FileInfo(path).Length > 0);
            popup.Close();
        });
    }

    [Fact]
    public void Renders_the_live_traffic_view_to_a_png()
    {
        OnSta(() =>
        {
            var popup = Make();
            popup.UpdateTraffic(Busy());
            string path = Path.Combine(AppContext.BaseDirectory, "portwatch_traffic.png");
            popup.RenderToPng(path);

            Assert.True(new FileInfo(path).Length > 0);
            Console.WriteLine($"View: {path}");
            popup.Close();
        });
    }
}
