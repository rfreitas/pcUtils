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
    [Fact]
    public void RenderMenu_StartAtLogin_Unchecked_SavesToPng()
    {
        using var menu = TrayMenuFactory.Build(startAtLogin: false);
        string path = RenderHelper.CaptureMenu(menu, "tray_menu_startup_off");

        Assert.True(File.Exists(path), $"Expected PNG at {path}");
        Assert.True(new FileInfo(path).Length > 0, "PNG file is empty");
        Console.WriteLine($"View: {path}");
    }

    [Fact]
    public void RenderMenu_StartAtLogin_Checked_SavesToPng()
    {
        using var menu = TrayMenuFactory.Build(startAtLogin: true);
        string path = RenderHelper.CaptureMenu(menu, "tray_menu_startup_on");

        Assert.True(File.Exists(path), $"Expected PNG at {path}");
        Assert.True(new FileInfo(path).Length > 0, "PNG file is empty");
        Console.WriteLine($"View: {path}");
    }
}
