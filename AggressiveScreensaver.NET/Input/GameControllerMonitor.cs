using System;
using System.Collections.Generic;
using SDL2;
using AggressiveScreensaver.Services;

namespace AggressiveScreensaver.Input;

/// <summary>
/// Polls every connected HID input device that could indicate the user is
/// actively gaming — controllers, wheels, flight sticks, arcade sticks, and
/// anything else SDL2 enumerates as a joystick — and detects meaningful input.
///
/// Devices SDL recognizes as a "game controller" (a known button/axis mapping —
/// Xbox, PlayStation DualSense/DualSense Edge, generic gamepads) are polled via
/// the abstracted SDL_GameController API. Everything else — wheels, flight
/// sticks, arcade sticks — has no such mapping in SDL's database, so it's
/// polled via the lower-level SDL_Joystick API instead, which works for any
/// HID device SDL enumerates, no mapping required. The two APIs disagree on
/// how many buttons/axes a device has and don't share state, so each
/// connected device is tracked as one or the other, never both.
///
/// Replaces two older paths (XInput: Xbox-class only; legacy winmm joyGetPosEx: unstable)
/// and a third (Windows.Gaming.Input.RawGameController): RawGameController depends on
/// Windows having already bound a HID game-controller class-driver node for the device,
/// which it never does for this DualSense Edge over Bluetooth (confirmed via PnP device
/// diagnostics — only the transport-level "Bluetooth HID Device" node exists). SDL2's
/// hidapi backend reads raw HID reports directly, the same approach Steam and Sony's
/// PS app use, bypassing that classification step entirely.
/// </summary>
internal sealed class GameControllerMonitor : IDisposable
{
    // Axis readings are normalized -1..1; this threshold mirrors XInput's ~8000/32767
    // stick deadzone ratio to ignore analog drift/noise on an analog stick.
    private const double GameControllerAxisDeadzone = 0.12;

    // Raw joystick axes (wheels, pedals, flight sticks) need a much smaller deadzone:
    // a wheel maps its full physical rotation (often 900+ degrees) onto -1..1, so a
    // realistic steering correction of a few degrees is nowhere near 12% of that
    // range and would never register as activity. This is small enough to still
    // filter pure electrical jitter at rest.
    private const double RawAxisDeadzone = 0.01;

    private const short AxisMax = 32767;

    private sealed class ControllerState
    {
        public IntPtr Handle;
        public bool IsGameController;
        public bool[] Buttons = Array.Empty<bool>();
        public double[] Axes = Array.Empty<double>();
        // POV/hat switches (common on wheels and flight sticks). Only tracked for raw
        // joysticks — SDL_GameController maps hats into the D-pad buttons instead.
        public byte[] Hats = Array.Empty<byte>();
    }

    // Keyed by SDL joystick instance ID (stable per-hardware-instance for the life of
    // the connection; SDL reassigns device indices as controllers come and go, so the
    // instance ID — not the index — is what state tracking must key on).
    private readonly Dictionary<int, ControllerState> _states = new();
    private bool _initialized;
    private bool _disposed;

    private void EnsureInitialized()
    {
        if (_initialized) return;
        if (SDL.SDL_Init(SDL.SDL_INIT_GAMECONTROLLER | SDL.SDL_INIT_JOYSTICK) != 0)
            throw new InvalidOperationException($"SDL_Init failed: {SDL.SDL_GetError()}");
        _initialized = true;
    }

    /// <summary>
    /// Returns true if any connected controller/wheel/joystick has meaningful input
    /// since the last call.
    /// </summary>
    public bool HasActivity()
    {
        EnsureInitialized();
        SDL.SDL_GameControllerUpdate();
        SDL.SDL_JoystickUpdate();

        OpenNewControllers();
        return PollOpenControllers();
    }

    private void OpenNewControllers()
    {
        int count = SDL.SDL_NumJoysticks();
        for (int deviceIndex = 0; deviceIndex < count; deviceIndex++)
        {
            int instanceId = SDL.SDL_JoystickGetDeviceInstanceID(deviceIndex);
            if (_states.ContainsKey(instanceId))
                continue;

            if (SDL.SDL_IsGameController(deviceIndex) == SDL.SDL_bool.SDL_TRUE)
                OpenGameController(deviceIndex, instanceId);
            else
                OpenRawJoystick(deviceIndex, instanceId);
        }
    }

    private void OpenGameController(int deviceIndex, int instanceId)
    {
        IntPtr handle = SDL.SDL_GameControllerOpen(deviceIndex);
        if (handle == IntPtr.Zero)
        {
            Logger.Log($"SDL_GameControllerOpen failed for device {deviceIndex}: {SDL.SDL_GetError()}");
            return;
        }

        Logger.Log($"SDL2 controller connected: {SDL.SDL_GameControllerName(handle)} " +
                   $"VID={SDL.SDL_GameControllerGetVendor(handle):X4} PID={SDL.SDL_GameControllerGetProduct(handle):X4}");

        _states[instanceId] = new ControllerState
        {
            Handle           = handle,
            IsGameController = true,
            Buttons          = new bool[(int)SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_MAX],
            Axes             = new double[(int)SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_MAX],
        };
    }

    private void OpenRawJoystick(int deviceIndex, int instanceId)
    {
        IntPtr handle = SDL.SDL_JoystickOpen(deviceIndex);
        if (handle == IntPtr.Zero)
        {
            Logger.Log($"SDL_JoystickOpen failed for device {deviceIndex}: {SDL.SDL_GetError()}");
            return;
        }

        Logger.Log($"SDL2 joystick connected: {SDL.SDL_JoystickName(handle)} (type={SDL.SDL_JoystickGetType(handle)}) " +
                   $"VID={SDL.SDL_JoystickGetVendor(handle):X4} PID={SDL.SDL_JoystickGetProduct(handle):X4}");

        _states[instanceId] = new ControllerState
        {
            Handle           = handle,
            IsGameController = false,
            Buttons          = new bool[Math.Max(0, SDL.SDL_JoystickNumButtons(handle))],
            Axes             = new double[Math.Max(0, SDL.SDL_JoystickNumAxes(handle))],
            Hats             = new byte[Math.Max(0, SDL.SDL_JoystickNumHats(handle))],
        };
    }

    private bool PollOpenControllers()
    {
        bool anyActivity = false;
        List<int>? disconnected = null;

        foreach (var (instanceId, state) in _states)
        {
            bool attached = state.IsGameController
                ? SDL.SDL_GameControllerGetAttached(state.Handle) == SDL.SDL_bool.SDL_TRUE
                : SDL.SDL_JoystickGetAttached(state.Handle) == SDL.SDL_bool.SDL_TRUE;

            if (!attached)
            {
                (disconnected ??= []).Add(instanceId);
                continue;
            }

            for (int i = 0; i < state.Buttons.Length; i++)
            {
                bool pressed = state.IsGameController
                    ? SDL.SDL_GameControllerGetButton(state.Handle, (SDL.SDL_GameControllerButton)i) != 0
                    : SDL.SDL_JoystickGetButton(state.Handle, i) != 0;

                if (pressed != state.Buttons[i])
                {
                    anyActivity = true;
                    state.Buttons[i] = pressed;
                }
            }

            for (int i = 0; i < state.Axes.Length; i++)
            {
                short raw = state.IsGameController
                    ? SDL.SDL_GameControllerGetAxis(state.Handle, (SDL.SDL_GameControllerAxis)i)
                    : SDL.SDL_JoystickGetAxis(state.Handle, i);

                double value = raw / (double)AxisMax;
                double deadzone = state.IsGameController ? GameControllerAxisDeadzone : RawAxisDeadzone;
                if (Math.Abs(value - state.Axes[i]) > deadzone)
                    anyActivity = true;
                state.Axes[i] = value;
            }

            for (int i = 0; i < state.Hats.Length; i++)
            {
                byte hat = SDL.SDL_JoystickGetHat(state.Handle, i);
                if (hat != state.Hats[i])
                    anyActivity = true;
                state.Hats[i] = hat;
            }
        }

        if (disconnected is not null)
        {
            foreach (var instanceId in disconnected)
            {
                CloseHandle(_states[instanceId]);
                _states.Remove(instanceId);
            }
        }

        return anyActivity;
    }

    private static void CloseHandle(ControllerState state)
    {
        if (state.IsGameController)
            SDL.SDL_GameControllerClose(state.Handle);
        else
            SDL.SDL_JoystickClose(state.Handle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var state in _states.Values)
            CloseHandle(state);
        _states.Clear();

        if (_initialized)
            SDL.SDL_QuitSubSystem(SDL.SDL_INIT_GAMECONTROLLER | SDL.SDL_INIT_JOYSTICK);
    }
}
