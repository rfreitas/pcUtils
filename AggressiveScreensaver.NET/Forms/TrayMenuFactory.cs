using System;
using System.Windows.Forms;
using Shared;

namespace AggressiveScreensaver.Forms;

/// <summary>
/// Builds the tray ContextMenuStrip from pure data + callbacks.
/// Extracted so tests can instantiate and render the menu without a running TrayApp.
/// </summary>
internal static class TrayMenuFactory
{
    /// <param name="startAtLogin">Initial checked state of the "Start at Login" item.</param>
    /// <param name="onIgnoreList">Callback for "Ignore List…". Pass null for render-only tests.</param>
    /// <param name="onDebug">Callback for "Show Details (Debug)". Pass null for render-only tests.</param>
    /// <param name="onStartupChanged">
    ///   Called when the "Start at Login" checkbox changes.
    ///   Receives the new desired state; should return false to revert the checkbox.
    ///   Pass null for render-only tests.
    /// </param>
    /// <param name="onExit">Callback for "Exit". Pass null for render-only tests.</param>
    public static ContextMenuStrip Build(
        bool       startAtLogin,
        bool       suppressFullscreen       = false,
        bool       ignoreUnfocusedBlockers  = true,
        bool       ignoreNonvisibleBlockers = false,
        bool       nativeScreensaverActive  = false,
        Func<bool>? isNativeScreensaverActive = null,
        Action?    onIgnoreList             = null,
        Action?    onDebug                  = null,
        Func<bool, bool>? onStartupChanged         = null,
        Func<bool, bool>? onSuppressFullscreenChanged = null,
        Func<bool, bool>? onIgnoreUnfocusedBlockersChanged = null,
        Func<bool, bool>? onIgnoreNonvisibleBlockersChanged = null,
        Func<bool, bool>? onNativeScreensaverChanged = null,
        Action?    onExit                  = null)
    {
        var menu = new ContextMenuStrip
        {
            Renderer        = new DarkMenuRenderer(),
            // Turn off all margins to avoid WinForms High-DPI layout bugs entirely.
            // We'll use text characters ("✓ " vs "   ") for perfect alignment.
            ShowCheckMargin = false,
            ShowImageMargin = false,
        };

        const string space = "   ";
        const string check = "✓ ";

        // Header
        menu.Items.Add(new ToolStripMenuItem(" Power Request Monitor") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(space + "Ignore List…",    null, (_, _) => onIgnoreList?.Invoke());
        menu.Items.Add(space + "Show Details (Debug)", null, (_, _) => onDebug?.Invoke());
        menu.Items.Add(new ToolStripSeparator());

        // Start at Login checkbox
        var startupItem = new ToolStripMenuItem((startAtLogin ? check : space) + "Start at Login")
        {
            CheckOnClick = true,
            Checked      = startAtLogin,
        };

        if (onStartupChanged is not null)
        {
            bool reverting = false;
            startupItem.CheckedChanged += (sender, _) =>
            {
                if (reverting || sender is not ToolStripMenuItem item) return;

                item.Text = (item.Checked ? check : space) + "Start at Login";

                bool ok = onStartupChanged(item.Checked);
                if (!ok)
                {
                    reverting = true;
                    item.Checked = !item.Checked;
                    item.Text = (item.Checked ? check : space) + "Start at Login";
                    reverting = false;
                }
            };
        }
        else
        {
            // For render-only tests
            startupItem.CheckedChanged += (sender, _) =>
            {
                if (sender is ToolStripMenuItem item)
                    item.Text = (item.Checked ? check : space) + "Start at Login";
            };
        }

        menu.Items.Add(startupItem);

        // Suppress in Fullscreen checkbox
        var fullscreenItem = new ToolStripMenuItem((suppressFullscreen ? check : space) + "Suppress in Fullscreen")
        {
            CheckOnClick = true,
            Checked      = suppressFullscreen,
        };

        if (onSuppressFullscreenChanged is not null)
        {
            bool reverting = false;
            fullscreenItem.CheckedChanged += (sender, _) =>
            {
                if (reverting || sender is not ToolStripMenuItem item) return;

                item.Text = (item.Checked ? check : space) + "Suppress in Fullscreen";

                bool ok = onSuppressFullscreenChanged(item.Checked);
                if (!ok)
                {
                    reverting = true;
                    item.Checked = !item.Checked;
                    item.Text = (item.Checked ? check : space) + "Suppress in Fullscreen";
                    reverting = false;
                }
            };
        }
        else
        {
            fullscreenItem.CheckedChanged += (sender, _) =>
            {
                if (sender is ToolStripMenuItem item)
                    item.Text = (item.Checked ? check : space) + "Suppress in Fullscreen";
            };
        }

        menu.Items.Add(fullscreenItem);

        // Ignore Nonvisible Blockers checkbox — declared first so the sibling
        // "Ignore Unfocused Blockers" toggle below can gray it out; it has no
        // effect unless that one is also on (see IsScreenBlocked in TrayApp).
        var nonvisibleBlockersItem = new ToolStripMenuItem((ignoreNonvisibleBlockers ? check : space) + "Ignore Nonvisible Blockers")
        {
            CheckOnClick = true,
            Checked      = ignoreNonvisibleBlockers,
            Enabled      = ignoreUnfocusedBlockers,
        };

        // Ignore Unfocused Blockers checkbox
        var unfocusedBlockersItem = new ToolStripMenuItem((ignoreUnfocusedBlockers ? check : space) + "Ignore Unfocused Blockers")
        {
            CheckOnClick = true,
            Checked      = ignoreUnfocusedBlockers,
        };

        if (onIgnoreUnfocusedBlockersChanged is not null)
        {
            bool reverting = false;
            unfocusedBlockersItem.CheckedChanged += (sender, _) =>
            {
                if (reverting || sender is not ToolStripMenuItem item) return;

                item.Text = (item.Checked ? check : space) + "Ignore Unfocused Blockers";
                nonvisibleBlockersItem.Enabled = item.Checked;

                bool ok = onIgnoreUnfocusedBlockersChanged(item.Checked);
                if (!ok)
                {
                    reverting = true;
                    item.Checked = !item.Checked;
                    item.Text = (item.Checked ? check : space) + "Ignore Unfocused Blockers";
                    nonvisibleBlockersItem.Enabled = item.Checked;
                    reverting = false;
                }
            };
        }
        else
        {
            unfocusedBlockersItem.CheckedChanged += (sender, _) =>
            {
                if (sender is ToolStripMenuItem item)
                {
                    item.Text = (item.Checked ? check : space) + "Ignore Unfocused Blockers";
                    nonvisibleBlockersItem.Enabled = item.Checked;
                }
            };
        }

        menu.Items.Add(unfocusedBlockersItem);

        if (onIgnoreNonvisibleBlockersChanged is not null)
        {
            bool reverting = false;
            nonvisibleBlockersItem.CheckedChanged += (sender, _) =>
            {
                if (reverting || sender is not ToolStripMenuItem item) return;

                item.Text = (item.Checked ? check : space) + "Ignore Nonvisible Blockers";

                bool ok = onIgnoreNonvisibleBlockersChanged(item.Checked);
                if (!ok)
                {
                    reverting = true;
                    item.Checked = !item.Checked;
                    item.Text = (item.Checked ? check : space) + "Ignore Nonvisible Blockers";
                    reverting = false;
                }
            };
        }
        else
        {
            nonvisibleBlockersItem.CheckedChanged += (sender, _) =>
            {
                if (sender is ToolStripMenuItem item)
                    item.Text = (item.Checked ? check : space) + "Ignore Nonvisible Blockers";
            };
        }

        menu.Items.Add(nonvisibleBlockersItem);
        menu.Items.Add(new ToolStripSeparator());

        // Native Windows Screensaver checkbox — mirrors the OS flag, which competes with this
        // app's own blanking (see NativeScreensaverService). The flag can change outside this app
        // (Windows Settings), so isNativeScreensaverActive re-reads it each time the menu opens.
        var nativeScreensaverItem = new ToolStripMenuItem((nativeScreensaverActive ? check : space) + "Native Screensaver")
        {
            CheckOnClick = true,
            Checked      = nativeScreensaverActive,
        };

        bool refreshing = false;   // true while syncing the item to the OS: must not write the value back
        if (onNativeScreensaverChanged is not null)
        {
            bool reverting = false;
            nativeScreensaverItem.CheckedChanged += (sender, _) =>
            {
                if (reverting || refreshing || sender is not ToolStripMenuItem item) return;

                item.Text = (item.Checked ? check : space) + "Native Screensaver";

                bool ok = onNativeScreensaverChanged(item.Checked);
                if (!ok)
                {
                    reverting = true;
                    item.Checked = !item.Checked;
                    item.Text = (item.Checked ? check : space) + "Native Screensaver";
                    reverting = false;
                }
            };
        }
        else
        {
            nativeScreensaverItem.CheckedChanged += (sender, _) =>
            {
                if (sender is ToolStripMenuItem item)
                    item.Text = (item.Checked ? check : space) + "Native Screensaver";
            };
        }

        menu.Items.Add(nativeScreensaverItem);

        if (isNativeScreensaverActive is not null)
            menu.Opening += (_, _) =>
            {
                bool current = isNativeScreensaverActive();
                if (current == nativeScreensaverItem.Checked) return;

                refreshing = true;
                nativeScreensaverItem.Checked = current;
                nativeScreensaverItem.Text    = (current ? check : space) + "Native Screensaver";
                refreshing = false;
            };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(space + "Exit", null, (_, _) => onExit?.Invoke());

        return menu;
    }
}
