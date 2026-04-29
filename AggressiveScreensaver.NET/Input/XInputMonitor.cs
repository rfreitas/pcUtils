using System;
using System.Runtime.InteropServices;
using AggressiveScreensaver.Services;

namespace AggressiveScreensaver.Input;

/// <summary>
/// Polls up to 4 XInput controllers (indices 0-3) and detects meaningful activity.
/// Ported from GetXInputState / HasControllerActivity in index.ahk.
/// </summary>
internal sealed class XInputMonitor
{
    // -------------------------------------------------------------------------
    // P/Invoke  (XInputGetState via dynamic load)
    // -------------------------------------------------------------------------
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte   bLeftTrigger;
        public byte   bRightTrigger;
        public short  sThumbLX;
        public short  sThumbLY;
        public short  sThumbRX;
        public short  sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint          dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    private delegate uint XInputGetStateDelegate(uint dwUserIndex, ref XINPUT_STATE pState);

    // -------------------------------------------------------------------------
    // Constants (mirroring AHK)
    // -------------------------------------------------------------------------
    private const short  Deadzone        = 8000;
    private const byte   TriggerThreshold = 30;
    private const int    MaxControllers  = 4;

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------
    private XInputGetStateDelegate? _getState;
    private readonly XINPUT_STATE[] _lastStates = new XINPUT_STATE[MaxControllers];
    private readonly bool[]         _initialized = new bool[MaxControllers];

    // -------------------------------------------------------------------------
    // Init
    // -------------------------------------------------------------------------
    public void Initialize()
    {
        string[] candidates = ["xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll"];
        foreach (string dll in candidates)
        {
            IntPtr hLib = LoadLibrary(dll);
            if (hLib == IntPtr.Zero) continue;

            IntPtr fn = GetProcAddress(hLib, "XInputGetState");
            if (fn == IntPtr.Zero) continue;

            _getState = Marshal.GetDelegateForFunctionPointer<XInputGetStateDelegate>(fn);
            return;
        }
        // XInput not available (e.g., pre-Win8); just means no XInput controller activity
    }

    // -------------------------------------------------------------------------
    // Poll
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns true if any XInput controller has meaningful input since the last call.
    /// </summary>
    public bool HasActivity()
    {
        if (_getState is null) return false;

        for (uint i = 0; i < MaxControllers; i++)
        {
            var state = new XINPUT_STATE();
            uint result = _getState(i, ref state);
            if (result != 0) continue; // ERROR_DEVICE_NOT_CONNECTED = 1167

            if (!_initialized[i])
            {
                _lastStates[i] = state;
                _initialized[i] = true;
                continue;
            }

            ref var last = ref _lastStates[i];
            if (state.dwPacketNumber == last.dwPacketNumber) continue;

            bool hasInput =
                state.Gamepad.wButtons != 0 ||
                state.Gamepad.bLeftTrigger  > TriggerThreshold ||
                state.Gamepad.bRightTrigger > TriggerThreshold ||
                Math.Abs(state.Gamepad.sThumbLX) > Deadzone ||
                Math.Abs(state.Gamepad.sThumbLY) > Deadzone ||
                Math.Abs(state.Gamepad.sThumbRX) > Deadzone ||
                Math.Abs(state.Gamepad.sThumbRY) > Deadzone;

            _lastStates[i] = state;
            if (hasInput) return true;
        }
        return false;
    }
}
