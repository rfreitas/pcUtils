namespace RefreshRateOverlay.WPF.Services;

/// <summary>
/// Owns the three per-app profile settings (rate, HDR, DSX controller profile)
/// plus the hotkey. Each setting is a ProfileSetting&lt;T&gt; built the same
/// way, so default + per-app persistence stays structurally identical across
/// types instead of being hand-duplicated per setting.
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
