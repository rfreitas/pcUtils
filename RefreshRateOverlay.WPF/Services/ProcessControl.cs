using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RefreshRateOverlay.WPF.Services;

/// <summary>Current values of the attributes the optimizer may change.
/// IoPriority: 0 very low, 1 low, 2 normal (null if unreadable).</summary>
internal sealed record ProcessState(long CreationTime, ProcessPriorityClass Priority, uint[] CpuSets, int? IoPriority);

/// <summary>
/// The only place the game optimizer writes to other processes. Every setter
/// takes the creation time the caller expects and re-checks it on the opened
/// handle, so a pid that was reused by a newer process is never touched.
/// Behind an interface so the enforcer's decisions are testable with fakes.
/// </summary>
internal interface IProcessControl
{
    ProcessState? Read(int pid);
    bool SetPriority(int pid, long creationTime, ProcessPriorityClass priority);
    /// <summary>Empty array clears the process default CPU sets.</summary>
    bool SetCpuSets(int pid, long creationTime, uint[] cpuSetIds);
    bool SetIoPriority(int pid, long creationTime, int ioPriority);
}

internal sealed class Win32ProcessControl : IProcessControl
{
    private const uint PROCESS_SET_INFORMATION = 0x0200;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_SET_LIMITED_INFORMATION = 0x2000;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ProcessIoPriority = 33;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    private static extern uint GetPriorityClass(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetPriorityClass(IntPtr h, uint priorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessDefaultCpuSets(IntPtr h, uint[]? ids, uint count, out uint required);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessDefaultCpuSets(IntPtr h, uint[]? ids, uint count);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr h, int infoClass, out int value, int length, out int returned);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationProcess(IntPtr h, int infoClass, ref int value, int length);

    public ProcessState? Read(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION, false, pid);
        if (h == IntPtr.Zero) h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            if (!GetProcessTimes(h, out long created, out _, out _, out _)) return null;
            uint pc = GetPriorityClass(h);
            if (pc == 0) return null;

            uint[] sets = Array.Empty<uint>();
            if (!GetProcessDefaultCpuSets(h, null, 0, out uint required) && required > 0)
            {
                var buf = new uint[required];
                if (GetProcessDefaultCpuSets(h, buf, required, out _)) sets = buf;
            }

            int? io = NtQueryInformationProcess(h, ProcessIoPriority, out int v, 4, out _) == 0 ? v : null;
            return new ProcessState(created, (ProcessPriorityClass)(int)pc, sets, io);
        }
        finally { CloseHandle(h); }
    }

    public bool SetPriority(int pid, long creationTime, ProcessPriorityClass priority) =>
        WithVerifiedHandle(pid, creationTime, PROCESS_SET_INFORMATION, h => SetPriorityClass(h, (uint)priority));

    public bool SetCpuSets(int pid, long creationTime, uint[] cpuSetIds) =>
        WithVerifiedHandle(pid, creationTime, PROCESS_SET_LIMITED_INFORMATION,
            h => SetProcessDefaultCpuSets(h, cpuSetIds.Length == 0 ? null : cpuSetIds, (uint)cpuSetIds.Length));

    public bool SetIoPriority(int pid, long creationTime, int ioPriority) =>
        WithVerifiedHandle(pid, creationTime, PROCESS_SET_INFORMATION,
            h => { int v = ioPriority; return NtSetInformationProcess(h, ProcessIoPriority, ref v, 4) == 0; });

    private static bool WithVerifiedHandle(int pid, long creationTime, uint access, Func<IntPtr, bool> action)
    {
        IntPtr h = OpenProcess(access | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            if (!GetProcessTimes(h, out long created, out _, out _, out _) || created != creationTime) return false;
            return action(h);
        }
        finally { CloseHandle(h); }
    }
}
