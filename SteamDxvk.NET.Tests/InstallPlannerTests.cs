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
    public void Nothing_detected_installs_both_the_d3d9_and_d3d11_sets_and_says_so()
    {
        var plan = Plan(TestData.Scan("G", GfxApi.None));
        Assert.True(plan.CanInstall);
        Assert.Equal(GfxApi.D3D9 | GfxApi.D3D11, plan.Apis);
        Assert.Contains(plan.Warnings, w => w.Contains("Couldn't tell which API"));
    }

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
    public void Anti_cheat_warns_and_names_it()
    {
        var plan = Plan(TestData.Scan("G", GfxApi.D3D11, anticheat: ["EasyAntiCheat", "BattlEye"]));
        Assert.True(plan.CanInstall);
        var warning = Assert.Single(plan.Warnings);
        Assert.Contains("EasyAntiCheat, BattlEye", warning);
    }

    [Fact]
    public void Anti_cheat_and_a_missing_launch_flag_both_warn() =>
        Assert.Equal(2, Plan(TestData.Scan("G", GfxApi.D3D11 | GfxApi.D3D12, anticheat: ["BattlEye"], engine: "")).Warnings.Count);

    [Fact]
    public void Once_dxvk_is_installed_the_d3d12_warning_is_left_to_the_launch_plan()
    {
        var scan = TestData.Scan("G", GfxApi.D3D11 | GfxApi.D3D12, engine: "Godot", status: new DxvkStatus(true, "DXVK v1", null));
        Assert.Empty(Plan(scan).Warnings);
    }

    // ------------------------------------------------------------------ plan summary

    private static ExeInfo Exe(GfxApi apis) => new(@"C:\G\g.exe", 64, 1, apis, apis);

    private static string? Describe(GfxApi detected, RunMode mode, bool installed, string engine = "Unreal")
    {
        var scan = TestData.Scan("G", detected, detected, engine: engine);
        return PlanSummary.Describe(scan, scan.Primary, mode, installed);
    }

    [Theory]
    [InlineData(GfxApi.D3D11, RunMode.Default, false, "The game uses D3D11. Using DXVK translates it to Vulkan.")]
    [InlineData(GfxApi.D3D11, RunMode.Default, true, "The game uses D3D11; DXVK translates it to Vulkan.")]
    [InlineData(GfxApi.D3D9, RunMode.Default, false, "The game uses D3D9. Using DXVK translates it to Vulkan.")]
    [InlineData(GfxApi.D3D8, RunMode.Default, true, "The game uses D3D8; DXVK translates it to Vulkan.")]
    [InlineData(GfxApi.D3D10, RunMode.Default, true, "The game uses D3D10; DXVK translates it to Vulkan.")]
    [InlineData(GfxApi.D3D9 | GfxApi.D3D11 | GfxApi.D3D12, RunMode.Default, true, "The game uses D3D11; DXVK translates it to Vulkan.")]   // newest DXVK-servable one
    [InlineData(GfxApi.D3D12, RunMode.D3D11, false, "The game uses D3D11. Using DXVK translates it to Vulkan.")]    // Run as decides
    public void The_summary_names_the_api_dxvk_will_serve_and_that_the_result_is_vulkan(GfxApi detected, RunMode mode, bool installed, string expected) =>
        Assert.Equal(expected, Describe(detected, mode, installed));

    [Fact]
    public void Run_as_d3d12_and_vulkan_say_dxvk_is_not_involved()
    {
        Assert.Contains("DXVK can't translate", Describe(GfxApi.D3D11, RunMode.D3D12, false));
        Assert.Contains("DXVK isn't involved", Describe(GfxApi.D3D11, RunMode.Vulkan, false));
    }

    [Fact]
    public void When_the_api_was_not_detected_the_summary_says_both_sets_are_used()
    {
        Assert.Contains("D3D9 and D3D11", Describe(GfxApi.None, RunMode.Default, false));
        Assert.Contains("both installed", Describe(GfxApi.None, RunMode.Default, true)!.Replace("files are ", ""));
    }

    [Theory]
    [InlineData(GfxApi.D3D12)]
    [InlineData(GfxApi.Vulkan)]
    [InlineData(GfxApi.OpenGL)]
    public void Nothing_to_say_when_dxvk_has_nothing_to_translate(GfxApi detected) =>
        Assert.Null(Describe(detected, RunMode.Default, false));

    [Fact]
    public void No_exe_no_summary() => Assert.Null(PlanSummary.Describe(TestData.Scan("G", GfxApi.D3D11), null, RunMode.Default, false));

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
