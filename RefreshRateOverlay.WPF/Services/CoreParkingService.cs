using System;
using System.Runtime.InteropServices;

namespace RefreshRateOverlay.WPF.Services;

internal enum CoreParkingResult
{
    /// <summary>The plan already matched the target: nothing written.</summary>
    NoChange,
    /// <summary>At least one value was rewritten and the plan re-activated.</summary>
    Applied,
    /// <summary>A read, write or re-activate failed; the plan may not match the target.</summary>
    Failed,
}

/// <summary>
/// The slice of the Windows power API the core-parking enforcer needs, split out
/// so the decision logic can be tested with fake values instead of touching the
/// machine's real power plans (tests never call system commands). Every call
/// names the plan explicitly: the enforcer fetches the active plan once per pass,
/// so a plan switch mid-pass can't make reads and writes land on different plans.
/// </summary>
internal interface IProcessorPowerSettings
{
    /// <summary>The currently active plan's GUID; false if it can't be determined.</summary>
    bool TryGetActivePlan(out Guid plan);

    /// <summary>Reads one processor-subgroup setting from the given plan. False
    /// when the setting doesn't exist on this system (the efficiency-core class
    /// setting is absent on non-hybrid CPUs).</summary>
    bool TryRead(Guid plan, Guid setting, out uint ac, out uint dc);

    bool WriteAc(Guid plan, Guid setting, uint value);
    bool WriteDc(Guid plan, Guid setting, uint value);

    /// <summary>Re-activates the plan so edited values take effect now.</summary>
    bool Reactivate(Guid plan);
}

/// <summary>
/// Remembers what each value was before parking was disabled, so turning the
/// setting off for an app can put the plan back instead of guessing. Keyed by
/// plan + setting + AC/DC (values differ per plan, and a plan switch must never
/// restore one plan's numbers into another); persisted in the INI so it survives
/// a restart.
/// </summary>
internal interface IParkingRestoreStore
{
    bool TryGet(string key, out uint value);
    void Set(string key, uint value);
}

internal sealed class IniParkingRestoreStore : IParkingRestoreStore
{
    private const string Section = "CoreParkingRestore";
    private readonly IniStore _ini;

    public IniParkingRestoreStore(IniStore ini) => _ini = ini;

    public bool TryGet(string key, out uint value)
    {
        string raw = _ini.ReadString(Section, key);
        return uint.TryParse(raw, out value);
    }

    public void Set(string key, uint value) => _ini.WriteString(Section, key, value.ToString());
}

/// <summary>
/// Core parking as an on/off setting: "disabled" means "Processor performance
/// core parking min cores" is pinned to 100% on the active power plan, for both
/// efficiency classes, AC and DC. Same default+per-app shape as HDR (a
/// SyncableSetting&lt;bool&gt; in TrayApp); this class is only the hardware half:
/// read the live state, push a target.
///
/// Idempotent by design (see CLAUDE.md, "Routine re-assertion must no-op when
/// nothing's actually changing"): it reads first, writes only values that differ,
/// and re-activates the plan only when it wrote. Re-activating fires the same
/// power-setting notifications that make TrayApp re-check, so the second pass
/// finding nothing to do is what ends that loop.
///
/// "Allowed" is defined by the same live read as everything else: if
/// IsDisabled is already false there is nothing to push, so a mixed state (some
/// values at 100 because of another tool or the vendor plan) is left untouched.
/// </summary>
internal static class CoreParkingEnforcer
{
    public const uint FullyUnparked = 100;

    /// <summary>What a value becomes when parking is re-allowed from a fully
    /// pinned state but nothing was remembered for it (e.g. the user pinned it to
    /// 100 by hand before this app ever touched it): the setting's own minimum,
    /// i.e. Windows decides.</summary>
    public const uint AllowParkingFallback = 0;

    public static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");

    /// <summary>Min cores for class 0 (performance cores on a hybrid CPU) and
    /// class 1 (efficiency cores). 100 here means every core stays unparked.</summary>
    public static readonly Guid[] MinCoresSettings =
    {
        new("0cc5b647-c1df-4637-891a-dec35c318583"),
        new("0cc5b647-c1df-4637-891a-dec35c318584"),
    };

    /// <summary>Power settings whose changes should trigger a re-check: the
    /// active plan itself (switching plans loads that plan's own parking values)
    /// and the min-cores settings (another tool editing them).</summary>
    public static readonly Guid[] NotificationSettings =
    {
        new("31f9f286-5084-42fe-b720-2b0264993763"), // GUID_ACTIVE_POWERSCHEME
        MinCoresSettings[0],
        MinCoresSettings[1],
    };

    public static string RestoreKey(Guid plan, Guid setting, bool ac) =>
        $"{plan:N}.{setting:N}.{(ac ? "ac" : "dc")}";

    /// <summary>Whether the plan's parking values can be read at all. An
    /// unreadable plan makes the setting unavailable (not "allowed"), so a
    /// transient powrprof failure is never absorbed as the user's choice.</summary>
    public static bool IsAvailable(IProcessorPowerSettings power)
    {
        if (!power.TryGetActivePlan(out Guid plan)) return false;
        foreach (Guid setting in MinCoresSettings)
            if (power.TryRead(plan, setting, out _, out _)) return true;
        return false;
    }

    /// <summary>Live state: true only when at least one setting is readable and
    /// every readable value is 100% on both AC and DC.</summary>
    public static bool IsDisabled(IProcessorPowerSettings power)
    {
        if (!power.TryGetActivePlan(out Guid plan)) return false;
        return IsDisabled(power, plan);
    }

    private static bool IsDisabled(IProcessorPowerSettings power, Guid plan)
    {
        bool anyRead = false;
        foreach (Guid setting in MinCoresSettings)
        {
            if (!power.TryRead(plan, setting, out uint ac, out uint dc)) continue;
            anyRead = true;
            if (ac != FullyUnparked || dc != FullyUnparked) return false;
        }
        return anyRead;
    }

    /// <summary>Pushes the target onto the active plan. disableParking=true pins
    /// every value to 100, remembering each value it replaces. false does nothing
    /// unless the plan currently reads as fully disabled; then it puts back the
    /// remembered value (or AllowParkingFallback). It therefore never touches a
    /// plan whose live state already counts as "allowed".</summary>
    public static CoreParkingResult Apply(IProcessorPowerSettings power, IParkingRestoreStore memory, bool disableParking)
    {
        if (!power.TryGetActivePlan(out Guid plan)) return CoreParkingResult.Failed;
        if (!disableParking && !IsDisabled(power, plan)) return CoreParkingResult.NoChange;

        bool wroteAnything = false;
        bool failed = false;
        foreach (Guid setting in MinCoresSettings)
        {
            if (!power.TryRead(plan, setting, out uint ac, out uint dc)) continue;

            foreach (bool isAc in new[] { true, false })
            {
                uint current = isAc ? ac : dc;
                string key = RestoreKey(plan, setting, isAc);
                uint? desired = null;

                if (disableParking && current != FullyUnparked)
                {
                    memory.Set(key, current);
                    desired = FullyUnparked;
                }
                else if (!disableParking && current == FullyUnparked)
                {
                    desired = memory.TryGet(key, out uint remembered) && remembered != FullyUnparked
                        ? remembered
                        : AllowParkingFallback;
                }

                if (desired is not { } value) continue;
                bool ok = isAc ? power.WriteAc(plan, setting, value) : power.WriteDc(plan, setting, value);
                if (!ok) { failed = true; break; }
                wroteAnything = true;
            }
            if (failed) break;
        }

        // Re-activate even after a partial failure: values already written would
        // otherwise sit in the plan unapplied, while the live read already shows
        // them, so no later pass would ever notice.
        bool reactivated = !wroteAnything || power.Reactivate(plan);
        if (failed || !reactivated) return CoreParkingResult.Failed;
        return wroteAnything ? CoreParkingResult.Applied : CoreParkingResult.NoChange;
    }
}

/// <summary>Real implementation over powrprof.dll. Needs admin to write, which
/// this app already requires via its manifest.</summary>
internal sealed class PowrProfProcessorPowerSettings : IProcessorPowerSettings
{
    private const uint ErrorSuccess = 0;

    public bool TryGetActivePlan(out Guid plan)
    {
        plan = default;
        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr p) != ErrorSuccess || p == IntPtr.Zero) return false;
        try { plan = Marshal.PtrToStructure<Guid>(p); return true; }
        finally { LocalFree(p); }
    }

    public bool TryRead(Guid plan, Guid setting, out uint ac, out uint dc)
    {
        ac = dc = 0;
        Guid sub = CoreParkingEnforcer.SubProcessor;
        return PowerReadACValueIndex(IntPtr.Zero, ref plan, ref sub, ref setting, out ac) == ErrorSuccess
            && PowerReadDCValueIndex(IntPtr.Zero, ref plan, ref sub, ref setting, out dc) == ErrorSuccess;
    }

    public bool WriteAc(Guid plan, Guid setting, uint value)
    {
        Guid sub = CoreParkingEnforcer.SubProcessor;
        return PowerWriteACValueIndex(IntPtr.Zero, ref plan, ref sub, ref setting, value) == ErrorSuccess;
    }

    public bool WriteDc(Guid plan, Guid setting, uint value)
    {
        Guid sub = CoreParkingEnforcer.SubProcessor;
        return PowerWriteDCValueIndex(IntPtr.Zero, ref plan, ref sub, ref setting, value) == ErrorSuccess;
    }

    public bool Reactivate(Guid plan) => PowerSetActiveScheme(IntPtr.Zero, ref plan) == ErrorSuccess;

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid subGroup, ref Guid setting, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subGroup, ref Guid setting, uint value);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
