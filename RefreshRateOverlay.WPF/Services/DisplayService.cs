using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RefreshRateOverlay.WPF.Services;

/// <summary>
/// Wraps EnumDisplaySettingsW and ChangeDisplaySettingsW for the primary monitor.
/// </summary>
internal static class DisplayService
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? lpszDeviceName, int iModeNum, IntPtr lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsW(IntPtr lpDevMode, uint dwFlags);

    private const int  ENUM_CURRENT_SETTINGS = -1;
    private const int  DISP_CHANGE_SUCCESSFUL = 0;
    private const int  DEVMODE_SIZE = 220;
    private const uint CDS_UPDATEREGISTRY = 0x00000001;

    // DEVMODE field offsets (verified against Windows SDK)
    private const int OFF_SIZE      = 68;
    private const int OFF_BITS      = 168;
    private const int OFF_WIDTH     = 172;
    private const int OFF_HEIGHT    = 176;
    private const int OFF_FREQUENCY = 184;

    /// <summary>
    /// Returns available refresh rates for the current resolution, sorted descending.
    /// </summary>
    public static List<int> GetAvailableRates()
    {
        var buf = AllocDevMode();
        try
        {
            uint targetBits = 0, targetW = 0, targetH = 0;
            if (EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, buf))
            {
                targetBits = ReadU32(buf, OFF_BITS);
                targetW    = ReadU32(buf, OFF_WIDTH);
                targetH    = ReadU32(buf, OFF_HEIGHT);
            }

            var rateSet = new HashSet<int>();
            int i = 0;
            while (EnumDisplaySettingsW(null, i++, buf))
            {
                if (ReadU32(buf, OFF_BITS) != targetBits) continue;
                if (ReadU32(buf, OFF_WIDTH) != targetW) continue;
                if (ReadU32(buf, OFF_HEIGHT) != targetH) continue;

                int rate = (int)ReadU32(buf, OFF_FREQUENCY);
                if (rate > 0) rateSet.Add(rate);
            }

            var list = new List<int>(rateSet);
            if (list.Count == 0)
                return [240, 144, 120, 60]; // fallback

            list.Sort((a, b) => b.CompareTo(a)); // descending
            return list;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>
    /// Returns the current refresh rate of the primary monitor.
    /// </summary>
    public static int GetCurrentRate()
    {
        var buf = AllocDevMode();
        try
        {
            return EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, buf)
                ? (int)ReadU32(buf, OFF_FREQUENCY)
                : 60;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>
    /// Applies the given refresh rate to the primary monitor. Returns true on success.
    ///
    /// Must pass CDS_UPDATEREGISTRY — without it, ChangeDisplaySettingsW only
    /// changes the live/dynamic mode for the current session, never Windows'
    /// own persisted "current settings" for the display (what Settings >
    /// Display shows, what survives logoff/reboot). Confirmed as the actual
    /// cause of a real production bug: with the rate left un-persisted, some
    /// transitions (virtual-desktop switches, a fullscreen-exclusive app
    /// losing/regaining exclusive mode) made the display transiently fall
    /// back toward that still-60Hz persisted value before our own reconcile
    /// loop caught it and pushed the rate back — each of those was a second,
    /// avoidable mode-set on top of the first, both visible as a black
    /// flash. Manually setting Windows' own display settings to match this
    /// app's target rate eliminated the transitions entirely, which is what
    /// pinned this down: the two were fighting over two different sources of
    /// truth for "the" refresh rate, not actually disagreeing on the value.
    /// </summary>
    public static bool SetRate(int rate)
    {
        var buf = AllocDevMode();
        try
        {
            if (!EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, buf))
                return false;

            WriteU32(buf, OFF_FREQUENCY, (uint)rate);
            return ChangeDisplaySettingsW(buf, CDS_UPDATEREGISTRY) == DISP_CHANGE_SUCCESSFUL;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    // -------------------------------------------------------------------------

    private static IntPtr AllocDevMode()
    {
        var buf = Marshal.AllocHGlobal(DEVMODE_SIZE);
        for (int i = 0; i < DEVMODE_SIZE; i += 4)
            Marshal.WriteInt32(buf, i, 0);
        Marshal.WriteInt16(buf, OFF_SIZE, DEVMODE_SIZE);
        return buf;
    }

    private static uint ReadU32(IntPtr buf, int offset) =>
        (uint)Marshal.ReadInt32(buf, offset);

    private static void WriteU32(IntPtr buf, int offset, uint value) =>
        Marshal.WriteInt32(buf, offset, (int)value);
}
