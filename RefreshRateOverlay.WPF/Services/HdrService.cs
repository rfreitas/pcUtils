using System;
using System.Runtime.InteropServices;

namespace RefreshRateOverlay.WPF.Services;

/// <summary>
/// Reads and sets HDR state on the primary monitor.
/// Supports both Win11 24H2+ API (type 15/16) and legacy (type 9/10).
/// </summary>
internal static class HdrService
{
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPath, out uint numMode);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint numPath, IntPtr pathArray, ref uint numMode, IntPtr modeArray, IntPtr currentTopologyId);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(IntPtr requestPacket);
    [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(IntPtr requestPacket);
    [DllImport("user32.dll")] private static extern int SetDisplayConfig(uint numPathArrayElements, IntPtr pathArray, uint numModeInfoArrayElements, IntPtr modeInfoArray, uint flags);

    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const int  PATH_SIZE             = 72;
    private const int  MODE_SIZE             = 64;
    private const uint FLAGS_ACTIVE          = 1;
    private const uint SDC_APPLY             = 0x00000080;

    // DISPLAYCONFIG_DEVICE_INFO type constants
    private const uint GET_ADVANCED_COLOR_INFO   = 9;   // legacy
    private const uint SET_ADVANCED_COLOR_STATE  = 10;  // legacy
    private const uint GET_ADVANCED_COLOR_INFO_2 = 15;  // Win11 24H2+
    private const uint SET_HDR_STATE             = 16;  // Win11 24H2+

    /// <summary>Returns (supported, enabled).</summary>
    public static (bool Supported, bool Enabled) GetState()
    {
        if (!QueryPaths(out int numPaths, out IntPtr paths, out IntPtr modes))
            return (false, false);

        try
        {
            for (int i = 0; i < numPaths; i++)
            {
                IntPtr pathBase = paths + i * PATH_SIZE;
                uint flags = (uint)Marshal.ReadInt32(pathBase, 64);
                if ((flags & FLAGS_ACTIVE) == 0) continue;

                uint adLow  = (uint)Marshal.ReadInt32(pathBase, 20);
                int  adHigh = Marshal.ReadInt32(pathBase, 24);
                uint target = (uint)Marshal.ReadInt32(pathBase, 28);

                // Try type 15 (Win11 24H2+): struct size = 36
                var info15 = AllocHeader(GET_ADVANCED_COLOR_INFO_2, 36, adLow, adHigh, target);
                try
                {
                    if (DisplayConfigGetDeviceInfo(info15) == 0)
                    {
                        uint val = (uint)Marshal.ReadInt32(info15, 20);
                        bool supp = (val & 16) != 0; // bit 4 = highDynamicRangeSupported
                        if (supp)
                        {
                            uint mode = (uint)Marshal.ReadInt32(info15, 32);
                            return (true, mode == 2); // 2 = HDR
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(info15); }

                // Fallback type 9: struct size = 32
                var info9 = AllocHeader(GET_ADVANCED_COLOR_INFO, 32, adLow, adHigh, target);
                try
                {
                    if (DisplayConfigGetDeviceInfo(info9) == 0)
                    {
                        uint val = (uint)Marshal.ReadInt32(info9, 20);
                        bool supp = (val & 1) != 0;
                        if (supp)
                            return (true, (val & 2) != 0);
                    }
                }
                finally { Marshal.FreeHGlobal(info9); }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(paths);
            Marshal.FreeHGlobal(modes);
        }
        return (false, false);
    }

    /// <summary>Enables or disables HDR on the primary monitor.</summary>
    public static void SetState(bool enable)
    {
        if (!QueryPaths(out int numPaths, out IntPtr paths, out IntPtr modes))
            return;

        try
        {
            for (int i = 0; i < numPaths; i++)
            {
                IntPtr pathBase = paths + i * PATH_SIZE;
                uint flags = (uint)Marshal.ReadInt32(pathBase, 64);
                if ((flags & FLAGS_ACTIVE) == 0) continue;

                uint adLow  = (uint)Marshal.ReadInt32(pathBase, 20);
                int  adHigh = Marshal.ReadInt32(pathBase, 24);
                uint target = (uint)Marshal.ReadInt32(pathBase, 28);

                // Try type 16 (Win11 24H2+): struct size = 24
                var set16 = AllocHeader(SET_HDR_STATE, 24, adLow, adHigh, target);
                try
                {
                    Marshal.WriteInt32(set16, 20, enable ? 1 : 0);
                    if (DisplayConfigSetDeviceInfo(set16) == 0)
                    {
                        SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY);
                        return;
                    }
                }
                finally { Marshal.FreeHGlobal(set16); }

                // Fallback type 10: struct size = 24
                var set10 = AllocHeader(SET_ADVANCED_COLOR_STATE, 24, adLow, adHigh, target);
                try
                {
                    Marshal.WriteInt32(set10, 20, enable ? 1 : 0);
                    DisplayConfigSetDeviceInfo(set10);
                    SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY);
                    return;
                }
                finally { Marshal.FreeHGlobal(set10); }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(paths);
            Marshal.FreeHGlobal(modes);
        }
    }

    // -------------------------------------------------------------------------

    private static bool QueryPaths(out int numPaths, out IntPtr paths, out IntPtr modes)
    {
        numPaths = 0; paths = IntPtr.Zero; modes = IntPtr.Zero;

        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0)
            return false;

        paths = Marshal.AllocHGlobal((int)(np * PATH_SIZE));
        modes = Marshal.AllocHGlobal((int)(nm * MODE_SIZE));

        uint np2 = np, nm2 = nm;
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np2, paths, ref nm2, modes, IntPtr.Zero) != 0)
        {
            Marshal.FreeHGlobal(paths); Marshal.FreeHGlobal(modes);
            paths = IntPtr.Zero; modes = IntPtr.Zero;
            return false;
        }
        numPaths = (int)np2;
        return true;
    }

    private static IntPtr AllocHeader(uint type, int size, uint adLow, int adHigh, uint targetId)
    {
        var buf = Marshal.AllocHGlobal(size);
        for (int i = 0; i < size; i += 4) Marshal.WriteInt32(buf, i, 0);
        Marshal.WriteInt32(buf, 0,  (int)type);
        Marshal.WriteInt32(buf, 4,  size);
        Marshal.WriteInt32(buf, 8,  (int)adLow);
        Marshal.WriteInt32(buf, 12, adHigh);
        Marshal.WriteInt32(buf, 16, (int)targetId);
        return buf;
    }
}
