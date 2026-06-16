using RefreshRateOverlay.NET.Tests;

namespace RefreshRateOverlay.NET.Tests;

/// <summary>
/// Render tests for the tray context menu.
/// Run: dotnet test --filter TrayMenuRenderTest
/// </summary>
public class TrayMenuRenderTest
{
    private static ContextMenuStrip BuildMenu(bool startAtLogin)
    {
        const string space = "   ";
        const string check = "✓ ";

        var menu = new ContextMenuStrip
        {
            Renderer        = new DarkMenuRenderer(),
            ShowCheckMargin = false,
            ShowImageMargin = false,
        };

        menu.Items.Add(new ToolStripMenuItem(" Refresh Rate Overlay") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(space + "Settings…");
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add((startAtLogin ? check : space) + "Start at Login");
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(space + "Exit");

        return menu;
    }

    [Theory]
    [MemberData(nameof(RenderHelper.ScaleFactors), MemberType = typeof(RenderHelper))]
    public void RenderMenu_StartAtLogin_Off_SavesToPng(float scale, string dpiLabel)
    {
        using var menu = BuildMenu(startAtLogin: false);
        string path = RenderHelper.CaptureMenu(menu, $"tray_menu_off_{dpiLabel}", scaleFactor: scale);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 0);
        Console.WriteLine($"View: {path}");
    }

    [Theory]
    [MemberData(nameof(RenderHelper.ScaleFactors), MemberType = typeof(RenderHelper))]
    public void RenderMenu_StartAtLogin_On_SavesToPng(float scale, string dpiLabel)
    {
        using var menu = BuildMenu(startAtLogin: true);
        string path = RenderHelper.CaptureMenu(menu, $"tray_menu_on_{dpiLabel}", scaleFactor: scale);
        Assert.True(File.Exists(path));
        Console.WriteLine($"View: {path}");
    }
}
