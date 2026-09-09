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
///
/// ShouldPush below is a second, related decision this type owns: not "what
/// changed and where do we persist it" but "do we even need to push right
/// now" — the guard every routine ApplyProfile/ApplyGsyncMode push needs and,
/// before it was pulled out here, didn't reliably get. See its own remarks.
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

    /// <summary>Should a routine re-assertion (ApplyProfile/ApplyGsyncMode on
    /// every foreground-app resolve, including Sticky Profiles resolving back
    /// to the SAME app) actually push `target`, or skip because it's already
    /// been pushed? True whenever `lastApplied` doesn't yet equal `target` —
    /// including the very first call (lastApplied is null, nothing pushed
    /// yet).
    ///
    /// This is the cache-based sibling of Plan above, for settings where
    /// re-reading live hardware/driver state before every push isn't a good
    /// trade: NVIDIA's G-SYNC push (NvidiaGsyncService.SetGlobalMode) opens
    /// and saves a full DRS session every call with no cheap "is this already
    /// the value" check of its own, and re-reading live state first to decide
    /// would either double that session cost every single push, or — worse —
    /// open a real time-of-check-to-time-of-use gap (read says "already
    /// matches", something external changes it a moment later, we wrongly
    /// skip the write that would have fixed it). Comparing against what THIS
    /// app itself last successfully pushed avoids both: no live read at all
    /// on the routine path, and a real external change is still caught
    /// independently by whatever polls live state (e.g. TrayApp's
    /// PollGsyncMode + ReconcileAll) — this cache is never consulted there,
    /// so it can never mask genuine drift, only elide a redundant re-push of
    /// a value that was never actually changing.
    ///
    /// The bug this guards against was real and shipped: every focus change
    /// used to call NvidiaGsyncService.SetGlobalMode and HdrService.SetState
    /// unconditionally, and both of those underlying OS/driver calls (a DRS
    /// session Save(), SetDisplayConfig(SDC_APPLY)) are enough on their own
    /// to make the display visibly flash/blank even when the value being
    /// (re)written hasn't changed — so alt-tabbing away from and back to a
    /// G-SYNC/HDR app produced a black flash for no reason, on every single
    /// switch, with nothing about the target ever actually drifting.</summary>
    public static bool ShouldPush<T>(T? lastApplied, T target) where T : struct =>
        !lastApplied.HasValue || !EqualityComparer<T>.Default.Equals(lastApplied.Value, target);
}
