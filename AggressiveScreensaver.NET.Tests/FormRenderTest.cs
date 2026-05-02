using System.IO;
using System.Windows.Forms;
using AggressiveScreensaver.Forms;
using AggressiveScreensaver.Input;
using AggressiveScreensaver.Services;
using Xunit;

namespace AggressiveScreensaver.NET.Tests;

public class FormRenderTest
{
    [Theory]
    [MemberData(nameof(RenderHelper.ScaleFactors), MemberType = typeof(RenderHelper))]
    public void Render_BlacklistForm_SavesToPng(float scale, string dpiLabel)
    {
        // Mock IniStore
        string tempIni = Path.GetTempFileName();
        File.WriteAllText(tempIni, "[Settings]\nBlockingScreenApps=vlc.exe,chrome.exe\n");
        var ini = new IniStore(tempIni);
        var powercfg = new PowercfgService(ini);
        powercfg.HistoryApps.TryAdd("vlc.exe", true);
        powercfg.HistoryApps.TryAdd("chrome.exe", true);

        string path = RenderHelper.CaptureForm(() => new BlacklistForm(powercfg, ini), $"blacklist_form_render_{dpiLabel}", scaleFactor: scale);

        Assert.True(File.Exists(path));
        System.Console.WriteLine($"View: {path}");
        
        File.Delete(tempIni);
    }

    [Theory]
    [MemberData(nameof(RenderHelper.ScaleFactors), MemberType = typeof(RenderHelper))]
    public void Render_DebugForm_SavesToPng(float scale, string dpiLabel)
    {
        string tempIni = Path.GetTempFileName();
        var ini = new IniStore(tempIni);
        var powercfg = new PowercfgService(ini);
        
        // Use a longer settleMs so powercfg /requests has time to return output
        string path = RenderHelper.CaptureForm(() => new DebugForm(powercfg), $"debug_form_render_{dpiLabel}", settleMs: 1500, scaleFactor: scale);

        Assert.True(File.Exists(path));
        System.Console.WriteLine($"View: {path}");
        
        File.Delete(tempIni);
    }
}
