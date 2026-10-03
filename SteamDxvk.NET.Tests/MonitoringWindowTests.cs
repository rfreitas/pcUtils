using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using SteamDxvk;
using Xunit;

namespace SteamDxvk.Tests;

/// <summary>The window's running-game awareness, driven by a fake process list (no real games, Steam or crash logs involved).</summary>
[Collection(WpfCollection.Name)]   // see MainWindowTests: windows can't be built from several threads at once
public class MonitoringWindowTests
{
    private const string Win = @"C:\Windows";
    private static readonly (string, string) D3D12Core = ("D3D12Core.dll", @"C:\Windows\System32\D3D12Core.dll");
    private static readonly (string, string) System11 = ("d3d11.dll", @"C:\Windows\System32\d3d11.dll");

    private static string Temp(string kind) => Path.Combine(Path.GetTempPath(), $"SteamDxvk{kind}_{Guid.NewGuid():N}.json");

    private static MainWindow Window(string? history = null) => new()
    {
        Settings = new GameSettings(Temp("Settings")),
        History = new RunHistory(history ?? Temp("History")),
    };

    private static GameScan Unreal(string name = "Racer", GfxApi apis = GfxApi.D3D11 | GfxApi.D3D12) =>
        TestData.Scan(name, apis, apis, engine: "Unreal");

    private static string Shown(TextBlock block) => new TextRange(block.ContentStart, block.ContentEnd).Text;

    /// <summary>Wires a fake process list and a stand-in for the crash judge; returns what a launch asked Steam for.</summary>
    private static (FakeProbe Probe, List<string> Launched) Setup(MainWindow w, GameScan scan, Func<string, DateTime, DateTime, string?, CrashAssessment>? assess = null)
    {
        var probe = new FakeProbe();
        var launched = new List<string>();
        w.LoadScans([scan]);
        w.SelectByName(scan.Name);
        w.AttachMonitor(new GameMonitor(probe, windowsDir: Win));
        w.AssessRun = assess ?? ((_, _, _, _) => new CrashAssessment(RunOutcome.Ok, "ran for 5 min"));
        w.StartGame = (id, args) => launched.Add(TestData.Describe(id, args));
        return (probe, launched);
    }

    private static string RowText(MainWindow w, string name) =>
        w.Rows.Children.Cast<Border>().Select(b => Shown((TextBlock)((Grid)b.Child).Children[0])).First(t => t.Contains(name));

    private static void Poll(MainWindow w)
    {
        w.PollMonitor();
        MainWindow.Pump();
    }

    private static RunChoice Choice(MainWindow w, RunMode mode) => w.RunBox.Items.Cast<RunChoice>().First(c => c.Mode == mode);

    private static Color ColorOf(RunChoice c) => ((SolidColorBrush)c.Brush).Color;

    private static readonly Color Pink = Color.FromRgb(0xFF, 0x8F, 0xA3);
    private static readonly Color Amber = Color.FromRgb(0xFF, 0xB4, 0x54);

    /// <summary>Launch from the app with a Run as, then have the game appear, run, and disappear.</summary>
    private static void RunOnce(MainWindow w, GameScan scan, FakeProbe probe, RunMode mode, params (string Name, string Path)[] modules)
    {
        w.SetRunMode(mode);
        w.LaunchAsync().GetAwaiter().GetResult();
        probe.Add(10, scan.Primary!.Path, DateTime.UtcNow, modules);
        Poll(w);
        probe.Remove(10);
        Poll(w);
    }

    [Fact]
    public void A_running_game_shows_its_api_in_the_panel_and_a_green_marker_in_the_list()
    {
        Sta.Run(() =>
        {
            var scan = Unreal();
            var w = Window();
            var (probe, _) = Setup(w, scan);

            probe.Add(10, scan.Primary!.Path, DateTime.UtcNow, D3D12Core);
            Poll(w);

            string status = Shown(w.RunStatusText);
            Assert.Contains("Running", status);
            Assert.Contains("D3D12", status);
            Assert.StartsWith("\u25CF", RowText(w, "Racer"));
            Assert.Contains("is running", w.LogBox.Text);
            Assert.Contains("running D3D12", w.LogBox.Text);
        });
    }

    [Fact]
    public void While_the_api_is_still_unknown_the_panel_says_it_is_detecting_without_a_false_warning()
    {
        Sta.Run(() =>
        {
            var scan = Unreal();
            var w = Window();
            var (probe, _) = Setup(w, scan);
            w.SetRunMode(RunMode.D3D11);
            w.LaunchAsync().GetAwaiter().GetResult();

            probe.Add(10, scan.Primary!.Path, DateTime.UtcNow, ("kernel32.dll", @"C:\Windows\System32\kernel32.dll"));
            Poll(w);

            string status = Shown(w.RunStatusText);
            Assert.Contains("detecting", status);
            Assert.DoesNotContain("Requested", status);
        });
    }

    [Fact]
    public void A_mismatch_between_the_request_and_the_running_api_is_a_warning()
    {
        Sta.Run(() =>
        {
            var scan = Unreal();
            var w = Window();
            var (probe, launched) = Setup(w, scan);
            w.SetRunMode(RunMode.D3D11);
            w.LaunchAsync().GetAwaiter().GetResult();
            Assert.Equal([$"{scan.AppId} -dx11"], launched);

            probe.Add(10, scan.Primary!.Path, DateTime.UtcNow, D3D12Core);   // the game ignored the flag and went D3D12
            Poll(w);

            Assert.Contains("Requested D3D11, but the game is running D3D12", Shown(w.RunStatusText));
            Assert.StartsWith("\u26A0", RowText(w, "Racer"));
            Assert.Contains("WARNING: Requested D3D11", w.LogBox.Text);
        });
    }

    [Fact]
    public void When_the_running_api_is_what_was_requested_there_is_no_warning()
    {
        Sta.Run(() =>
        {
            var scan = Unreal();
            var w = Window();
            var (probe, _) = Setup(w, scan);
            w.SetRunMode(RunMode.D3D12);
            w.LaunchAsync().GetAwaiter().GetResult();

            probe.Add(10, scan.Primary!.Path, DateTime.UtcNow, D3D12Core);
            Poll(w);

            Assert.DoesNotContain("Requested", Shown(w.RunStatusText));
            Assert.StartsWith("\u25CF", RowText(w, "Racer"));
        });
    }

    [Fact]
    public void A_game_started_outside_the_app_had_no_flag_so_nothing_is_compared()
    {
        Sta.Run(() =>
        {
            var scan = Unreal();
            var w = Window();
            var (probe, _) = Setup(w, scan);
            w.SetRunMode(RunMode.D3D11);   // saved choice, but the game was started from Steam directly: the flag was never passed

            probe.Add(10, scan.Primary!.Path, DateTime.UtcNow, D3D12Core);
            Poll(w);

            Assert.DoesNotContain("Requested", Shown(w.RunStatusText));
            Assert.Contains("no API requested", w.LogBox.Text);
        });
    }

    [Fact]
    public void The_marker_and_status_go_away_when_the_game_closes()
    {
        Sta.Run(() =>
        {
            var scan = Unreal();
            var w = Window();
            var (probe, _) = Setup(w, scan);
            probe.Add(10, scan.Primary!.Path, DateTime.UtcNow, D3D12Core);
            Poll(w);

            probe.Remove(10);
            Poll(w);

            Assert.DoesNotContain("\u25CF", RowText(w, "Racer"));
            Assert.DoesNotContain("Running", Shown(w.RunStatusText));
            Assert.Contains("closed normally", w.LogBox.Text);
        });
    }

    [Fact]
    public void A_crash_is_remembered_for_that_setup_and_flags_it_in_the_dropdown_and_the_warning()
    {
        Sta.Run(() =>
        {
            var scan = Unreal();
            var w = Window();
            var (probe, _) = Setup(w, scan, (_, _, _, _) => new(RunOutcome.Crashed, "DXGI_ERROR_UNSUPPORTED"));

            RunOnce(w, scan, probe, RunMode.D3D12, D3D12Core);

            var crashed = Choice(w, RunMode.D3D12);
            Assert.Contains("\u2716 crashed", crashed.Text);
            Assert.Equal(Pink, ColorOf(crashed));
            Assert.Equal("D3D11", Choice(w, RunMode.D3D11).Text);   // other modes are untouched
            string warning = Shown(w.WarningText);
            Assert.Contains("Crashed last time with Run as D3D12", warning);
            Assert.Contains("DXGI_ERROR_UNSUPPORTED", warning);
            Assert.Equal("Launch anyway", w.LaunchBtn.Content);
            Assert.Contains("CRASHED", w.LogBox.Text);
        });
    }

    [Fact]
    public void An_early_exit_is_an_amber_warning_not_a_crash()
    {
        Sta.Run(() =>
        {
            var scan = Unreal();
            var w = Window();
            var (probe, _) = Setup(w, scan, (_, _, _, _) => new(RunOutcome.ExitedEarly, "exited after 20s with no crash report"));

            RunOnce(w, scan, probe, RunMode.Vulkan, ("vulkan-1.dll", @"C:\Windows\System32\vulkan-1.dll"));

            var choice = Choice(w, RunMode.Vulkan);
            Assert.Contains("\u26A0 exited early", choice.Text);
            Assert.Equal(Amber, ColorOf(choice));
            Assert.Contains("Exited early last time with Run as Vulkan", Shown(w.WarningText));
        });
    }

    [Fact]
    public void A_crash_belongs_to_the_setup_so_another_mode_is_not_flagged_and_coming_back_to_it_is()
    {
        Sta.Run(() =>
        {
            var scan = Unreal();
            var w = Window();
            var (probe, _) = Setup(w, scan, (_, _, _, _) => new(RunOutcome.Crashed, "boom"));
            RunOnce(w, scan, probe, RunMode.D3D12, D3D12Core);

            w.SetRunMode(RunMode.D3D11);
            Assert.DoesNotContain("last time", Shown(w.WarningText));
            Assert.Equal("Launch via Steam", w.LaunchBtn.Content);

            w.SetRunMode(RunMode.D3D12);
            Assert.Contains("Crashed last time", Shown(w.WarningText));
            Assert.Equal("Launch anyway", w.LaunchBtn.Content);
        });
    }

    [Fact]
    public void A_later_good_run_of_the_same_setup_clears_the_flag()
    {
        Sta.Run(() =>
        {
            var scan = Unreal();
            var w = Window();
            var outcomes = new Queue<CrashAssessment>([new(RunOutcome.Crashed, "boom"), new(RunOutcome.Ok, "ran for 12 min")]);
            var (probe, _) = Setup(w, scan, (_, _, _, _) => outcomes.Dequeue());

            RunOnce(w, scan, probe, RunMode.D3D12, D3D12Core);
            Assert.Contains("crashed", Choice(w, RunMode.D3D12).Text);

            RunOnce(w, scan, probe, RunMode.D3D12, D3D12Core);

            Assert.Equal("D3D12", Choice(w, RunMode.D3D12).Text);
            Assert.DoesNotContain("last time", Shown(w.WarningText));
            Assert.Equal("Launch via Steam", w.LaunchBtn.Content);
            Assert.Contains("Last run with this setup", Shown(w.RunStatusText));
            Assert.Contains("ran for 12 min", Shown(w.RunStatusText));
        });
    }

    [Fact]
    public void A_run_started_outside_the_app_is_judged_against_the_default_setup()
    {
        Sta.Run(() =>
        {
            var scan = Unreal();
            var w = Window();
            var (probe, _) = Setup(w, scan, (_, _, _, _) => new(RunOutcome.Crashed, "boom"));

            probe.Add(10, scan.Primary!.Path, DateTime.UtcNow, D3D12Core);   // no launch from the app
            Poll(w);
            probe.Remove(10);
            Poll(w);

            Assert.Contains("crashed", Choice(w, RunMode.Default).Text);
            Assert.Equal("D3D11", Choice(w, RunMode.D3D11).Text);
        });
    }

    [Fact]
    public void Crash_memory_survives_closing_and_reopening_the_app()
    {
        string history = Temp("History");
        var scan = Unreal();
        Sta.Run(() =>
        {
            var w = Window(history);
            var (probe, _) = Setup(w, scan, (_, _, _, _) => new(RunOutcome.Crashed, "boom"));
            RunOnce(w, scan, probe, RunMode.D3D12, D3D12Core);
        });

        Sta.Run(() =>
        {
            var w = Window(history);   // a fresh window reading the same history file
            w.LoadScans([scan]);
            w.SelectByName(scan.Name);
            w.SetRunMode(RunMode.D3D12);

            Assert.Contains("crashed", Choice(w, RunMode.D3D12).Text);
            Assert.Contains("Crashed last time", Shown(w.WarningText));
        });
        File.Delete(history);
    }

    [Fact]
    public void The_crash_judge_is_told_which_exe_when_it_started_and_where_dxvks_logs_are()
    {
        using var game = new TempDir();
        string exe = game.Write(@"bin\Game-Win64-Shipping.exe", "x");
        game.Write(@"bin\d3d11.dll", "MZ this is DXVK");
        var scan = new GameScan(new Game(5, "Judged", game.Path))
        {
            Exes = [new ExeInfo(exe, 64, 1, GfxApi.D3D11, GfxApi.D3D11)],
            Engine = "Unreal",
        };
        scan.Status = DxvkInstaller.StatusOf(scan);
        (string Exe, DateTime Start, DateTime End, string? Dir)? seen = null;

        Sta.Run(() =>
        {
            var w = Window();
            var (probe, _) = Setup(w, scan, (e, s, end, d) => { seen = (e, s, end, d); return new(RunOutcome.Ok, "fine"); });
            var started = DateTime.UtcNow.AddMinutes(-2);
            probe.Add(10, exe, started, System11);
            Poll(w);
            probe.Remove(10);
            Poll(w);

            Assert.Equal(exe, seen!.Value.Exe);
            Assert.Equal(started, seen.Value.Start);
            Assert.True(seen.Value.End >= started);
            Assert.Equal(game.Combine("bin"), seen.Value.Dir);
        });
    }

    [Fact]
    public void Unreal_ships_a_dropdown_for_every_mode_and_each_is_judged_on_its_own_history()
    {
        Sta.Run(() =>
        {
            var w = Window();
            var scan = Unreal();
            w.LoadScans([scan]);
            w.SelectByName(scan.Name);

            Assert.Equal(4, w.RunBox.Items.Count);
            Assert.All(w.RunBox.Items.Cast<RunChoice>(), c => Assert.Equal(c.Mode.ToString(), c.Text));   // nothing flagged on a clean history
        });
    }
}
