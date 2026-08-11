using System.Linq;
using System.Windows.Forms;
using AggressiveScreensaver.Forms;

namespace AggressiveScreensaver.NET.Tests;

public class TrayMenuBehaviorTests
{
    private static ToolStripMenuItem Item(ContextMenuStrip menu, string textPart) =>
        menu.Items.OfType<ToolStripMenuItem>()
            .First(i => (i.Text ?? "").Contains(textPart));

    // -------------------------------------------------------------------------
    // Suppress in Fullscreen — initial state
    // -------------------------------------------------------------------------

    [Fact]
    public void SuppressFullscreen_ItemExists()
    {
        using var menu = TrayMenuFactory.Build(startAtLogin: false);
        Assert.NotNull(Item(menu, "Suppress in Fullscreen"));
    }

    [Fact]
    public void SuppressFullscreen_False_ItemUnchecked()
    {
        using var menu = TrayMenuFactory.Build(startAtLogin: false, suppressFullscreen: false);
        var item = Item(menu, "Suppress in Fullscreen");
        Assert.False(item.Checked);
        Assert.StartsWith("   ", item.Text);
    }

    [Fact]
    public void SuppressFullscreen_True_ItemChecked()
    {
        using var menu = TrayMenuFactory.Build(startAtLogin: false, suppressFullscreen: true);
        var item = Item(menu, "Suppress in Fullscreen");
        Assert.True(item.Checked);
        Assert.StartsWith("✓", item.Text);
    }

    // -------------------------------------------------------------------------
    // Suppress in Fullscreen — toggling
    // -------------------------------------------------------------------------

    [Fact]
    public void SuppressFullscreen_Toggle_InvokesCallbackWithNewState()
    {
        bool? received = null;
        using var menu = TrayMenuFactory.Build(
            startAtLogin: false,
            suppressFullscreen: false,
            onSuppressFullscreenChanged: v => { received = v; return true; });

        Item(menu, "Suppress in Fullscreen").Checked = true;

        Assert.True(received);
    }

    [Fact]
    public void SuppressFullscreen_Toggle_UpdatesTextPrefix()
    {
        using var menu = TrayMenuFactory.Build(
            startAtLogin: false,
            suppressFullscreen: false,
            onSuppressFullscreenChanged: _ => true);

        var item = Item(menu, "Suppress in Fullscreen");
        item.Checked = true;
        Assert.StartsWith("✓", item.Text);

        item.Checked = false;
        Assert.StartsWith("   ", item.Text);
    }

    [Fact]
    public void SuppressFullscreen_Toggle_CallbackReturnsFalse_Reverts()
    {
        using var menu = TrayMenuFactory.Build(
            startAtLogin: false,
            suppressFullscreen: false,
            onSuppressFullscreenChanged: _ => false);

        var item = Item(menu, "Suppress in Fullscreen");
        item.Checked = true;

        Assert.False(item.Checked);
        Assert.StartsWith("   ", item.Text);
    }

    // -------------------------------------------------------------------------
    // Start at Login — verify existing behaviour unaffected
    // -------------------------------------------------------------------------

    [Fact]
    public void StartAtLogin_AndSuppressFullscreen_BothReflectIndependentState()
    {
        using var menu = TrayMenuFactory.Build(startAtLogin: true, suppressFullscreen: false);
        Assert.StartsWith("✓", Item(menu, "Start at Login").Text);
        Assert.StartsWith("   ", Item(menu, "Suppress in Fullscreen").Text);
    }

    // -------------------------------------------------------------------------
    // Ignore Unfocused Blockers — initial state
    // -------------------------------------------------------------------------

    [Fact]
    public void IgnoreUnfocusedBlockers_ItemExists()
    {
        using var menu = TrayMenuFactory.Build(startAtLogin: false);
        Assert.NotNull(Item(menu, "Ignore Unfocused Blockers"));
    }

    [Fact]
    public void IgnoreUnfocusedBlockers_DefaultsToChecked()
    {
        using var menu = TrayMenuFactory.Build(startAtLogin: false);
        var item = Item(menu, "Ignore Unfocused Blockers");
        Assert.True(item.Checked);
        Assert.StartsWith("✓", item.Text);
    }

    [Fact]
    public void IgnoreUnfocusedBlockers_False_ItemUnchecked()
    {
        using var menu = TrayMenuFactory.Build(startAtLogin: false, ignoreUnfocusedBlockers: false);
        var item = Item(menu, "Ignore Unfocused Blockers");
        Assert.False(item.Checked);
        Assert.StartsWith("   ", item.Text);
    }

    // -------------------------------------------------------------------------
    // Ignore Unfocused Blockers — toggling
    // -------------------------------------------------------------------------

    [Fact]
    public void IgnoreUnfocusedBlockers_Toggle_InvokesCallbackWithNewState()
    {
        bool? received = null;
        using var menu = TrayMenuFactory.Build(
            startAtLogin: false,
            ignoreUnfocusedBlockers: true,
            onIgnoreUnfocusedBlockersChanged: v => { received = v; return true; });

        Item(menu, "Ignore Unfocused Blockers").Checked = false;

        Assert.False(received);
    }

    [Fact]
    public void IgnoreUnfocusedBlockers_Toggle_CallbackReturnsFalse_Reverts()
    {
        using var menu = TrayMenuFactory.Build(
            startAtLogin: false,
            ignoreUnfocusedBlockers: true,
            onIgnoreUnfocusedBlockersChanged: _ => false);

        var item = Item(menu, "Ignore Unfocused Blockers");
        item.Checked = false;

        Assert.True(item.Checked);
        Assert.StartsWith("✓", item.Text);
    }
}
