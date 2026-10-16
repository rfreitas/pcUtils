using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Shared;

namespace ClaudeFleet;

/// <summary>
/// NotifyIcon + click-to-open flyout (UX guide §1). The icon is a live CPU meter for all Claude trees.
///
/// Cost model (plan): when every session is idle and Claude uses under 1 % CPU nothing runs but a
/// FileSystemWatcher on the session registry and one kernel exit-wait per session. A 5 s sample timer runs only
/// while a session is busy or Claude is still using CPU; a 2 s timer with full detail runs only while the flyout is open.
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
    /// <summary>CPU needs two samples; the first flyout refresh comes quickly so the meters fill in.</summary>
    private static readonly TimeSpan FirstFlyoutSample = TimeSpan.FromMilliseconds(700);

    private readonly NotifyIcon _tray;
    private readonly FleetPopup _popup = new();
    private readonly FleetCollector? _collector;
    private readonly MockFleet? _mock;
    private readonly SynchronizationContext _ui = SynchronizationContext.Current ?? new SynchronizationContext();
    private readonly System.Windows.Threading.DispatcherTimer _trayTimer = new() { Interval = TraySampleInterval };
    private readonly System.Windows.Threading.DispatcherTimer _flyoutTimer = new() { Interval = FlyoutSampleInterval };
    private readonly System.Windows.Threading.DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly Dictionary<int, Process> _exitWatch = new();
    private readonly Dictionary<string, (string Name, bool Busy)> _previous = new();
    private FileSystemWatcher? _watcher;
    private bool _showMcp;
    private bool _notify = true;
    private bool _primed;
    private readonly SamplingGovernor _governor = new();
    private int _iconLevel = -1;
    private string _signature = "";
    private Icon? _icon;
    private FleetSnapshot _last = new([], false);

    public TrayApp(bool mock = false)
    {
        if (mock) _mock = new MockFleet(); else _collector = new FleetCollector();

        _tray = new NotifyIcon { Visible = true, ContextMenuStrip = BuildContextMenu() };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OnIconClick(); };
        _popup.OnAction = OnRowAction;

        _trayTimer.Tick += (_, _) => { var s = Collect(detail: false); Apply(s); AdjustTrayTimer(s); };
        _flyoutTimer.Tick += (_, _) => RefreshFlyout();
        _debounce.Tick += (_, _) => { _debounce.Stop(); Reevaluate(); };
        _popup.IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) return;
            _flyoutTimer.Stop();
            Reevaluate();   // back to background mode: restarts the tray timer only if Claude is still working
        };

        if (_mock is null) StartWatching();
        Reevaluate();
        if (_mock is not null) _trayTimer.Start();   // the mock is always busy
    }

    // ------------------------------------------------------------------ watching

    private void StartWatching()
    {
        string dir = SessionRegistry.DefaultDir;
        try
        {
            Directory.CreateDirectory(dir);
            _watcher = new FileSystemWatcher(dir, "*.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler h = (_, _) => _ui.Post(_ => { _debounce.Stop(); _debounce.Start(); }, null);
            _watcher.Changed += h; _watcher.Created += h; _watcher.Deleted += h;
            _watcher.Renamed += (_, _) => _ui.Post(_ => { _debounce.Stop(); _debounce.Start(); }, null);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Logger.Log($"watcher failed: {ex.Message}"); }
    }

    /// <summary>One kernel wait per session: a crashed session leaves its file behind, so file events alone would miss it.</summary>
    private void SyncExitWatchers(FleetSnapshot snap)
    {
        var live = snap.All().Select(s => s.Pid).ToHashSet();
        foreach (var pid in _exitWatch.Keys.Where(p => !live.Contains(p)).ToList())
        {
            _exitWatch[pid].Dispose();
            _exitWatch.Remove(pid);
        }
        foreach (var pid in live.Where(p => !_exitWatch.ContainsKey(p)))
        {
            try
            {
                var p = Process.GetProcessById(pid);
                p.EnableRaisingEvents = true;
                p.Exited += (_, _) => _ui.Post(_ => { _debounce.Stop(); _debounce.Start(); }, null);
                _exitWatch[pid] = p;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    // ------------------------------------------------------------------ sampling

    private FleetSnapshot Collect(bool detail)
    {
        try
        {
            if (_mock is not null) { _mock.Tick(); return _last = _mock.Snapshot(); }
            return _last = _collector!.Collect(detail);
        }
        catch (Exception ex)
        {
            Logger.Log($"collect failed: {ex}");
            return _last;
        }
    }

    /// <summary>Session or process event: refresh the tray from a light sample and start/stop the tray timer as needed.</summary>
    private void Reevaluate()
    {
        if (_popup.IsVisible) return;   // the flyout timer is already sampling
        var snap = Collect(detail: false);
        Apply(snap);
        SyncExitWatchers(snap);
        AdjustTrayTimer(snap);
    }

    private void AdjustTrayTimer(FleetSnapshot snap)
    {
        if (_mock is not null || _popup.IsVisible) return;
        if (_governor.Observe(snap)) { if (!_trayTimer.IsEnabled) _trayTimer.Start(); }
        else _trayTimer.Stop();   // idle and quiet: nothing runs until the watcher or an exit wait fires
    }

    private void Apply(FleetSnapshot snap)
    {
        _tray.Text = FleetFormat.Tooltip(snap);
        int level = FleetFormat.MeterLevel(snap.TotalCpu);
        if (level != _iconLevel)   // redraw only when the step changes
        {
            _iconLevel = level;
            var old = _icon;
            _icon = TrayIconRenderer.CreateIcon(level, SystemInformation.SmallIconSize.Width);
            _tray.Icon = _icon;
            old?.Dispose();
        }
        NotifyFinished(snap);
    }

    /// <summary>Balloon when a session that was busy goes idle or disappears. The first sample only primes the state.</summary>
    private void NotifyFinished(FleetSnapshot snap)
    {
        var now = snap.All().ToDictionary(s => s.SessionId, s => (s.Name, Busy: s.Status == SessionStatus.Busy));
        if (_primed && _notify && _mock is null)
        {
            var done = _previous.Where(p => p.Value.Busy && (!now.TryGetValue(p.Key, out var n) || !n.Busy)).Select(p => p.Value.Name).ToList();
            if (done.Count > 0)
                _tray.ShowBalloonTip(3000, "Claude finished", string.Join(", ", done), ToolTipIcon.None);
        }
        _previous.Clear();
        foreach (var kv in now) _previous[kv.Key] = kv.Value;
        _primed = true;
    }

    // ------------------------------------------------------------------ flyout

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
        var snap = Collect(detail: true);
        _signature = snap.Signature();
        _popup.ShowMcp = _showMcp;
        _popup.SetFleet(snap);
        Apply(snap);
        _flyoutTimer.Interval = FirstFlyoutSample;
        _flyoutTimer.Start();
    }

    private void RefreshFlyout()
    {
        _flyoutTimer.Interval = FlyoutSampleInterval;
        var snap = Collect(detail: true);
        string sig = snap.Signature();
        if (sig != _signature) { _signature = sig; _popup.SetFleet(snap); }   // rows appeared or went: rebuild once
        else _popup.UpdateCpu(snap);                                          // same rows: recolour meters only
        Apply(snap);
        SyncExitWatchers(snap);
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = TrayMenu.Create();
        if (_mock is not null) menu.Items.Add(TrayMenu.Header("ClaudeFleet (mock data)"));
        menu.Items.Add(TrayMenu.CheckItem("Notify when a session finishes", _notify, on => { _notify = on; return true; }));
        menu.Items.Add(TrayMenu.CheckItem("Show MCP servers", _showMcp, on => { _showMcp = on; return true; }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(TrayMenu.StartAtLoginItem(TaskName, ExePath, TaskDescription, requireElevation: false, Logger.Log, "ClaudeFleet"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(TrayMenu.ExitItem(() => System.Windows.Application.Current.Shutdown()));
        return menu;
    }

    private void OnRowAction(string action, object target)
    {
        try
        {
            switch (action, target)
            {
                case ("focus", SessionView s):      RowActions.FocusTerminal(s.Pid, ProcessSnapshot.Capture()); break;
                case ("resume", SessionView s):     System.Windows.Clipboard.SetText($"claude --resume {s.SessionId}"); break;
                case ("folder", SessionView s):     RowActions.OpenFolder(s.Cwd); break;
                case ("transcript", SessionView s): RowActions.ShowTranscript(s); break;
                case ("kill", SessionView s):       RowActions.KillTree(s.Pid, $"session {s.Name}", isSession: true); break;
                case ("copycmd", ProcView p):       System.Windows.Clipboard.SetText(p.Label); break;
                case ("kill", ProcView p):          RowActions.KillTree(p.Pid, p.Label, isSession: false); break;
            }
        }
        catch (Exception ex) { Logger.Log($"action {action} failed: {ex}"); }
    }

    // ------------------------------------------------------------------ diagnostics

    public FleetPopup Popup => _popup;

    public bool PopupVisible => _popup.IsVisible;

    /// <summary>Whether the 5 s background timer is running (tests and diagnostics).</summary>
    public bool TrayTimerRunning => _trayTimer.IsEnabled;

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
        _debounce.Stop();
        _watcher?.Dispose();
        foreach (var p in _exitWatch.Values) p.Dispose();
        _popup.Close();
        _tray.Visible = false;
        _tray.Dispose();
        _icon?.Dispose();
    }
}
