using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Shared;

namespace TaskbarReveal;

internal sealed class TrayApp : ApplicationContext, IDisposable
{
    private readonly TaskbarService _taskbar;
    private readonly NotifyIcon     _tray;

    private const string TaskName        = "TaskbarReveal";
    private static readonly string ExePath = Path.Combine(AppContext.BaseDirectory, "TaskbarReveal.exe");
    private const string TaskDescription = "Launches TaskbarReveal at logon with administrator privileges.";

    public TrayApp()
    {
        _taskbar = new TaskbarService();

        _tray = new NotifyIcon
        {
            Icon             = LoadTrayIcon(),
            Text             = "Taskbar Reveal",
            Visible          = true,
            ContextMenuStrip = BuildMenu(),
        };
    }

    private ContextMenuStrip BuildMenu()
    {
        const string space = "   ";
        const string check = "✓ ";

        var menu = new ContextMenuStrip
        {
            Renderer        = new DarkMenuRenderer(),
            ShowCheckMargin = false,
            ShowImageMargin = false,
        };

        menu.Items.Add(new ToolStripMenuItem(" Taskbar Reveal") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        // Auto Hide toggle
        bool autoHide = _taskbar.AutoHide;
        var autoHideItem = new ToolStripMenuItem((autoHide ? check : space) + "Auto Hide Taskbar")
        {
            CheckOnClick = true,
            Checked      = autoHide,
        };
        autoHideItem.CheckedChanged += (sender, _) =>
        {
            if (sender is not ToolStripMenuItem item) return;
            item.Text = (item.Checked ? check : space) + "Auto Hide Taskbar";
            _taskbar.AutoHide = item.Checked;
        };
        menu.Items.Add(autoHideItem);

        menu.Items.Add(space + "Hide Taskbar", null, (_, _) => _taskbar.HideTaskbar());
        menu.Items.Add(new ToolStripSeparator());

        // Start at Login toggle
        bool startAtLogin = StartupTaskService.IsInstalled(TaskName);
        var startupItem = new ToolStripMenuItem((startAtLogin ? check : space) + "Start at Login")
        {
            CheckOnClick = true,
            Checked      = startAtLogin,
        };
        bool reverting = false;
        startupItem.CheckedChanged += (sender, _) =>
        {
            if (reverting || sender is not ToolStripMenuItem item) return;
            item.Text = (item.Checked ? check : space) + "Start at Login";
            bool ok = item.Checked
                ? StartupTaskService.Install(TaskName, ExePath, TaskDescription)
                : StartupTaskService.Uninstall(TaskName);
            if (!ok)
            {
                reverting = true;
                item.Checked = !item.Checked;
                reverting = false;
                MessageBox.Show(
                    "Failed to update the startup task.",
                    "Taskbar Reveal",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        };
        menu.Items.Add(startupItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(space + "Exit", null, (_, _) => Exit());

        return menu;
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("TaskbarReveal.Resources.tray.ico");
            if (stream is not null)
                return new Icon(stream);
        }
        catch { }
        return SystemIcons.Application;
    }

    private void Exit()
    {
        _taskbar.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }

    private bool _disposed;
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            _taskbar.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
