using System.Windows.Forms;
using AggressiveScreensaver.Forms;

namespace AggressiveScreensaver.NET.Tests;

/// <summary>
/// Isolated render tests for the tray context menu.
/// No running app required — builds the menu via TrayMenuFactory with null callbacks.
///
/// Run:  dotnet test --filter TrayMenuRenderTest
/// Output: AggressiveScreensaver.NET.Tests/bin/.../tray_menu_*.png
/// </summary>
public class TrayMenuRenderTest
{
    [Theory]
    [MemberData(nameof(RenderHelper.ScaleFactors), MemberType = typeof(RenderHelper))]
    public void RenderMenu_StartAtLogin_Unchecked_SavesToPng(float scale, string dpiLabel)
    {
        using var menu = TrayMenuFactory.Build(startAtLogin: false);
        string path = RenderHelper.CaptureMenu(menu, $"tray_menu_startup_off_{dpiLabel}", scaleFactor: scale);

        Assert.True(File.Exists(path), $"Expected PNG at {path}");
        Assert.True(new FileInfo(path).Length > 0, "PNG file is empty");
        Console.WriteLine($"View: {path}");
    }

    [Theory]
    [MemberData(nameof(RenderHelper.ScaleFactors), MemberType = typeof(RenderHelper))]
    public void RenderMenu_StartAtLogin_Checked_SavesToPng(float scale, string dpiLabel)
    {
        using var menu = TrayMenuFactory.Build(startAtLogin: true);
        string path = RenderHelper.CaptureMenu(menu, $"tray_menu_startup_on_{dpiLabel}", scaleFactor: scale);

        Assert.True(File.Exists(path), $"Expected PNG at {path}");
        Assert.True(new FileInfo(path).Length > 0, "PNG file is empty");
        Console.WriteLine($"View: {path}");
    }

    [Theory]
    [MemberData(nameof(RenderHelper.ScaleFactors), MemberType = typeof(RenderHelper))]
    public void RenderMenu_SuppressFullscreen_Unchecked_SavesToPng(float scale, string dpiLabel)
    {
        using var menu = TrayMenuFactory.Build(startAtLogin: false, suppressFullscreen: false);
        string path = RenderHelper.CaptureMenu(menu, $"tray_menu_suppress_off_{dpiLabel}", scaleFactor: scale);

        Assert.True(File.Exists(path), $"Expected PNG at {path}");
        Assert.True(new FileInfo(path).Length > 0, "PNG file is empty");
        Console.WriteLine($"View: {path}");
    }

    [Theory]
    [MemberData(nameof(RenderHelper.ScaleFactors), MemberType = typeof(RenderHelper))]
    public void RenderMenu_SuppressFullscreen_Checked_SavesToPng(float scale, string dpiLabel)
    {
        using var menu = TrayMenuFactory.Build(startAtLogin: false, suppressFullscreen: true);
        string path = RenderHelper.CaptureMenu(menu, $"tray_menu_suppress_on_{dpiLabel}", scaleFactor: scale);

        Assert.True(File.Exists(path), $"Expected PNG at {path}");
        Assert.True(new FileInfo(path).Length > 0, "PNG file is empty");
        Console.WriteLine($"View: {path}");
    }

    [Theory]
    [MemberData(nameof(RenderHelper.ScaleFactors), MemberType = typeof(RenderHelper))]
    public void RenderMenu_NativeScreensaver_WithTimeout_SavesToPng(float scale, string dpiLabel)
    {
        using var menu = TrayMenuFactory.Build(startAtLogin: true, nativeScreensaverActive: true,
            getNativeScreensaverTimeoutSec: () => 300);
        string path = RenderHelper.CaptureMenu(menu, $"tray_menu_native_screensaver_{dpiLabel}", scaleFactor: scale);

        Assert.True(File.Exists(path), $"Expected PNG at {path}");
        Assert.True(new FileInfo(path).Length > 0, "PNG file is empty");
        Console.WriteLine($"View: {path}");
    }
}
