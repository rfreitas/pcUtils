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
/// Writes into NVIDIA's own DRS driver-profile database — the same store the
/// NVIDIA app, NVIDIA Control Panel, and Profile Inspector all read and write.
/// Two different scoping models live here side by side:
///
/// - VRR_MODE (GsyncGlobalMode) only ever touches the **base profile** — see
///   its remarks for why. This class isn't the source of truth for it:
///   ProfileService's GsyncMode setting (our own INI) is, and TrayApp pushes
///   the resolved value in here on every foreground-app change and on Apply,
///   the same relationship DisplayService/HdrService have with hardware.
///   TryGetGlobalMode is deliberately not called on any recurring cadence —
///   a call costs ~130ms (loading NVIDIA's entire ~8,000-profile DRS
///   database; see RefreshRateOverlay.WPF/plans/2026-09-10-gsync-cache.md
///   for the measurements). TrayApp calls it in exactly three places: once
///   at startup to seed the overlay/INI default, once whenever focus leaves
///   the NVIDIA App (TrayApp.RefreshGsyncCacheIfLeavingNvidiaApp — the point
///   someone is most likely to have just changed it by hand), and inside
///   EnforceGsyncAsync's settle loop to confirm a just-issued push actually
///   landed. Everything else in TrayApp reads its own write-through cache
///   (_gsyncCache) instead of calling this.
/// - AppVrr (VRRApplicationOverride) and FrameCap (FRL_FPS) are the opposite:
///   genuinely per-application settings with no default/global scope of their
///   own, so this class *is* the source of truth for them — read and written
///   straight off this app's own NVIDIA profile, only ever while that app's
///   own profile is in save mode (never a background reconciliation the way
///   Rate/HDR/GsyncMode get). See TryGetAppSetting/SetAppSetting/
///   EnsureAppProfile for the shared per-app read/write/profile-creation
///   mechanics both share.
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

    // FRL_FPS, NVCP's "Max Frame Rate" (0x10835002 — confirmed against
    // nvidiaProfileInspector's own compiled NvApiDriverSettings.h, the same
    // class of source SetAppVrrOverride's remarks warn to re-verify against
    // before trusting a scraped/community ID). Unlike VRR_MODE, this one is a
    // genuinely per-application DRS setting the driver does honor per-profile
    // — no base-profile-only caveat needed here. Valid raw range is 0
    // (FRL_FPS_DISABLED) through 1023 per NVIDIA's own EValues_FRL_FPS.
    private const uint FrlFpsId = 0x10835002;

    /// <summary>Sentinel for "this app's own profile has never had FRL_FPS
    /// explicitly touched" — outside FRL_FPS's real 0-1023 range, same "real,
    /// displayable answer, not a read failure" treatment VrrAppState.NotSet
    /// gets for the same reason (GetSetting returns null, not an exception,
    /// when nothing's been set).</summary>
    public const uint FrameCapNotSet = uint.MaxValue;

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
    /// override to for this specific app's own profile. See SetAppVrrOverride
    /// for the write counterpart, and TryGetAppSetting for the shared read
    /// mechanics (FindApplicationProfile + a null-safe GetSetting).</summary>
    public static bool TryGetAppVrrState(string app, out VrrAppState state)
    {
        state = VrrAppState.NotSet;
        if (!TryGetAppSetting(app, VrrAppOverrideId, out uint? raw)) return false;
        state = raw is null ? VrrAppState.NotSet : (VrrAppState)raw.Value;
        return true;
    }

    /// <summary>Writes the per-app G-SYNC override directly onto this app's
    /// own NVIDIA profile — unlike every other write in this class, never the
    /// base profile, since VRRApplicationOverride has no default/global scope
    /// of its own to fall back to (see VrrAppState remarks). See
    /// EnsureAppProfile for the profile-creation fallback and SetAppSetting
    /// for the shared write mechanics.</summary>
    public static bool SetAppVrrOverride(string app, VrrAppState state) =>
        SetAppSetting(app, VrrAppOverrideId, state == VrrAppState.NotSet ? null : (uint)state,
            logLabel: "app-specific VRR override");

    /// <summary>Reads NVIDIA's per-app Frame Rate Limiter (FRL_FPS, NVCP's
    /// "Max Frame Rate") for this specific app's own profile. FrameCapNotSet
    /// means never explicitly touched — see its remarks. See SetAppFrameCap
    /// for the write counterpart.</summary>
    public static bool TryGetAppFrameCap(string app, out uint capFps)
    {
        capFps = FrameCapNotSet;
        if (!TryGetAppSetting(app, FrlFpsId, out uint? raw)) return false;
        capFps = raw ?? FrameCapNotSet;
        return true;
    }

    /// <summary>Writes the per-app Frame Rate Limiter directly onto this
    /// app's own NVIDIA profile — same no-default/global-scope treatment
    /// AppVrr gets (there's nothing to fall back to; this is purely per-app).
    /// FrameCapNotSet deletes the setting (back to driver default); any other
    /// value (0 = explicitly disabled, 1-1023 = the cap) is written as-is.</summary>
    public static bool SetAppFrameCap(string app, uint capFps) =>
        SetAppSetting(app, FrlFpsId, capFps == FrameCapNotSet ? null : capFps,
            logLabel: "app-specific frame cap");

    /// <summary>Shared read half of every per-app-only setting in this class
    /// (AppVrr, FrameCap): resolves this app's own profile (via
    /// FindApplicationProfile, which falls back to the base profile when the
    /// app has no profile of its own) and reads one raw setting off it. Null
    /// means the resolved profile has never had this particular setting
    /// explicitly touched — GetSetting returns null, not an exception, for
    /// that — the common case (most apps never get a per-app override), and a
    /// real, displayable answer, not a read failure. Callers each apply their
    /// own sentinel-vs-null convention on top (VrrAppState.NotSet,
    /// FrameCapNotSet).</summary>
    private static bool TryGetAppSetting(string app, uint settingId, out uint? rawValue)
    {
        rawValue = null;
        if (!IsAvailable) return false;
        try
        {
            using var session = DriverSettingsSession.CreateAndLoad();
            var setting = session.FindApplicationProfile(app)?.GetSetting(settingId);
            rawValue = setting is null ? null : Convert.ToUInt32(setting.CurrentValue);
            return true;
        }
        catch (Exception ex) { Logger.LogException(ex); return false; }
    }

    /// <summary>Shared write half of every per-app-only setting in this class:
    /// resolves (creating if needed — see EnsureAppProfile) this app's own
    /// profile, then sets or deletes one raw setting on it and saves. Null
    /// deletes the setting; any other value writes it as-is. One place for
    /// the open-session/write/save/dispose-immediately shape every per-app
    /// write here needs (see the class remarks on DRS session collision
    /// risk) instead of each setting hand-duplicating it.</summary>
    private static bool SetAppSetting(string app, uint settingId, uint? rawValue, string logLabel)
    {
        if (!IsAvailable) return false;
        try
        {
            using var session = DriverSettingsSession.CreateAndLoad();
            var profile = EnsureAppProfile(session, app);

            if (rawValue is null) profile.DeleteSetting(settingId);
            else profile.SetSetting(settingId, rawValue.Value);
            session.Save();

            Logger.Log($"NvidiaGsyncService: {logLabel} for '{app}' -> {(rawValue?.ToString() ?? "not set")} (profile='{profile.Name}').");
            return true;
        }
        catch (Exception ex) { Logger.LogException(ex); return false; }
    }

    /// <summary>Resolves this app's own NVIDIA profile, creating one (and
    /// associating the exe with it) if it doesn't have one yet — the same
    /// thing NVIDIA App/NVCP do the first time you customize a game there.
    ///
    /// Detection deliberately reuses FindApplicationProfile rather than
    /// FindApplication — an earlier version used FindApplication to probe for
    /// an existing association and it failed to find one even for an app
    /// NVIDIA App had already registered, so this code went ahead and tried
    /// to create a duplicate registration (NVAPI_EXECUTABLE_ALREADY_IN_USE).
    ///
    /// A second earlier version checked profile.IsPredefined instead of
    /// comparing against BaseProfile — also wrong, and for the same class of
    /// reason: NVIDIA ships thousands of built-in per-game profiles (acs.exe
    /// almost certainly resolves to NVIDIA's own predefined "Assetto Corsa"
    /// profile), and those are real, already-associated profiles that happen
    /// to be predefined, not the base/global fallback. IsPredefined answers
    /// "did NVIDIA ship this profile," not "does this app have no profile of
    /// its own" — comparing Name against BaseProfile is what actually answers
    /// the second question.</summary>
    private static DriverSettingsProfile EnsureAppProfile(DriverSettingsSession session, string app)
    {
        var profile = session.FindApplicationProfile(app);
        if (profile.Name == session.BaseProfile.Name)
        {
            // Resolved all the way back to the global base profile — no
            // app-specific profile (predefined or custom) exists at all.
            profile = DriverSettingsProfile.CreateProfile(session, app, null);
            ProfileApplication.CreateApplication(profile, app, app, "", Array.Empty<string>(), false, "");
        }
        return profile;
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
