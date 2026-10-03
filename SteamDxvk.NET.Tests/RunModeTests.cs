using System.IO;
using System.Runtime.CompilerServices;
using SteamDxvk;
using Xunit;

namespace SteamDxvk.Tests;

/// <summary>Tests must never write into the real %LOCALAPPDATA%\SteamDxvk\SteamDxvk.log.</summary>
internal static class TestLogRedirect
{
    [ModuleInitializer]
    internal static void Init() => Logger.Redirect(Path.Combine(Path.GetTempPath(), "SteamDxvkTests_logs"));
}

public class EngineFlagsTests
{
    [Theory]
    [InlineData("Unreal", RunMode.D3D11, "-dx11")]
    [InlineData("Unreal", RunMode.D3D12, "-dx12")]
    [InlineData("Unreal", RunMode.Vulkan, "-vulkan")]
    [InlineData("Unity", RunMode.D3D11, "-force-d3d11")]
    [InlineData("Unity", RunMode.D3D12, "-force-d3d12")]
    [InlineData("Unity", RunMode.Vulkan, "-force-vulkan")]
    [InlineData("unreal", RunMode.D3D11, "-dx11")]   // engine names are case-insensitive
    public void Known_engines_have_a_flag_per_mode(string engine, RunMode mode, string expected) =>
        Assert.Equal(expected, EngineFlags.Flag(engine, mode));

    [Theory]
    [InlineData("Unreal", RunMode.Default)]
    [InlineData("", RunMode.D3D11)]
    [InlineData("Godot", RunMode.D3D11)]
    public void No_flag_for_default_or_unknown_engines(string engine, RunMode mode) =>
        Assert.Null(EngineFlags.Flag(engine, mode));

    [Fact]
    public void Available_modes_follow_the_engine()
    {
        Assert.Equal([RunMode.Default, RunMode.D3D11, RunMode.D3D12, RunMode.Vulkan], EngineFlags.Available("Unreal"));
        Assert.Equal([RunMode.Default], EngineFlags.Available(""));
        Assert.Equal([RunMode.Default], EngineFlags.Available("Godot"));
    }

    [Fact]
    public void Every_flag_is_a_plain_token_safe_to_put_in_a_steam_url()
    {
        foreach (var engine in new[] { "Unreal", "Unity" })
            foreach (var mode in new[] { RunMode.D3D11, RunMode.D3D12, RunMode.Vulkan })
                Assert.Matches("^-[a-z0-9-]+$", EngineFlags.Flag(engine, mode)!);   // no spaces, slashes or quotes
    }
}

public class GameSettingsTests
{
    [Fact]
    public void Defaults_to_Default_and_survives_a_round_trip()
    {
        using var dir = new TempDir();
        string path = dir.Combine("settings.json");

        Assert.Equal(RunMode.Default, new GameSettings(path).GetRunMode(861650));

        new GameSettings(path).SetRunMode(861650, RunMode.D3D11);
        new GameSettings(path).SetRunMode(1245620, RunMode.Vulkan);

        var reloaded = new GameSettings(path);
        Assert.Equal(RunMode.D3D11, reloaded.GetRunMode(861650));
        Assert.Equal(RunMode.Vulkan, reloaded.GetRunMode(1245620));
        Assert.Equal(RunMode.Default, reloaded.GetRunMode(1));
    }

    [Fact]
    public void Setting_Default_forgets_the_game()
    {
        using var dir = new TempDir();
        string path = dir.Combine("settings.json");
        var settings = new GameSettings(path);
        settings.SetRunMode(7, RunMode.D3D11);
        settings.SetRunMode(7, RunMode.Default);

        Assert.Equal(RunMode.Default, new GameSettings(path).GetRunMode(7));
        Assert.DoesNotContain("\"7\"", File.ReadAllText(path));
    }

    [Fact]
    public void The_file_is_readable_text_with_the_mode_by_name()
    {
        using var dir = new TempDir();
        string path = dir.Combine("settings.json");
        new GameSettings(path).SetRunMode(861650, RunMode.D3D11);
        Assert.Contains("\"D3D11\"", File.ReadAllText(path));
    }

    [Fact]
    public void The_use_dxvk_choice_is_null_until_made_then_remembered_either_way()
    {
        using var dir = new TempDir();
        string path = dir.Combine("settings.json");
        Assert.Null(new GameSettings(path).GetUseDxvk(5));

        new GameSettings(path).SetUseDxvk(5, true);
        new GameSettings(path).SetUseDxvk(6, false);

        var reloaded = new GameSettings(path);
        Assert.True(reloaded.GetUseDxvk(5));
        Assert.False(reloaded.GetUseDxvk(6));   // a deliberate "no" is a choice too, not "undecided"
        Assert.Null(reloaded.GetUseDxvk(7));
    }

    [Fact]
    public void The_choice_options_and_run_as_are_kept_side_by_side()
    {
        using var dir = new TempDir();
        string path = dir.Combine("settings.json");
        var settings = new GameSettings(path);
        settings.SetRunMode(9, RunMode.D3D11);
        settings.SetUseDxvk(9, true);
        settings.SetDxvkOptions(9, new DxvkOptions(true, "False", true));

        var reloaded = new GameSettings(path);
        Assert.Equal(RunMode.D3D11, reloaded.GetRunMode(9));
        Assert.True(reloaded.GetUseDxvk(9));
        Assert.Equal(new DxvkOptions(true, "False", true), reloaded.GetDxvkOptions(9));

        reloaded.SetRunMode(9, RunMode.Default);   // going back to Default must not forget the other choices
        Assert.True(new GameSettings(path).GetUseDxvk(9));
        Assert.Equal(new DxvkOptions(true, "False", true), new GameSettings(path).GetDxvkOptions(9));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"5\": {\"RunMode\": \"Banana\"}}")]   // value that isn't a mode
    [InlineData("")]
    public void A_damaged_file_falls_back_to_defaults_instead_of_failing(string content)
    {
        using var dir = new TempDir();
        dir.Write("settings.json", content);
        var settings = new GameSettings(dir.Combine("settings.json"));
        Assert.Equal(RunMode.Default, settings.GetRunMode(5));
        settings.SetRunMode(5, RunMode.D3D11);   // and it can still be written afterwards
        Assert.Equal(RunMode.D3D11, new GameSettings(dir.Combine("settings.json")).GetRunMode(5));
    }
}

public class LaunchPlannerTests
{
    private static GameScan Unreal(GfxApi apis = GfxApi.D3D11 | GfxApi.D3D12) => TestData.Scan("G", apis, apis, engine: "Unreal");

    [Fact]
    public void Url_without_args_uses_rungameid() => Assert.Equal("steam://rungameid/861650", SteamLaunch.Url(861650));

    [Fact]
    public void Url_with_args_uses_the_documented_run_form() =>
        Assert.Equal("steam://run/861650//-dx11/", SteamLaunch.Url(861650, "-dx11"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Url_with_blank_args_is_the_plain_form(string? args) => Assert.Equal("steam://rungameid/5", SteamLaunch.Url(5, args));

    [Fact]
    public void Url_escapes_anything_unusual_in_args() => Assert.Equal("steam://run/5//-a%20b%2F%26c/", SteamLaunch.Url(5, "-a b/&c"));

    [Fact]
    public void Default_mode_launches_with_no_args()
    {
        var plan = LaunchPlanner.Plan(Unreal(), RunMode.Default, dxvkInstalled: false);
        Assert.True(plan.CanLaunch);
        Assert.Null(plan.Args);
        Assert.Empty(plan.Warnings);
    }

    [Theory]
    [InlineData(RunMode.D3D11, "-dx11")]
    [InlineData(RunMode.Vulkan, "-vulkan")]
    public void A_chosen_mode_passes_the_engine_flag(RunMode mode, string flag)
    {
        var plan = LaunchPlanner.Plan(Unreal(), mode, dxvkInstalled: false);
        Assert.True(plan.CanLaunch);
        Assert.Equal(flag, plan.Args);
    }

    [Fact]
    public void D3D11_with_dxvk_installed_is_the_intended_setup_and_has_no_warnings()
    {
        var plan = LaunchPlanner.Plan(Unreal(), RunMode.D3D11, dxvkInstalled: true);
        Assert.True(plan.CanLaunch);
        Assert.Equal("-dx11", plan.Args);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void An_engine_with_no_known_flag_cannot_apply_a_mode()
    {
        var plan = LaunchPlanner.Plan(TestData.Scan("G", GfxApi.D3D11, engine: ""), RunMode.D3D11, dxvkInstalled: false);
        Assert.False(plan.CanLaunch);
        Assert.Contains("No launch flag", plan.Blocked);
    }

    [Fact]
    public void D3D12_with_dxvk_installed_is_blocked_because_it_crashes_at_startup()
    {
        var plan = LaunchPlanner.Plan(Unreal(), RunMode.D3D12, dxvkInstalled: true);
        Assert.False(plan.CanLaunch);
        Assert.Contains("D3D11", plan.Blocked);
        Assert.Contains("dxgi.dll", plan.Blocked);
    }

    [Fact]
    public void D3D12_without_dxvk_is_fine() => Assert.Equal("-dx12", LaunchPlanner.Plan(Unreal(), RunMode.D3D12, dxvkInstalled: false).Args);

    [Fact]
    public void Default_mode_with_dxvk_on_a_game_that_may_start_in_D3D12_warns_but_still_launches()
    {
        var plan = LaunchPlanner.Plan(Unreal(), RunMode.Default, dxvkInstalled: true);   // exactly Session's situation
        Assert.True(plan.CanLaunch);
        Assert.Contains(plan.Warnings, w => w.Contains("Run as to D3D11"));
    }

    [Fact]
    public void Default_mode_with_dxvk_on_a_d3d11_only_game_has_no_warning() =>
        Assert.Empty(LaunchPlanner.Plan(Unreal(GfxApi.D3D11), RunMode.Default, dxvkInstalled: true).Warnings);

    [Fact]
    public void Vulkan_mode_is_not_blocked_by_dxvk_since_dxvk_is_unused_then()
    {
        var plan = LaunchPlanner.Plan(Unreal(), RunMode.Vulkan, dxvkInstalled: true);
        Assert.True(plan.CanLaunch);
        Assert.Equal("-vulkan", plan.Args);
    }
}

public class SteamLaunchTests
{
    private const string SteamExe = @"C:\Program Files (x86)\Steam\steam.exe";

    [Fact]
    public void No_flag_launches_by_the_plain_game_url_whatever_steam_exe_is()
    {
        Assert.Equal(new LaunchCommand("steam://rungameid/861650", ""), SteamLaunch.Command(861650, null, SteamExe));
        Assert.Equal(new LaunchCommand("steam://rungameid/861650", ""), SteamLaunch.Command(861650, "  ", null));
    }

    [Fact]
    public void A_flag_goes_through_applaunch_which_steam_does_not_ask_the_user_to_confirm()
    {
        var command = SteamLaunch.Command(861650, "-dx11", SteamExe);
        Assert.Equal(SteamExe, command.FileName);
        Assert.Equal("-applaunch 861650 -dx11", command.Arguments);
    }

    [Fact]
    public void Without_steam_exe_a_flag_falls_back_to_the_url_form_which_steam_does_confirm()
    {
        var command = SteamLaunch.Command(861650, "-dx11", steamExe: null);
        Assert.Equal("steam://run/861650//-dx11/", command.FileName);
        Assert.Equal("", command.Arguments);
    }

    [Theory]
    [InlineData("-dx11 & calc")]
    [InlineData("-dx11 -log")]           // two arguments
    [InlineData("dx11")]                 // not a flag
    [InlineData("-")]
    [InlineData("-dx11\" -evil")]
    [InlineData("-dx11;calc")]
    [InlineData("-dx11|more")]
    [InlineData("-dx 11")]
    [InlineData("--dx11/x")]
    public void Anything_but_a_single_plain_flag_is_refused_because_it_lands_on_a_command_line(string args) =>
        Assert.Throws<ArgumentException>(() => SteamLaunch.Command(1, args, SteamExe));

    [Fact]
    public void Every_flag_the_app_can_produce_is_accepted()
    {
        foreach (var engine in new[] { "Unreal", "Unity" })
            foreach (var mode in new[] { RunMode.D3D11, RunMode.D3D12, RunMode.Vulkan })
                Assert.Equal($"-applaunch 7 {EngineFlags.Flag(engine, mode)}", SteamLaunch.Command(7, EngineFlags.Flag(engine, mode), SteamExe).Arguments);
    }

    [Fact]
    public void The_command_reads_naturally_in_logs()
    {
        Assert.Equal("steam.exe -applaunch 5 -vulkan", SteamLaunch.Command(5, "-vulkan", SteamExe).ToString());
        Assert.Equal("steam://rungameid/5", SteamLaunch.Command(5, null, SteamExe).ToString());
    }
}

public class InstallPlannerRunModeTests
{
    private static InstallPlan Plan(GameScan scan, RunMode mode) => InstallPlanner.Plan(scan, scan.Primary, mode);

    [Fact]
    public void Run_as_D3D11_makes_a_d3d12_only_looking_game_installable_for_d3d11()
    {
        var plan = Plan(TestData.Scan("G", GfxApi.D3D12, GfxApi.D3D12, engine: "Unreal"), RunMode.D3D11);
        Assert.True(plan.CanInstall);
        Assert.Equal(GfxApi.D3D11, plan.Apis);
    }

    [Fact]
    public void Run_as_D3D11_removes_the_d3d12_default_warning_because_the_app_forces_the_api()
    {
        var plan = Plan(TestData.Scan("G", GfxApi.D3D11 | GfxApi.D3D12, engine: "Unreal"), RunMode.D3D11);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void In_default_mode_an_engine_with_a_flag_needs_no_warning_because_ticking_dxvk_switches_to_d3d11()
    {
        var plan = Plan(TestData.Scan("G", GfxApi.D3D11 | GfxApi.D3D12, engine: "Unreal"), RunMode.Default);
        Assert.True(plan.CanInstall);
        Assert.Empty(plan.Warnings);
    }

    [Theory]
    [InlineData(RunMode.D3D12)]
    [InlineData(RunMode.Vulkan)]
    public void Run_as_d3d12_or_vulkan_means_there_is_nothing_for_dxvk_to_do(RunMode mode)
    {
        var plan = Plan(TestData.Scan("G", GfxApi.D3D11, GfxApi.D3D11, engine: "Unreal"), mode);
        Assert.False(plan.CanInstall);
        Assert.Contains("Run as " + mode, plan.Blocked);
    }

    [Fact]
    public void Run_as_d3d11_decides_the_dlls_even_when_detection_found_older_apis()
    {
        var plan = Plan(TestData.Scan("G", GfxApi.D3D9, engine: "Unreal"), RunMode.D3D11);
        Assert.Equal(GfxApi.D3D11, plan.Apis);
    }
}

public class RunDiagnosisTests
{
    private const string Exe = "SessionGame-Win64-Shipping";

    private static string D3D11Log(params string[] extra) => string.Join("\n",
        ["info:  Game: Game.exe", "info:  DXVK: v3.1.1", "info:  Found device: NVIDIA GeForce RTX 4070 Ti SUPER (NVIDIA 616.56.0)", .. extra]);

    [Fact]
    public void A_d3d12_game_is_recognised_from_the_swap_chain_error()
    {
        using var dir = new TempDir();
        dir.Write($"{Exe}_dxgi.log", "info:  Game: x.exe\nwarn:  CreateDXGIFactory2: Ignoring flags\nerr:   DXGI: CreateSwapChainForHwnd: Unsupported device type\n");

        var report = RunDiagnosis.Check(dir.Path, null);

        Assert.False(report.DxvkActive);
        Assert.Contains("D3D12", report.Verdict);
        Assert.Contains("Run as to D3D11", report.Verdict);
    }

    [Fact]
    public void A_working_d3d11_run_names_the_api_and_the_gpu()
    {
        using var dir = new TempDir();
        dir.Write($"{Exe}_dxgi.log", "info:  Game: x.exe\n");
        dir.Write($"{Exe}_d3d11.log", D3D11Log());

        var report = RunDiagnosis.Check(dir.Path, null);

        Assert.True(report.DxvkActive);
        Assert.Equal("DXVK is rendering D3D11 on NVIDIA GeForce RTX 4070 Ti SUPER (NVIDIA 616.56.0).", report.Verdict);
    }

    [Fact]
    public void The_gpu_name_is_read_from_the_real_d3d11_log_layout()
    {
        using var dir = new TempDir();
        // Layout seen in a real Session run: no "Found device" line, the name follows "Creating device:".
        dir.Write($"{Exe}_d3d11.log",
            "info:  Creating device:\ninfo:  NVIDIA GeForce RTX 4070 Ti SUPER:\ninfo:    Driver   : NVIDIA 616.56.0\ninfo:  Queues:\n");
        Assert.Equal("DXVK is rendering D3D11 on NVIDIA GeForce RTX 4070 Ti SUPER.", RunDiagnosis.Check(dir.Path, null).Verdict);
    }

    [Fact]
    public void Error_lines_in_a_working_run_are_counted()
    {
        using var dir = new TempDir();
        dir.Write($"{Exe}_d3d11.log", D3D11Log("err:   something broke", "err:   again"));
        Assert.Contains("2 error line(s)", RunDiagnosis.Check(dir.Path, null).Verdict);
    }

    [Fact]
    public void An_old_error_from_a_previous_run_is_ignored_when_the_latest_run_is_clean()
    {
        using var dir = new TempDir();
        // DXVK appends: yesterday's D3D12 crash is still in the file above today's good run.
        dir.Write($"{Exe}_dxgi.log",
            "info:  Game: x.exe\nerr:   DXGI: CreateSwapChainForHwnd: Unsupported device type\n" +
            "info:  Game: x.exe\ninfo:  DXVK: v3.1.1\n");
        dir.Write($"{Exe}_d3d11.log", D3D11Log());

        var report = RunDiagnosis.Check(dir.Path, null);

        Assert.True(report.DxvkActive);
        Assert.DoesNotContain("D3D12", report.Verdict);
    }

    [Fact]
    public void Logs_older_than_the_launch_do_not_count()
    {
        using var dir = new TempDir();
        string stale = dir.Write($"{Exe}_d3d11.log", D3D11Log());
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-3));

        var report = RunDiagnosis.Check(dir.Path, DateTime.UtcNow.AddMinutes(-1));

        Assert.False(report.DxvkActive);
        Assert.Contains("No DXVK log was written since", report.Verdict);
    }

    [Fact]
    public void Dxgi_loaded_without_any_device_means_the_game_is_not_using_d3d11()
    {
        using var dir = new TempDir();
        dir.Write($"{Exe}_dxgi.log", "info:  Game: x.exe\ninfo:  DXVK: v3.1.1\n");

        var report = RunDiagnosis.Check(dir.Path, null);

        Assert.False(report.DxvkActive);
        Assert.Contains("isn't rendering with D3D11", report.Verdict);
    }

    [Fact]
    public void A_d3d9_run_is_reported_as_d3d9()
    {
        using var dir = new TempDir();
        dir.Write("hl2_d3d9.log", D3D11Log());
        Assert.StartsWith("DXVK is rendering D3D9", RunDiagnosis.Check(dir.Path, null).Verdict);
    }

    [Fact]
    public void No_logs_at_all_says_dxvk_was_not_loaded()
    {
        using var dir = new TempDir();
        var report = RunDiagnosis.Check(dir.Path, null);
        Assert.False(report.DxvkActive);
        Assert.Contains("wasn't loaded", report.Verdict);
    }

    [Fact]
    public void A_log_the_game_still_has_open_can_be_read()
    {
        using var dir = new TempDir();
        string log = dir.Write($"{Exe}_d3d11.log", D3D11Log());
        using var held = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);   // the running game
        Assert.True(RunDiagnosis.Check(dir.Path, null).DxvkActive);
    }

    [Fact]
    public void LastRun_returns_only_the_final_process_section()
    {
        Assert.Equal("Game: b\nz", RunDiagnosis.LastRun("info:  Game: a\nx\ninfo:  Game: b\nz").Replace("info:  ", ""));
        Assert.Equal("no marker", RunDiagnosis.LastRun("no marker"));
    }
}
