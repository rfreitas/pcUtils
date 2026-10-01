using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace Shared;

/// <summary>
/// The tray right-click menu every app in this repo uses: dark, no WinForms margins (they break at
/// high DPI), and checkbox items drawn as a "✓ " / "   " text prefix so labels stay aligned.
///
/// Checkbox callbacks return <c>bool</c>: return false to REVERT the checkbox (e.g. the change could not
/// be persisted). See TRAY_APP_UX.md.
/// </summary>
internal static class TrayMenu
{
    public const string Check = "✓ ";
    public const string Space = "   ";

    /// <summary>An empty dark menu with the repo's standard renderer and margins off.</summary>
    public static ContextMenuStrip Create() => new()
    {
        Renderer        = new DarkMenuRenderer(),
        ShowCheckMargin = false,
        ShowImageMargin = false,
    };

    /// <summary>Disabled, dimmed title row shown at the top of a menu.</summary>
    public static ToolStripMenuItem Header(string text) => new(" " + text) { Enabled = false };

    /// <summary>A plain action row, aligned with checkbox rows.</summary>
    public static ToolStripMenuItem Action(string label, Action onClick) =>
        new(Space + label, null, (_, _) => onClick());

    /// <summary>
    /// A checkbox row. <paramref name="onChanged"/> receives the new state and returns false to revert it.
    /// Pass null for render-only menus (tests, screenshots).
    /// </summary>
    public static ToolStripMenuItem CheckItem(string label, bool isChecked, Func<bool, bool>? onChanged = null)
    {
        var item = new ToolStripMenuItem(Prefix(isChecked) + label)
        {
            CheckOnClick = true,
            Checked      = isChecked,
        };

        bool reverting = false;
        item.CheckedChanged += (sender, _) =>
        {
            if (reverting || sender is not ToolStripMenuItem self) return;

            self.Text = Prefix(self.Checked) + label;
            if (onChanged is null || onChanged(self.Checked)) return;

            reverting    = true;
            self.Checked = !self.Checked;
            self.Text    = Prefix(self.Checked) + label;
            reverting    = false;
        };
        return item;
    }

    /// <summary>
    /// A set of mutually exclusive rows drawn like checkboxes (✓ on the selected one). Clicking a row selects it and
    /// calls <paramref name="onSelect"/> with its index; clicking the selected row again does nothing.
    /// </summary>
    public static IReadOnlyList<ToolStripMenuItem> RadioGroup(IReadOnlyList<string> labels, int selected, Action<int> onSelect)
    {
        var items = new List<ToolStripMenuItem>();
        for (int i = 0; i < labels.Count; i++)
        {
            int index = i;
            var item = new ToolStripMenuItem(Prefix(i == selected) + labels[i]) { Checked = i == selected };
            item.Click += (_, _) =>
            {
                if (items[index].Checked) return;

                for (int j = 0; j < items.Count; j++)
                {
                    items[j].Checked = j == index;
                    items[j].Text    = Prefix(j == index) + labels[j];
                }
                onSelect(index);
            };
            items.Add(item);
        }
        return items;
    }

    /// <summary>
    /// "Start at Login" backed by a Task Scheduler logon task (see <see cref="StartupTaskService"/>).
    /// Set <paramref name="requireElevation"/> to match the app manifest: true only for requireAdministrator
    /// apps; a normal asInvoker app must pass false or registering the task fails with "Access is denied".
    /// </summary>
    public static ToolStripMenuItem StartAtLoginItem(
        string taskName, string exePath, string description, bool requireElevation,
        Action<string>? log, string appTitle) =>
        CheckItem("Start at Login", StartupTaskService.IsInstalled(taskName), wantEnabled =>
        {
            bool ok = wantEnabled
                ? StartupTaskService.Install(taskName, exePath, description, log, requireElevation)
                : StartupTaskService.Uninstall(taskName, log);

            if (!ok)
                MessageBox.Show(
                    "Failed to update the startup task.\nCheck the log file for details.",
                    appTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return ok;
        });

    /// <summary>The standard last row. <paramref name="onExit"/> normally calls Application.Current.Shutdown().</summary>
    public static ToolStripMenuItem ExitItem(Action onExit) => Action("Exit", onExit);

    private static string Prefix(bool isChecked) => isChecked ? Check : Space;
}
