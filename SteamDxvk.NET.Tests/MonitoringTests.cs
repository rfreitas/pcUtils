using System.Diagnostics.Eventing.Reader;
using System.IO;
using SteamDxvk;
using Xunit;

namespace SteamDxvk.Tests;

internal sealed class FakeProbe : IProcessProbe
{
    public List<ProbedProcess> Processes { get; } = [];

    public IReadOnlyList<ProbedProcess> Snapshot() => [.. Processes];

    public void Add(int pid, string path, DateTime? start = null, params (string Name, string Path)[] modules) =>
        Processes.Add(new ProbedProcess(pid, path, () => start ?? DateTime.UtcNow, () => modules.Select(m => new ModuleInfo(m.Name, m.Path)).ToList()));

    public void Remove(int pid) => Processes.RemoveAll(p => p.Pid == pid);
}

public class ApiDetectorTests
{
    private const string Win = @"C:\Windows";
    private static readonly (string, string) System11 = ("d3d11.dll", @"C:\Windows\System32\d3d11.dll");
    private static readonly (string, string) GameDxvk11 = ("d3d11.dll", @"C:\Games\G\Binaries\d3d11.dll");
    private static readonly (string, string) Vulkan = ("vulkan-1.dll", @"C:\Windows\System32\vulkan-1.dll");
    private static readonly (string, string) D3D12Core = ("D3D12Core.dll", @"C:\Windows\System32\D3D12Core.dll");

    private static RunningApi Detect(GfxApi dxvk, params (string Name, string Path)[] modules) =>
        ApiDetector.Detect(modules.Select(m => new ModuleInfo(m.Name, m.Path)).ToList(), dxvk, Win);

    [Fact]
    public void D3D12Core_means_a_d3d12_device_exists_whatever_else_is_loaded()
    {
        var api = Detect(GfxApi.None, System11, Vulkan, D3D12Core);
        Assert.Equal(GfxApi.D3D12, api.Api);
        Assert.True(api.Confirmed);
        Assert.False(api.ViaDxvk);
    }

    [Fact]
    public void A_dxvk_device_means_the_api_runs_through_dxvk_even_though_vulkan_and_a_game_folder_d3d11_are_loaded()
    {
        var api = Detect(GfxApi.D3D11, GameDxvk11, Vulkan);
        Assert.Equal(GfxApi.D3D11, api.Api);
        Assert.True(api.ViaDxvk);
        Assert.True(api.Confirmed);
        Assert.Equal("D3D11 via DXVK", api.Label);
    }

    [Fact]
    public void Dxvks_dll_merely_being_loaded_is_not_evidence_it_is_used()
    {
        // Real case: Session launched with -vulkan still loads DXVK's d3d11.dll (the exe links it statically) but creates no device.
        var api = Detect(GfxApi.None, GameDxvk11, Vulkan);
        Assert.Equal(GfxApi.Vulkan, api.Api);
        Assert.False(api.ViaDxvk);
        Assert.False(api.Confirmed);
        Assert.Equal("Vulkan (likely)", api.Label);
    }

    [Fact]
    public void The_system_d3d11_alone_is_a_likely_native_d3d11()
    {
        var api = Detect(GfxApi.None, System11);
        Assert.Equal(GfxApi.D3D11, api.Api);
        Assert.False(api.Confirmed);
        Assert.Equal("D3D11 (likely)", api.Label);
    }

    [Fact]
    public void A_game_folder_d3d11_alone_says_nothing_so_the_api_stays_unknown() =>
        Assert.Equal(GfxApi.None, Detect(GfxApi.None, GameDxvk11).Api);

    [Fact]
    public void The_system_d3d9_is_a_likely_d3d9() =>
        Assert.Equal(GfxApi.D3D9, Detect(GfxApi.None, ("d3d9.dll", @"C:\Windows\System32\d3d9.dll")).Api);

    [Fact]
    public void Opengl_alone_is_a_last_resort_guess() =>
        Assert.Equal(GfxApi.OpenGL, Detect(GfxApi.None, ("OPENGL32.dll", @"C:\Windows\System32\OPENGL32.dll")).Api);

    [Fact]
    public void Module_names_are_matched_case_insensitively() =>
        Assert.Equal(GfxApi.D3D12, Detect(GfxApi.None, ("d3d12core.DLL", @"X:\d3d12core.dll")).Api);

    [Fact]
    public void No_graphics_modules_is_unknown()
    {
        var api = Detect(GfxApi.None, ("kernel32.dll", @"C:\Windows\System32\kernel32.dll"));
        Assert.Equal(GfxApi.None, api.Api);
        Assert.Equal("unknown API", api.Label);
        Assert.Equal(GfxApi.None, Detect(GfxApi.None).Api);
    }

    [Theory]
    [InlineData(RunMode.Default, GfxApi.D3D12, false)]    // nothing requested, nothing to compare
    [InlineData(RunMode.D3D11, GfxApi.D3D11, false)]
    [InlineData(RunMode.D3D12, GfxApi.D3D12, false)]
    [InlineData(RunMode.D3D11, GfxApi.D3D12, true)]
    [InlineData(RunMode.D3D12, GfxApi.D3D11, true)]
    [InlineData(RunMode.D3D11, GfxApi.Vulkan, true)]
    [InlineData(RunMode.Vulkan, GfxApi.Vulkan, false)]
    [InlineData(RunMode.Vulkan, GfxApi.D3D11, true)]
    [InlineData(RunMode.D3D11, GfxApi.None, false)]       // can't tell yet: no false alarm
    public void Mismatch_compares_request_with_reality(RunMode requested, GfxApi running, bool expectWarning)
    {
        var api = running == GfxApi.None ? RunningApi.Unknown : new RunningApi(running, false, true, "test");
        Assert.Equal(expectWarning, ApiDetector.Mismatch(requested, api) is not null);
    }

    [Fact]
    public void D3D11_requested_and_served_by_dxvk_matches()
    {
        Assert.Null(ApiDetector.Mismatch(RunMode.D3D11, new RunningApi(GfxApi.D3D11, true, true, "x")));
    }

    [Fact]
    public void Vulkan_requested_but_running_through_dxvk_is_a_mismatch_because_it_is_not_the_games_own_renderer()
    {
        var warning = ApiDetector.Mismatch(RunMode.Vulkan, new RunningApi(GfxApi.D3D11, true, true, "x"));
        Assert.Equal("Requested Vulkan, but the game is running D3D11 via DXVK.", warning);
    }
}

public class RunHistoryTests
{
    private static RunRecord Record(int app, string config, RunOutcome outcome, string detail = "d", DateTime? when = null) =>
        new(app, config, outcome, detail, when ?? DateTime.UtcNow);

    [Fact]
    public void A_bad_run_is_a_problem_until_a_later_good_run_of_the_same_setup_clears_it()
    {
        using var dir = new TempDir();
        var history = new RunHistory(dir.Combine("h.json"));
        const string config = "D3D12|no-dxvk";

        history.Add(Record(1, config, RunOutcome.Crashed, "boom"));
        Assert.Equal("boom", history.Problem(1, config)!.Detail);

        history.Add(Record(1, config, RunOutcome.Ok));
        Assert.Null(history.Problem(1, config));
        Assert.Equal(RunOutcome.Ok, history.Last(1, config)!.Outcome);
    }

    [Fact]
    public void Results_belong_to_a_setup_and_a_game_not_to_the_game_as_a_whole()
    {
        using var dir = new TempDir();
        var history = new RunHistory(dir.Combine("h.json"));
        history.Add(Record(1, "D3D12|dxvk-async-gplAuto", RunOutcome.Crashed));

        Assert.NotNull(history.Problem(1, "D3D12|dxvk-async-gplAuto"));
        Assert.Null(history.Problem(1, "D3D11|dxvk-async-gplAuto"));   // other mode: fine
        Assert.Null(history.Problem(1, "D3D12|no-dxvk"));              // same mode without DXVK: fine
        Assert.Null(history.Problem(2, "D3D12|dxvk-async-gplAuto"));   // other game: fine
    }

    [Fact]
    public void Early_exits_are_problems_too()
    {
        using var dir = new TempDir();
        var history = new RunHistory(dir.Combine("h.json"));
        history.Add(Record(1, "Vulkan|no-dxvk", RunOutcome.ExitedEarly));
        Assert.Equal(RunOutcome.ExitedEarly, history.Problem(1, "Vulkan|no-dxvk")!.Outcome);
    }

    [Fact]
    public void History_survives_a_restart_with_outcome_names_in_the_file()
    {
        using var dir = new TempDir();
        string path = dir.Combine("h.json");
        var when = new DateTime(2026, 10, 3, 13, 13, 28, DateTimeKind.Utc);
        new RunHistory(path).Add(Record(861650, "D3D12|dxvk-async-gplAuto", RunOutcome.Crashed, "DXGI_ERROR_UNSUPPORTED", when));

        var reloaded = new RunHistory(path).Problem(861650, "D3D12|dxvk-async-gplAuto")!;
        Assert.Equal(RunOutcome.Crashed, reloaded.Outcome);
        Assert.Equal("DXGI_ERROR_UNSUPPORTED", reloaded.Detail);
        Assert.Equal(when, reloaded.WhenUtc);
        Assert.Contains("\"Crashed\"", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[{\"Outcome\":\"Banana\"}]")]
    public void A_damaged_file_starts_empty_and_can_be_written_again(string content)
    {
        using var dir = new TempDir();
        dir.Write("h.json", content);
        var history = new RunHistory(dir.Combine("h.json"));
        Assert.Null(history.Last(1, "x"));
        history.Add(Record(1, "x", RunOutcome.Ok));
        Assert.NotNull(new RunHistory(dir.Combine("h.json")).Last(1, "x"));
    }

    [Fact]
    public void Only_the_most_recent_entries_are_kept()
    {
        using var dir = new TempDir();
        var history = new RunHistory(dir.Combine("h.json"));
        for (int i = 0; i < 305; i++) history.Add(Record(i, "c", RunOutcome.Ok));
        Assert.Null(history.Last(0, "c"));      // oldest dropped
        Assert.NotNull(history.Last(304, "c"));
    }

    [Fact]
    public void Config_keys_include_the_mode_and_the_dxvk_state()
    {
        Assert.Equal("D3D11|no-dxvk", ConfigKey.Of(RunMode.D3D11, null));
        Assert.Equal("Default|dxvk-manual", ConfigKey.Of(RunMode.Default, new DxvkStatus(false, "DXVK (manual)", null)));
        Assert.Equal("D3D11|dxvk-async-gplFalse", ConfigKey.Of(RunMode.D3D11, new DxvkStatus(true, "x", new DxvkOptions(true, "False", false))));
        Assert.Equal("D3D11|dxvk-official-gplAuto", ConfigKey.Of(RunMode.D3D11, new DxvkStatus(true, "x", new DxvkOptions())));
    }

    [Fact]
    public void Config_keys_ignore_the_hud_and_the_dxvk_version()
    {
        string a = ConfigKey.Of(RunMode.D3D11, new DxvkStatus(true, "DXVK v1", new DxvkOptions(Hud: false)));
        string b = ConfigKey.Of(RunMode.D3D11, new DxvkStatus(true, "DXVK v2", new DxvkOptions(Hud: true)));
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("D3D12|no-dxvk", "Run as D3D12")]
    [InlineData("D3D11|dxvk-async-gplAuto", "Run as D3D11 + DXVK async")]
    [InlineData("Default|dxvk-official-gplAuto", "Run as Default + DXVK")]
    public void Describe_reads_naturally(string key, string expected) => Assert.Equal(expected, ConfigKey.Describe(key));
}

public class CrashDetectorTests
{
    private static readonly DateTime Start = new(2026, 10, 3, 13, 0, 0, DateTimeKind.Utc);
    private static string? NoEvent(string exe, DateTime a, DateTime b) => null;

    private static string UnrealExe(TempDir dir) => dir.Write(@"game\Binaries\Win64\SessionGame-Win64-Shipping.exe", "x");

    private static void CrashReport(TempDir localAppData, string project, string error, DateTime when)
    {
        string path = localAppData.Write($@"{project}\Saved\Crashes\UE4CC-Windows-ABC_0000\CrashContext.runtime-xml",
            $"<FGenericCrashContext><RuntimeProperties><ErrorMessage>{error}</ErrorMessage></RuntimeProperties></FGenericCrashContext>");
        File.SetLastWriteTimeUtc(path, when);
    }

    [Fact]
    public void An_unreal_crash_report_written_during_the_run_means_it_crashed()
    {
        using var dir = new TempDir();
        using var local = new TempDir();
        CrashReport(local, "SessionGame", "hr failed ... DXGI_ERROR_UNSUPPORTED", Start.AddSeconds(30));

        var a = CrashDetector.Assess(UnrealExe(dir), Start, Start.AddMinutes(5), null, local.Path, NoEvent);

        Assert.Equal(RunOutcome.Crashed, a.Outcome);
        Assert.Contains("DXGI_ERROR_UNSUPPORTED", a.Detail);
        Assert.StartsWith("Unreal crash report:", a.Detail);
    }

    [Fact]
    public void A_crash_report_from_before_the_run_is_ignored()
    {
        using var dir = new TempDir();
        using var local = new TempDir();
        CrashReport(local, "SessionGame", "old crash", Start.AddHours(-2));

        var a = CrashDetector.Assess(UnrealExe(dir), Start, Start.AddMinutes(5), null, local.Path, NoEvent);

        Assert.Equal(RunOutcome.Ok, a.Outcome);
    }

    [Fact]
    public void A_long_unreal_crash_message_is_trimmed_and_whitespace_collapsed()
    {
        using var dir = new TempDir();
        using var local = new TempDir();
        CrashReport(local, "SessionGame", "line one\n   line two " + new string('x', 400), Start.AddSeconds(5));

        var a = CrashDetector.Assess(UnrealExe(dir), Start, Start.AddMinutes(5), null, local.Path, NoEvent);

        Assert.StartsWith("Unreal crash report: line one line two", a.Detail);
        Assert.EndsWith("...", a.Detail);
        Assert.True(a.Detail.Length < 200);
    }

    [Fact]
    public void A_windows_crash_event_means_it_crashed_even_without_an_unreal_report()
    {
        using var dir = new TempDir();
        using var local = new TempDir();
        string exe = dir.Write("Game.exe", "x");

        var a = CrashDetector.Assess(exe, Start, Start.AddMinutes(5), null, local.Path,
            (name, s, e) => name == "Game.exe" ? "Windows logged a crash (faulting module nvwgf2umx.dll)" : null);

        Assert.Equal(RunOutcome.Crashed, a.Outcome);
        Assert.Contains("nvwgf2umx.dll", a.Detail);
    }

    [Fact]
    public void Dxvk_seeing_a_d3d12_device_means_it_crashed()
    {
        using var dir = new TempDir();
        using var local = new TempDir();
        string exe = dir.Write("Game-Win64-Shipping.exe", "x");
        string log = dir.Write("Game-Win64-Shipping_dxgi.log", "info:  Game: x\nerr:   DXGI: CreateSwapChainForHwnd: Unsupported device type\n");
        File.SetLastWriteTimeUtc(log, Start.AddMinutes(2));   // written during the run (the file's real time would be "now", not Start-relative)

        var a = CrashDetector.Assess(exe, Start, Start.AddMinutes(5), dir.Path, local.Path, NoEvent);

        Assert.Equal(RunOutcome.Crashed, a.Outcome);
        Assert.Contains("D3D12", a.Detail);
    }

    [Fact]
    public void A_very_short_run_with_no_evidence_is_flagged_as_exited_early()
    {
        using var dir = new TempDir();
        using var local = new TempDir();   // the real Session -vulkan case: quit after 20 s, no crash report at all

        var a = CrashDetector.Assess(UnrealExe(dir), Start, Start.AddSeconds(20), null, local.Path, NoEvent);

        Assert.Equal(RunOutcome.ExitedEarly, a.Outcome);
        Assert.Contains("20s", a.Detail);
    }

    [Theory]
    [InlineData(44, RunOutcome.ExitedEarly)]
    [InlineData(45, RunOutcome.Ok)]
    [InlineData(300, RunOutcome.Ok)]
    public void The_early_exit_threshold_is_45_seconds(int seconds, RunOutcome expected)
    {
        using var dir = new TempDir();
        using var local = new TempDir();
        Assert.Equal(expected, CrashDetector.Assess(UnrealExe(dir), Start, Start.AddSeconds(seconds), null, local.Path, NoEvent).Outcome);
    }

    [Theory]
    [InlineData(300, "5 min")]
    [InlineData(90, "90s")]
    [InlineData(7200, "2 h")]
    public void A_good_run_reports_how_long_it_lasted(int seconds, string expected)
    {
        using var dir = new TempDir();
        using var local = new TempDir();
        var a = CrashDetector.Assess(UnrealExe(dir), Start, Start.AddSeconds(seconds), null, local.Path, NoEvent);
        Assert.Equal($"ran for {expected}", a.Detail);
    }

    [Fact]
    public void Real_evidence_outranks_a_short_run()
    {
        using var dir = new TempDir();
        using var local = new TempDir();
        CrashReport(local, "SessionGame", "fatal", Start.AddSeconds(3));
        var a = CrashDetector.Assess(UnrealExe(dir), Start, Start.AddSeconds(5), null, local.Path, NoEvent);
        Assert.Equal(RunOutcome.Crashed, a.Outcome);   // not merely "exited early"
    }

    [Fact]
    public void Exes_that_are_not_unreal_shipping_builds_have_no_unreal_report_to_find()
    {
        using var dir = new TempDir();
        using var local = new TempDir();
        CrashReport(local, "Game", "fatal", Start.AddSeconds(3));
        string exe = dir.Write("Game.exe", "x");   // name has no -Win64- part
        Assert.Null(CrashDetector.UnrealCrash(exe, Start, local.Path));
    }

    [Fact]
    public void The_event_log_query_is_valid()
    {
        // FindWindowsCrashEvent swallows query errors (it must never break the app), so a typo in the XPath would silently
        // disable crash detection. Run the exact query against the real log and require that it is accepted.
        string query = CrashDetector.EventQuery(DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow);
        using var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, query));
        reader.ReadEvent();   // throws EventLogException if the XPath is malformed
    }

    [Fact]
    public void Finding_a_windows_crash_for_an_exe_that_never_ran_gives_nothing() =>
        Assert.Null(CrashDetector.FindWindowsCrashEvent("definitely-not-a-real-game-12345.exe", DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow));
}

public class GameMonitorTests
{
    private const string Root = @"C:\Games\Session";
    private const string Exe = @"C:\Games\Session\Binaries\Game-Win64-Shipping.exe";
    private const string Win = @"C:\Windows";
    private static readonly (string, string) D3D12Core = ("D3D12Core.dll", @"C:\Windows\System32\D3D12Core.dll");

    private static GameScan Scan(int id = 1, string root = Root) =>
        new(new Game(id, "Session", root)) { Exes = [new ExeInfo(Exe, 64, 1, GfxApi.D3D11, GfxApi.D3D11)] };

    private sealed class Events
    {
        public List<RunSession> Started { get; } = [];
        public List<(RunSession Session, string Label)> Api { get; } = [];
        public List<(RunSession Session, DateTime End)> Ended { get; } = [];

        public Events(GameMonitor m)
        {
            m.Started += s => Started.Add(s);
            m.ApiChanged += s => Api.Add((s, s.Api.Label));
            m.Ended += (s, end) => Ended.Add((s, end));
        }
    }

    [Fact]
    public void A_process_under_a_game_folder_starts_a_session_with_its_real_start_time()
    {
        var probe = new FakeProbe();
        var start = new DateTime(2026, 10, 3, 13, 25, 31, DateTimeKind.Utc);
        probe.Add(100, Exe, start);
        var monitor = new GameMonitor(probe, windowsDir: Win);
        var events = new Events(monitor);

        monitor.Poll([Scan()]);

        var session = Assert.Single(events.Started);
        Assert.Equal(start, session.StartUtc);
        Assert.Equal(1, session.Scan.AppId);
        Assert.Contains(100, session.Pids);
    }

    [Fact]
    public void Processes_outside_every_game_folder_are_ignored()
    {
        var probe = new FakeProbe();
        probe.Add(1, @"C:\Windows\explorer.exe");
        probe.Add(2, @"C:\Games\SessionOther\x.exe");   // shares a prefix with the root but is not inside it
        var monitor = new GameMonitor(probe, windowsDir: Win);
        var events = new Events(monitor);

        monitor.Poll([Scan()]);

        Assert.Empty(events.Started);
        Assert.Empty(monitor.Running);
    }

    [Fact]
    public void A_session_is_reported_once_however_many_polls_see_it()
    {
        var probe = new FakeProbe();
        probe.Add(100, Exe);
        var monitor = new GameMonitor(probe, windowsDir: Win);
        var events = new Events(monitor);

        for (int i = 0; i < 6; i++) monitor.Poll([Scan()]);

        Assert.Single(events.Started);
        Assert.Empty(events.Ended);
    }

    [Fact]
    public void The_api_is_detected_when_the_game_loads_its_renderer_and_announced_on_change()
    {
        var probe = new FakeProbe();
        probe.Add(100, Exe, null, ("kernel32.dll", @"C:\Windows\System32\kernel32.dll"));
        var monitor = new GameMonitor(probe, windowsDir: Win);
        var events = new Events(monitor);
        monitor.Poll([Scan()]);
        Assert.Empty(events.Api);   // nothing graphical yet: no announcement, no false alarm

        probe.Remove(100);
        probe.Add(100, Exe, null, D3D12Core);
        monitor.Poll([Scan()]);

        var change = Assert.Single(events.Api);
        Assert.Equal("D3D12", change.Label);
        Assert.Equal(GfxApi.D3D12, monitor.For(1)!.Api.Api);
    }

    [Fact]
    public void A_best_guess_is_replaced_when_conclusive_evidence_appears()
    {
        var probe = new FakeProbe();
        probe.Add(100, Exe, null, ("vulkan-1.dll", @"C:\Windows\System32\vulkan-1.dll"));
        var monitor = new GameMonitor(probe, windowsDir: Win);
        var events = new Events(monitor);
        monitor.Poll([Scan()]);

        probe.Remove(100);
        probe.Add(100, Exe, null, ("vulkan-1.dll", @"C:\Windows\System32\vulkan-1.dll"), D3D12Core);
        monitor.Poll([Scan()]);

        Assert.Equal(["Vulkan (likely)", "D3D12"], events.Api.Select(a => a.Label));
    }

    [Fact]
    public void The_game_exe_is_asked_before_the_launcher_stub_and_a_stub_without_modules_is_harmless()
    {
        var probe = new FakeProbe();
        probe.Add(50, @"C:\Games\Session\Game.exe");   // launcher stub, no graphics modules
        probe.Add(100, Exe, null, D3D12Core);
        var monitor = new GameMonitor(probe, windowsDir: Win);
        var events = new Events(monitor);

        monitor.Poll([Scan()]);

        Assert.Single(events.Started);   // two processes, one game, one session
        Assert.Equal(GfxApi.D3D12, monitor.For(1)!.Api.Api);
        Assert.Equal(Exe, monitor.For(1)!.ExePath);
    }

    [Fact]
    public void A_session_ends_only_when_the_last_process_of_the_game_is_gone()
    {
        var probe = new FakeProbe();
        probe.Add(50, @"C:\Games\Session\Game.exe");
        probe.Add(100, Exe);
        var clock = new DateTime(2026, 10, 3, 14, 0, 0, DateTimeKind.Utc);
        var monitor = new GameMonitor(probe, () => clock, Win);
        var events = new Events(monitor);
        monitor.Poll([Scan()]);

        probe.Remove(50);
        monitor.Poll([Scan()]);
        Assert.Empty(events.Ended);

        probe.Remove(100);
        clock = clock.AddMinutes(1);
        monitor.Poll([Scan()]);

        var ended = Assert.Single(events.Ended);
        Assert.Equal(clock, ended.End);
        Assert.Empty(monitor.Running);
    }

    [Fact]
    public void Starting_again_after_an_end_is_a_new_session()
    {
        var probe = new FakeProbe();
        var monitor = new GameMonitor(probe, windowsDir: Win);
        var events = new Events(monitor);

        probe.Add(100, Exe);
        monitor.Poll([Scan()]);
        probe.Remove(100);
        monitor.Poll([Scan()]);
        probe.Add(200, Exe);
        monitor.Poll([Scan()]);

        Assert.Equal(2, events.Started.Count);
        Assert.Single(events.Ended);
    }

    [Fact]
    public void Two_games_at_once_are_tracked_separately()
    {
        var probe = new FakeProbe();
        probe.Add(1, Exe);
        probe.Add(2, @"C:\Games\Other\o.exe");
        var monitor = new GameMonitor(probe, windowsDir: Win);
        var events = new Events(monitor);

        monitor.Poll([Scan(1), Scan(2, @"C:\Games\Other")]);

        Assert.Equal([1, 2], events.Started.Select(s => s.Scan.AppId).Order());
    }

    [Fact]
    public void A_process_whose_modules_cannot_be_read_does_not_break_the_poll()
    {
        var probe = new FakeProbe();
        probe.Processes.Add(new ProbedProcess(100, Exe, () => DateTime.UtcNow, () => throw new System.ComponentModel.Win32Exception(5)));
        var monitor = new GameMonitor(probe, windowsDir: Win);
        var events = new Events(monitor);

        monitor.Poll([Scan()]);

        Assert.Single(events.Started);                  // still known to be running
        Assert.Equal(GfxApi.None, monitor.For(1)!.Api.Api);   // just can't tell the API
    }

    [Fact]
    public void A_fresh_dxvk_log_in_the_game_folder_makes_it_d3d11_via_dxvk()
    {
        using var game = new TempDir();
        string exe = game.Write(@"bin\Game-Win64-Shipping.exe", "x");
        game.Write(@"bin\d3d11.dll", "MZ this is DXVK");   // recognised as a DXVK install
        game.Write(@"bin\Game-Win64-Shipping_d3d11.log", "info:  Game: Game.exe\ninfo:  Creating device:\ninfo:  Test GPU:\n");
        var scan = new GameScan(new Game(1, "G", game.Path)) { Exes = [new ExeInfo(exe, 64, 1, GfxApi.D3D11, GfxApi.D3D11)] };
        var probe = new FakeProbe();
        probe.Add(100, exe, DateTime.UtcNow.AddMinutes(-1), ("d3d11.dll", game.Combine(@"bin\d3d11.dll")), ("vulkan-1.dll", @"C:\Windows\System32\vulkan-1.dll"));
        var monitor = new GameMonitor(probe, windowsDir: Win);

        monitor.Poll([scan]);

        Assert.Equal("D3D11 via DXVK", monitor.For(1)!.Api.Label);
    }

    [Fact]
    public void A_stale_dxvk_log_from_an_earlier_run_does_not_count()
    {
        using var game = new TempDir();
        string exe = game.Write(@"bin\Game-Win64-Shipping.exe", "x");
        game.Write(@"bin\d3d11.dll", "MZ this is DXVK");
        string log = game.Write(@"bin\Game-Win64-Shipping_d3d11.log", "info:  Game: Game.exe\ninfo:  Creating device:\ninfo:  Test GPU:\n");
        File.SetLastWriteTimeUtc(log, DateTime.UtcNow.AddHours(-1));
        var scan = new GameScan(new Game(1, "G", game.Path)) { Exes = [new ExeInfo(exe, 64, 1, GfxApi.D3D11, GfxApi.D3D11)] };
        var probe = new FakeProbe();
        probe.Add(100, exe, DateTime.UtcNow.AddSeconds(-20), ("d3d11.dll", game.Combine(@"bin\d3d11.dll")), ("vulkan-1.dll", @"C:\Windows\System32\vulkan-1.dll"));
        var monitor = new GameMonitor(probe, windowsDir: Win);

        monitor.Poll([scan]);

        Assert.Equal("Vulkan (likely)", monitor.For(1)!.Api.Label);   // the -vulkan Session case: DXVK loaded but unused
    }

    [Theory]
    [InlineData(@"C:\Games\Session\a.exe", @"C:\Games\Session", true)]
    [InlineData(@"c:\games\session\a.exe", @"C:\Games\Session\", true)]
    [InlineData(@"C:\Games\SessionOther\a.exe", @"C:\Games\Session", false)]
    [InlineData(@"C:\Games\a.exe", @"C:\Games\Session", false)]
    public void IsUnder_respects_folder_boundaries_and_case(string path, string root, bool expected) =>
        Assert.Equal(expected, GameMonitor.IsUnder(path, root));
}
