using System.ComponentModel;
using System.Linq;
using System.Reflection;
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

    // -------------------------------------------------------------------------
    // Ignore Nonvisible Blockers — initial state
    // -------------------------------------------------------------------------

    [Fact]
    public void IgnoreNonvisibleBlockers_ItemExists()
    {
        using var menu = TrayMenuFactory.Build(startAtLogin: false);
        Assert.NotNull(Item(menu, "Ignore Nonvisible Blockers"));
    }

    [Fact]
    public void IgnoreNonvisibleBlockers_DefaultsToUnchecked()
    {
        using var menu = TrayMenuFactory.Build(startAtLogin: false);
        var item = Item(menu, "Ignore Nonvisible Blockers");
        Assert.False(item.Checked);
        Assert.StartsWith("   ", item.Text);
    }

    [Fact]
    public void IgnoreNonvisibleBlockers_True_ItemChecked()
    {
        using var menu = TrayMenuFactory.Build(startAtLogin: false, ignoreNonvisibleBlockers: true);
        var item = Item(menu, "Ignore Nonvisible Blockers");
        Assert.True(item.Checked);
        Assert.StartsWith("✓", item.Text);
    }

    // -------------------------------------------------------------------------
    // Ignore Nonvisible Blockers — toggling
    // -------------------------------------------------------------------------

    [Fact]
    public void IgnoreNonvisibleBlockers_Toggle_InvokesCallbackWithNewState()
    {
        bool? received = null;
        using var menu = TrayMenuFactory.Build(
            startAtLogin: false,
            ignoreUnfocusedBlockers: true,
            ignoreNonvisibleBlockers: false,
            onIgnoreNonvisibleBlockersChanged: v => { received = v; return true; });

        Item(menu, "Ignore Nonvisible Blockers").Checked = true;

        Assert.True(received);
    }

    [Fact]
    public void IgnoreNonvisibleBlockers_Toggle_UpdatesTextPrefix()
    {
        using var menu = TrayMenuFactory.Build(
            startAtLogin: false,
            ignoreUnfocusedBlockers: true,
            ignoreNonvisibleBlockers: false,
            onIgnoreNonvisibleBlockersChanged: _ => true);

        var item = Item(menu, "Ignore Nonvisible Blockers");
        item.Checked = true;
        Assert.StartsWith("✓", item.Text);

        item.Checked = false;
        Assert.StartsWith("   ", item.Text);
    }

    [Fact]
    public void IgnoreNonvisibleBlockers_Toggle_CallbackReturnsFalse_Reverts()
    {
        using var menu = TrayMenuFactory.Build(
            startAtLogin: false,
            ignoreUnfocusedBlockers: true,
            ignoreNonvisibleBlockers: false,
            onIgnoreNonvisibleBlockersChanged: _ => false);

        var item = Item(menu, "Ignore Nonvisible Blockers");
        item.Checked = true;

        Assert.False(item.Checked);
        Assert.StartsWith("   ", item.Text);
    }

    // -------------------------------------------------------------------------
    // Ignore Nonvisible Blockers — grayed out when Ignore Unfocused Blockers is off
    // -------------------------------------------------------------------------

    [Fact]
    public void IgnoreNonvisibleBlockers_Disabled_WhenUnfocusedBlockersOff()
    {
        using var menu = TrayMenuFactory.Build(startAtLogin: false, ignoreUnfocusedBlockers: false);
        Assert.False(Item(menu, "Ignore Nonvisible Blockers").Enabled);
    }

    [Fact]
    public void IgnoreNonvisibleBlockers_Enabled_WhenUnfocusedBlockersOn()
    {
        using var menu = TrayMenuFactory.Build(startAtLogin: false, ignoreUnfocusedBlockers: true);
        Assert.True(Item(menu, "Ignore Nonvisible Blockers").Enabled);
    }

    [Fact]
    public void IgnoreNonvisibleBlockers_BecomesDisabled_WhenUnfocusedBlockersToggledOff()
    {
        using var menu = TrayMenuFactory.Build(
            startAtLogin: false,
            ignoreUnfocusedBlockers: true,
            onIgnoreUnfocusedBlockersChanged: _ => true);

        Item(menu, "Ignore Unfocused Blockers").Checked = false;

        Assert.False(Item(menu, "Ignore Nonvisible Blockers").Enabled);
    }

    [Fact]
    public void IgnoreNonvisibleBlockers_BecomesEnabled_WhenUnfocusedBlockersToggledOn()
    {
        using var menu = TrayMenuFactory.Build(
            startAtLogin: false,
            ignoreUnfocusedBlockers: false,
            onIgnoreUnfocusedBlockersChanged: _ => true);

        Item(menu, "Ignore Unfocused Blockers").Checked = true;

        Assert.True(Item(menu, "Ignore Nonvisible Blockers").Enabled);
    }

    // -------------------------------------------------------------------------
    // Native Screensaver — re-synced from the OS every time the menu opens
    // -------------------------------------------------------------------------

    // ContextMenuStrip.Opening is raised by the protected OnOpening; tests have no shell to open a real menu.
    private static void RaiseOpening(ContextMenuStrip menu) =>
        typeof(ToolStripDropDown).GetMethod("OnOpening", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(menu, [new CancelEventArgs()]);

    [Fact]
    public void NativeScreensaver_Opening_PicksUpAnOutsideChangeToOn()
    {
        bool os = false;
        using var menu = TrayMenuFactory.Build(startAtLogin: false, nativeScreensaverActive: false,
            isNativeScreensaverActive: () => os, onNativeScreensaverChanged: _ => true);
        var item = Item(menu, "Native Screensaver");
        Assert.False(item.Checked);

        os = true;   // changed in Windows Settings while the app was running
        RaiseOpening(menu);

        Assert.True(item.Checked);
        Assert.StartsWith("✓", item.Text);
    }

    [Fact]
    public void NativeScreensaver_Opening_PicksUpAnOutsideChangeToOff()
    {
        bool os = true;
        using var menu = TrayMenuFactory.Build(startAtLogin: false, nativeScreensaverActive: true,
            isNativeScreensaverActive: () => os, onNativeScreensaverChanged: _ => true);
        var item = Item(menu, "Native Screensaver");

        os = false;
        RaiseOpening(menu);

        Assert.False(item.Checked);
        Assert.StartsWith("   ", item.Text);
    }

    [Fact]
    public void NativeScreensaver_Opening_SyncDoesNotWriteTheValueBackToTheOs()
    {
        int writes = 0;
        bool os = false;
        using var menu = TrayMenuFactory.Build(startAtLogin: false, nativeScreensaverActive: false,
            isNativeScreensaverActive: () => os, onNativeScreensaverChanged: _ => { writes++; return true; });

        os = true;
        RaiseOpening(menu);

        Assert.Equal(0, writes);
    }

    [Fact]
    public void NativeScreensaver_UserClickAfterASync_StillWritesToTheOs()
    {
        bool? written = null;
        bool os = false;
        using var menu = TrayMenuFactory.Build(startAtLogin: false, nativeScreensaverActive: false,
            isNativeScreensaverActive: () => os, onNativeScreensaverChanged: v => { written = v; return true; });

        os = true;
        RaiseOpening(menu);                                   // sync (no write)
        Item(menu, "Native Screensaver").Checked = false;     // user unticks it

        Assert.False(written);
    }
}
