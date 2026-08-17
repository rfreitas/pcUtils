using System.Collections.Generic;

namespace RefreshRateOverlay.WPF.Services;

internal enum ReconcileAction { None, WriteProfile, WriteDefault }

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
}
