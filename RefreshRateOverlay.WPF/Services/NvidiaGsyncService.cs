using System;
using NvAPIWrapper;
using NvAPIWrapper.DRS;

namespace RefreshRateOverlay.WPF.Services;

/// <summary>Mirrors NVIDIA's VRR_MODE enum ("Enable G-SYNC"). Confirmed by testing
/// to be a true global/base-profile-only setting in practice: an app-profile
/// override never changed whether a game got G-SYNC, only the base profile did
/// — despite NVCP/Profile Inspector exposing "per-app" scope for it, the driver
/// doesn't appear to honor an override here the way it does for other DRS
/// settings. So this app never writes it anywhere but the base profile.</summary>
public enum GsyncGlobalMode
{
    Disabled = 0,
    FullscreenOnly = 1,
    FullscreenAndWindowed = 2,
}

/// <summary>NVIDIA's actual per-application G-SYNC override (DRS setting ID
/// 0x10A879CF, NvAPIWrapper's KnownSettingId.VRRApplicationOverride) — what
/// NVIDIA App/NVCP write when you set G-SYNC for one specific game, distinct
/// from VRRMode (0x1194F158, global/base-profile only). Confirmed against
/// NvAPIWrapper's own compiled SettingValues.VRRApplicationOverride enum,
/// which matches NVIDIA's public NVAPI docs (VRR_APP_OVERRIDE_ALLOW/DEFAULT/
/// DISALLOW/FIXED_REFRESH/FORCE_OFF) — Allow and Default share the same
/// underlying 0, hence no separate Default case here.
///
/// An earlier version of this enum/ID (0x10A879CE, "VSyncVRRControl") was
/// wrong — a neighboring but different setting, sourced from a scraped
/// community reference rather than the library's own compiled constants.
/// Read it back to NVIDIA App/NVCP's per-app VRR toggle before trusting this
/// again if it's ever in doubt.</summary>
public enum VrrAppState : uint
{
    Allow        = 0x00000000,
    ForceOff     = 0x00000001,
    DisAllow     = 0x00000002,
    ULMB         = 0x00000003,
    FixedRefresh = 0x00000004,

    // Sentinel, not a real driver value: the resolved profile has never had
    // this setting explicitly touched — the common case (most apps never get
    // a per-app override), and a legitimate answer worth showing, not a
    // failure to hide the row behind.
    NotSet = 0xFFFFFFFE,
}

/// <summary>
/// Writes G-SYNC's VRR_MODE into NVIDIA's own DRS driver-profile database — the
/// same store the NVIDIA app, NVIDIA Control Panel, and Profile Inspector all
/// read and write. Only ever touches the **base profile** — see GsyncGlobalMode
/// remarks for why. This class is not the source of truth: ProfileService's
/// GsyncMode setting (backed by our own INI) is, and TrayApp pushes the
/// resolved value in here on every foreground-app change and on Apply — the
/// same relationship DisplayService and HdrService have with hardware, giving
/// GsyncMode per-app *behavior* without NVIDIA having any real per-app storage
/// for it. TryGetGlobalMode exists purely to seed the overlay's dropdown/INI
/// default the first time, before the user has ever set one through this app.
///
/// Every call opens a session, does its work, and disposes immediately rather
/// than holding one open — DRS sessions don't merge, so a session left open
/// while the NVIDIA app or NVCP also has one open risks one side's save
/// silently dropping the other's concurrent edit.
/// </summary>
internal static class NvidiaGsyncService
{
    private const uint VrrModeId = 0x1194F158;
    private const uint VrrAppOverrideId = 0x10A879CF;

    // Initialize() is ref-counted; deliberately never paired with Unload() so
    // NVAPI just stays loaded for the process lifetime once confirmed working,
    // same as this app never tears down its other native handles mid-run.
    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            NVIDIA.Initialize();
            using var probe = DriverSettingsSession.CreateAndLoad();
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"NvidiaGsyncService: NVAPI unavailable ({ex.Message}).");
            return false;
        }
    });

    public static bool IsAvailable => Available.Value;

    public static bool TryGetGlobalMode(out GsyncGlobalMode mode)
    {
        mode = GsyncGlobalMode.FullscreenOnly;
        if (!IsAvailable) return false;
        try
        {
            using var session = DriverSettingsSession.CreateAndLoad();
            var setting = session.BaseProfile.GetSetting(VrrModeId);
            mode = (GsyncGlobalMode)Convert.ToUInt32(setting.CurrentValue);
            return true;
        }
        catch (Exception ex) { Logger.LogException(ex); return false; }
    }

    /// <summary>Reads what NVIDIA currently resolves the per-app G-SYNC
    /// override to for this specific app's own profile (via
    /// FindApplicationProfile, which falls back to the base profile when the
    /// app has no profile of its own) — read-only, no write counterpart.
    /// Purely for display; see VrrAppState remarks.</summary>
    public static bool TryGetAppVrrState(string app, out VrrAppState state)
    {
        state = VrrAppState.NotSet;
        if (!IsAvailable) return false;
        try
        {
            using var session = DriverSettingsSession.CreateAndLoad();
            var profile = session.FindApplicationProfile(app);
            // GetSetting returns null (not an exception) when the resolved
            // profile has never had this particular setting explicitly
            // touched — the common case (most apps never get a per-app
            // override), and a real, displayable answer (NotSet), not a
            // read failure.
            var setting = profile?.GetSetting(VrrAppOverrideId);
            state = setting is null ? VrrAppState.NotSet : (VrrAppState)Convert.ToUInt32(setting.CurrentValue);
            return true;
        }
        catch (Exception ex) { Logger.LogException(ex); return false; }
    }

    public static bool SetGlobalMode(GsyncGlobalMode mode)
    {
        if (!IsAvailable) return false;
        try
        {
            using var session = DriverSettingsSession.CreateAndLoad();
            session.BaseProfile.SetSetting(VrrModeId, (uint)mode);
            session.Save();
            Logger.Log($"NvidiaGsyncService: global VRR_MODE -> {mode}");
            return true;
        }
        catch (Exception ex) { Logger.LogException(ex); return false; }
    }
}
