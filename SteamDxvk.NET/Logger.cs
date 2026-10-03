using System.IO;

namespace SteamDxvk;

/// <summary>Appends to %LOCALAPPDATA%\SteamDxvk\SteamDxvk.log (never the exe folder).</summary>
internal static class Logger
{
    private static string _dir = AppPaths.Dir;
    private static readonly object Gate = new();

    public static string LogPath => Path.Combine(_dir, "SteamDxvk.log");

    /// <summary>Tests redirect the log so they never write into the real one.</summary>
    public static void Redirect(string dir) => _dir = dir;

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(_dir);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never throw */ }
    }

    public static void InstallGlobalHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log($"UNHANDLED (terminating={e.IsTerminating}): {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => Log($"UNOBSERVED TASK: {e.Exception}");
    }
}
