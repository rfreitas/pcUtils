using System.Management;
using System.Windows.Threading;

namespace RefreshRateOverlay.WPF.Services;

/// <summary>
/// Fires ProcessStarted the instant a new process is created, via
/// Win32_ProcessStartTrace (an ETW-backed WMI trace) — well before that
/// process could ever have a window, let alone foreground focus. Exists for
/// settings like HDR that some games' render pipelines only ever query once,
/// during startup: ForegroundTracker's focus-based trigger fires too late for
/// those, since the pipeline has already queried and cached HDR state by the
/// time the window shows and takes focus.
///
/// Win32_ProcessStartTrace requires the watching process to be elevated —
/// this app already runs as administrator (see app.manifest), so that's free.
///
/// ManagementEventWatcher.EventArrived fires on a ThreadPool thread by
/// default, unlike every other event source in this app (ForegroundTracker's
/// Timer, SystemMessageSink's WndProc, NotifyIcon) which all run on the main
/// UI thread's message pump. Left unmarshaled, this caused a real bug: a
/// game whose process starts around the same moment it takes foreground
/// focus (exactly Content Manager launching Assetto Corsa) could fire
/// OnProcessStarted and OnAppChanged concurrently on two different threads,
/// racing unsynchronized on TrayApp's shared apply/settle state
/// (_currentRate, _settling, _settleCts) and corrupting it — observed as
/// a saved rate profile silently reverting a few seconds after being set
/// correctly. Capturing the UI Dispatcher at construction (this class is
/// always constructed from TrayApp's constructor, itself on that thread) and
/// marshaling through it puts ProcessStarted on the same thread as every
/// other event source, eliminating the race entirely.
/// </summary>
internal sealed class ProcessStartWatcher : IDisposable
{
    public event EventHandler<string>? ProcessStarted;

    private readonly ManagementEventWatcher _watcher;
    private readonly Dispatcher _dispatcher;

    public ProcessStartWatcher()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _watcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
        _watcher.EventArrived += OnEventArrived;
    }

    public void Start() => _watcher.Start();

    private void OnEventArrived(object sender, EventArrivedEventArgs e)
    {
        if (e.NewEvent.Properties["ProcessName"]?.Value is string name)
            RaiseProcessStarted(name);
    }

    /// <summary>The actual fix under test lives here: marshaling onto the
    /// dispatcher captured at construction, regardless of which thread calls
    /// this. Internal (not private) so a test can call it directly from a
    /// background thread to simulate WMI's own delivery thread — a real
    /// EventArrivedEventArgs can't practically be constructed outside a live
    /// WMI callback, but this marshaling behavior doesn't depend on WMI at
    /// all, so it doesn't need one to verify.</summary>
    internal void RaiseProcessStarted(string processName) =>
        _dispatcher.BeginInvoke(() => ProcessStarted?.Invoke(this, processName));

    public void Dispose()
    {
        _watcher.Stop();
        _watcher.Dispose();
    }
}
