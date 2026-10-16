using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Shared;

namespace ClaudeFleet;

/// <summary>
/// NotifyIcon + click-to-open flyout (UX guide §1). The icon is a live CPU meter for all Claude trees.
/// Mock stage: data comes from <see cref="MockFleet"/>; the real collectors replace it in later plan steps.
/// </summary>
internal sealed class TrayApp : IDisposable
{
    private const string TaskName        = "ClaudeFleet";
    private static readonly string ExePath = Path.Combine(AppContext.BaseDirectory, "ClaudeFleet.exe");
    private const string TaskDescription = "Launches ClaudeFleet (tray view of Claude Code sessions) at logon.";

    /// <summary>Background sampling while Claude is working; drives the tray meter only.</summary>
    public static readonly TimeSpan TraySampleInterval = TimeSpan.FromSeconds(5);
    /// <summary>Sampling while the flyout is open.</summary>
    public static readonly TimeSpan FlyoutSampleInterval = TimeSpan.FromSeconds(2);

    private readonly NotifyIcon _tray;
    private readonly FleetPopup _popup = new();
    private readonly MockFleet _mock = new();
    private readonly System.Windows.Threading.DispatcherTimer _trayTimer = new() { Interval = TraySampleInterval };
    private readonly System.Windows.Threading.DispatcherTimer _flyoutTimer = new() { Interval = FlyoutSampleInterval };
    private bool _showMcp;
    private bool _notify = true;
    private int _iconLevel = -1;
    private Icon? _icon;

    public TrayApp()
    {
        _tray = new NotifyIcon { Visible = true, ContextMenuStrip = BuildContextMenu() };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OnIconClick(); };
        _popup.OnAction = OnRowAction;

        _trayTimer.Tick += (_, _) => SampleForTray();
        _flyoutTimer.Tick += (_, _) => SampleForFlyout();
        _popup.IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) return;
            _flyoutTimer.Stop();
            _trayTimer.Start();
        };

        SampleForTray();
        _trayTimer.Start();   // mock sessions are always busy; real data starts/stops this from the session watcher
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = TrayMenu.Create();
        menu.Items.Add(TrayMenu.Header("ClaudeFleet (mock data)"));
        menu.Items.Add(TrayMenu.CheckItem("Notify when a session finishes", _notify, on => { _notify = on; return true; }));
        menu.Items.Add(TrayMenu.CheckItem("Show MCP servers", _showMcp, on => { _showMcp = on; return true; }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(TrayMenu.StartAtLoginItem(TaskName, ExePath, TaskDescription, requireElevation: false, Logger.Log, "ClaudeFleet"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(TrayMenu.ExitItem(() => System.Windows.Application.Current.Shutdown()));
        return menu;
    }

    public void OnIconClick() =>
        _popup.ToggleShow(() =>
        {
            Open();
            _popup.ShowNear(Cursor.Position);
            _popup.Activate();   // needed so Deactivated fires on click-away
        });

    private void Open()
    {
        _trayTimer.Stop();
        _popup.ShowMcp = _showMcp;
        _popup.SetFleet(_mock.Snapshot());
        _flyoutTimer.Start();
    }

    private void SampleForTray()
    {
        _mock.Tick();
        ApplyToTray(_mock.Snapshot());
    }

    private void SampleForFlyout()
    {
        _mock.Tick();
        var snap = _mock.Snapshot();
        _popup.UpdateCpu(snap);
        ApplyToTray(snap);
    }

    private void ApplyToTray(FleetSnapshot snap)
    {
        _tray.Text = FleetFormat.Tooltip(snap);
        int level = FleetFormat.MeterLevel(snap.TotalCpu);
        if (level == _iconLevel) return;   // redraw only when the step changes
        _iconLevel = level;
        var old = _icon;
        _icon = TrayIconRenderer.CreateIcon(level, SystemInformation.SmallIconSize.Width);
        _tray.Icon = _icon;
        old?.Dispose();
    }

    private void OnRowAction(string action, object target)
    {
        switch (action, target)
        {
            case ("resume", SessionView s):
                System.Windows.Clipboard.SetText($"claude --resume {s.SessionId}");
                break;
            case ("copycmd", ProcView p):
                System.Windows.Clipboard.SetText(p.Label);
                break;
        }
        string what = target switch { SessionView s => s.Name, ProcView p => $"{p.Label} ({p.Pid})", _ => "?" };
        Logger.Log($"mock action: {action} on {what}");
        _tray.ShowBalloonTip(2000, "ClaudeFleet (mock)", $"{action}: {what}", ToolTipIcon.None);
    }

    public FleetPopup Popup => _popup;

    public bool PopupVisible => _popup.IsVisible;

    /// <summary>Shows the popup as if the tray icon had been clicked with the cursor at <paramref name="cursorPx"/> (diagnostics).</summary>
    public void ShowAt(System.Drawing.Point cursorPx)
    {
        Open();
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
        _trayTimer.Stop();
        _flyoutTimer.Stop();
        _popup.Close();
        _tray.Visible = false;
        _tray.Dispose();
        _icon?.Dispose();
    }
}
