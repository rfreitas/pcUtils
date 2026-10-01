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

    /// <summary>A snapshot where chrome receives, Virtual Desktop sends and receives, everything else is idle.</summary>
    private static TrafficSnapshot Busy()
    {
        long ms = 10_000_000;
        var tracker = new TrafficTracker(() => ms);
        ms += 5_000;
        // chrome: 4 KB/s in on 5353
        tracker.Record(new("UDP", TrafficDirection.In, 11, 55000, 5353, 8192));
        // Virtual Desktop streamer: heavy out on UDP 38850, light in on TCP 38810
        tracker.Record(new("UDP", TrafficDirection.Out, 21, 38850, 60000, 3_000_000));
        tracker.Record(new("UDP", TrafficDirection.In,  21, 60000, 38850, 40_000));
        tracker.Record(new("TCP", TrafficDirection.In,  21, 51000, 38810, 2048));
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

    [Fact]
    public void Busy_processes_show_rates_and_idle_ones_show_nothing()
    {
        OnSta(() =>
        {
            var popup = new PortPopup();
            popup.SetRows(PortGrouper.Group(Entries));
            popup.UpdateTraffic(Busy());

            Assert.Contains("↓ 4.0 KB/s", popup.RowText("chrome"));
            Assert.DoesNotContain("↑", popup.RowText("chrome"));

            string vd = popup.RowText("VirtualDesktop.Streamer");
            Assert.Contains("↑ 1.4 MB/s", vd);
            Assert.Contains("↓", vd);

            Assert.DoesNotContain("/s", popup.RowText("idle-server"));
            Assert.DoesNotContain("/s", popup.RowText("svchost"));
            popup.Close();
        });
    }

    [Fact]
    public void Only_the_ports_that_move_data_get_an_arrow()
    {
        OnSta(() =>
        {
            var popup = new PortPopup();
            popup.SetRows(PortGrouper.Group(Entries));
            popup.UpdateTraffic(Busy());

            string vd = popup.RowText("VirtualDesktop.Streamer");
            Assert.Contains("38810 ↓", vd);    // TCP 38810 receives
            Assert.Contains("38850 ↕", vd);    // UDP 38850 sends and receives
            Assert.DoesNotContain("8080 ↓", popup.RowText("idle-server"));
            Assert.Equal("   TCP 8080", popup.RowText("idle-server").Substring(popup.RowText("idle-server").IndexOf("   TCP")));
            popup.Close();
        });
    }

    [Fact]
    public void Traffic_that_stops_clears_the_arrows_again()
    {
        OnSta(() =>
        {
            var popup = new PortPopup();
            popup.SetRows(PortGrouper.Group(Entries));
            popup.UpdateTraffic(Busy());
            Assert.Contains("/s", popup.RowText("chrome"));

            popup.UpdateTraffic(TrafficSnapshot.Empty);

            Assert.DoesNotContain("/s", popup.RowText("chrome"));
            Assert.DoesNotContain("↓", popup.RowText("chrome"));
            popup.Close();
        });
    }

    [Fact]
    public void Unavailable_traffic_is_explained_in_the_footer()
    {
        OnSta(() =>
        {
            var popup = new PortPopup();
            popup.SetTrafficStatus("Traffic needs an elevated PortWatch.");
            // Rendering must not throw with the warning footer.
            string path = Path.Combine(AppContext.BaseDirectory, "portwatch_traffic_unavailable.png");
            popup.SetRows(PortGrouper.Group(Entries));
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
            var popup = new PortPopup();
            popup.SetRows(PortGrouper.Group(Entries));
            popup.UpdateTraffic(Busy());
            string path = Path.Combine(AppContext.BaseDirectory, "portwatch_traffic.png");
            popup.RenderToPng(path);

            Assert.True(new FileInfo(path).Length > 0);
            Console.WriteLine($"View: {path}");
            popup.Close();
        });
    }
}
