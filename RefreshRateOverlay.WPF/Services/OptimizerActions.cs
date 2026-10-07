using System;
using System.Diagnostics;

namespace RefreshRateOverlay.WPF.Services;

/// <summary>Priority choices offered in the optimizer panel. Realtime is
/// deliberately absent (it can starve input, audio and the VR compositor).
/// Persisted by name: append only.</summary>
public enum PriorityChoice { Unchanged, AboveNormal, High, BelowNormal, Idle }

/// <summary>Which cores a process is steered to (process default CPU sets).
/// Any = CPU sets left as they are. Persisted by name: append only.</summary>
public enum CoreChoice { Any, PCores, ECores }

/// <summary>
/// What Live mode does to the game and to throttled processes, chosen in the
/// side panel and stored globally in the INI. Pure: the planner uses it for
/// the action text, the enforcer for the actual values.
/// </summary>
internal sealed record OptimizerActions(
    PriorityChoice GamePriority, CoreChoice GameCores,
    PriorityChoice ThrottlePriority, CoreChoice ThrottleCores)
{
    /// <summary>Game priority unchanged: a High game outranks the Normal-priority
    /// VR streamer/runtime threads (see CLAUDE.md).</summary>
    public static readonly OptimizerActions Default =
        new(PriorityChoice.Unchanged, CoreChoice.PCores, PriorityChoice.BelowNormal, CoreChoice.ECores);

    public static readonly PriorityChoice[] GamePriorities = { PriorityChoice.Unchanged, PriorityChoice.AboveNormal, PriorityChoice.High };
    public static readonly PriorityChoice[] ThrottlePriorities = { PriorityChoice.Unchanged, PriorityChoice.BelowNormal, PriorityChoice.Idle };
    public static readonly CoreChoice[] Cores = { CoreChoice.Any, CoreChoice.PCores, CoreChoice.ECores };

    /// <summary>Only ever raise the game and only ever lower a throttled
    /// process; anything else read from a hand-edited INI falls back to the default.</summary>
    public OptimizerActions Normalized() => this with
    {
        GamePriority = Array.IndexOf(GamePriorities, GamePriority) >= 0 ? GamePriority : Default.GamePriority,
        ThrottlePriority = Array.IndexOf(ThrottlePriorities, ThrottlePriority) >= 0 ? ThrottlePriority : Default.ThrottlePriority,
    };

    public static ProcessPriorityClass? ToPriorityClass(PriorityChoice c) => c switch
    {
        PriorityChoice.AboveNormal => ProcessPriorityClass.AboveNormal,
        PriorityChoice.High => ProcessPriorityClass.High,
        PriorityChoice.BelowNormal => ProcessPriorityClass.BelowNormal,
        PriorityChoice.Idle => ProcessPriorityClass.Idle,
        _ => null,
    };

    public static string Label(PriorityChoice c) => c switch
    {
        PriorityChoice.Unchanged => "Unchanged",
        PriorityChoice.AboveNormal => "Above normal",
        PriorityChoice.High => "High",
        PriorityChoice.BelowNormal => "Below normal",
        PriorityChoice.Idle => "Idle",
        _ => c.ToString(),
    };

    public static string Label(CoreChoice c) => c switch
    {
        CoreChoice.Any => "Any",
        CoreChoice.PCores => "P-cores",
        CoreChoice.ECores => "E-cores",
        _ => c.ToString(),
    };

    public string GameDescription
    {
        get
        {
            string cores = GameCores switch
            {
                CoreChoice.PCores => "P-cores preferred",
                CoreChoice.ECores => "E-cores preferred",
                _ => "any cores",
            };
            string priority = GamePriority == PriorityChoice.Unchanged ? "priority unchanged" : $"{Label(GamePriority)} priority";
            return $"{cores}, {priority}";
        }
    }

    public string ThrottleDescription
    {
        get
        {
            string priority = ThrottlePriority == PriorityChoice.Unchanged ? "priority unchanged" : Label(ThrottlePriority);
            string cores = ThrottleCores == CoreChoice.Any ? "any cores" : Label(ThrottleCores);
            return $"{priority}, {cores}, low I/O";
        }
    }
}
