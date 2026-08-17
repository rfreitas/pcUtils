using System.Management;

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
/// </summary>
internal sealed class ProcessStartWatcher : IDisposable
{
    public event EventHandler<string>? ProcessStarted;

    private readonly ManagementEventWatcher _watcher;

    public ProcessStartWatcher()
    {
        _watcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
        _watcher.EventArrived += OnEventArrived;
    }

    public void Start() => _watcher.Start();

    private void OnEventArrived(object sender, EventArrivedEventArgs e)
    {
        if (e.NewEvent.Properties["ProcessName"]?.Value is string name)
            ProcessStarted?.Invoke(this, name);
    }

    public void Dispose()
    {
        _watcher.Stop();
        _watcher.Dispose();
    }
}
