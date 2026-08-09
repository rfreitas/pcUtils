using System.Runtime.InteropServices;

namespace LgtvBrightness.Services;

/// <summary>
/// Wraps EnumDisplaySettingsW for the primary monitor's current refresh rate,
/// used by AutoBrightnessService to scale backlight with the refresh-rate ratio.
/// </summary>
internal static class DisplayService
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? lpszDeviceName, int iModeNum, IntPtr lpDevMode);

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DEVMODE_SIZE  = 220;
    private const int OFF_SIZE      = 68;
    private const int OFF_FREQUENCY = 184;

    public static int GetCurrentRate()
    {
        var buf = Marshal.AllocHGlobal(DEVMODE_SIZE);
        try
        {
            for (int i = 0; i < DEVMODE_SIZE; i += 4)
                Marshal.WriteInt32(buf, i, 0);
            Marshal.WriteInt16(buf, OFF_SIZE, DEVMODE_SIZE);

            return EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, buf)
                ? Marshal.ReadInt32(buf, OFF_FREQUENCY)
                : 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }
}
