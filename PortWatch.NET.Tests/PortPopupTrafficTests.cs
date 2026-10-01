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

    private sealed class Clock { public long Ms = 5_000_000; public long Now() => Ms; }

    private static void OnSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        Assert.True(error is null, error?.ToString());
    }

    private static PortPopup Make(ArrowTarget target, Clock? clock = null)
    {
        var popup = new PortPopup((clock ?? new Clock()).Now) { Target = target };
        popup.SetRows(PortGrouper.Group(Entries));
        return popup;
    }

    // -------------------------------------------------------------------------------------------------------
    // Arrows on ports (default): ports carry the arrows and the colour; processes stay plain
    // -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Port_mode_lights_port_arrows_and_colours_the_numbers()
    {
        OnSta(() =>
        {
            var popup = Make(ArrowTarget.Port);
            popup.UpdateTraffic(Busy());

            Assert.Equal("in",   popup.PortActivity("chrome", "UDP", 5353));
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
    public void Port_mode_has_no_process_arrows()
    {
        OnSta(() =>
        {
            var popup = Make(ArrowTarget.Port);
            popup.UpdateTraffic(Busy());

            Assert.Equal("off", popup.ProcessActivity("chrome"));
            popup.Close();
        });
    }

    // -------------------------------------------------------------------------------------------------------
    // Arrows on processes: one pair per row; port numbers are left alone
    // -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Process_mode_lights_process_arrows_by_direction()
    {
        OnSta(() =>
        {
            var popup = Make(ArrowTarget.Process);
            popup.UpdateTraffic(Busy());

            Assert.Equal("in",   popup.ProcessActivity("chrome"));
            Assert.Equal("both", popup.ProcessActivity("VirtualDesktop.Streamer"));
            Assert.Equal("out",  popup.ProcessActivity("idle-server"));
            Assert.Equal("idle", popup.ProcessActivity("svchost"));
            popup.Close();
        });
    }

    [Fact]
    public void Process_mode_leaves_ports_uncoloured_and_without_arrows()
    {
        OnSta(() =>
        {
            var popup = Make(ArrowTarget.Process);
            popup.UpdateTraffic(Busy());

            Assert.Equal("off", popup.PortActivity("VirtualDesktop.Streamer", "UDP", 38850));
            Assert.Same(PortPopup.IdleBrush, popup.PortBrush("VirtualDesktop.Streamer", "UDP", 38850));
            Assert.Same(PortPopup.IdleBrush, popup.PortBrush("idle-server", "TCP", 8080));
            popup.Close();
        });
    }

    [Fact]
    public void Only_the_chosen_level_reserves_arrow_space()
    {
        OnSta(() =>
        {
            string onPorts = Make(ArrowTarget.Port).AllText();
            string onProcesses = Make(ArrowTarget.Process).AllText();

            // Port mode: a slot before every port number. Process mode: a slot after every process name only.
            Assert.Equal(Entries.Select(e => (e.ProcessName, e.Protocol, e.Port)).Distinct().Count(), Count(onPorts, '↓'));
            Assert.Equal(Entries.Select(e => e.ProcessName).Distinct().Count(), Count(onProcesses, '↓'));
        });

        static int Count(string text, char c) => text.Count(ch => ch == c);
    }

    // -------------------------------------------------------------------------------------------------------
    // Stability and the 2 s hold
    // -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ArrowTarget.Port)]
    [InlineData(ArrowTarget.Process)]
    public void Traffic_changes_never_change_the_layout(ArrowTarget target)
    {
        OnSta(() =>
        {
            var clock = new Clock();
            var popup = Make(target, clock);
            popup.UpdateLayout();
            popup.Show();
            var before = (popup.ActualWidth, popup.ActualHeight);
            string idleText = popup.AllText();

            popup.UpdateTraffic(Busy());
            popup.UpdateLayout();
            var busy = (popup.ActualWidth, popup.ActualHeight);

            clock.Ms += PortPopup.HoldMs + 1;
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
    public void Arrows_stay_lit_for_two_seconds_after_the_traffic_stops()
    {
        OnSta(() =>
        {
            var clock = new Clock();
            var popup = Make(ArrowTarget.Port, clock);
            popup.UpdateTraffic(Busy());                       // lit at t = 0
            Assert.Equal("in", popup.PortActivity("chrome", "UDP", 5353));

            clock.Ms += 500;
            popup.UpdateTraffic(TrafficSnapshot.Empty);
            Assert.Equal("in", popup.PortActivity("chrome", "UDP", 5353));      // held at 0.5 s
            Assert.Same(PortPopup.InBrush, popup.PortBrush("chrome", "UDP", 5353));   // the number holds its colour too

            clock.Ms += 1_499;                                                  // 1.999 s
            popup.UpdateTraffic(TrafficSnapshot.Empty);
            Assert.Equal("in", popup.PortActivity("chrome", "UDP", 5353));

            clock.Ms += 1;                                                      // 2.000 s: gone
            popup.UpdateTraffic(TrafficSnapshot.Empty);
            Assert.Equal("idle", popup.PortActivity("chrome", "UDP", 5353));
            Assert.Same(PortPopup.IdleBrush, popup.PortBrush("chrome", "UDP", 5353));
            popup.Close();
        });
    }

    [Fact]
    public void The_hold_applies_to_process_arrows_too()
    {
        OnSta(() =>
        {
            var clock = new Clock();
            var popup = Make(ArrowTarget.Process, clock);
            popup.UpdateTraffic(Busy());

            clock.Ms += 1_900;
            popup.UpdateTraffic(TrafficSnapshot.Empty);
            Assert.Equal("in", popup.ProcessActivity("chrome"));

            clock.Ms += 100;
            popup.UpdateTraffic(TrafficSnapshot.Empty);
            Assert.Equal("idle", popup.ProcessActivity("chrome"));
            popup.Close();
        });
    }

    [Fact]
    public void Fresh_traffic_restarts_the_hold()
    {
        OnSta(() =>
        {
            var clock = new Clock();
            var popup = Make(ArrowTarget.Port, clock);
            popup.UpdateTraffic(Busy());

            clock.Ms += 1_500;
            popup.UpdateTraffic(Busy());                       // still moving data: hold restarts from here
            clock.Ms += 1_500;                                 // 3 s after the first, 1.5 s after the last
            popup.UpdateTraffic(TrafficSnapshot.Empty);

            Assert.Equal("in", popup.PortActivity("chrome", "UDP", 5353));
            popup.Close();
        });
    }

    [Fact]
    public void Directions_are_held_independently()
    {
        OnSta(() =>
        {
            var clock = new Clock();
            var popup = Make(ArrowTarget.Port, clock);
            popup.UpdateTraffic(Busy());                       // VD UDP 38850: both

            // 1.5 s later only the incoming half is still moving.
            clock.Ms += 1_500;
            long ms = 10_000_000;
            var tracker = new TrafficTracker(() => ms);
            ms += 5_000;
            tracker.Record(new("UDP", TrafficDirection.In, 21, 60000, 38850, 40_000));
            ms += 1_000;
            popup.UpdateTraffic(tracker.Snapshot(Entries));
            Assert.Equal("both", popup.PortActivity("VirtualDesktop.Streamer", "UDP", 38850));

            clock.Ms += 600;                                   // 2.1 s since the last OUT, 0.6 s since the last IN
            popup.UpdateTraffic(TrafficSnapshot.Empty);
            Assert.Equal("in", popup.PortActivity("VirtualDesktop.Streamer", "UDP", 38850));
            popup.Close();
        });
    }

    [Fact]
    public void Unavailable_traffic_is_explained_in_the_footer()
    {
        OnSta(() =>
        {
            var popup = Make(ArrowTarget.Port);
            popup.SetTrafficStatus("Traffic needs an elevated PortWatch.");
            string path = Path.Combine(AppContext.BaseDirectory, "portwatch_traffic_unavailable.png");
            popup.RenderToPng(path);
            Assert.True(new FileInfo(path).Length > 0);
            popup.Close();
        });
    }

    [Theory]
    [InlineData(ArrowTarget.Port,    "portwatch_traffic_ports.png")]
    [InlineData(ArrowTarget.Process, "portwatch_traffic_processes.png")]
    public void Renders_the_live_traffic_view_to_a_png(ArrowTarget target, string file)
    {
        OnSta(() =>
        {
            var popup = Make(target);
            popup.UpdateTraffic(Busy());
            string path = Path.Combine(AppContext.BaseDirectory, file);
            popup.RenderToPng(path);

            Assert.True(new FileInfo(path).Length > 0);
            Console.WriteLine($"View: {path}");
            popup.Close();
        });
    }
}

public class ActivityLatchTests
{
    [Fact]
    public void Lit_while_active_and_for_the_hold_afterwards()
    {
        var latch = new ActivityLatch();
        Assert.Equal((true, false), latch.Observe(new Rate(10, 0), nowMs: 1_000, holdMs: 2_000));
        Assert.Equal((true, false), latch.Observe(default,         nowMs: 2_999, holdMs: 2_000));
        Assert.Equal((false, false), latch.Observe(default,        nowMs: 3_000, holdMs: 2_000));
    }

    [Fact]
    public void Never_active_is_never_lit_even_at_clock_zero()
    {
        Assert.Equal((false, false), new ActivityLatch().Observe(default, nowMs: 0, holdMs: 2_000));
    }

    [Fact]
    public void In_and_out_are_independent()
    {
        var latch = new ActivityLatch();
        latch.Observe(new Rate(0, 10), 1_000, 2_000);
        latch.Observe(new Rate(10, 0), 2_500, 2_000);

        Assert.Equal((true, true),   latch.Observe(default, 2_900, 2_000));   // out lit until 3.0 s, in until 4.5 s
        Assert.Equal((true, false),  latch.Observe(default, 3_100, 2_000));
    }
}

public class SettingsTests
{
    private static string Temp() => Path.Combine(Path.GetTempPath(), "pw_settings_" + Guid.NewGuid().ToString("N"), "PortWatch.ini");

    [Fact]
    public void Defaults_to_arrows_on_ports()
    {
        Assert.Equal(ArrowTarget.Port, Settings.Load(Temp()).Arrows);
    }

    [Fact]
    public void Round_trips_through_the_file()
    {
        string path = Temp();
        try
        {
            new Settings(path) { Arrows = ArrowTarget.Process }.Save();
            Assert.Equal(ArrowTarget.Process, Settings.Load(path).Arrows);

            new Settings(path) { Arrows = ArrowTarget.Port }.Save();
            Assert.Equal(ArrowTarget.Port, Settings.Load(path).Arrows);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }
    }

    [Theory]
    [InlineData("ArrowsOn=Banana")]
    [InlineData("ArrowsOn=99")]
    [InlineData("garbage without equals")]
    [InlineData("")]
    public void Damaged_values_fall_back_to_the_default(string content)
    {
        string path = Temp();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, content);
            Assert.Equal(ArrowTarget.Port, Settings.Load(path).Arrows);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }
    }
}
