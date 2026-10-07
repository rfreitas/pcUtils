using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RefreshRateOverlay.WPF.Services;

internal enum OptimizerChange { Boost, Throttle }

/// <summary>
/// One process this app changed: what it was, and what we set. An Applied value
/// of null means that attribute was not changed (already at the target, the
/// write failed, or not applicable, e.g. CPU sets on a non-hybrid CPU).
/// </summary>
internal sealed record OptimizerJournalEntry
{
    public int Pid { get; init; }
    public long CreationTime { get; init; }
    public string Name { get; init; } = "";
    public OptimizerChange Change { get; init; }
    public ProcessPriorityClass OriginalPriority { get; init; }
    public ProcessPriorityClass? AppliedPriority { get; init; }
    public uint[] OriginalCpuSets { get; init; } = Array.Empty<uint>();
    public uint[]? AppliedCpuSets { get; init; }
    public int? OriginalIoPriority { get; init; }
    public int? AppliedIoPriority { get; init; }
}

internal interface IOptimizerJournalStore
{
    List<OptimizerJournalEntry> Load();
    void Save(IReadOnlyCollection<OptimizerJournalEntry> entries);
}

/// <summary>JSON file next to the log, so a crash of this app can be undone on
/// its next start. Written before every change (write-ahead).</summary>
internal sealed class JsonOptimizerJournalStore : IOptimizerJournalStore
{
    private readonly string _path;

    public JsonOptimizerJournalStore(string path) => _path = path;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RefreshRateOverlay.WPF", "optimizer-journal.json");

    public List<OptimizerJournalEntry> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<List<OptimizerJournalEntry>>(File.ReadAllText(_path)) ?? new()
                : new();
        }
        catch (Exception ex)
        {
            Logger.Log($"Optimizer journal unreadable ({ex.Message}); starting empty.");
            return new();
        }
    }

    public void Save(IReadOnlyCollection<OptimizerJournalEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (entries.Count == 0) { File.Delete(_path); return; }
        string tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entries));
        File.Move(tmp, _path, overwrite: true);
    }
}

/// <summary>
/// Applies the planner's decisions in Live mode and undoes them. See
/// plans/2026-10-07-game-optimizer-live.md. Rules:
/// - Write-ahead: the journal is saved before a process is changed.
/// - Restore per attribute, only if it still holds the value we applied (a
///   later change by the user or another tool wins), and only for the same
///   process (creation time; pid reuse is never touched).
/// - A session is one app. A new app, a mode other than Live, exit, or startup
///   (crash recovery) restores everything.
/// - A throttled process stays throttled for the session; it is released early
///   only if it becomes Game, launch chain, VR pipeline or Protected.
/// - A child of a throttled process inherits its priority; its journal entry
///   records the parent's original so restore doesn't leave it throttled.
/// </summary>
internal sealed class GameOptimizerEnforcer
{
    public const int IoVeryLow = 0, IoNormal = 2;

    private readonly IProcessControl _control;
    private readonly IOptimizerJournalStore _store;
    private readonly CpuTopology _topology;
    private readonly Action<string> _log;
    private readonly Dictionary<int, OptimizerJournalEntry> _entries = new();
    private readonly Dictionary<int, string> _failures = new();
    // child pid → its throttled parent's entry, from the latest snapshot. A child
    // starts with the parent's Below normal/Idle priority (and may inherit more),
    // so its true original is the parent's original, not what it reads now.
    private readonly Dictionary<int, OptimizerJournalEntry> _throttledParent = new();

    public GameOptimizerEnforcer(IProcessControl control, IOptimizerJournalStore store, CpuTopology topology, Action<string>? log = null)
    {
        _control = control;
        _store = store;
        _topology = topology;
        _log = log ?? (_ => { });
        foreach (var e in store.Load()) _entries[e.Pid] = e;
    }

    public string? SessionApp { get; private set; }

    public OptimizerActions Actions { get; private set; } = OptimizerActions.Default;

    /// <summary>New panel choices. Everything applied under the old choices is
    /// restored first; the next Enforce applies the new ones from the true originals.</summary>
    public void SetActions(OptimizerActions actions)
    {
        actions = actions.Normalized();
        if (actions == Actions) return;
        RestoreAll("optimizer settings changed");
        Actions = actions;
    }
    public IReadOnlyDictionary<int, OptimizerJournalEntry> Entries => _entries;

    /// <summary>pid → why a change could not be applied (this session).</summary>
    public IReadOnlyDictionary<int, string> Failures => _failures;

    /// <summary>Planner input with journaled processes shown at their ORIGINAL
    /// priority, so our own Below normal doesn't reclassify them as
    /// "Priority already set" on the next pass. A child of a throttled process
    /// that carries the parent's applied priority is shown at the parent's
    /// original, because that priority came from us, not from the user.</summary>
    public List<ProcessSnapshot> WithOriginalPriorities(IEnumerable<ProcessSnapshot> snapshots)
    {
        _throttledParent.Clear();
        var result = new List<ProcessSnapshot>();
        foreach (var s in snapshots)
        {
            OptimizerJournalEntry? source = null;
            if (_entries.TryGetValue(s.Pid, out var own)) source = own;
            else if (s.ParentPid is { } pp && _entries.TryGetValue(pp, out var parent) && parent.Change == OptimizerChange.Throttle)
            {
                _throttledParent[s.Pid] = parent;
                source = parent;
            }
            result.Add(source is { AppliedPriority: not null } e && s.PriorityClass == e.AppliedPriority
                ? s with { PriorityClass = e.OriginalPriority }
                : s);
        }
        return result;
    }

    public void Enforce(OptimizerReport report)
    {
        if (!string.Equals(SessionApp, report.App, StringComparison.OrdinalIgnoreCase))
        {
            RestoreAll($"session moved to '{report.App}'");
            SessionApp = report.App;
        }

        var byPid = report.Decisions.GroupBy(d => d.Pid).ToDictionary(g => g.Key, g => g.First());

        // Release what should no longer be changed.
        foreach (var entry in _entries.Values.ToList())
        {
            byPid.TryGetValue(entry.Pid, out var d);
            bool keep = d is not null && (entry.Change == OptimizerChange.Boost
                ? d.Role == OptimizerRole.Game
                : d.Role is OptimizerRole.WouldThrottle or OptimizerRole.LeftAlone); // sticky throttle
            if (!keep) Restore(entry, d is null ? "exited" : $"now {d.Role}");
        }

        // Apply what is new.
        foreach (var d in report.Decisions)
        {
            if (_entries.ContainsKey(d.Pid) || _failures.ContainsKey(d.Pid)) continue;
            if (d.Role == OptimizerRole.Game) Apply(d, OptimizerChange.Boost);
            else if (d.Role == OptimizerRole.WouldThrottle) Apply(d, OptimizerChange.Throttle);
        }
    }

    private void Apply(OptimizerDecision d, OptimizerChange change)
    {
        var state = _control.Read(d.Pid);
        if (state is null) { Fail(d, "no access"); return; }

        // Values come from the panel's choices (Actions). The default leaves the
        // game's priority alone: a High game outranks the Normal-priority threads
        // of the VR streamer/runtime that must keep up with it (see CLAUDE.md).
        bool boost = change == OptimizerChange.Boost;
        ProcessPriorityClass? priority = OptimizerActions.ToPriorityClass(boost ? Actions.GamePriority : Actions.ThrottlePriority);
        uint[]? sets = !_topology.IsHybrid ? null : (boost ? Actions.GameCores : Actions.ThrottleCores) switch
        {
            CoreChoice.PCores => _topology.PerformanceSetIds.ToArray(),
            CoreChoice.ECores => _topology.EfficiencySetIds.ToArray(),
            _ => null,
        };
        int? io = change == OptimizerChange.Throttle ? IoVeryLow : null;

        // Originals: what the process has now, except values it inherited from a
        // throttled parent (equal to what we applied there), which go back to the
        // parent's original on restore instead of staying throttled forever.
        ProcessPriorityClass origPriority = state.Priority;
        uint[] origSets = state.CpuSets;
        int? origIo = state.IoPriority;
        if (change == OptimizerChange.Throttle && _throttledParent.TryGetValue(d.Pid, out var parent))
        {
            if (parent.AppliedPriority is { } pp && state.Priority == pp) origPriority = parent.OriginalPriority;
            if (parent.AppliedCpuSets is { } ps && SameSets(state.CpuSets, ps)) origSets = parent.OriginalCpuSets;
            if (parent.AppliedIoPriority is { } pi && state.IoPriority == pi) origIo = parent.OriginalIoPriority;
        }

        var entry = new OptimizerJournalEntry
        {
            Pid = d.Pid,
            CreationTime = state.CreationTime,
            Name = d.Name,
            Change = change,
            OriginalPriority = origPriority,
            AppliedPriority = priority is null || origPriority == priority ? null : priority,
            OriginalCpuSets = origSets,
            AppliedCpuSets = sets is not null && !SameSets(origSets, sets) ? sets : null,
            OriginalIoPriority = origIo,
            AppliedIoPriority = io is not null && origIo != io ? io : null,
        };
        if (entry.AppliedPriority is null && entry.AppliedCpuSets is null && entry.AppliedIoPriority is null) return;

        _entries[d.Pid] = entry;
        Persist(); // write-ahead: recorded before anything is changed

        // An inherited value may already be the target; then there is nothing to
        // write, but the entry still restores it.
        var failed = new List<string>();
        if (entry.AppliedPriority is { } p && state.Priority != p && !_control.SetPriority(d.Pid, state.CreationTime, p))
        { failed.Add("priority"); entry = entry with { AppliedPriority = null }; }
        if (entry.AppliedCpuSets is { } s && !SameSets(state.CpuSets, s) && !_control.SetCpuSets(d.Pid, state.CreationTime, s))
        { failed.Add("cores"); entry = entry with { AppliedCpuSets = null }; }
        if (entry.AppliedIoPriority is { } i && state.IoPriority != i && !_control.SetIoPriority(d.Pid, state.CreationTime, i))
        { failed.Add("disk priority"); entry = entry with { AppliedIoPriority = null }; }

        if (entry.AppliedPriority is null && entry.AppliedCpuSets is null && entry.AppliedIoPriority is null)
            _entries.Remove(d.Pid);
        else
            _entries[d.Pid] = entry;
        Persist();

        if (failed.Count > 0) Fail(d, "could not set " + string.Join(", ", failed));
        _log($"Optimizer: {change} '{d.Name}' pid={d.Pid}{(failed.Count > 0 ? " (partial: " + string.Join(", ", failed) + " failed)" : "")}.");
    }

    private void Fail(OptimizerDecision d, string reason)
    {
        _failures[d.Pid] = reason;
        _log($"Optimizer: could not change '{d.Name}' pid={d.Pid}: {reason}.");
    }

    /// <summary>Undoes everything (session end, mode change, exit, startup recovery).</summary>
    public void RestoreAll(string why)
    {
        if (_entries.Count > 0) _log($"Optimizer: restoring {_entries.Count} process(es) ({why}).");
        foreach (var entry in _entries.Values.ToList()) Restore(entry, why);
        _failures.Clear();
        SessionApp = null;
    }

    private void Restore(OptimizerJournalEntry e, string why)
    {
        var state = _control.Read(e.Pid);
        if (state is not null && state.CreationTime == e.CreationTime)
        {
            if (e.AppliedPriority is { } ap && state.Priority == ap)
                _control.SetPriority(e.Pid, e.CreationTime, e.OriginalPriority);
            if (e.AppliedCpuSets is { } aps && SameSets(state.CpuSets, aps))
                _control.SetCpuSets(e.Pid, e.CreationTime, e.OriginalCpuSets);
            if (e.AppliedIoPriority is { } ai && state.IoPriority == ai)
                _control.SetIoPriority(e.Pid, e.CreationTime, e.OriginalIoPriority ?? IoNormal);
        }
        _entries.Remove(e.Pid);
        Persist();
    }

    private void Persist()
    {
        try { _store.Save(_entries.Values.ToList()); }
        catch (Exception ex) { _log($"Optimizer: could not save journal: {ex.Message}"); }
    }

    private static bool SameSets(uint[] a, uint[] b) =>
        a.Length == b.Length && a.OrderBy(x => x).SequenceEqual(b.OrderBy(x => x));
}
