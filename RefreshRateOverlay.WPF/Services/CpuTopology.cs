using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace RefreshRateOverlay.WPF.Services;

/// <summary>
/// P-core and E-core CPU set IDs, from GetSystemCpuSetInformation. The highest
/// EfficiencyClass is the performance class. On a non-hybrid CPU every set has
/// the same class and <see cref="IsHybrid"/> is false: the optimizer then
/// leaves CPU sets alone and only changes priorities.
/// </summary>
internal sealed record CpuTopology(IReadOnlyList<uint> PerformanceSetIds, IReadOnlyList<uint> EfficiencySetIds)
{
    public bool IsHybrid => PerformanceSetIds.Count > 0 && EfficiencySetIds.Count > 0;

    public static readonly CpuTopology Unknown = new(Array.Empty<uint>(), Array.Empty<uint>());

    // SYSTEM_CPU_SET_INFORMATION: Size(4) Type(4) Id(4) Group(2) LogicalProcessorIndex(1)
    // CoreIndex(1) LastLevelCacheIndex(1) NumaNodeIndex(1) EfficiencyClass(1) AllFlags(1) ...
    private const int OffsetSize = 0, OffsetType = 4, OffsetId = 8, OffsetEfficiencyClass = 18;
    private const uint CpuSetInformation = 0;

    /// <summary>Pure: parses the variable-length records GetSystemCpuSetInformation returns.</summary>
    public static CpuTopology Parse(byte[] buffer, int length)
    {
        var sets = new List<(uint Id, byte Class)>();
        int offset = 0;
        while (offset + OffsetEfficiencyClass < length)
        {
            uint size = BitConverter.ToUInt32(buffer, offset + OffsetSize);
            if (size == 0) break;
            if (BitConverter.ToUInt32(buffer, offset + OffsetType) == CpuSetInformation)
                sets.Add((BitConverter.ToUInt32(buffer, offset + OffsetId), buffer[offset + OffsetEfficiencyClass]));
            offset += (int)size;
        }
        if (sets.Count == 0) return Unknown;

        byte top = sets.Max(s => s.Class);
        return new CpuTopology(
            sets.Where(s => s.Class == top).Select(s => s.Id).ToList(),
            sets.Where(s => s.Class != top).Select(s => s.Id).ToList());
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(byte[]? information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);

    public static CpuTopology Read()
    {
        try
        {
            GetSystemCpuSetInformation(null, 0, out uint needed, IntPtr.Zero, 0);
            if (needed == 0) return Unknown;
            var buffer = new byte[needed];
            return GetSystemCpuSetInformation(buffer, needed, out uint got, IntPtr.Zero, 0)
                ? Parse(buffer, (int)got)
                : Unknown;
        }
        catch { return Unknown; }
    }
}
