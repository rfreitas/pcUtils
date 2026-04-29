using System;
using System.IO;

namespace AggressiveScreensaver.Services;

/// <summary>
/// Simple file logger. All methods swallow exceptions — logging must never crash the app.
/// </summary>
internal static class Logger
{
    private static readonly string DataDir = EnsureDataDir();

    private static readonly string LogPath = Path.Combine(DataDir, "AggressiveScreensaver.log");

    private static readonly object _lock = new();

    private static string EnsureDataDir()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AggressiveScreensaver");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Log(string message)
    {
        try
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}: {message}";
            lock (_lock)
            {
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch { /* never crash the app due to logging */ }
    }

    public static void LogException(Exception ex, bool severe = false)
    {
        string prefix = severe ? "CRITICAL ERROR" : "HANDLED ERROR";
        Log($"{prefix}: {ex.Message} ({ex.GetType().Name})" +
            (ex.StackTrace is not null ? $"\n    Stack: {ex.StackTrace}" : ""));
    }

    /// <summary>
    /// Installs global unhandled-exception hooks that route to this logger.
    /// Call once from Program.Main before Application.Run.
    /// </summary>
    /// <remarks>
    /// These handlers catch managed exceptions only. Native Access Violations
    /// (0xc0000005) bypass them entirely in .NET 8 — the runtime terminates
    /// immediately without invoking any managed code. To diagnose those, use
    /// Windows Event Viewer (Application log, provider "Application Error").
    /// The real fix is to prevent AVs at the source (e.g. guarding GDI+
    /// GraphicsPath calls against zero-size rectangles).
    /// </remarks>
    public static void InstallGlobalHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            string msg = e.ExceptionObject is Exception ex
                ? $"UNHANDLED CLR EXCEPTION: {ex.Message}\n    Stack: {ex.StackTrace}"
                : $"UNHANDLED EXCEPTION OBJECT: {e.ExceptionObject}";
            Log(msg);
        };

        System.Windows.Forms.Application.ThreadException += (_, e) =>
        {
            LogException(e.Exception, severe: true);
        };
    }
}
