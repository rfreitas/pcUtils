using System;
using System.Runtime.InteropServices;

namespace AggressiveScreensaver.Input;

/// <summary>
/// Tracks soft idle (any input including simulated) and physical idle
/// (hardware keyboard/mouse only) using Win32 hooks and GetLastInputInfo.
///
/// Physical idle is measured by installing low-level WH_KEYBOARD_LL and
/// WH_MOUSE_LL hooks that record a timestamp on every physical event.
/// This mirrors what AHK does internally for A_TimeIdlePhysical.
///
/// IMPORTANT: Install() must be called from the UI/message-loop thread so
/// Windows can dispatch hook callbacks.
/// </summary>
internal sealed class IdleTimers : IDisposable
{
    // -------------------------------------------------------------------------
    // P/Invoke
    // -------------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetTickCount();

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL    = 14;

    // -------------------------------------------------------------------------
    // Fields
    // -------------------------------------------------------------------------
    private IntPtr _kbdHook  = IntPtr.Zero;
    private IntPtr _mouseHook = IntPtr.Zero;
    private LowLevelProc? _kbdProc;    // keep delegate alive
    private LowLevelProc? _mouseProc;
    private volatile uint _lastPhysicalTick;

    // -------------------------------------------------------------------------
    // Installation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Installs the low-level hooks. Must be called on the message-loop thread.
    /// </summary>
    public void Install()
    {
        _lastPhysicalTick = GetTickCount();

        _kbdProc   = KbdHookCallback;
        _mouseProc = MouseHookCallback;

        // hMod = IntPtr.Zero is valid for low-level hooks when dwThreadId = 0
        _kbdHook   = SetWindowsHookEx(WH_KEYBOARD_LL, _kbdProc,   IntPtr.Zero, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL,    _mouseProc, IntPtr.Zero, 0);
    }

    private IntPtr KbdHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
            _lastPhysicalTick = GetTickCount();
        return CallNextHookEx(_kbdHook, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
            _lastPhysicalTick = GetTickCount();
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Soft idle: milliseconds since any input (physical or simulated).
    /// Equivalent to AHK's A_TimeIdle.
    /// </summary>
    public uint SoftIdleMs
    {
        get
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            GetLastInputInfo(ref info);
            return unchecked(GetTickCount() - info.dwTime);
        }
    }

    /// <summary>
    /// Physical idle: milliseconds since the last hardware keyboard/mouse event.
    /// Equivalent to AHK's A_TimeIdlePhysical.
    /// Returns 0 if hooks are not installed yet.
    /// </summary>
    public uint PhysicalIdleMs => unchecked(GetTickCount() - _lastPhysicalTick);

    public void Dispose()
    {
        if (_kbdHook   != IntPtr.Zero) { UnhookWindowsHookEx(_kbdHook);   _kbdHook   = IntPtr.Zero; }
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
    }
}
