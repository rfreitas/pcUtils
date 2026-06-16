namespace RefreshRateOverlay.NET.Tests;

public class ProfileServiceTests : IDisposable
{
    private readonly string     _tempIni;
    private readonly IniStore   _ini;
    private readonly ProfileService _svc;

    public ProfileServiceTests()
    {
        _tempIni = Path.GetTempFileName();
        _ini = new IniStore(_tempIni);
        _svc = new ProfileService(_ini);
    }

    public void Dispose()
    {
        _ini.Dispose();
        try { File.Delete(_tempIni); } catch { }
    }

    // ---- defaults -----------------------------------------------------------

    [Fact]
    public void DefaultRate_RoundTrips()
    {
        _svc.WriteDefaultRate(144);
        Assert.Equal(144, _svc.ReadDefaultRate());
    }

    [Fact]
    public void DefaultHdr_RoundTrips()
    {
        _svc.WriteDefaultHdr(true);
        Assert.True(_svc.ReadDefaultHdr());
        _svc.WriteDefaultHdr(false);
        Assert.False(_svc.ReadDefaultHdr());
    }

    [Fact]
    public void DefaultRate_MissingKey_Returns60()
    {
        Assert.Equal(60, _svc.ReadDefaultRate());
    }

    // ---- rate profiles ------------------------------------------------------

    [Fact]
    public void RateProfile_WriteReadDelete()
    {
        _svc.WriteRateProfile("chrome.exe", 120);
        Assert.True(_svc.HasRateProfile("chrome.exe"));
        Assert.Equal(120, _svc.ReadRateProfile("chrome.exe"));

        _svc.DeleteRateProfile("chrome.exe");
        Assert.False(_svc.HasRateProfile("chrome.exe"));
        Assert.Null(_svc.ReadRateProfile("chrome.exe"));
    }

    [Fact]
    public void RateProfile_UnknownApp_ReturnsNull()
    {
        Assert.Null(_svc.ReadRateProfile("unknown.exe"));
        Assert.False(_svc.HasRateProfile("unknown.exe"));
    }

    [Fact]
    public void RateProfile_MultipleApps_IndependentEntries()
    {
        _svc.WriteRateProfile("game.exe",   144);
        _svc.WriteRateProfile("browser.exe", 60);

        Assert.Equal(144, _svc.ReadRateProfile("game.exe"));
        Assert.Equal(60,  _svc.ReadRateProfile("browser.exe"));
    }

    // ---- HDR profiles -------------------------------------------------------

    [Fact]
    public void HdrProfile_WriteReadDelete()
    {
        _svc.WriteHdrProfile("game.exe", true);
        Assert.Equal(true, _svc.ReadHdrProfile("game.exe"));

        _svc.DeleteHdrProfile("game.exe");
        Assert.Null(_svc.ReadHdrProfile("game.exe"));
    }

    [Fact]
    public void HdrProfile_UnknownApp_ReturnsNull()
    {
        Assert.Null(_svc.ReadHdrProfile("unknown.exe"));
    }

    // ---- new: apply profile logic -------------------------------------------

    [Fact]
    public void ApplyProfile_NoProfile_UsesDefaultRate()
    {
        _svc.WriteDefaultRate(60);
        int? profileRate = _svc.ReadRateProfile("someapp.exe");
        int effective = profileRate ?? _svc.ReadDefaultRate();
        Assert.Equal(60, effective);
    }

    [Fact]
    public void ApplyProfile_WithProfile_UsesProfileRate()
    {
        _svc.WriteDefaultRate(60);
        _svc.WriteRateProfile("game.exe", 144);
        int? profileRate = _svc.ReadRateProfile("game.exe");
        int effective = profileRate ?? _svc.ReadDefaultRate();
        Assert.Equal(144, effective);
    }

    [Fact]
    public void DeleteProfile_ThenApply_FallsBackToDefault()
    {
        _svc.WriteDefaultRate(60);
        _svc.WriteRateProfile("game.exe", 144);
        _svc.DeleteRateProfile("game.exe");
        int effective = _svc.ReadRateProfile("game.exe") ?? _svc.ReadDefaultRate();
        Assert.Equal(60, effective);
    }
}
