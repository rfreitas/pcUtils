using SteamDxvk;
using Xunit;

namespace SteamDxvk.Tests;

public class InstallPlannerTests
{
    private static InstallPlan Plan(GameScan scan, RunMode mode = RunMode.Default) => InstallPlanner.Plan(scan, scan.Primary, mode);

    [Fact]
    public void D3D11_only_can_use_dxvk_with_no_warnings()
    {
        var plan = Plan(TestData.Scan("G", GfxApi.D3D11, GfxApi.D3D11));
        Assert.True(plan.CanInstall);
        Assert.Equal(GfxApi.D3D11, plan.Apis);
        Assert.Empty(plan.Warnings);
        Assert.Null(plan.Blocked);
    }

    [Fact]
    public void D3D12_only_cannot_use_dxvk()
    {
        var plan = Plan(TestData.Scan("G", GfxApi.D3D12));
        Assert.False(plan.CanInstall);
        Assert.Contains("only uses D3D12", plan.Blocked);
    }

    [Fact]
    public void Vulkan_only_cannot_use_dxvk_because_it_already_is_vulkan()
    {
        var plan = Plan(TestData.Scan("G", GfxApi.Vulkan));
        Assert.False(plan.CanInstall);
        Assert.Contains("already uses Vulkan", plan.Blocked);
    }

    [Fact]
    public void Opengl_only_cannot_use_dxvk_because_it_does_not_translate_opengl()
    {
        var plan = Plan(TestData.Scan("G", GfxApi.OpenGL));
        Assert.False(plan.CanInstall);
        Assert.Contains("OpenGL", plan.Blocked);
    }

    [Fact]
    public void Nothing_detected_installs_both_the_d3d9_and_d3d11_sets_and_flags_it_for_the_tooltip_not_a_message_line()
    {
        var plan = Plan(TestData.Scan("G", GfxApi.None));
        Assert.True(plan.CanInstall);
        Assert.Equal(GfxApi.D3D9 | GfxApi.D3D11, plan.Apis);
        Assert.True(plan.Guessed);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void A_detected_api_is_not_a_guess() => Assert.False(Plan(TestData.Scan("G", GfxApi.D3D11, GfxApi.D3D11)).Guessed);

    [Fact]
    public void No_exe_cannot_use_dxvk()
    {
        var scan = new GameScan(new Game(1, "G", @"C:\G"));
        var plan = InstallPlanner.Plan(scan, null);
        Assert.False(plan.CanInstall);
        Assert.NotNull(plan.Blocked);
    }

    [Theory]
    [InlineData(GfxApi.D3D9, "d3d9.dll")]
    [InlineData(GfxApi.D3D11, "d3d10core.dll,d3d11.dll,dxgi.dll")]
    [InlineData(GfxApi.D3D8, "d3d8.dll,d3d9.dll")]
    [InlineData(GfxApi.D3D9 | GfxApi.D3D11, "d3d10core.dll,d3d11.dll,d3d9.dll,dxgi.dll")]
    public void The_dlls_follow_from_the_detected_api_without_the_user_choosing(GfxApi detected, string expectedDlls)
    {
        var plan = Plan(TestData.Scan("G", detected, detected));
        Assert.Equal(expectedDlls, string.Join(",", DxvkInstaller.DllsFor(plan.Apis)));
    }

    [Fact]
    public void An_engine_without_a_launch_flag_gets_the_d3d12_crash_warning()
    {
        var plan = Plan(TestData.Scan("G", GfxApi.D3D11 | GfxApi.D3D12, engine: "Godot"));
        Assert.True(plan.CanInstall);
        Assert.Equal(GfxApi.D3D11, plan.Apis);   // D3D12 is never part of what gets installed
        Assert.Contains(plan.Warnings, w => w.Contains("Untick DXVK"));
    }

    [Fact]
    public void An_engine_with_a_launch_flag_needs_no_d3d12_warning_because_ticking_sets_run_as_d3d11()
    {
        var plan = Plan(TestData.Scan("G", GfxApi.D3D11 | GfxApi.D3D12, engine: "Unreal"));
        Assert.True(plan.CanInstall);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Anti_cheat_is_a_risk_for_the_confirmation_and_names_it_but_not_a_standing_message()
    {
        var plan = Plan(TestData.Scan("G", GfxApi.D3D11, anticheat: ["EasyAntiCheat", "BattlEye"]));
        Assert.True(plan.CanInstall);
        Assert.Contains("EasyAntiCheat, BattlEye", plan.Risk);
        Assert.Empty(plan.Warnings);   // the list's Notes column already flags anti-cheat per game
    }

    [Fact]
    public void No_anti_cheat_no_risk() => Assert.Null(Plan(TestData.Scan("G", GfxApi.D3D11)).Risk);

    [Fact]
    public void Anti_cheat_and_a_missing_launch_flag_are_separate_things()
    {
        var plan = Plan(TestData.Scan("G", GfxApi.D3D11 | GfxApi.D3D12, anticheat: ["BattlEye"], engine: ""));
        Assert.NotNull(plan.Risk);
        Assert.Single(plan.Warnings);   // only the D3D12 one, which the UI can't prevent
    }

    [Fact]
    public void Once_dxvk_is_installed_the_d3d12_warning_is_left_to_the_launch_plan()
    {
        var scan = TestData.Scan("G", GfxApi.D3D11 | GfxApi.D3D12, engine: "Godot", status: new DxvkStatus(true, "DXVK v1", null));
        Assert.Empty(Plan(scan).Warnings);
    }

    // ------------------------------------------------------------------------ run as row

    [Fact]
    public void Run_as_is_only_shown_for_engines_with_launch_flags()
    {
        Assert.True(PlanSummary.ShowRunAs(TestData.Scan("G", GfxApi.D3D11, engine: "Unreal")));
        Assert.True(PlanSummary.ShowRunAs(TestData.Scan("G", GfxApi.D3D11, engine: "Unity")));
        Assert.False(PlanSummary.ShowRunAs(TestData.Scan("G", GfxApi.D3D11, engine: "")));
        Assert.False(PlanSummary.ShowRunAs(TestData.Scan("G", GfxApi.D3D11, engine: "Godot")));
    }

    // ----------------------------------------------------------------------- filter

    [Theory]
    [InlineData("All", "", true)]
    [InlineData("D3D11", "", true)]
    [InlineData("D3D12", "", false)]
    [InlineData("Vulkan", "", false)]
    [InlineData("No API detected", "", false)]
    [InlineData("DXVK installed", "", false)]
    [InlineData("All", "dirt", true)]
    [InlineData("All", "DIRT", true)]
    [InlineData("All", "forza", false)]
    [InlineData("D3D11", "forza", false)]
    public void Filter_matches(string filter, string search, bool expected) =>
        Assert.Equal(expected, GameFilter.Matches(TestData.Scan("DiRT Rally 2.0", GfxApi.D3D11), filter, search));

    [Fact]
    public void Filter_dxvk_installed_and_no_api()
    {
        var installed = TestData.Scan("G", GfxApi.D3D11, status: new DxvkStatus(true, "DXVK v1", null));
        Assert.True(GameFilter.Matches(installed, "DXVK installed", ""));
        Assert.True(GameFilter.Matches(TestData.Scan("G", GfxApi.None), "No API detected", ""));
    }

    [Fact]
    public void Filter_options_are_all_understood()
    {
        var scan = TestData.Scan("G", GfxApi.D3D11 | GfxApi.D3D12 | GfxApi.Vulkan | GfxApi.OpenGL);
        foreach (var option in GameFilter.Options)
            Assert.IsType<bool>(GameFilter.Matches(scan, option, ""));   // no option throws
    }
}
