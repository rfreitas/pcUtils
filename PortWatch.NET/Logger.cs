using System.IO;

namespace PortWatch;

/// <summary>Appends to %LOCALAPPDATA%\PortWatch\PortWatch.log (never the exe folder).</summary>
internal static class Logger
{
    private static readonly string Dir  = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortWatch");
    private static readonly string File_ = Path.Combine(Dir, "PortWatch.log");
    private static readonly object Gate = new();

    public static string LogPath => File_;

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(File_, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
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
