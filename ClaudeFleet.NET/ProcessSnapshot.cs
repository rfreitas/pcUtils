using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeFleet;

/// <summary>One process from a system snapshot. Times are 100 ns units (FILETIME scale).</summary>
internal sealed record ProcEntry(int Pid, int ParentPid, string Name, long CreateTime, long CpuTime, long WorkingSet);

/// <summary>
/// All processes in one call (<c>NtQuerySystemInformation(SystemProcessInformation)</c>, the Task Manager source):
/// about a millisecond, no WMI. Command lines are fetched separately and only for processes we display.
/// </summary>
internal static class ProcessSnapshot
{
    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int ProcessCommandLineInformation = 60;

    // Offsets into SYSTEM_PROCESS_INFORMATION on x64.
    private const int OffNext = 0, OffCreate = 32, OffUser = 40, OffKernel = 48, OffNameLen = 56, OffNamePtr = 64,
                      OffPid = 80, OffParent = 88, OffWorkingSet = 144;

    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int needed);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr buf, int len, out int needed);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    private const uint QueryLimited = 0x1000;

    public static IReadOnlyList<ProcEntry> Capture()
    {
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("offsets assume x64");
        int size = 1 << 20;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(SystemProcessInformation, buf, size, out int needed);
                if (status == StatusInfoLengthMismatch) { size = Math.Max(size * 2, needed + (64 << 10)); continue; }
                if (status < 0) throw new InvalidOperationException($"NtQuerySystemInformation failed: 0x{status:X8}");
                return Parse(buf);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        throw new InvalidOperationException("process list kept growing");
    }

    private static List<ProcEntry> Parse(IntPtr buf)
    {
        var list = new List<ProcEntry>(512);
        long offset = 0;
        while (true)
        {
            IntPtr p = buf + (int)offset;
            int next = Marshal.ReadInt32(p, OffNext);
            int nameBytes = Marshal.ReadInt16(p, OffNameLen);
            IntPtr namePtr = Marshal.ReadIntPtr(p, OffNamePtr);
            string name = nameBytes > 0 && namePtr != IntPtr.Zero ? Marshal.PtrToStringUni(namePtr, nameBytes / 2) : "System Idle Process";
            list.Add(new ProcEntry(
                (int)Marshal.ReadIntPtr(p, OffPid).ToInt64(), (int)Marshal.ReadIntPtr(p, OffParent).ToInt64(), name,
                Marshal.ReadInt64(p, OffCreate), Marshal.ReadInt64(p, OffUser) + Marshal.ReadInt64(p, OffKernel),
                Marshal.ReadIntPtr(p, OffWorkingSet).ToInt64()));
            if (next == 0) break;
            offset += next;
        }
        return list;
    }

    /// <summary>The process's full command line, or null when it cannot be read (exited, protected).</summary>
    public static string? CommandLine(int pid)
    {
        IntPtr h = OpenProcess(QueryLimited, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            int size = 1024;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    int status = NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, size, out int needed);
                    if (status == StatusInfoLengthMismatch || status == unchecked((int)0xC0000023) /* buffer too small */) { size = Math.Max(needed, size * 2); continue; }
                    if (status < 0) return null;
                    // UNICODE_STRING { USHORT Length; USHORT Max; PWSTR Buffer } followed by the characters.
                    int bytes = Marshal.ReadInt16(buf, 0);
                    IntPtr chars = Marshal.ReadIntPtr(buf, 8);
                    return bytes == 0 ? "" : Marshal.PtrToStringUni(chars, bytes / 2);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Creation time (FILETIME) of a live process, or null when it does not exist. Used to validate registry files.</summary>
    public static long? CreateTime(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.StartTime.ToFileTime();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }
}
