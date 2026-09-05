using AggressiveScreensaver.Forms;
using AggressiveScreensaver.Services;
using Xunit;

namespace AggressiveScreensaver.NET.Tests;

public class FormRenderTest
{
    [Theory]
    [MemberData(nameof(RenderHelper.ScaleFactors), MemberType = typeof(RenderHelper))]
    public void Render_IgnoreListWindow_SavesToPng(float scale, string dpiLabel)
    {
        string tempIni = Path.GetTempFileName();
        File.WriteAllText(tempIni, "[Settings]\nBlockingScreenApps=vlc.exe,chrome.exe\n");
        var ini = new IniStore(tempIni);
        var powercfg = new PowercfgService(ini);
        powercfg.HistoryApps.TryAdd("vlc.exe", true);
        powercfg.HistoryApps.TryAdd("chrome.exe", true);

        string path = RenderHelper.CaptureWpfWindow(() => new IgnoreListWindow(powercfg, ini), $"ignore_list_window_render_{dpiLabel}", scaleFactor: scale);

        Assert.True(File.Exists(path));
        Console.WriteLine($"View: {path}");

        File.Delete(tempIni);
    }

    [Theory]
    [MemberData(nameof(RenderHelper.ScaleFactors), MemberType = typeof(RenderHelper))]
    public void Render_DebugWindow_SavesToPng(float scale, string dpiLabel)
    {
        string tempIni = Path.GetTempFileName();
        var ini = new IniStore(tempIni);
        var powercfg = new PowercfgService(ini);

        // Use a longer settleMs so powercfg /requests has time to return output
        string path = RenderHelper.CaptureWpfWindow(() => new DebugWindow(powercfg), $"debug_window_render_{dpiLabel}", settleMs: 1500, scaleFactor: scale);

        Assert.True(File.Exists(path));
        Console.WriteLine($"View: {path}");

        File.Delete(tempIni);
    }
}
