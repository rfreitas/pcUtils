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
