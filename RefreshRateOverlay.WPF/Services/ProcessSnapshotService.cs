using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace RefreshRateOverlay.WPF.Services;

/// <summary>
/// Read-only process sampler for the game optimizer's dry run. Opens processes
/// with PROCESS_QUERY_LIMITED_INFORMATION only, so it can never change one.
/// CPU is measured as the TotalProcessorTime delta between two calls, in cores
/// (1.0 = one logical processor fully busy); the first call for a pid has no
/// delta and reports null. Thread-safe: TrayApp calls it from the thread pool.
/// </summary>
internal sealed class ProcessSnapshotService
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern uint GetPriorityClass(IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(IntPtr hProcess, out long creation, out long exit, out long kernel, out long user);

    private const uint TH32CS_SNAPPROCESS = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    /// <summary>pid → parent pid for every process, in one system call.</summary>
    private static Dictionary<int, int> ReadParentPids()
    {
        var map = new Dictionary<int, int>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref e)) return map;
            do { map[(int)e.th32ProcessID] = (int)e.th32ParentProcessID; }
            while (Process32NextW(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return map;
    }

    /// <summary>Highest thread base priority of a process. Process.GetProcesses
    /// already read every thread in its one system-information call, so this is
    /// a walk over data in memory, not another query.</summary>
    private static int? MaxThreadPriority(Process p)
    {
        try
        {
            int max = -1;
            foreach (ProcessThread t in p.Threads)
                using (t) max = Math.Max(max, t.BasePriority);
            return max >= 0 ? max : null;
        }
        catch { return null; }
    }

    private readonly object _lock = new();
    private Dictionary<(int Pid, long Created), long> _lastCpu = new();
    // Highest thread priority ever seen per process instance. MMCSS raises a
    // thread only while its task is active, so one sample can miss it; once seen,
    // it counts for the life of the process.
    private Dictionary<(int Pid, long Created), int> _maxThreadPriority = new();
    private long _lastSampleTicks;

    public List<ProcessSnapshot> Take()
    {
        lock (_lock)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            double elapsedSec = _lastSampleTicks == 0 ? 0 : (nowTicks - _lastSampleTicks) / (double)Stopwatch.Frequency;
            var nextCpu = new Dictionary<(int, long), long>();
            var result = new List<ProcessSnapshot>();
            var created = new Dictionary<int, long>();
            var parents = ReadParentPids();
            var nextMaxPri = new Dictionary<(int, long), int>();

            foreach (Process p in Process.GetProcesses())
            {
                using (p)
                {
                    string name = p.ProcessName;
                    int pid = p.Id;
                    int session = SafeSession(p);
                    string? path = null;
                    ProcessPriorityClass? priority = null;
                    double? cores = null;
                    int? maxPri = MaxThreadPriority(p);

                    IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (h != IntPtr.Zero)
                    {
                        try
                        {
                            path = QueryPath(h);
                            uint pc = GetPriorityClass(h);
                            if (pc != 0) priority = (ProcessPriorityClass)(int)pc;

                            if (GetProcessTimes(h, out long createdAt, out _, out long kernel, out long user))
                            {
                                created[pid] = createdAt;
                                long cpu = kernel + user; // 100 ns units
                                var key = (pid, createdAt);
                                nextCpu[key] = cpu;
                                if (elapsedSec > 0 && _lastCpu.TryGetValue(key, out long prev))
                                    cores = Math.Max(0, (cpu - prev) / 1e7 / elapsedSec);
                                if (_maxThreadPriority.TryGetValue(key, out int seen))
                                    maxPri = Math.Max(seen, maxPri ?? seen);
                                if (maxPri is { } mp) nextMaxPri[key] = mp;
                            }
                        }
                        finally { CloseHandle(h); }
                    }

                    result.Add(new ProcessSnapshot(name, pid, path, session, priority, cores) { MaxThreadPriority = maxPri });
                }
            }

            _lastCpu = nextCpu;
            _maxThreadPriority = nextMaxPri;
            _lastSampleTicks = nowTicks;

            // A parent pid can be stale: the parent exited and its pid went to a
            // newer process. A real parent was created before its child.
            for (int i = 0; i < result.Count; i++)
            {
                var s = result[i];
                if (!parents.TryGetValue(s.Pid, out int ppid) || ppid == s.Pid) continue;
                bool stale = created.TryGetValue(s.Pid, out long childAt)
                             && created.TryGetValue(ppid, out long parentAt)
                             && parentAt > childAt;
                if (!stale) result[i] = s with { ParentPid = ppid };
            }
            return result;
        }
    }

    private static int SafeSession(Process p)
    {
        try { return p.SessionId; } catch { return 0; }
    }

    private static string? QueryPath(IntPtr h)
    {
        var sb = new StringBuilder(1024);
        uint size = (uint)sb.Capacity;
        return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
    }

    /// <summary>The active OpenXR runtime manifest path from the registry, or null.</summary>
    public static string? ReadActiveOpenXrRuntime()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Khronos\OpenXR\1");
            return key?.GetValue("ActiveRuntime") as string;
        }
        catch { return null; }
    }

    /// <summary>
    /// Folders of every VR runtime and VR tool registered on this PC, active or
    /// not: each OpenXR runtime manifest's folder (active + AvailableRuntimes),
    /// each OpenXR API layer's folder (overlays and toolkits such as OpenXR
    /// Toolkit or OpenKneeboard, machine and user, enabled or not), and the
    /// OpenVR runtime and driver folders from openvrpaths.vrpath. Registrations
    /// rather than names, so a runtime or tool installed later is covered
    /// without a list.
    /// </summary>
    public static List<string> ReadVrRuntimeFolders()
    {
        var folders = new List<string>();
        void AddManifest(string? manifest)
        {
            if (!string.IsNullOrWhiteSpace(manifest) && System.IO.Path.GetDirectoryName(manifest.Trim()) is { Length: > 0 } dir)
                folders.Add(dir);
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Khronos\OpenXR\1");
            AddManifest(key?.GetValue("ActiveRuntime") as string);
            using var available = key?.OpenSubKey("AvailableRuntimes");
            if (available is not null)
                foreach (string manifest in available.GetValueNames()) AddManifest(manifest);
        }
        catch { }

        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            foreach (string kind in new[] { "Implicit", "Explicit" })
            {
                try
                {
                    using var layers = hive.OpenSubKey($@"SOFTWARE\Khronos\OpenXR\1\ApiLayers\{kind}");
                    if (layers is not null)
                        foreach (string manifest in layers.GetValueNames()) AddManifest(manifest);
                }
                catch { }
            }

        try
        {
            string vrpath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openvr", "openvrpaths.vrpath");
            if (System.IO.File.Exists(vrpath))
                folders.AddRange(ParseOpenVrPaths(System.IO.File.ReadAllText(vrpath)));
        }
        catch { }

        return folders;
    }

    /// <summary>"runtime" and "external_drivers" folders from openvrpaths.vrpath.</summary>
    internal static List<string> ParseOpenVrPaths(string json)
    {
        var result = new List<string>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (string prop in new[] { "runtime", "external_drivers" })
                if (doc.RootElement.TryGetProperty(prop, out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var item in arr.EnumerateArray())
                        if (item.ValueKind == System.Text.Json.JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                            result.Add(s);
        }
        catch (System.Text.Json.JsonException) { }
        return result;
    }
}
