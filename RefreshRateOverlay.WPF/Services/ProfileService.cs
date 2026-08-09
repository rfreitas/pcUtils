using System.Collections.Generic;

namespace RefreshRateOverlay.WPF.Services;

/// <summary>
/// Manages per-app refresh-rate and HDR profiles persisted in the INI file.
/// </summary>
internal sealed class ProfileService
{
    private const string SecSettings    = "Settings";
    private const string SecProfiles    = "Profiles";
    private const string SecHdrProfiles = "HDRProfiles";
    private const string KeyDefaultRate = "DefaultRefreshRate";
    private const string KeyDefaultHdr  = "DefaultHDR";
    private const string KeyHotkeyMods  = "HotkeyModifiers";
    private const string KeyHotkeyVk    = "HotkeyKey";

    private readonly IniStore _ini;

    public ProfileService(IniStore ini) => _ini = ini;

    // ---- defaults -----------------------------------------------------------

    public int  ReadDefaultRate()           => _ini.ReadInt(SecSettings, KeyDefaultRate, 60);
    public void WriteDefaultRate(int rate)  => _ini.WriteInt(SecSettings, KeyDefaultRate, rate);

    public bool ReadDefaultHdr()            => _ini.ReadBool(SecSettings, KeyDefaultHdr, false);
    public void WriteDefaultHdr(bool hdr)   => _ini.WriteBool(SecSettings, KeyDefaultHdr, hdr);

    // ---- rate profiles ------------------------------------------------------

    public bool HasRateProfile(string app) =>
        _ini.ReadString(SecProfiles, app) != string.Empty;

    public int? ReadRateProfile(string app)
    {
        string v = _ini.ReadString(SecProfiles, app);
        return int.TryParse(v, out int n) ? n : null;
    }

    public void WriteRateProfile(string app, int rate) =>
        _ini.WriteInt(SecProfiles, app, rate);

    public void DeleteRateProfile(string app) =>
        _ini.DeleteKey(SecProfiles, app);

    // ---- HDR profiles -------------------------------------------------------

    public bool? ReadHdrProfile(string app)
    {
        string v = _ini.ReadString(SecHdrProfiles, app);
        if (v == "") return null;
        return v == "1";
    }

    public void WriteHdrProfile(string app, bool hdr) =>
        _ini.WriteBool(SecHdrProfiles, app, hdr);

    public void DeleteHdrProfile(string app) =>
        _ini.DeleteKey(SecHdrProfiles, app);

    // ---- hotkey ---------------------------------------------------------------

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

    // ---- convenience --------------------------------------------------------

    public Dictionary<string, string> AllRateProfiles() =>
        _ini.ReadSection(SecProfiles);
}
