using System.Diagnostics;
using RefreshRateOverlay.WPF.Services;

namespace RefreshRateOverlay.WPF.Tests;

/// <summary>
/// Live-mode enforcement against fake processes and an in-memory journal: no
/// real process is touched. Covers apply/restore, the "only undo our own value"
/// and pid-reuse rules, sticky throttling, sessions and crash recovery.
/// </summary>
public class GameOptimizerEnforcerTests
{
    private sealed class FakeProcess
    {
        public long Created;
        public string Name = "";
        public ProcessPriorityClass Priority = ProcessPriorityClass.Normal;
        public uint[] CpuSets = Array.Empty<uint>();
        public int Io = 2;
        public bool Denied;
    }

    private sealed class FakeControl : IProcessControl
    {
        public Dictionary<int, FakeProcess> Procs { get; } = new();
        public List<string> Writes { get; } = new();
        public bool FailCpuSets { get; set; }

        public ProcessState? Read(int pid) =>
            Procs.TryGetValue(pid, out var p) && !p.Denied ? new ProcessState(p.Created, p.Priority, p.CpuSets.ToArray(), p.Io) : null;

        private FakeProcess? Get(int pid, long created) =>
            Procs.TryGetValue(pid, out var p) && !p.Denied && p.Created == created ? p : null;

        public bool SetPriority(int pid, long created, ProcessPriorityClass priority)
        {
            if (Get(pid, created) is not { } p) return false;
            p.Priority = priority; Writes.Add($"{pid} pri {priority}"); return true;
        }

        public bool SetCpuSets(int pid, long created, uint[] ids)
        {
            if (FailCpuSets || Get(pid, created) is not { } p) return false;
            p.CpuSets = ids.ToArray(); Writes.Add($"{pid} sets {ids.Length}"); return true;
        }

        public bool SetIoPriority(int pid, long created, int io)
        {
            if (Get(pid, created) is not { } p) return false;
            p.Io = io; Writes.Add($"{pid} io {io}"); return true;
        }
    }

    private sealed class MemoryStore : IOptimizerJournalStore
    {
        public List<OptimizerJournalEntry> Saved { get; private set; } = new();
        public int SaveCount { get; private set; }
        public List<OptimizerJournalEntry> Load() => Saved.ToList();
        public void Save(IReadOnlyCollection<OptimizerJournalEntry> entries) { Saved = entries.ToList(); SaveCount++; }
    }

    private static readonly CpuTopology Hybrid = new(new uint[] { 256, 257 }, new uint[] { 300, 301, 302 });

    private readonly FakeControl _ctl = new();
    private readonly MemoryStore _store = new();

    private FakeProcess Add(int pid, string name, long created = 1000)
    {
        var p = new FakeProcess { Created = created, Name = name };
        _ctl.Procs[pid] = p;
        return p;
    }

    private GameOptimizerEnforcer NewEnforcer(CpuTopology? topo = null) => new(_ctl, _store, topo ?? Hybrid);

    private static OptimizerReport Report(string app, params (int Pid, string Name, OptimizerRole Role)[] ds) =>
        new(app, DateTime.Now, ds.Select(d => new OptimizerDecision(d.Name, d.Pid, d.Role, "r", null, 0)).ToList());

    [Fact]
    public void Game_IsBoostedToHighOnPCores_ThrottledGoesBelowNormalOnECoresWithLowIo()
    {
        var game = Add(1, "eldenring");
        var build = Add(2, "rustc");
        var e = NewEnforcer();

        e.Enforce(Report("eldenring.exe", (1, "eldenring", OptimizerRole.Game), (2, "rustc", OptimizerRole.WouldThrottle)));

        Assert.Equal(ProcessPriorityClass.High, game.Priority);
        Assert.Equal(new uint[] { 256, 257 }, game.CpuSets);
        Assert.Equal(2, game.Io); // the game's disk priority is not raised
        Assert.Equal(ProcessPriorityClass.BelowNormal, build.Priority);
        Assert.Equal(new uint[] { 300, 301, 302 }, build.CpuSets);
        Assert.Equal(GameOptimizerEnforcer.IoVeryLow, build.Io);
    }

    [Fact]
    public void ProtectedChainAndLeftAlone_AreNeverTouched()
    {
        Add(3, "steam"); Add(4, "VirtualDesktop.Streamer"); Add(5, "notepad");
        var e = NewEnforcer();

        e.Enforce(Report("g.exe", (3, "steam", OptimizerRole.GameChain), (4, "VirtualDesktop.Streamer", OptimizerRole.Protected),
            (5, "notepad", OptimizerRole.LeftAlone)));

        Assert.Empty(_ctl.Writes);
        Assert.Empty(e.Entries);
    }

    [Fact]
    public void Journal_IsWrittenBeforeTheProcessIsChanged()
    {
        Add(2, "rustc");
        var store = new OrderCheckingStore(_ctl);
        var e = new GameOptimizerEnforcer(_ctl, store, Hybrid);

        e.Enforce(Report("g.exe", (2, "rustc", OptimizerRole.WouldThrottle)));

        Assert.True(store.SawEntryBeforeAnyWrite);
    }

    private sealed class OrderCheckingStore : IOptimizerJournalStore
    {
        private readonly FakeControl _ctl;
        public bool SawEntryBeforeAnyWrite { get; private set; }
        public OrderCheckingStore(FakeControl ctl) => _ctl = ctl;
        public List<OptimizerJournalEntry> Load() => new();
        public void Save(IReadOnlyCollection<OptimizerJournalEntry> entries)
        {
            if (entries.Count > 0 && _ctl.Writes.Count == 0) SawEntryBeforeAnyWrite = true;
        }
    }

    [Fact]
    public void RestoreAll_PutsEveryOriginalBack_AndEmptiesTheJournal()
    {
        var game = Add(1, "g"); game.Priority = ProcessPriorityClass.Normal; game.CpuSets = new uint[] { 999 };
        var build = Add(2, "rustc"); build.Io = 2;
        var e = NewEnforcer();
        e.Enforce(Report("g.exe", (1, "g", OptimizerRole.Game), (2, "rustc", OptimizerRole.WouldThrottle)));

        e.RestoreAll("test");

        Assert.Equal(ProcessPriorityClass.Normal, game.Priority);
        Assert.Equal(new uint[] { 999 }, game.CpuSets);
        Assert.Equal(ProcessPriorityClass.Normal, build.Priority);
        Assert.Empty(build.CpuSets);
        Assert.Equal(2, build.Io);
        Assert.Empty(e.Entries);
        Assert.Empty(_store.Saved);
    }

    [Fact]
    public void Restore_LeavesAValueSomeoneElseChangedAfterUs()
    {
        var build = Add(2, "rustc");
        var e = NewEnforcer();
        e.Enforce(Report("g.exe", (2, "rustc", OptimizerRole.WouldThrottle)));

        build.Priority = ProcessPriorityClass.Idle; // e.g. the user, or Process Lasso, after us
        e.RestoreAll("test");

        Assert.Equal(ProcessPriorityClass.Idle, build.Priority);      // theirs, kept
        Assert.Empty(build.CpuSets);                                   // ours, undone
        Assert.Equal(2, build.Io);                                     // ours, undone
    }

    [Fact]
    public void Restore_NeverTouchesAReusedPid()
    {
        Add(2, "rustc", created: 1000);
        var e = NewEnforcer();
        e.Enforce(Report("g.exe", (2, "rustc", OptimizerRole.WouldThrottle)));

        var newcomer = Add(2, "someone-else", created: 5000); // rustc exited, pid reused
        newcomer.Priority = ProcessPriorityClass.BelowNormal;
        _ctl.Writes.Clear();
        e.RestoreAll("test");

        Assert.Empty(_ctl.Writes);
        Assert.Equal(ProcessPriorityClass.BelowNormal, newcomer.Priority);
        Assert.Empty(e.Entries);
    }

    [Fact]
    public void ThrottleIsSticky_WhenCpuDrops_ButReleasedWhenItBecomesProtected()
    {
        var build = Add(2, "rustc");
        var e = NewEnforcer();
        e.Enforce(Report("g.exe", (2, "rustc", OptimizerRole.WouldThrottle)));

        e.Enforce(Report("g.exe", (2, "rustc", OptimizerRole.LeftAlone))); // CPU dropped
        Assert.Equal(ProcessPriorityClass.BelowNormal, build.Priority);

        e.Enforce(Report("g.exe", (2, "rustc", OptimizerRole.Protected)));
        Assert.Equal(ProcessPriorityClass.Normal, build.Priority);
        Assert.Empty(e.Entries);
    }

    [Fact]
    public void NewApp_EndsTheOldSession_RestoringEverything()
    {
        var a = Add(1, "gameA");
        var e = NewEnforcer();
        e.Enforce(Report("gameA.exe", (1, "gameA", OptimizerRole.Game)));
        Assert.Equal(ProcessPriorityClass.High, a.Priority);

        e.Enforce(Report("gameB.exe", (1, "gameA", OptimizerRole.WouldThrottle)));

        // Restored first, then throttled fresh in the new session from the true original.
        Assert.Equal(ProcessPriorityClass.BelowNormal, a.Priority);
        Assert.Equal(ProcessPriorityClass.Normal, e.Entries[1].OriginalPriority);
    }

    [Fact]
    public void CrashRecovery_ANewEnforcerRestoresWhatTheJournalHolds()
    {
        var build = Add(2, "rustc");
        NewEnforcer().Enforce(Report("g.exe", (2, "rustc", OptimizerRole.WouldThrottle)));
        Assert.Equal(ProcessPriorityClass.BelowNormal, build.Priority);

        // The app crashed; on next start a fresh enforcer loads the same journal.
        var afterCrash = NewEnforcer();
        afterCrash.RestoreAll("startup recovery");

        Assert.Equal(ProcessPriorityClass.Normal, build.Priority);
        Assert.Empty(_store.Saved);
    }

    [Fact]
    public void NoAccess_IsReportedAsFailure_AndNotRetriedEveryTick()
    {
        Add(1, "eldenring").Denied = true;
        var e = NewEnforcer();

        e.Enforce(Report("eldenring.exe", (1, "eldenring", OptimizerRole.Game)));
        e.Enforce(Report("eldenring.exe", (1, "eldenring", OptimizerRole.Game)));

        Assert.Equal("no access", e.Failures[1]);
        Assert.Empty(e.Entries);
    }

    [Fact]
    public void PartialFailure_JournalsOnlyWhatWasApplied()
    {
        var build = Add(2, "rustc");
        _ctl.FailCpuSets = true;
        var e = NewEnforcer();

        e.Enforce(Report("g.exe", (2, "rustc", OptimizerRole.WouldThrottle)));

        var entry = e.Entries[2];
        Assert.NotNull(entry.AppliedPriority);
        Assert.Null(entry.AppliedCpuSets);
        Assert.Contains("cores", e.Failures[2]);
    }

    [Fact]
    public void NonHybridCpu_ChangesPriorityButNeverCpuSets()
    {
        var game = Add(1, "g");
        var e = NewEnforcer(CpuTopology.Unknown);

        e.Enforce(Report("g.exe", (1, "g", OptimizerRole.Game)));

        Assert.Equal(ProcessPriorityClass.High, game.Priority);
        Assert.Empty(game.CpuSets);
        Assert.DoesNotContain(_ctl.Writes, w => w.Contains("sets"));
    }

    [Fact]
    public void AlreadyAtTarget_IsNotJournaled()
    {
        var game = Add(1, "g"); game.Priority = ProcessPriorityClass.High;
        var e = NewEnforcer(CpuTopology.Unknown);

        e.Enforce(Report("g.exe", (1, "g", OptimizerRole.Game)));

        Assert.Empty(e.Entries);
        Assert.Empty(_ctl.Writes);
    }

    [Fact]
    public void PlannerInput_ShowsJournaledProcessesAtTheirOriginalPriority()
    {
        Add(2, "rustc");
        var e = NewEnforcer();
        e.Enforce(Report("g.exe", (2, "rustc", OptimizerRole.WouldThrottle)));

        var snap = new ProcessSnapshot("rustc", 2, @"C:\r\rustc.exe", 1, ProcessPriorityClass.BelowNormal, 1.0);
        var fed = e.WithOriginalPriorities(new[] { snap }).Single();

        Assert.Equal(ProcessPriorityClass.Normal, fed.PriorityClass);
        var d = GameOptimizerPlanner.Classify(fed, "g", -1, Array.Empty<string>(), 0.5);
        Assert.Equal(OptimizerRole.WouldThrottle, d.Role);
    }

    // ---- CPU topology parsing ----

    private static byte[] CpuSetRecord(uint id, byte efficiencyClass)
    {
        var b = new byte[32];
        BitConverter.GetBytes(32u).CopyTo(b, 0);   // Size
        BitConverter.GetBytes(0u).CopyTo(b, 4);    // Type = CpuSetInformation
        BitConverter.GetBytes(id).CopyTo(b, 8);    // Id
        b[18] = efficiencyClass;
        return b;
    }

    [Fact]
    public void Topology_HighestEfficiencyClassIsPerformance()
    {
        var buf = CpuSetRecord(256, 1).Concat(CpuSetRecord(257, 1)).Concat(CpuSetRecord(300, 0)).ToArray();
        var t = CpuTopology.Parse(buf, buf.Length);
        Assert.True(t.IsHybrid);
        Assert.Equal(new uint[] { 256, 257 }, t.PerformanceSetIds);
        Assert.Equal(new uint[] { 300 }, t.EfficiencySetIds);
    }

    [Fact]
    public void Topology_SameClassEverywhere_IsNotHybrid()
    {
        var buf = CpuSetRecord(256, 0).Concat(CpuSetRecord(257, 0)).ToArray();
        Assert.False(CpuTopology.Parse(buf, buf.Length).IsHybrid);
    }
}
