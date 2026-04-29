using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using AggressiveScreensaver.Services;

namespace AggressiveScreensaver;

internal static class Program
{
    private const string MutexName = @"Global\AggressiveScreensaver.NET";

    [STAThread]
    static void Main()
    {
        // --- Global exception handling ---
        Logger.InstallGlobalHandlers();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Logger.Log($"Starting. Admin: {IsAdmin()}");

        // --- Single-instance guard (#SingleInstance Force equivalent) ---
        // If another instance is running, kill it and take over.
        using var mutex = new Mutex(initiallyOwned: false, MutexName, out _);
        bool ownsMutex = false;
        try
        {
            ownsMutex = mutex.WaitOne(0, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            ownsMutex = true; // previous instance crashed; we own it now
        }

        if (!ownsMutex)
        {
            // Kill the existing instance by name+path
            KillExistingInstance();
            // Retry acquiring the mutex after a brief wait
            try { ownsMutex = mutex.WaitOne(2000, exitContext: false); }
            catch { /* ignore */ }

            if (!ownsMutex)
            {
                Logger.Log("Could not acquire mutex after killing previous instance. Exiting.");
                return;
            }
        }

        // --- DPI awareness ---
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Logger.Log("Launching TrayApp.");

        try
        {
            using var app = new TrayApp();
            Application.Run(app);
        }
        catch (Exception ex)
        {
            Logger.LogException(ex, severe: true);
        }
        finally
        {
            if (ownsMutex)
                mutex.ReleaseMutex();
            Logger.Log("Exiting.");
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

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
}
