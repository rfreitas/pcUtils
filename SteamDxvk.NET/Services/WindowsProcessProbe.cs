using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SteamDxvk;

/// <summary>The real process list. Cheap per process (one limited handle for the path); start times and modules are read lazily.</summary>
internal sealed class WindowsProcessProbe : IProcessProbe
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr handle, int flags, StringBuilder name, ref int size);

    public IReadOnlyList<ProbedProcess> Snapshot()
    {
        var list = new List<ProbedProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                int pid = process.Id;
                if (ImagePath(pid) is not { } path || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new ProbedProcess(pid, path, () => StartUtc(pid), () => ModulesOf(pid)));
            }
        }
        return list;
    }

    private static string? ImagePath(int pid)
    {
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;   // protected or already gone
        try
        {
            var name = new StringBuilder(1024);
            int size = name.Capacity;
            return QueryFullProcessImageNameW(handle, 0, name, ref size) ? name.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }

    private static DateTime StartUtc(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.StartTime.ToUniversalTime(); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException) { return DateTime.UtcNow; }
    }

    private static IReadOnlyList<ModuleInfo> ModulesOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.Modules.Cast<ProcessModule>().Select(m => new ModuleInfo(m.ModuleName, m.FileName)).ToList();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException or NotSupportedException) { return []; }
    }
}
