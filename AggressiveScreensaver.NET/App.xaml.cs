using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using AggressiveScreensaver.Services;

namespace AggressiveScreensaver;

/// <summary>
/// Entry point: single-instance guard, global exception logging, then hands
/// off to TrayApp (tray icon + services). Mirrors LGTV_brightness.NET's and
/// RefreshRateOverlay.WPF's App.xaml.cs.
/// </summary>
public partial class App : System.Windows.Application
{
    private const string MutexName = @"Global\AggressiveScreensaver.NET";

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
        Logger.Log($"Starting. Admin: {IsAdmin()}");
        EnsureCrashDumpsConfigured();

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

        foreach (var proc in Process.GetProcessesByName("AggressiveScreensaver"))
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

    private static bool IsAdmin() => PowercfgService.IsAdmin();

    /// <summary>
    /// Registers this exe for Windows Error Reporting local crash dumps
    /// (full dumps, kept 10 deep) so a native crash — which never reaches
    /// the app log or a managed handler — leaves a .dmp under the default
    /// %LOCALAPPDATA%\CrashDumps for later analysis. No-op without admin
    /// (writes to HKLM) and idempotent once already configured.
    /// </summary>
    private static void EnsureCrashDumpsConfigured()
    {
        if (!IsAdmin()) return;

        try
        {
            const string keyPath = @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\AggressiveScreensaver.exe";
            using var key = Registry.LocalMachine.CreateSubKey(keyPath);
            if (key is null) return;

            bool changed = false;
            if (key.GetValue("DumpType") is not int dumpType || dumpType != 2)
            {
                key.SetValue("DumpType", 2, RegistryValueKind.DWord);
                changed = true;
            }
            if (key.GetValue("DumpCount") is not int dumpCount || dumpCount != 10)
            {
                key.SetValue("DumpCount", 10, RegistryValueKind.DWord);
                changed = true;
            }

            if (changed)
                Logger.Log("Configured WER local crash dumps (full, 10 kept).");
        }
        catch (Exception ex)
        {
            Logger.LogException(ex);
        }
    }
}
