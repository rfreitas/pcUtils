using System.Collections.Generic;

namespace RefreshRateOverlay.Services;

/// <summary>
/// Manages per-app refresh-rate and HDR profiles persisted in the INI file.
/// Mirrors the AHK IniRead/IniWrite calls scattered across index.ahk.
/// </summary>
internal sealed class ProfileService
{
    private const string SecSettings    = "Settings";
    private const string SecProfiles    = "Profiles";
    private const string SecHdrProfiles = "HDRProfiles";
    private const string KeyDefaultRate = "DefaultRefreshRate";
    private const string KeyDefaultHdr  = "DefaultHDR";

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

    // ---- convenience --------------------------------------------------------

    public Dictionary<string, string> AllRateProfiles() =>
        _ini.ReadSection(SecProfiles);
}
