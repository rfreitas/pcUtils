using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;

namespace PortWatch;

/// <summary>
/// Reads the TCP listener and UDP endpoint tables (IP Helper API) with their
/// owning PIDs, for IPv4 and IPv6. Established connections are skipped:
/// only ports something is actually bound to are interesting.
/// </summary>
internal static class PortScanner
{
    private const int AF_INET = 2, AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;
    private const int UDP_TABLE_OWNER_PID = 1;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTable, ref int size, bool sort, int af, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pTable, ref int size, bool sort, int af, int tableClass, uint reserved);

    private delegate uint TableFn(IntPtr p, ref int size, bool sort, int af, int cls, uint r);

    public static IReadOnlyList<PortEntry> Scan()
    {
        var raw = new List<(string Proto, int Port, int Pid)>();
        foreach (int af in new[] { AF_INET, AF_INET6 })
        {
            // Row layout: v4 = addr,port,pid at 4/8/?; we only need port and pid.
            // TCP listener row: state(4) localAddr(4|16+4 scope) localPort(4) ... pid(4 at end).
            ReadTable(GetExtendedTcpTable, af, TCP_TABLE_OWNER_PID_LISTENER, "TCP", raw);
            ReadTable(GetExtendedUdpTable, af, UDP_TABLE_OWNER_PID, "UDP", raw);
        }

        var info = new Dictionary<int, (string Name, bool IsSystem)>();
        return raw
            .Distinct()
            .Select(r => { var i = InfoOf(r.Pid, info); return new PortEntry(r.Proto, r.Port, r.Pid, i.Name, i.IsSystem); })
            .ToList();
    }

    private static void ReadTable(TableFn fn, int af, int cls, string proto, List<(string, int, int)> into)
    {
        int size = 0;
        fn(IntPtr.Zero, ref size, false, af, cls, 0);
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (fn(buf, ref size, false, af, cls, 0) != 0) return;

            int count = Marshal.ReadInt32(buf);
            bool tcp = proto == "TCP";
            bool v6 = af == AF_INET6;
            // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid          (6 x 4)
            // MIB_UDPROW_OWNER_PID: localAddr, localPort, pid                                          (3 x 4)
            // v6 TCP: localAddr(16) scope(4) localPort(4) remoteAddr(16) scope(4) remotePort(4) state(4) pid(4) = 56
            // v6 UDP: localAddr(16) scope(4) localPort(4) pid(4) = 28
            (int rowSize, int portOff, int pidOff) = (tcp, v6) switch
            {
                (true,  false) => (24, 8,  20),
                (false, false) => (12, 4,  8),
                (true,  true)  => (56, 20, 52),
                (false, true)  => (28, 20, 24),
            };

            IntPtr row = buf + 4;
            for (int i = 0; i < count; i++, row += rowSize)
            {
                int port = IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row, portOff)) & 0xFFFF;
                int pid  = Marshal.ReadInt32(row, pidOff);
                into.Add((proto, port, pid));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // Windows' own services/hosts. Used when the exe path is unreadable (protected processes
    // such as lsass deny MainModule access to non-admin callers).
    private static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "System", "svchost", "lsass", "services", "wininit", "winlogon", "csrss", "smss",
        "spoolsv", "dwm", "fontdrvhost", "sihost", "taskhostw", "SearchIndexer", "MsMpEng", "NisSrv",
    };

    private static readonly string WindowsDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows) + Path.DirectorySeparatorChar;

    /// <summary>True for processes Windows owns: known system names, or an exe under the Windows directory.</summary>
    internal static bool IsSystemProcess(string name, string? exePath) =>
        SystemNames.Contains(name) ||
        (exePath is not null && exePath.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase));

    private static (string Name, bool IsSystem) InfoOf(int pid, Dictionary<int, (string, bool)> cache)
    {
        if (cache.TryGetValue(pid, out var cached)) return cached;
        (string, bool) info;
        if (pid == 0) info = ("Idle", true);
        else if (pid == 4) info = ("System", true);
        else
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { /* protected process */ }
                info = (p.ProcessName, IsSystemProcess(p.ProcessName, path));
            }
            catch { info = ($"PID {pid}", false); }
        }
        return cache[pid] = info;
    }
}
