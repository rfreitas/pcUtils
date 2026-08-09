using Shared;

namespace LgtvBrightness.Services;

/// <summary>
/// Reads/writes the settings this app persists in the INI file: auto-brightness
/// toggle and the two adjustment hotkeys (up/down).
/// </summary>
internal sealed class Settings
{
    private const string Sec = "Settings";
    private const string KeyAutoBrightness = "AutoBrightness";
    private const string KeyUpMods         = "HotkeyUpModifiers";
    private const string KeyUpVk           = "HotkeyUpKey";
    private const string KeyDownMods       = "HotkeyDownModifiers";
    private const string KeyDownVk         = "HotkeyDownKey";

    // Defaults mirror the original AHK script's "^#Up" / "^#Down" (Ctrl+Win+Up/Down).
    private const uint DefaultMods = HotkeyService.MOD_CONTROL | HotkeyService.MOD_WIN;
    private const uint VkUp   = 0x26;
    private const uint VkDown = 0x28;

    private readonly IniStore _ini;

    public Settings(IniStore ini) => _ini = ini;

    public bool ReadAutoBrightness() => _ini.ReadBool(Sec, KeyAutoBrightness, true);
    public void WriteAutoBrightness(bool value) => _ini.WriteBool(Sec, KeyAutoBrightness, value);

    public (uint Modifiers, uint Vk) ReadHotkeyUp() =>
        ReadHotkey(KeyUpMods, KeyUpVk, DefaultMods, VkUp);

    public void WriteHotkeyUp(uint modifiers, uint vk) =>
        WriteHotkey(KeyUpMods, KeyUpVk, modifiers, vk);

    public (uint Modifiers, uint Vk) ReadHotkeyDown() =>
        ReadHotkey(KeyDownMods, KeyDownVk, DefaultMods, VkDown);

    public void WriteHotkeyDown(uint modifiers, uint vk) =>
        WriteHotkey(KeyDownMods, KeyDownVk, modifiers, vk);

    private (uint, uint) ReadHotkey(string modKey, string vkKey, uint defaultMods, uint defaultVk)
    {
        int mods = _ini.ReadInt(Sec, modKey, -1);
        int vk   = _ini.ReadInt(Sec, vkKey, -1);
        return mods >= 0 && vk >= 0 ? ((uint)mods, (uint)vk) : (defaultMods, defaultVk);
    }

    private void WriteHotkey(string modKey, string vkKey, uint modifiers, uint vk)
    {
        _ini.WriteInt(Sec, modKey, (int)modifiers);
        _ini.WriteInt(Sec, vkKey, (int)vk);
    }
}
