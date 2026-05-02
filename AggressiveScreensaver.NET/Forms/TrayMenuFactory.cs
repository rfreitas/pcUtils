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
        Action?    onBlacklist      = null,
        Action?    onDebug          = null,
        Func<bool, bool>? onStartupChanged = null,
        Action?    onExit           = null)
    {
        var menu = new ContextMenuStrip();

        // Header
        menu.Items.Add(new ToolStripMenuItem("Power Request Monitor") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Blacklist Apps…",    null, (_, _) => onBlacklist?.Invoke());
        menu.Items.Add("Show Details (Debug)", null, (_, _) => onDebug?.Invoke());
        menu.Items.Add(new ToolStripSeparator());

        // Start at Login checkbox
        var startupItem = new ToolStripMenuItem("Start at Login")
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

                bool ok = onStartupChanged(item.Checked);
                if (!ok)
                {
                    reverting = true;
                    item.Checked = !item.Checked;
                    reverting = false;
                }
            };
        }

        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => onExit?.Invoke());

        return menu;
    }
}
