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
    private readonly Settings _settings = Settings.Load(Settings.DefaultPath);
    private readonly System.Windows.Threading.DispatcherTimer _trafficTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private TrafficCollector? _traffic;
    private IReadOnlyList<PortEntry> _entries = [];

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

        // Live traffic only runs while the flyout is open: start tracing on open, stop on close.
        _trafficTimer.Tick += (_, _) => RefreshTraffic();
        _popup.IsVisibleChanged += (_, e) => { if (!(bool)e.NewValue) StopTraffic(); };
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = TrayMenu.Create();
        menu.Items.Add(TrayMenu.Header("Traffic arrows on"));
        foreach (var item in TrayMenu.RadioGroup(["Ports", "Processes"], (int)_settings.Arrows, i =>
        {
            _settings.Arrows = (ArrowTarget)i;   // takes effect the next time the flyout opens
            _settings.Save();
        }))
            menu.Items.Add(item);
        menu.Items.Add(new ToolStripSeparator());
        // The manifest is requireAdministrator (kernel network tracing), so the logon task must match it.
        menu.Items.Add(TrayMenu.StartAtLoginItem(TaskName, ExePath, TaskDescription, requireElevation: true, Logger.Log, "PortWatch"));
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
            _entries = PortScanner.Scan();
            _popup.Target = _settings.Arrows;
            _popup.SetRows(PortGrouper.Group(_entries));
            StartTraffic();
            _popup.ShowNear(Cursor.Position);
            _popup.Activate();   // needed so Deactivated fires on click-away
        });

    private void StartTraffic()
    {
        StopTraffic();
        _traffic = TrafficCollector.TryStart(Logger.Log, out string? error);
        _popup.SetTrafficStatus(error);
        if (_traffic is not null) _trafficTimer.Start();
    }

    private void StopTraffic()
    {
        _trafficTimer.Stop();
        _traffic?.Dispose();
        _traffic = null;
    }

    private void RefreshTraffic()
    {
        if (_traffic is null) return;
        _popup.UpdateTraffic(_traffic.Tracker.Snapshot(_entries));
    }

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
        StopTraffic();
        _popup.Close();
        _tray.Visible = false;
        _tray.Dispose();
    }
}
