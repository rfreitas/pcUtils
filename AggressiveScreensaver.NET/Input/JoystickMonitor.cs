using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AggressiveScreensaver.Services;

namespace AggressiveScreensaver.Input;

/// <summary>
/// Polls up to 16 generic DirectInput-style joysticks via winmm.dll joyGetPosEx.
/// Ported from HasJoystickActivity() in index.ahk.
///
/// Axis values from joyGetPosEx are 0–65535; we rescale to 0–100 to match
/// AHK's JoyX/Y/Z/R/U/V range, then apply a change threshold of > 2 (same as AHK).
/// </summary>
internal sealed class JoystickMonitor
{
    // -------------------------------------------------------------------------
    // P/Invoke
    // -------------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    private struct JOYCAPS
    {
        public ushort wMid, wPid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint wXmin, wXmax, wYmin, wYmax, wZmin, wZmax;
        public uint wNumButtons, wPeriodMin, wPeriodMax;
        public uint wRmin, wRmax, wUmin, wUmax, wVmin, wVmax;
        public uint wMaxAxes, wNumAxes, wMaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szRegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szOEMVxD;
        public uint wCaps;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOYINFOEX
    {
        public uint dwSize;
        public uint dwFlags;
        public uint dwXpos, dwYpos, dwZpos;
        public uint dwRpos, dwUpos, dwVpos;
        public uint dwButtons;
        public uint dwButtonNumber;
        public uint dwPOV;
        public uint dwReserved1, dwReserved2;
    }

    [DllImport("winmm.dll")] private static extern uint joyGetDevCaps(uint uJoyID, ref JOYCAPS pjc, uint cbjc);
    [DllImport("winmm.dll")] private static extern uint joyGetPosEx(uint uJoyID, ref JOYINFOEX pji);

    private const uint JOYERR_NOERROR = 0;
    private const uint JOY_RETURNALL  = 0xFF;   // return all fields
    private const int  MaxJoysticks   = 16;
    private const int  AxisChangeThreshold = 2; // 0-100 scale, same as AHK
    private const long ConnectedListTtlMs  = 5000;

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------
    private struct JoyState
    {
        public uint dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos;
        public uint dwButtons;
        public uint dwPOV;
    }

    private readonly List<uint>       _connected = [];
    private readonly Dictionary<uint, JoyState> _lastState  = [];
    private long _lastDetectionMs;

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns true if any joystick has meaningful activity since the last call.
    /// </summary>
    // TODO: investigate crash on virtual controller app disconnect — device removal
    // between the connected-list cache refresh and the joyGetPosEx/joyGetDevCaps
    // calls may throw or corrupt state. Add defensive handling / re-enumerate on error.
    public bool HasActivity()
    {
        long now = Environment.TickCount64;

        // Refresh connected list every 5 s
        if (now - _lastDetectionMs > ConnectedListTtlMs)
        {
            _lastDetectionMs = now;
            _connected.Clear();
            var caps = new JOYCAPS();
            uint capsSize = (uint)Marshal.SizeOf<JOYCAPS>();
            for (uint id = 0; id < MaxJoysticks; id++)
            {
                if (joyGetDevCaps(id, ref caps, capsSize) == JOYERR_NOERROR)
                    _connected.Add(id);
            }
        }

        if (_connected.Count == 0) return false;

        bool anyActivity = false;
        List<uint>? needsReset = null;

        foreach (uint id in _connected)
        {
            // Defensive re-check: disconnect between cache refreshes can cause
            // bad reads; if joyGetDevCaps fails here the joystick was just removed.
            var caps = new JOYCAPS();
            if (joyGetDevCaps(id, ref caps, (uint)Marshal.SizeOf<JOYCAPS>()) != JOYERR_NOERROR)
            {
                (needsReset ??= []).Add(id);
                continue;
            }

            var info = new JOYINFOEX { dwSize = (uint)Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNALL };
            if (joyGetPosEx(id, ref info) != JOYERR_NOERROR) continue;

            // Rescale axes from 0-65535 to 0-100 to match AHK's JoyX/Y/Z scale
            var current = new JoyState
            {
                dwXpos    = Scale(info.dwXpos),
                dwYpos    = Scale(info.dwYpos),
                dwZpos    = Scale(info.dwZpos),
                dwRpos    = Scale(info.dwRpos),
                dwUpos    = Scale(info.dwUpos),
                dwVpos    = Scale(info.dwVpos),
                dwButtons = info.dwButtons,
                dwPOV     = info.dwPOV,
            };

            if (!_lastState.TryGetValue(id, out var last))
            {
                _lastState[id] = current;
                continue;
            }

            bool changed =
                AxisChanged(current.dwXpos, last.dwXpos) ||
                AxisChanged(current.dwYpos, last.dwYpos) ||
                AxisChanged(current.dwZpos, last.dwZpos) ||
                AxisChanged(current.dwRpos, last.dwRpos) ||
                AxisChanged(current.dwUpos, last.dwUpos) ||
                AxisChanged(current.dwVpos, last.dwVpos) ||
                current.dwButtons != last.dwButtons      ||
                current.dwPOV     != last.dwPOV;

            _lastState[id] = current;
            if (changed) anyActivity = true;
        }

        // Force full re-detection on next tick if any device disappeared
        if (needsReset is not null)
        {
            foreach (uint id in needsReset)
                _connected.Remove(id);
            _lastDetectionMs = 0;
        }

        return anyActivity;
    }

    private static uint Scale(uint raw) => raw * 100u / 65535u;

    private static bool AxisChanged(uint a, uint b) =>
        (int)Math.Abs((long)a - b) > AxisChangeThreshold;
}
