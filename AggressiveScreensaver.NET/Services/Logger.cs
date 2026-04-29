using System;
using System.IO;

namespace AggressiveScreensaver.Services;

/// <summary>
/// Simple file logger. All methods swallow exceptions — logging must never crash the app.
/// </summary>
internal static class Logger
{
    private static readonly string LogPath = Path.Combine(
        AppContext.BaseDirectory,
        "AggressiveScreensaver.log");

    private static readonly object _lock = new();

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
