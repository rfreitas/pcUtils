using System;

namespace RefreshRateOverlay.WPF.Services;

/// <summary>
/// Owns the per-app profile settings (rate, HDR, DSX controller profile,
/// G-SYNC mode) plus the hotkey. Each setting is a ProfileSetting&lt;T&gt;
/// built the same way, so default + per-app persistence stays structurally
/// identical across types instead of being hand-duplicated per setting.
///
/// GsyncMode is backed by this same INI, not NVIDIA's own DRS database — this
/// INI is the source of truth for it too, exactly like Rate/Hdr. NVIDIA has no
/// per-app concept for VRR_MODE that the driver actually honors (confirmed by
/// testing: an app-profile override never took effect, only the base profile
/// did) — so unlike a real per-app NVIDIA setting, "per-app" here works the
/// same way HDR's per-app behavior does despite HDR having no per-app concept
/// at the OS level either: TrayApp resolves (per-app override ?? default) on
/// every foreground switch and reasserts that single resolved value into
/// NVIDIA's one base-profile VRR_MODE setting (NvidiaGsyncService.SetGlobalMode)
/// — never into a per-app NVIDIA profile.
/// </summary>
internal sealed class ProfileService
{
    private const string SecSettings   = "Settings";
    private const string KeyHotkeyMods = "HotkeyModifiers";
    private const string KeyHotkeyVk   = "HotkeyKey";

    private readonly IniStore _ini;

    public ProfileSetting<int>    Rate { get; }
    public ProfileSetting<bool>   Hdr  { get; }
    public ProfileSetting<string> Dsx  { get; }

    public ProfileSetting<GsyncGlobalMode> GsyncMode { get; }

    public ProfileService(IniStore ini)
    {
        _ini = ini;

        Rate = new ProfileSetting<int>(
            ini, SecSettings, "DefaultRefreshRate", "Profiles",
            parse: (string s, out int v) => int.TryParse(s, out v),
            format: v => v.ToString(),
            fallback: 60);

        Hdr = new ProfileSetting<bool>(
            ini, SecSettings, "DefaultHDR", "HDRProfiles",
            parse: (string s, out bool v) => { v = s == "1"; return true; },
            format: v => v ? "1" : "0",
            fallback: false);

        Dsx = new ProfileSetting<string>(
            ini, SecSettings, "DefaultControllerProfile", "ControllerProfiles",
            parse: (string s, out string v) => { v = s; return true; },
            format: v => v,
            fallback: "");

        GsyncMode = new ProfileSetting<GsyncGlobalMode>(
            ini, SecSettings, "DefaultGsyncMode", "GsyncModeProfiles",
            parse: (string s, out GsyncGlobalMode v) => Enum.TryParse(s, out v),
            format: v => v.ToString(),
            fallback: GsyncGlobalMode.FullscreenOnly);
    }

    // ---- hotkey ---------------------------------------------------------------
    // Global-only (no per-app concept), so it doesn't fit ProfileSetting<T>.

    /// <summary>Returns the saved hotkey, or null if the user has never changed it.</summary>
    public (uint Modifiers, uint Vk)? ReadHotkey()
    {
        int mods = _ini.ReadInt(SecSettings, KeyHotkeyMods, -1);
        int vk   = _ini.ReadInt(SecSettings, KeyHotkeyVk, -1);
        return mods >= 0 && vk >= 0 ? ((uint)mods, (uint)vk) : null;
    }

    public void WriteHotkey(uint modifiers, uint vk)
    {
        _ini.WriteInt(SecSettings, KeyHotkeyMods, (int)modifiers);
        _ini.WriteInt(SecSettings, KeyHotkeyVk, (int)vk);
    }
}
