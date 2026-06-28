using System;
using System.Windows.Forms;

namespace AggressiveScreensaver.Forms;

/// <summary>
/// Builds the tray ContextMenuStrip from pure data + callbacks.
/// Extracted so tests can instantiate and render the menu without a running TrayApp.
/// </summary>
internal static class TrayMenuFactory
{
    /// <param name="startAtLogin">Initial checked state of the "Start at Login" item.</param>
    /// <param name="onBlacklist">Callback for "Blacklist Apps…". Pass null for render-only tests.</param>
    /// <param name="onDebug">Callback for "Show Details (Debug)". Pass null for render-only tests.</param>
    /// <param name="onStartupChanged">
    ///   Called when the "Start at Login" checkbox changes.
    ///   Receives the new desired state; should return false to revert the checkbox.
    ///   Pass null for render-only tests.
    /// </param>
    /// <param name="onExit">Callback for "Exit". Pass null for render-only tests.</param>
    public static ContextMenuStrip Build(
        bool       startAtLogin,
        bool       suppressFullscreen      = false,
        Action?    onBlacklist             = null,
        Action?    onDebug                 = null,
        Func<bool, bool>? onStartupChanged         = null,
        Func<bool, bool>? onSuppressFullscreenChanged = null,
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

        menu.Items.Add(space + "Blacklist Apps…",    null, (_, _) => onBlacklist?.Invoke());
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
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(space + "Exit", null, (_, _) => onExit?.Invoke());

        return menu;
    }
}
