using System.Collections.Generic;

namespace RefreshRateOverlay.WPF.Services;

internal enum ReconcileAction { None, WriteProfile, WriteDefault }

internal enum SeedAction { KeepStored, AdoptLive }

/// <summary>
/// Pure decision logic shared by every HardwareChange reconciler
/// (TrayApp.ReconcileRateAndHdr, TrayApp.ReconcileGsyncMode): given what's
/// actually live right now, what SHOULD be there for the current app, and
/// whether that app has its own profile, decide whether — and where — to
/// persist observed drift. Extracted into its own pure, generic type because
/// this exact branching is where both of the reconciliation bugs from this
/// app's development actually lived (comparing against the wrong reference
/// during settling; pushing the wrong app's resolved value to the overlay) —
/// neither was ever caught by a test, because there was nothing here to test
/// in isolation from real hardware/driver I/O until this was pulled out.
/// </summary>
internal static class ReconcilePlanner
{
    /// <summary>What this app's rate/HDR/GsyncMode/etc. SHOULD be right now:
    /// its own profile value if it has one, otherwise the shared default. Same
    /// resolution used both to apply a setting and to detect drift from it —
    /// one shared definition of "correct" instead of two that could
    /// disagree.</summary>
    public static T Resolve<T>(bool hasProfile, T profileValue, T defaultValue) =>
        hasProfile ? profileValue : defaultValue;

    /// <summary>Given the live value, what this app's target should be
    /// (typically from Resolve), and whether this app has its own profile:
    /// None if they already match, otherwise WriteProfile (this app's own
    /// slot) or WriteDefault (the shared slot) — whichever one is actually in
    /// effect for it.</summary>
    public static ReconcileAction Plan<T>(T live, T target, bool hasProfile)
    {
        if (EqualityComparer<T>.Default.Equals(live, target)) return ReconcileAction.None;
        return hasProfile ? ReconcileAction.WriteProfile : ReconcileAction.WriteDefault;
    }

    /// <summary>Should TrayApp's constructor adopt live hardware/driver state as
    /// this setting's default at startup, or keep whatever's already stored?
    ///
    /// True first run (hasStoredDefault false — the INI key has never been
    /// written) always adopts live: there's no persisted truth yet to protect,
    /// only a bootstrap decision, same as picking an initial value out of thin
    /// air. Every later run answers the exact same question ReconcileAll asks
    /// for every other drift during the session — "is an externally-observed
    /// value allowed to overwrite our own persisted truth?" — and must answer
    /// it the same way: only when reconciliationEnabled, mirroring Plan above.
    ///
    /// This exists because the pre-fix code answered that question differently
    /// at startup than everywhere else: it unconditionally adopted live state
    /// (rate only when the stored default happened to equal a hardcoded 60
    /// sentinel; HDR and G-SYNC on every single restart, no gate at all) with
    /// no regard for ReconciliationEnabled. With reconciliation off — meaning
    /// the INI is supposed to be the sole source of truth across restarts, the
    /// same way it already is mid-session — that let a live value merely left
    /// over from whatever was on screen at the moment of a crash/restart (a
    /// game's own profile rate/HDR/G-SYNC state) permanently overwrite the
    /// real default and persist it, indistinguishable from a deliberate
    /// profile change. Real bug, confirmed via RefreshRateOverlay.WPF.log: a
    /// restart while a 100Hz-profiled game was still driving the display
    /// silently rewrote DefaultRefreshRate 60 -> 100, and every unprofiled app
    /// (Explorer, other tools) ran at 100Hz from then on — exactly the shape
    /// of "a game's profile became the global default" the user reported,
    /// even though no profile save was ever involved.</summary>
    public static SeedAction PlanSeed<T>(bool hasStoredDefault, T stored, T live, bool reconciliationEnabled)
    {
        if (!hasStoredDefault) return SeedAction.AdoptLive;
        if (!reconciliationEnabled) return SeedAction.KeepStored;
        return EqualityComparer<T>.Default.Equals(stored, live) ? SeedAction.KeepStored : SeedAction.AdoptLive;
    }
}
