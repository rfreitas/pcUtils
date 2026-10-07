using System.Diagnostics;
using RefreshRateOverlay.WPF.Services;

namespace RefreshRateOverlay.WPF.Tests;

/// <summary>
/// The dry-run classifier, against mock process lists shaped like this machine
/// (Virtual Desktop as the OpenXR runtime, Oculus installed, a Rust build in the
/// background). One test per rule, plus rule ordering.
/// </summary>
public class GameOptimizerPlannerTests
{
    private const int OwnPid = 999;
    private const string VdManifest = @"C:\Program Files\Virtual Desktop Streamer\OpenXR\virtualdesktop-openxr.json";
    private static readonly IReadOnlyList<string> VdRoots = GameOptimizerPlanner.OpenXrRoots(VdManifest);

    private static ProcessSnapshot P(string name, string? path = null, int session = 1,
        ProcessPriorityClass? priority = ProcessPriorityClass.Normal, double? cpu = 0.0, int pid = 100) =>
        new(name, pid, path ?? $@"C:\Apps\{name}.exe", session, priority, cpu);

    private static OptimizerDecision One(ProcessSnapshot p, string app = "eldenring.exe") =>
        GameOptimizerPlanner.Plan(app, DateTime.Now, new[] { p }, OwnPid, VdRoots).Decisions.Single();

    // ---- OpenXR roots ----

    [Fact]
    public void Roots_VirtualDesktop_IncludesParentOfOpenXrFolder() =>
        Assert.Equal(new[] { @"C:\Program Files\Virtual Desktop Streamer\OpenXR\", @"C:\Program Files\Virtual Desktop Streamer\" }, VdRoots);

    [Fact]
    public void Roots_SteamVR_NeverIncludesSteamappsCommon()
    {
        var roots = GameOptimizerPlanner.OpenXrRoots(@"C:\Program Files (x86)\Steam\steamapps\common\SteamVR\steamxr_win64.json");
        Assert.Equal(new[] { @"C:\Program Files (x86)\Steam\steamapps\common\SteamVR\" }, roots);
    }

    [Fact]
    public void Roots_Oculus_IncludesTheSupportFolder_WithClientAndDash() =>
        Assert.Equal(new[] { @"C:\Program Files\Oculus\Support\oculus-runtime\", @"C:\Program Files\Oculus\Support\" },
            GameOptimizerPlanner.OpenXrRoots(@"C:\Program Files\Oculus\Support\oculus-runtime\oculus_openxr_64.json"));

    [Fact]
    public void Roots_AllRegisteredRuntimesAndDrivers_DeduplicatedAndNeverAContainer()
    {
        var roots = GameOptimizerPlanner.RuntimeRoots(new[]
        {
            @"C:\Program Files\Virtual Desktop Streamer\OpenXR",
            @"C:\Program Files\Virtual Desktop Streamer\OpenVRDriver",
            @"C:\Program Files (x86)\Steam\steamapps\common\SteamVR",
            @"C:\Program Files (x86)\Steam\steamapps\common\PlayStation VR2 App\SteamVR_Plug-In",
            @"C:\Users\me\AppData\Local\SomeRuntime",
            @"C:\Tool",
            @"C:\",
            null,
        });

        Assert.Equal(new[]
        {
            @"C:\Program Files\Virtual Desktop Streamer\OpenXR\",
            @"C:\Program Files\Virtual Desktop Streamer\",
            @"C:\Program Files\Virtual Desktop Streamer\OpenVRDriver\",
            @"C:\Program Files (x86)\Steam\steamapps\common\SteamVR\",
            @"C:\Program Files (x86)\Steam\steamapps\common\PlayStation VR2 App\SteamVR_Plug-In\",
            @"C:\Program Files (x86)\Steam\steamapps\common\PlayStation VR2 App\",
            @"C:\Users\me\AppData\Local\SomeRuntime\",
            @"C:\Tool\",
        }, roots);
    }

    [Fact]
    public void OpenVrPaths_ReadsRuntimeAndExternalDrivers()
    {
        const string json = """
            { "config": ["C:\\Steam\\config"],
              "external_drivers": ["C:\\Program Files\\Virtual Desktop Streamer\\OpenVRDriver"],
              "runtime": ["C:\\Steam\\steamapps\\common\\SteamVR"], "version": 1 }
            """;
        Assert.Equal(new[] { @"C:\Steam\steamapps\common\SteamVR", @"C:\Program Files\Virtual Desktop Streamer\OpenVRDriver" },
            ProcessSnapshotService.ParseOpenVrPaths(json));
        Assert.Empty(ProcessSnapshotService.ParseOpenVrPaths("not json"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Roots_NoRuntime_IsEmpty(string? manifest) =>
        Assert.Empty(GameOptimizerPlanner.OpenXrRoots(manifest));

    // ---- rules ----

    [Fact]
    public void OwnProcess_IsProtected()
    {
        var d = One(P("RefreshRateOverlay.WPF", pid: OwnPid));
        Assert.Equal(OptimizerRole.Protected, d.Role);
        Assert.Equal("Overlay app", d.Reason);
    }

    [Fact]
    public void FocusedApp_IsTheGame_WithBoostAction()
    {
        var d = One(P("eldenring"));
        Assert.Equal(OptimizerRole.Game, d.Role);
        Assert.Equal(GameOptimizerPlanner.GameAction, d.Action);
        Assert.DoesNotContain("no access", d.Reason);
    }

    [Fact]
    public void FocusedApp_Unreadable_SaysALiveBoostWouldFail()
    {
        var d = One(P("eldenring", path: null, priority: null) with { Path = null });
        Assert.Equal(OptimizerRole.Game, d.Role);
        Assert.Contains("no access", d.Reason);
    }

    [Fact]
    public void Session0_IsProtected() =>
        Assert.Equal(OptimizerRole.Protected, One(P("MsMpEng", session: 0, cpu: 3.0)).Role);

    [Fact]
    public void Unreadable_IsProtected() =>
        Assert.Equal(OptimizerRole.Protected, One(P("SomethingSecure", priority: null) with { Path = null }).Role);

    [Fact]
    public void ProcessUnderOpenXrRuntime_IsVrPipeline_EvenWithHighCpu()
    {
        var d = One(P("VirtualDesktop.Streamer", path: @"C:\Program Files\Virtual Desktop Streamer\VirtualDesktop.Streamer.exe", cpu: 2.0));
        Assert.Equal(OptimizerRole.VrPipeline, d.Role);
        Assert.Equal(GameOptimizerPlanner.VrFolderReason, d.Reason);
    }

    [Theory]
    [InlineData("vrcompositor")]
    [InlineData("vrserver")]
    [InlineData("OVRServer_x64")]
    public void KnownVrRuntimeNames_AreVrPipeline(string name)
    {
        var d = One(P(name, cpu: 3.0));
        Assert.Equal(OptimizerRole.VrPipeline, d.Role);
        Assert.Equal("VR runtime", d.Reason);
    }

    [Fact]
    public void Summary_PutsTheVrPipelineUnderTheGame_NotInProtected()
    {
        var r = GameOptimizerPlanner.Plan("eldenring.exe", DateTime.Now, new[]
        {
            P("eldenring", pid: 1, cpu: 5.0),
            P("VirtualDesktop.Streamer", path: @"C:\Program Files\Virtual Desktop Streamer\VirtualDesktop.Streamer.exe", pid: 2, cpu: 0.8),
            P("OVRServer_x64", pid: 3, cpu: 0.1),
            P("audiodg", pid: 4),
        }, OwnPid, VdRoots);

        var sections = GameOptimizerPlanner.Summarize(r);
        var game = sections.Single(s => s.Role == OptimizerRole.Game);
        Assert.Equal(new[] { "VirtualDesktop.Streamer", "OVRServer_x64" }, game.PipelineRows!.Select(x => x.Name));
        var prot = sections.Single(s => s.Role == OptimizerRole.Protected);
        Assert.Equal(1, prot.ProcessCount);
        Assert.DoesNotContain(GameOptimizerPlanner.GroupByReason(prot), g => g.Reason.Contains("runtime"));
    }

    [Theory]
    [InlineData("audiodg", "GPU / audio / input helper")]
    [InlineData("nvcontainer", "GPU / audio / input helper")]
    [InlineData("DSX", "GPU / audio / input helper")]
    [InlineData("EasyAntiCheat", "Anti-cheat")]
    [InlineData("steam", "Launcher")]
    [InlineData("obs64", "Capture")]
    public void KnownNames_AreProtected_WithTheirReason(string name, string reason)
    {
        var d = One(P(name, cpu: 3.0));
        Assert.Equal(OptimizerRole.Protected, d.Role);
        Assert.Equal(reason, d.Reason);
    }

    [Fact]
    public void ProtectedName_WinsOverCustomPriority()
    {
        var d = One(P("NVIDIA Overlay", priority: ProcessPriorityClass.AboveNormal));
        Assert.Equal(OptimizerRole.Protected, d.Role);
    }

    [Fact]
    public void CustomPriority_IsLeftAlone_EvenIfBusy()
    {
        var d = One(P("rustc", priority: ProcessPriorityClass.BelowNormal, cpu: 4.0));
        Assert.Equal(OptimizerRole.LeftAlone, d.Role);
        Assert.Equal("Already Below normal", d.Reason);
    }

    [Fact]
    public void BuildTool_WouldBeThrottled_EvenWhenIdleNow()
    {
        var d = One(P("rustc", cpu: 0.0));
        Assert.Equal(OptimizerRole.WouldThrottle, d.Role);
        Assert.Equal("Build tool", d.Reason);
        Assert.Equal(GameOptimizerPlanner.ThrottleAction, d.Action);
    }

    [Fact]
    public void BusyProcess_AtThreshold_WouldBeThrottled() =>
        Assert.Equal(OptimizerRole.WouldThrottle, One(P("chrome", cpu: GameOptimizerPlanner.DefaultCpuThresholdCores)).Role);

    [Fact]
    public void SingleBusyThread_IsCaught_ThresholdIsInCores()
    {
        // One full core on a 28-thread CPU is only 3.6% of total; it must still count.
        Assert.Equal(OptimizerRole.WouldThrottle, One(P("python", cpu: 1.0)).Role);
    }

    [Fact]
    public void BusyProcess_IsGroupedAsBusyNow() =>
        Assert.Equal(GameOptimizerPlanner.BusyReason, One(P("chrome", cpu: 2.0)).Reason);

    [Fact]
    public void QuietProcess_IsThrottledUpFront_AsBackground()
    {
        // Throttled before it spikes: a spike then lands on throttled cores at
        // throttled priority instead of waiting up to a sample to be caught.
        var d = One(P("notepad", cpu: 0.1));
        Assert.Equal(OptimizerRole.WouldThrottle, d.Role);
        Assert.Equal(GameOptimizerPlanner.BackgroundReason, d.Reason);
    }

    [Fact]
    public void NewProcess_WithoutCpu_IsThrottledAtBirth()
    {
        var d = One(P("chrome", cpu: null));
        Assert.Equal(OptimizerRole.WouldThrottle, d.Role);
        Assert.Equal(GameOptimizerPlanner.BackgroundReason, d.Reason);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(31)]
    public void RealtimeThreads_AreProtected_WithoutANameList(int threadPriority)
    {
        var d = One(P("SomeFutureStreamer", cpu: 0.2) with { MaxThreadPriority = threadPriority });
        Assert.Equal(OptimizerRole.Protected, d.Role);
        Assert.Equal(GameOptimizerPlanner.RealtimeReason, d.Reason);
    }

    [Fact]
    public void TimeCriticalUiThread_15_IsNotRealtime() =>
        Assert.Equal(OptimizerRole.WouldThrottle, One(P("explorer2", cpu: 0.0) with { MaxThreadPriority = 15 }).Role);

    [Fact]
    public void ProcessesStartedByTheVrPipeline_AreVrPipeline_UntilTheGame()
    {
        var r = GameOptimizerPlanner.Plan("eldenring.exe", DateTime.Now, new[]
        {
            P("OVRServer_x64", pid: 1),
            P("OVRUnknownHelper2030", path: @"D:\Elsewhere\helper.exe", pid: 2) with { ParentPid = 1 },
            P("encoder", path: @"D:\Elsewhere\encoder.exe", pid: 3) with { ParentPid = 2 },
            P("eldenring", pid: 4) with { ParentPid = 1 },             // launched from the VR dashboard
            P("crashpad", pid: 5) with { ParentPid = 4 },              // the game's own helper
            P("notepad", pid: 6),
        }, OwnPid, VdRoots);

        OptimizerDecision D(int pid) => r.Decisions.Single(d => d.Pid == pid);
        Assert.Equal(OptimizerRole.VrPipeline, D(2).Role);
        Assert.Equal(GameOptimizerPlanner.VrChildReason, D(2).Reason);
        Assert.Equal(OptimizerRole.VrPipeline, D(3).Role);
        Assert.Equal(OptimizerRole.Game, D(4).Role);
        Assert.Equal(OptimizerRole.WouldThrottle, D(5).Role);
        Assert.Equal(OptimizerRole.WouldThrottle, D(6).Role);
    }

    [Fact]
    public void GameMatch_IgnoresExeSuffixAndCase() =>
        Assert.Equal(OptimizerRole.Game, One(P("EldenRing"), app: "ELDENRING.EXE").Role);

    // ---- summary ----

    [Fact]
    public void Summary_CollapsesSameNameRows_SumsCpu_KeepsAllSections()
    {
        var report = GameOptimizerPlanner.Plan("eldenring.exe", DateTime.Now, new[]
        {
            P("eldenring", cpu: 6.0, pid: 1),
            P("nvcontainer", cpu: 0.1, pid: 2),
            P("nvcontainer", cpu: 0.2, pid: 3),
            P("rustc", cpu: 1.5, pid: 4),
            P("rustc", cpu: 0.5, pid: 5),
            P("lassoed", cpu: 0.0, pid: 6, priority: ProcessPriorityClass.Idle),
        }, OwnPid, VdRoots);

        var sections = GameOptimizerPlanner.Summarize(report);

        Assert.Equal(new[] { OptimizerRole.Game, OptimizerRole.WouldThrottle, OptimizerRole.Protected, OptimizerRole.LeftAlone },
            sections.Select(s => s.Role));
        var nv = sections.Single(s => s.Role == OptimizerRole.Protected).Rows.Single();
        Assert.Equal(2, nv.Count);
        Assert.Equal(0.3, nv.CpuCores!.Value, 3);
        var rust = sections.Single(s => s.Role == OptimizerRole.WouldThrottle).Rows.Single();
        Assert.Equal(2, rust.Count);
        Assert.Equal(2.0, rust.CpuCores!.Value, 3);
        Assert.Equal("Already Idle", sections.Single(s => s.Role == OptimizerRole.LeftAlone).Rows.Single().Reason);
    }

    [Fact]
    public void Summary_SortsRowsByCpuDescending()
    {
        var report = GameOptimizerPlanner.Plan("game.exe", DateTime.Now, new[]
        {
            P("a", cpu: 0.6, pid: 1), P("b", cpu: 2.0, pid: 2), P("c", cpu: 0.9, pid: 3),
        }, OwnPid, VdRoots);

        var rows = GameOptimizerPlanner.Summarize(report).Single(s => s.Role == OptimizerRole.WouldThrottle).Rows;
        Assert.Equal(new[] { "b", "c", "a" }, rows.Select(r => r.Name));
    }

    // ---- game parent chain ----

    private static OptimizerReport Chain(params ProcessSnapshot[] ps) =>
        GameOptimizerPlanner.Plan("eldenring.exe", DateTime.Now, ps, OwnPid, VdRoots);

    private static OptimizerDecision Of(OptimizerReport r, int pid) => r.Decisions.Single(d => d.Pid == pid);

    [Fact]
    public void Ancestors_OfTheGame_AreProtected_EvenWhenBusy()
    {
        var r = Chain(
            P("explorer", pid: 10, cpu: 0.0),
            P("MyLauncher", pid: 20, cpu: 3.0) with { ParentPid = 10 },
            P("start_protected_game_wrapper", pid: 30, cpu: 2.0) with { ParentPid = 20 },
            P("eldenring", pid: 40, cpu: 6.0) with { ParentPid = 30 },
            P("unrelated", pid: 50, cpu: 3.0) with { ParentPid = 10 });

        Assert.Equal(OptimizerRole.GameChain, Of(r, 20).Role);
        Assert.Equal(OptimizerRole.GameChain, Of(r, 30).Role);
        Assert.Equal(OptimizerRole.GameChain, Of(r, 10).Role);
        Assert.Equal(1, Of(r, 30).ChainDepth);
        Assert.Equal(3, Of(r, 10).ChainDepth);
        Assert.Null(Of(r, 30).Action); // never changed
        Assert.Equal(OptimizerRole.WouldThrottle, Of(r, 50).Role); // a sibling, not an ancestor
    }

    [Fact]
    public void Ancestors_StopAtACycle()
    {
        var r = Chain(
            P("a", pid: 20, cpu: 3.0) with { ParentPid = 30 },
            P("b", pid: 30, cpu: 3.0) with { ParentPid = 20 },
            P("eldenring", pid: 40) with { ParentPid = 20 });
        Assert.Equal(OptimizerRole.GameChain, Of(r, 20).Role);
        Assert.Equal(OptimizerRole.GameChain, Of(r, 30).Role);
    }

    [Fact]
    public void Ancestors_UnknownParent_ProtectsNothingExtra()
    {
        var r = Chain(P("busy", pid: 20, cpu: 3.0), P("eldenring", pid: 40));
        Assert.Equal(OptimizerRole.WouldThrottle, Of(r, 20).Role);
    }

    [Fact]
    public void Ancestors_NoGameRunning_IsEmpty() =>
        Assert.Empty(GameOptimizerPlanner.GameAncestors(new[] { P("x", pid: 1) with { ParentPid = 2 } }, "eldenring"));

    // ---- list changes ----

    [Theory]
    [InlineData("AutoHotkey64", "Input hook / hotkeys")]
    [InlineData("AggressiveScreensaver", "Input hook / hotkeys")]
    [InlineData("start_protected_game", "Anti-cheat")]
    [InlineData("obs-ffmpeg-mux", "Capture")]
    public void AddedNames_AreProtected(string name, string reason)
    {
        var d = One(P(name, cpu: 3.0));
        Assert.Equal(OptimizerRole.Protected, d.Role);
        Assert.Equal(reason, d.Reason);
    }

    [Theory]
    [InlineData("NVIDIA App")]
    [InlineData("ShareX")]
    [InlineData("SearchHost")]
    public void DroppedNames_FollowTheCpuRules(string name) =>
        Assert.Equal(OptimizerRole.WouldThrottle, One(P(name, cpu: 3.0)).Role);

    [Fact]
    public void GroupByReason_UsesRuleOrder_AndSumsCounts()
    {
        var r = Chain(
            P("explorer", pid: 10) with { ParentPid = 1 },
            P("eldenring", pid: 40) with { ParentPid = 10 },
            P("audiodg", pid: 50, cpu: 0.2),
            P("nvcontainer", pid: 51, cpu: 0.1),
            P("nvcontainer", pid: 52, cpu: 0.1),
            P("VirtualDesktop.Streamer", path: @"C:\Program Files\Virtual Desktop Streamer\VirtualDesktop.Streamer.exe", pid: 60, cpu: 0.8),
            P("MsMpEng", session: 0, pid: 70));

        var prot = GameOptimizerPlanner.Summarize(r).Single(s => s.Role == OptimizerRole.Protected);
        var groups = GameOptimizerPlanner.GroupByReason(prot);

        Assert.Equal(new[]
        {
            "GPU / audio / input helper", "Windows / service",
        }, groups.Select(g => g.Reason));
        var gpu = groups.Single(g => g.Reason == "GPU / audio / input helper");
        Assert.Equal(3, gpu.ProcessCount);
        Assert.Equal(0.4, gpu.CpuCores!.Value, 3);
    }

    [Fact]
    public void Summary_PutsTheChainUnderTheGame_NearestParentFirst()
    {
        var r = Chain(
            P("explorer", pid: 10, cpu: 0.0),
            P("steam", pid: 20, cpu: 0.9) with { ParentPid = 10 },
            P("start_protected_game", pid: 30, cpu: 0.0) with { ParentPid = 20 },
            P("eldenring", pid: 40, cpu: 6.0) with { ParentPid = 30 });

        var sections = GameOptimizerPlanner.Summarize(r);
        var game = sections.Single(s => s.Role == OptimizerRole.Game);

        Assert.Single(game.Rows);
        Assert.Equal(1, game.ProcessCount);
        Assert.Equal(new[] { "start_protected_game", "steam", "explorer" }, game.ChainRows!.Select(x => x.Name));
        Assert.Equal(0, sections.Single(s => s.Role == OptimizerRole.Protected).ProcessCount);
    }

    [Fact]
    public void Chain_StopsAtTheGamesOwnProcess()
    {
        // A game that relaunches itself: the outer copy is Game, not chain.
        var r = Chain(
            P("explorer", pid: 10),
            P("eldenring", pid: 20) with { ParentPid = 10 },
            P("eldenring", pid: 30) with { ParentPid = 20 });
        Assert.Equal(OptimizerRole.Game, Of(r, 20).Role);
        Assert.Equal(OptimizerRole.GameChain, Of(r, 10).Role);
    }
}
