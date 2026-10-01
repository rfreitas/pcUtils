using System.Runtime.InteropServices;

namespace AggressiveScreensaver.Services;

/// <summary>
/// Reads/writes the OS-level screensaver toggle via SystemParametersInfo, so
/// AggressiveScreensaver's own blanking doesn't race the stock Windows
/// screensaver (which has its own independent timeout).
/// </summary>
internal static class NativeScreensaverService
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SystemParametersInfo")]
    private static extern bool SystemParametersInfoUInt(uint uiAction, uint uiParam, ref uint pvParam, uint fWinIni);

    private const uint SPI_GETSCREENSAVETIMEOUT = 0x000E;
    private const uint SPI_GETSCREENSAVEACTIVE = 0x0010;
    private const uint SPI_SETSCREENSAVEACTIVE = 0x0011;
    private const uint SPIF_UPDATEINIFILE      = 0x01;
    private const uint SPIF_SENDCHANGE         = 0x02;

    public static bool IsActive()
    {
        bool active = false;
        SystemParametersInfo(SPI_GETSCREENSAVEACTIVE, 0, ref active, 0);
        return active;
    }

    /// <summary>The Windows screensaver idle timeout in seconds, or 0 if it cannot be read.</summary>
    public static int GetTimeoutSeconds()
    {
        uint seconds = 0;
        return SystemParametersInfoUInt(SPI_GETSCREENSAVETIMEOUT, 0, ref seconds, 0) ? (int)seconds : 0;
    }

    public static bool SetActive(bool active)
    {
        bool value = active;
        return SystemParametersInfo(SPI_SETSCREENSAVEACTIVE, 0, ref value, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
    }
}
