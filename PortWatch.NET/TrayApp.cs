using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Shared;

namespace PortWatch;

/// <summary>
/// NotifyIcon + click-to-open flyout, the same pattern as AggressiveScreensaver: left click opens the
/// popup above the taskbar and it stays until click-away / Escape (TrayFlyoutWindow); clicking the
/// icon again closes it. Right click shows the menu.
/// </summary>
internal sealed class TrayApp : IDisposable
{
    private const string TaskName        = "PortWatch";
    private static readonly string ExePath = Path.Combine(AppContext.BaseDirectory, "PortWatch.exe");
    private const string TaskDescription = "Launches PortWatch (tray port viewer) at logon.";

    private readonly NotifyIcon _tray;
    private readonly PortPopup _popup = new();

    public TrayApp()
    {
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "PortWatch - click to show listening ports",
            Visible = true,
            ContextMenuStrip = BuildContextMenu(),
        };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OnIconClick(); };
    }

    private static ContextMenuStrip BuildContextMenu()
    {
        var menu = TrayMenu.Create();
        // Runs as a normal user (asInvoker manifest), so the startup task must not request elevation.
        menu.Items.Add(TrayMenu.StartAtLoginItem(TaskName, ExePath, TaskDescription, requireElevation: false, Logger.Log, "PortWatch"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(TrayMenu.ExitItem(() => System.Windows.Application.Current.Shutdown()));
        return menu;
    }

    /// <summary>
    /// Left click: opens the popup above the taskbar, or closes it if it is open. ToggleShow also
    /// swallows the reopen that a click-away on the icon would otherwise cause.
    /// </summary>
    public void OnIconClick() =>
        _popup.ToggleShow(() =>
        {
            _popup.SetRows(PortGrouper.Group(PortScanner.Scan()));
            _popup.ShowNear(Cursor.Position);
            _popup.Activate();   // needed so Deactivated fires on click-away
        });

    public PortPopup Popup => _popup;

    public bool PopupVisible => _popup.IsVisible;

    /// <summary>Shows the popup as if the tray icon had been clicked with the cursor at <paramref name="cursorPx"/> (diagnostics).</summary>
    public void ShowAt(System.Drawing.Point cursorPx)
    {
        _popup.SetRows(PortGrouper.Group(PortScanner.Scan()));
        _popup.ShowNear(cursorPx);
    }

    /// <summary>Posts a genuine left-click (button down + up) to the NotifyIcon's window, exactly as the shell does (diagnostics).</summary>
    public void PostTrayLeftClick()
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var window = (NativeWindow)typeof(NotifyIcon).GetField("_window", flags)!.GetValue(_tray)!;
        var id = Convert.ToInt32(typeof(NotifyIcon).GetField("_id", flags)!.GetValue(_tray)!);
        PostMessage(window.Handle, 0x800 /* WM_USER+1024 tray callback */, (IntPtr)id, (IntPtr)0x201 /* WM_LBUTTONDOWN */);
        PostMessage(window.Handle, 0x800, (IntPtr)id, (IntPtr)0x202 /* WM_LBUTTONUP */);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    public void Dispose()
    {
        _popup.Close();
        _tray.Visible = false;
        _tray.Dispose();
    }
}
