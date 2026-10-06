using RefreshRateOverlay.WPF.Services;

namespace RefreshRateOverlay.WPF.Tests;

/// <summary>
/// CoreParkingEnforcer against in-memory fakes of the power API and the restore
/// memory: no real power plan is read or written. The values mirror what powercfg
/// printed on the dev machine (min cores 4% on class 0, 0% on class 1).
/// </summary>
public class CoreParkingEnforcerTests
{
    private static readonly Guid PlanA = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    private static readonly Guid PlanB = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    private sealed class FakePower : IProcessorPowerSettings
    {
        public Guid ActivePlan { get; set; } = PlanA;
        public bool PlanReadable { get; set; } = true;
        public Dictionary<(Guid Plan, Guid Setting), (uint Ac, uint Dc)> Values { get; } = new();
        public List<string> Writes { get; } = new();
        public List<Guid> Reactivated { get; } = new();
        public int Reactivations => Reactivated.Count;
        public bool FailWrites { get; set; }
        public int FailWritesAfter { get; set; } = int.MaxValue; // let this many writes succeed, then fail
        public bool FailReactivate { get; set; }

        public bool TryGetActivePlan(out Guid plan) { plan = ActivePlan; return PlanReadable; }

        public bool TryRead(Guid plan, Guid setting, out uint ac, out uint dc)
        {
            if (Values.TryGetValue((plan, setting), out var v)) { (ac, dc) = v; return true; }
            ac = dc = 0; return false;
        }

        private bool CanWrite() => !FailWrites && Writes.Count < FailWritesAfter;

        public bool WriteAc(Guid plan, Guid setting, uint value)
        {
            if (!CanWrite()) return false;
            Writes.Add($"{plan:N} AC {setting} {value}");
            Values[(plan, setting)] = (value, Values[(plan, setting)].Dc);
            return true;
        }

        public bool WriteDc(Guid plan, Guid setting, uint value)
        {
            if (!CanWrite()) return false;
            Writes.Add($"{plan:N} DC {setting} {value}");
            Values[(plan, setting)] = (Values[(plan, setting)].Ac, value);
            return true;
        }

        public bool Reactivate(Guid plan) { Reactivated.Add(plan); return !FailReactivate; }

        public (uint Ac, uint Dc) Get(Guid setting, Guid? plan = null) => Values[(plan ?? ActivePlan, setting)];
        public void Set(Guid setting, uint ac, uint dc, Guid? plan = null) => Values[(plan ?? ActivePlan, setting)] = (ac, dc);
    }

    private sealed class FakeMemory : IParkingRestoreStore
    {
        public Dictionary<string, uint> Values { get; } = new();
        public bool TryGet(string key, out uint value) => Values.TryGetValue(key, out value);
        public void Set(string key, uint value) => Values[key] = value;
    }

    private static Guid Class0 => CoreParkingEnforcer.MinCoresSettings[0];
    private static Guid Class1 => CoreParkingEnforcer.MinCoresSettings[1];

    private static FakePower DevMachine()
    {
        var p = new FakePower();
        p.Set(Class0, 4, 4);
        p.Set(Class1, 0, 0);
        return p;
    }

    private static string Key(Guid setting, bool ac, Guid? plan = null) =>
        CoreParkingEnforcer.RestoreKey(plan ?? PlanA, setting, ac);

    // ---- IsDisabled / IsAvailable (the live reads the sync dot and reconciliation use) ----

    [Fact]
    public void IsDisabled_ParkingAllowed_IsFalse() =>
        Assert.False(CoreParkingEnforcer.IsDisabled(DevMachine()));

    [Fact]
    public void IsDisabled_AllValuesAt100_IsTrue()
    {
        var p = new FakePower();
        p.Set(Class0, 100, 100);
        p.Set(Class1, 100, 100);
        Assert.True(CoreParkingEnforcer.IsDisabled(p));
    }

    [Fact]
    public void IsDisabled_OnlyDcStillParks_IsFalse()
    {
        var p = new FakePower();
        p.Set(Class0, 100, 4);
        p.Set(Class1, 100, 100);
        Assert.False(CoreParkingEnforcer.IsDisabled(p));
    }

    [Fact]
    public void IsDisabled_MissingClass1_JudgedOnClass0Alone()
    {
        var p = new FakePower();
        p.Set(Class0, 100, 100);
        Assert.True(CoreParkingEnforcer.IsDisabled(p));
    }

    [Fact]
    public void IsDisabled_NothingReadable_IsFalse() =>
        Assert.False(CoreParkingEnforcer.IsDisabled(new FakePower()));

    [Fact]
    public void IsAvailable_ReadableSettingOnActivePlan_IsTrue() =>
        Assert.True(CoreParkingEnforcer.IsAvailable(DevMachine()));

    [Fact]
    public void IsAvailable_PlanUnreadable_IsFalse()
    {
        var p = DevMachine();
        p.PlanReadable = false;
        Assert.False(CoreParkingEnforcer.IsAvailable(p));
        Assert.False(CoreParkingEnforcer.IsDisabled(p));
    }

    [Fact]
    public void IsAvailable_NoSettingReadable_IsFalse() =>
        Assert.False(CoreParkingEnforcer.IsAvailable(new FakePower()));

    // ---- Apply: disable ----

    [Fact]
    public void Disable_WritesAllFourValuesReactivatesOnceAndRemembersOriginals()
    {
        var p = DevMachine();
        var mem = new FakeMemory();

        var result = CoreParkingEnforcer.Apply(p, mem, disableParking: true);

        Assert.Equal(CoreParkingResult.Applied, result);
        Assert.Equal((100u, 100u), p.Get(Class0));
        Assert.Equal((100u, 100u), p.Get(Class1));
        Assert.Equal(4, p.Writes.Count);
        Assert.Equal(new[] { PlanA }, p.Reactivated);
        Assert.Equal(4u, mem.Values[Key(Class0, ac: true)]);
        Assert.Equal(4u, mem.Values[Key(Class0, ac: false)]);
        Assert.Equal(0u, mem.Values[Key(Class1, ac: true)]);
    }

    [Fact]
    public void Disable_SecondPass_IsANoOp()
    {
        var p = DevMachine();
        var mem = new FakeMemory();
        CoreParkingEnforcer.Apply(p, mem, true);
        p.Writes.Clear();
        int reactivations = p.Reactivations;

        var result = CoreParkingEnforcer.Apply(p, mem, true);

        Assert.Equal(CoreParkingResult.NoChange, result);
        Assert.Empty(p.Writes);
        Assert.Equal(reactivations, p.Reactivations);
    }

    [Fact]
    public void Disable_DoesNotOverwriteMemoryWith100()
    {
        var p = DevMachine();
        var mem = new FakeMemory();
        CoreParkingEnforcer.Apply(p, mem, true);
        CoreParkingEnforcer.Apply(p, mem, true);

        Assert.Equal(4u, mem.Values[Key(Class0, ac: true)]);
    }

    [Fact]
    public void Disable_OnlyTheWrongValuesAreWritten()
    {
        var p = new FakePower();
        p.Set(Class0, 100, 4);
        p.Set(Class1, 100, 100);

        var result = CoreParkingEnforcer.Apply(p, new FakeMemory(), true);

        Assert.Equal(CoreParkingResult.Applied, result);
        Assert.Equal(new[] { $"{PlanA:N} DC {Class0} 100" }, p.Writes);
    }

    [Fact]
    public void Disable_MissingClass1Setting_IsSkippedNotAnError()
    {
        var p = new FakePower();
        p.Set(Class0, 4, 4);

        var result = CoreParkingEnforcer.Apply(p, new FakeMemory(), true);

        Assert.Equal(CoreParkingResult.Applied, result);
        Assert.False(p.Values.ContainsKey((PlanA, Class1)));
    }

    [Fact]
    public void Disable_NoSettingsReadable_TouchesNothing()
    {
        var p = new FakePower();

        Assert.Equal(CoreParkingResult.NoChange, CoreParkingEnforcer.Apply(p, new FakeMemory(), true));
        Assert.Empty(p.Writes);
        Assert.Equal(0, p.Reactivations);
    }

    [Fact]
    public void Apply_PlanUnreadable_ReportsFailedAndTouchesNothing()
    {
        var p = DevMachine();
        p.PlanReadable = false;

        Assert.Equal(CoreParkingResult.Failed, CoreParkingEnforcer.Apply(p, new FakeMemory(), true));
        Assert.Empty(p.Writes);
        Assert.Equal(0, p.Reactivations);
    }

    [Fact]
    public void Disable_FirstWriteFails_ReportsFailedAndDoesNotReactivate()
    {
        var p = DevMachine();
        p.FailWrites = true;

        Assert.Equal(CoreParkingResult.Failed, CoreParkingEnforcer.Apply(p, new FakeMemory(), true));
        Assert.Equal(0, p.Reactivations);
    }

    [Fact]
    public void Disable_PartialWriteFailure_StillReactivatesWhatWasWritten()
    {
        var p = DevMachine();
        p.FailWritesAfter = 2; // two values land, the third fails

        var result = CoreParkingEnforcer.Apply(p, new FakeMemory(), true);

        Assert.Equal(CoreParkingResult.Failed, result);
        Assert.Equal(2, p.Writes.Count);
        Assert.Equal(1, p.Reactivations); // the two written values must not sit unapplied
    }

    [Fact]
    public void Disable_ReactivateFailure_ReportsFailed()
    {
        var p = DevMachine();
        p.FailReactivate = true;

        Assert.Equal(CoreParkingResult.Failed, CoreParkingEnforcer.Apply(p, new FakeMemory(), true));
    }

    // ---- Apply: allow parking again ----

    [Fact]
    public void Allow_RestoresTheRememberedValues()
    {
        var p = DevMachine();
        var mem = new FakeMemory();
        CoreParkingEnforcer.Apply(p, mem, true);

        var result = CoreParkingEnforcer.Apply(p, mem, disableParking: false);

        Assert.Equal(CoreParkingResult.Applied, result);
        Assert.Equal((4u, 4u), p.Get(Class0));
        Assert.Equal((0u, 0u), p.Get(Class1));
        Assert.False(CoreParkingEnforcer.IsDisabled(p));
    }

    [Fact]
    public void Allow_WhenParkingAlreadyAllowed_LeavesOtherToolsValuesAlone()
    {
        var p = DevMachine(); // 4% / 0%, e.g. whatever ParkControl set
        var mem = new FakeMemory();

        var result = CoreParkingEnforcer.Apply(p, mem, false);

        Assert.Equal(CoreParkingResult.NoChange, result);
        Assert.Empty(p.Writes);
        Assert.Equal(0, p.Reactivations);
    }

    [Fact]
    public void Allow_MixedState_IsLeftUntouched()
    {
        // Some values already 100 (another tool, or the vendor plan) but not all:
        // IsDisabled reads false, so "allowed" is already true and nothing may be
        // rewritten, with or without remembered values.
        var p = new FakePower();
        p.Set(Class0, 100, 10);
        p.Set(Class1, 100, 100);

        var result = CoreParkingEnforcer.Apply(p, new FakeMemory(), false);

        Assert.Equal(CoreParkingResult.NoChange, result);
        Assert.Empty(p.Writes);
        Assert.Equal((100u, 10u), p.Get(Class0));
    }

    [Fact]
    public void Allow_FullyPinnedWithNothingRemembered_FallsBackToTheSettingMinimum()
    {
        var p = new FakePower();
        p.Set(Class0, 100, 100); // pinned by hand before this app ever ran
        p.Set(Class1, 100, 100);

        var result = CoreParkingEnforcer.Apply(p, new FakeMemory(), false);

        Assert.Equal(CoreParkingResult.Applied, result);
        Assert.Equal((CoreParkingEnforcer.AllowParkingFallback, CoreParkingEnforcer.AllowParkingFallback), p.Get(Class0));
    }

    [Fact]
    public void Allow_RememberedValueOf100_IsNotRestoredAsParkingOff()
    {
        var p = new FakePower();
        p.Set(Class0, 100, 100);
        var mem = new FakeMemory();
        mem.Set(Key(Class0, true), 100);
        mem.Set(Key(Class0, false), 100);

        CoreParkingEnforcer.Apply(p, mem, false);

        Assert.False(CoreParkingEnforcer.IsDisabled(p));
    }

    // ---- restore memory is per plan ----

    [Fact]
    public void Memory_IsKeyedPerPlan_AndNeverLeaksAcrossPlanSwitches()
    {
        var p = new FakePower();
        p.Set(Class0, 4, 4, PlanA);
        p.Set(Class0, 100, 100, PlanB); // already pinned (e.g. a vendor "highest performance" plan)
        var mem = new FakeMemory();

        // Disable on plan A: remembers A's 4s.
        CoreParkingEnforcer.Apply(p, mem, true);
        Assert.Equal(4u, mem.Values[Key(Class0, true, PlanA)]);

        // Switch to plan B, which was already at 100: nothing remembered for B.
        p.ActivePlan = PlanB;
        Assert.Equal(CoreParkingResult.NoChange, CoreParkingEnforcer.Apply(p, mem, true));
        Assert.False(mem.Values.ContainsKey(Key(Class0, true, PlanB)));

        // Allow on plan B must NOT write plan A's remembered 4s into plan B.
        CoreParkingEnforcer.Apply(p, mem, false);
        Assert.Equal((CoreParkingEnforcer.AllowParkingFallback, CoreParkingEnforcer.AllowParkingFallback), p.Get(Class0, PlanB));
        Assert.Equal((100u, 100u), p.Get(Class0, PlanA)); // plan A untouched by the pass on B
    }

    [Fact]
    public void Apply_ReadsAndWritesTheSamePlanItReactivates()
    {
        var p = DevMachine();
        p.ActivePlan = PlanB;
        p.Set(Class0, 4, 4, PlanB);

        CoreParkingEnforcer.Apply(p, new FakeMemory(), true);

        Assert.All(p.Writes, w => Assert.StartsWith(PlanB.ToString("N"), w));
        Assert.Equal(new[] { PlanB }, p.Reactivated);
    }

    [Fact]
    public void NotificationSettings_IncludeActivePlanAndBothMinCoresSettings()
    {
        Assert.Contains(new Guid("31f9f286-5084-42fe-b720-2b0264993763"), CoreParkingEnforcer.NotificationSettings);
        Assert.Contains(Class0, CoreParkingEnforcer.NotificationSettings);
        Assert.Contains(Class1, CoreParkingEnforcer.NotificationSettings);
    }
}
