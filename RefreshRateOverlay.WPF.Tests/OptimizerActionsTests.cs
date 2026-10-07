using System.Diagnostics;
using System.IO;
using RefreshRateOverlay.WPF.Services;

namespace RefreshRateOverlay.WPF.Tests;

/// <summary>Panel-chosen priority and core type: text, safety clamps,
/// persistence, enforcement and the panel pickers.</summary>
public class OptimizerActionsTests
{
    [Fact]
    public void Defaults_DescribeAsBefore()
    {
        Assert.Equal("P-cores preferred, priority unchanged", OptimizerActions.Default.GameDescription);
        Assert.Equal("Below normal, E-cores, low I/O", OptimizerActions.Default.ThrottleDescription);
    }

    [Fact]
    public void CustomChoices_Describe()
    {
        var a = new OptimizerActions(PriorityChoice.High, CoreChoice.Any, PriorityChoice.Idle, CoreChoice.PCores);
        Assert.Equal("any cores, High priority", a.GameDescription);
        Assert.Equal("Idle, P-cores, low I/O", a.ThrottleDescription);
    }

    [Fact]
    public void Normalized_NeverLowersTheGame_NorRaisesAThrottledProcess()
    {
        var bad = new OptimizerActions(PriorityChoice.Idle, CoreChoice.PCores, PriorityChoice.High, CoreChoice.ECores).Normalized();
        Assert.Equal(OptimizerActions.Default.GamePriority, bad.GamePriority);
        Assert.Equal(OptimizerActions.Default.ThrottlePriority, bad.ThrottlePriority);
    }

    [Fact]
    public void PlannerActionText_FollowsTheChoices()
    {
        var a = new OptimizerActions(PriorityChoice.AboveNormal, CoreChoice.PCores, PriorityChoice.Idle, CoreChoice.ECores);
        var r = GameOptimizerPlanner.Plan("g.exe", DateTime.Now, new[]
        {
            new ProcessSnapshot("g", 1, @"C:\g.exe", 1, ProcessPriorityClass.Normal, 1.0),
            new ProcessSnapshot("rustc", 2, @"C:\rustc.exe", 1, ProcessPriorityClass.Normal, 1.0),
        }, -1, Array.Empty<string>(), actions: a);

        Assert.Equal("P-cores preferred, Above normal priority", r.Decisions.Single(d => d.Pid == 1).Action);
        Assert.Equal("Idle, E-cores, low I/O", r.Decisions.Single(d => d.Pid == 2).Action);
    }

    [Fact]
    public void ProfileService_RoundTripsTheChoices_AndDefaultsWhenAbsent()
    {
        string path = Path.Combine(Path.GetTempPath(), $"rro_actions_{Guid.NewGuid():N}.ini");
        try
        {
            using var ini = new IniStore(path);
            var profiles = new ProfileService(ini);
            Assert.Equal(OptimizerActions.Default, profiles.ReadOptimizerActions());

            var a = new OptimizerActions(PriorityChoice.High, CoreChoice.Any, PriorityChoice.Idle, CoreChoice.PCores);
            profiles.WriteOptimizerActions(a);
            Assert.Equal(a, profiles.ReadOptimizerActions());
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // ---- enforcer with custom choices (fakes, no real process) ----

    private sealed class Fake : IProcessControl
    {
        public Dictionary<int, (long Created, ProcessPriorityClass Pri, uint[] Sets, int Io)> P { get; } = new();
        public ProcessState? Read(int pid) => P.TryGetValue(pid, out var p) ? new ProcessState(p.Created, p.Pri, p.Sets, p.Io) : null;
        public bool SetPriority(int pid, long c, ProcessPriorityClass v) { var p = P[pid]; P[pid] = p with { Pri = v }; return true; }
        public bool SetCpuSets(int pid, long c, uint[] v) { var p = P[pid]; P[pid] = p with { Sets = v }; return true; }
        public bool SetIoPriority(int pid, long c, int v) { var p = P[pid]; P[pid] = p with { Io = v }; return true; }
    }

    private sealed class Store : IOptimizerJournalStore
    {
        public List<OptimizerJournalEntry> Load() => new();
        public void Save(IReadOnlyCollection<OptimizerJournalEntry> entries) { }
    }

    private static readonly CpuTopology Hybrid = new(new uint[] { 256, 257 }, new uint[] { 300 });

    private static OptimizerReport Report() => new("g.exe", DateTime.Now, new[]
    {
        new OptimizerDecision("g", 1, OptimizerRole.Game, "r", null, 1),
        new OptimizerDecision("rustc", 2, OptimizerRole.WouldThrottle, "r", null, 1),
    });

    [Fact]
    public void Enforcer_AppliesTheChosenPriorityAndCores()
    {
        var f = new Fake();
        f.P[1] = (10, ProcessPriorityClass.Normal, Array.Empty<uint>(), 2);
        f.P[2] = (20, ProcessPriorityClass.Normal, Array.Empty<uint>(), 2);
        var e = new GameOptimizerEnforcer(f, new Store(), Hybrid);
        e.SetActions(new OptimizerActions(PriorityChoice.High, CoreChoice.Any, PriorityChoice.Idle, CoreChoice.PCores));

        e.Enforce(Report());

        Assert.Equal(ProcessPriorityClass.High, f.P[1].Pri);
        Assert.Empty(f.P[1].Sets);                       // Any: CPU sets untouched
        Assert.Equal(ProcessPriorityClass.Idle, f.P[2].Pri);
        Assert.Equal(new uint[] { 256, 257 }, f.P[2].Sets);
    }

    [Fact]
    public void Enforcer_ChangingChoices_RestoresThenReappliesFromTheTrueOriginal()
    {
        var f = new Fake();
        f.P[1] = (10, ProcessPriorityClass.Normal, Array.Empty<uint>(), 2);
        f.P[2] = (20, ProcessPriorityClass.Normal, Array.Empty<uint>(), 2);
        var e = new GameOptimizerEnforcer(f, new Store(), Hybrid);
        e.Enforce(Report());
        Assert.Equal(ProcessPriorityClass.BelowNormal, f.P[2].Pri);

        e.SetActions(new OptimizerActions(PriorityChoice.Unchanged, CoreChoice.PCores, PriorityChoice.Idle, CoreChoice.ECores));
        Assert.Equal(ProcessPriorityClass.Normal, f.P[2].Pri); // restored immediately
        Assert.Empty(e.Entries);

        e.Enforce(Report());
        Assert.Equal(ProcessPriorityClass.Idle, f.P[2].Pri);
        Assert.Equal(ProcessPriorityClass.Normal, e.Entries[2].OriginalPriority);
    }

    [Fact]
    public void Enforcer_SameChoicesAgain_IsANoOp()
    {
        var f = new Fake();
        f.P[2] = (20, ProcessPriorityClass.Normal, Array.Empty<uint>(), 2);
        var e = new GameOptimizerEnforcer(f, new Store(), Hybrid);
        e.Enforce(Report());

        e.SetActions(OptimizerActions.Default);

        Assert.Equal(ProcessPriorityClass.BelowNormal, f.P[2].Pri); // not restored
    }

    // ---- panel pickers ----

    private static OptimizerReport PanelReport() => GameOptimizerPlanner.Plan("g.exe", DateTime.Now, new[]
    {
        new ProcessSnapshot("g", 1, @"C:\g.exe", 1, ProcessPriorityClass.Normal, 1.0),
        new ProcessSnapshot("rustc", 2, @"C:\rustc.exe", 1, ProcessPriorityClass.Normal, 1.0),
    }, -1, Array.Empty<string>());

    [Fact]
    public void Panel_PickersShowTheStoredChoices() => StaTestHelper.Run(() =>
    {
        var panel = new OptimizerPanelWindow();
        panel.SetActions(new OptimizerActions(PriorityChoice.High, CoreChoice.Any, PriorityChoice.Idle, CoreChoice.PCores));

        Assert.Equal("High", panel.GamePriorityPicker.SelectedItem);
        Assert.Equal("Any", panel.GameCoresPicker.SelectedItem);
        Assert.Equal("Idle", panel.ThrottlePriorityPicker.SelectedItem);
        Assert.Equal("P-cores", panel.ThrottleCoresPicker.SelectedItem);
        Assert.Equal(new[] { "Unchanged", "Above normal", "High" }, panel.GamePriorityPicker.Items.Cast<string>());
        Assert.Equal(new[] { "Unchanged", "Below normal", "Idle" }, panel.ThrottlePriorityPicker.Items.Cast<string>());
    });

    [Fact]
    public void Panel_PickingRaisesActionsChanged_SetActionsDoesNot() => StaTestHelper.Run(() =>
    {
        var panel = new OptimizerPanelWindow();
        var raised = new List<OptimizerActions>();
        panel.ActionsChanged += (_, a) => raised.Add(a);

        panel.SetActions(OptimizerActions.Default);
        Assert.Empty(raised);

        panel.ThrottlePriorityPicker.SelectedIndex = 2; // Idle
        Assert.Single(raised);
        Assert.Equal(PriorityChoice.Idle, raised[0].ThrottlePriority);
        Assert.Equal(OptimizerActions.Default.GameCores, raised[0].GameCores);
    });

    [Fact]
    public void Panel_PickersSitInTheGameAndThrottleSections_AndSurviveRefreshes() => StaTestHelper.Run(() =>
    {
        var panel = new OptimizerPanelWindow();
        panel.Update(PanelReport(), OptimizerMode.DryRun);
        var gameRow = (System.Windows.FrameworkElement)panel.GamePriorityPicker.Parent;
        var throttleRow = (System.Windows.FrameworkElement)panel.ThrottlePriorityPicker.Parent;
        Assert.NotNull(gameRow.Parent);
        Assert.NotNull(throttleRow.Parent);

        panel.ThrottleCoresPicker.SelectedIndex = 0; // Any
        panel.Update(PanelReport(), OptimizerMode.DryRun);

        Assert.Equal("Any", panel.ThrottleCoresPicker.SelectedItem);       // same control, kept its choice
        Assert.Same(throttleRow, panel.ThrottlePriorityPicker.Parent);
        Assert.NotNull(throttleRow.Parent);                                // re-attached to the new section
    });
}
