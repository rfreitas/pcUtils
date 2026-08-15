using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using RefreshRateOverlay.WPF.Services;
using RefreshRateOverlay.WPF.Tray;

namespace RefreshRateOverlay.WPF;

/// <summary>
/// Entry point: single-instance guard, global exception logging, then hands
/// off to TrayApp (tray icon + services). Mirrors RefreshRateOverlay.NET's
/// Program.cs.
/// </summary>
public partial class App : System.Windows.Application
{
    private const string MutexName = @"Global\RefreshRateOverlay.WPF.NET";

    private Mutex?   _mutex;
    private TrayApp? _trayApp;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, ex) =>
        {
            Logger.LogException(ex.Exception, severe: true);
            ex.Handled = true;
        };
        Logger.InstallGlobalHandlers();
        Logger.Log("Starting.");

        _mutex = new Mutex(initiallyOwned: false, MutexName, out _);
        bool ownsMutex;
        try
        {
            ownsMutex = _mutex.WaitOne(0, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            ownsMutex = true;
        }

        if (!ownsMutex)
        {
            KillExistingInstance();
            try { ownsMutex = _mutex.WaitOne(2000, exitContext: false); }
            catch (AbandonedMutexException) { ownsMutex = true; }

            if (!ownsMutex)
            {
                Logger.Log("Could not acquire mutex after killing previous instance. Exiting.");
                Shutdown();
                return;
            }
        }

        Logger.Log("Launching TrayApp.");
        _trayApp = new TrayApp();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayApp?.Dispose();
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch { /* not owned */ }
            _mutex.Dispose();
        }
        Logger.Log("Exiting.");
        base.OnExit(e);
    }

    private static void KillExistingInstance()
    {
        string? currentPath = Process.GetCurrentProcess().MainModule?.FileName;
        if (currentPath is null) return;

        foreach (var proc in Process.GetProcessesByName("RefreshRateOverlay.WPF"))
        {
            if (proc.Id == Environment.ProcessId) continue;
            try
            {
                string? otherPath = proc.MainModule?.FileName;
                if (string.Equals(otherPath, currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Log($"Killing existing instance PID {proc.Id}.");
                    proc.Kill();
                    proc.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex);
            }
        }
    }
}
