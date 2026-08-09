using System;
using System.IO;

namespace LgtvBrightness.Services;

internal static class Logger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LGTV_brightness", "LGTV_brightness.log");

    private static readonly object _lock = new();

    static Logger()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!); } catch { }
    }

    public static void Log(string message)
    {
        try
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}: {message}";
            lock (_lock)
                File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch { }
    }

    public static void LogException(Exception ex, bool severe = false)
    {
        string prefix = severe ? "CRITICAL ERROR" : "HANDLED ERROR";
        Log($"{prefix}: {ex.Message} ({ex.GetType().Name})" +
            (ex.StackTrace is not null ? $"\n    Stack: {ex.StackTrace}" : ""));
    }

    // AppDomain handler only: WPF's DispatcherUnhandledException is wired in
    // App.xaml.cs since it needs the running Application instance.
    public static void InstallGlobalHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            string msg = e.ExceptionObject is Exception ex
                ? $"UNHANDLED CLR EXCEPTION: {ex.Message}\n    Stack: {ex.StackTrace}"
                : $"UNHANDLED EXCEPTION OBJECT: {e.ExceptionObject}";
            Log(msg);
        };
    }
}
