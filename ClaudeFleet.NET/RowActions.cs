using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ClaudeFleet;

/// <summary>Flyout row actions that touch the OS. Each logs and swallows its own failures.</summary>
internal static class RowActions
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);

    /// <summary>
    /// Brings the terminal window that hosts a session to the front: walks up the parent chain to the first process
    /// with a main window. Windows Terminal exposes the window, not the tab, so the right tab is not selected.
    /// </summary>
    public static bool FocusTerminal(int sessionPid, IReadOnlyList<ProcEntry> entries)
    {
        var byPid = entries.GroupBy(e => e.Pid).ToDictionary(g => g.Key, g => g.First());
        int pid = sessionPid;
        for (int guard = 0; guard < 32 && byPid.TryGetValue(pid, out var e); guard++)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                IntPtr h = p.MainWindowHandle;
                if (h != IntPtr.Zero)
                {
                    if (IsIconic(h)) ShowWindow(h, 9 /* SW_RESTORE */);
                    return SetForegroundWindow(h);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
            if (e.ParentPid == pid) break;
            pid = e.ParentPid;
        }
        Logger.Log($"focus: no terminal window found above pid {sessionPid}");
        return false;
    }

    public static void OpenFolder(string cwd)
    {
        try { if (Directory.Exists(cwd)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{cwd}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Log($"open folder failed: {ex.Message}"); }
    }

    /// <summary>Opens Explorer with the session's transcript selected.</summary>
    public static void ShowTranscript(SessionView s)
    {
        try
        {
            string file = Path.Combine(AgentReader.DefaultProjectsDir, AgentReader.Slug(s.Cwd), s.SessionId + ".jsonl");
            if (File.Exists(file)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
            else Logger.Log($"transcript not found: {file}");
        }
        catch (Exception ex) { Logger.Log($"show transcript failed: {ex.Message}"); }
    }

    /// <summary>Kills a process and everything below it (taskkill /T), after a confirmation naming the target.</summary>
    public static void KillTree(int pid, string what, bool isSession)
    {
        string warn = isSession ? "\n\nThis ends the Claude session itself and everything it started." : "";
        var answer = System.Windows.MessageBox.Show(
            $"Kill {what} (PID {pid}) and all of its child processes?{warn}", "ClaudeFleet",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("taskkill.exe", $"/PID {pid} /T /F") { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(5000);
            Logger.Log($"kill tree: {what} pid {pid}");
        }
        catch (Exception ex) { Logger.Log($"kill failed: {ex.Message}"); }
    }
}
