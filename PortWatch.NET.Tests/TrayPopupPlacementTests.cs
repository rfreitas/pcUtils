using System.Drawing;
using Shared;
using Xunit;

public class TrayPopupPlacementTests
{
    // 4K monitor, 48px taskbar at the bottom => taskbar top edge at y=2112.
    private static readonly Rectangle Screen = new(0, 0, 3840, 2160);
    private const int TaskbarTop = 2112;
    private static readonly Size Popup = new(400, 600);

    [Fact]
    public void Sits_above_the_taskbar_not_under_it()
    {
        var p = TrayPopupPlacement.Compute(new Point(3600, 2140), Popup, margin: 6, Screen, TaskbarTop);
        Assert.True(p.Y + Popup.Height <= TaskbarTop, "popup bottom must be above the taskbar top");
        Assert.Equal(TaskbarTop - Popup.Height - 6, p.Y);
    }

    [Fact]
    public void Overflow_flyout_icon_anchors_to_the_cursor_when_it_is_higher()
    {
        var p = TrayPopupPlacement.Compute(new Point(3600, 1900), Popup, margin: 6, Screen, TaskbarTop);
        Assert.Equal(1900 - Popup.Height - 6, p.Y);
    }

    [Fact]
    public void Stays_inside_the_right_edge()
    {
        var p = TrayPopupPlacement.Compute(new Point(3830, 2140), Popup, margin: 6, Screen, TaskbarTop);
        Assert.Equal(Screen.Right - Popup.Width, p.X);
    }

    [Fact]
    public void Stays_inside_the_left_edge()
    {
        var p = TrayPopupPlacement.Compute(new Point(5, 2140), Popup, margin: 6, Screen, TaskbarTop);
        Assert.Equal(0, p.X);
    }

    [Fact]
    public void Taller_than_the_screen_clamps_to_the_top()
    {
        var p = TrayPopupPlacement.Compute(new Point(3600, 2140), new Size(400, 5000), margin: 6, Screen, TaskbarTop);
        Assert.Equal(0, p.Y);
    }
}
