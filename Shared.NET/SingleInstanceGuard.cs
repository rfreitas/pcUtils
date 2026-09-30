using System;
using System.Diagnostics;
using System.Threading;

namespace Shared;

/// <summary>
/// One running copy of a tray app. The newest launch wins: if another copy holds the mutex it is killed
/// (same exe path only) and the mutex re-acquired, so relaunching after a rebuild just works.
/// Call from Application.OnStartup and Dispose from OnExit (on the same thread: a mutex must be released
/// by the thread that owns it).
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private Mutex? _mutex;

    private SingleInstanceGuard(Mutex mutex) => _mutex = mutex;

    /// <param name="mutexName">e.g. @"Global\MyApp.NET".</param>
    /// <param name="processName">Image name without ".exe", used to find the old copy.</param>
    /// <param name="replaceExisting">True (the repo convention) kills the old copy; false just reports failure.</param>
    /// <returns>The guard, or null if another copy owns the mutex and could not be replaced.</returns>
    public static SingleInstanceGuard? TryAcquire(
        string mutexName, string processName, bool replaceExisting = true, Action<string>? log = null)
    {
        var mutex = new Mutex(initiallyOwned: false, mutexName, out _);
        bool owned = Own(mutex, 0);
        if (!owned && replaceExisting)
        {
            KillOtherCopies(processName, log);
            owned = Own(mutex, 2000);
        }

        if (!owned)
        {
            log?.Invoke("Could not acquire the single-instance mutex. Exiting.");
            mutex.Dispose();
            return null;
        }
        return new SingleInstanceGuard(mutex);
    }

    // Ownership is taken exactly once per successful TryAcquire (mutex ownership is re-entrant per thread,
    // so acquiring twice would leave it held after Dispose).
    private static bool Own(Mutex mutex, int waitMs)
    {
        try
        {
            if (!mutex.WaitOne(waitMs, exitContext: false)) return false;
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died without releasing: we own it now.
        }
        return true;
    }

    private static void KillOtherCopies(string processName, Action<string>? log)
    {
        string? currentPath = Process.GetCurrentProcess().MainModule?.FileName;
        if (currentPath is null) return;

        foreach (var proc in Process.GetProcessesByName(processName))
        {
            if (proc.Id == Environment.ProcessId) continue;
            try
            {
                if (string.Equals(proc.MainModule?.FileName, currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    log?.Invoke($"Killing existing instance PID {proc.Id}.");
                    proc.Kill();
                    proc.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"Could not stop PID {proc.Id}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (_mutex is null) return;
        try { _mutex.ReleaseMutex(); } catch { /* not owned */ }
        _mutex.Dispose();
        _mutex = null;
    }
}
