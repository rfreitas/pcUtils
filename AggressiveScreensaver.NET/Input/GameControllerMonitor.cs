using System;
using System.Collections.Generic;
using SDL2;
using AggressiveScreensaver.Services;

namespace AggressiveScreensaver.Input;

/// <summary>
/// Polls every connected HID game controller — Xbox, PlayStation DualSense/DualSense
/// Edge, generic gamepads, anything SDL2 recognizes — via SDL2's game controller API,
/// and detects meaningful input.
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
    // stick deadzone ratio to ignore analog drift/noise.
    private const double AxisDeadzone = 0.12;
    private const short AxisMax = 32767;

    private sealed class ControllerState
    {
        public IntPtr Handle;
        public bool[] Buttons = new bool[(int)SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_MAX];
        public double[] Axes = new double[(int)SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_MAX];
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
        if (SDL.SDL_Init(SDL.SDL_INIT_GAMECONTROLLER) != 0)
            throw new InvalidOperationException($"SDL_Init failed: {SDL.SDL_GetError()}");
        _initialized = true;
    }

    /// <summary>
    /// Returns true if any connected controller has meaningful input since the last call.
    /// </summary>
    public bool HasActivity()
    {
        EnsureInitialized();
        SDL.SDL_GameControllerUpdate();

        OpenNewControllers();
        return PollOpenControllers();
    }

    private void OpenNewControllers()
    {
        int count = SDL.SDL_NumJoysticks();
        for (int deviceIndex = 0; deviceIndex < count; deviceIndex++)
        {
            if (SDL.SDL_IsGameController(deviceIndex) != SDL.SDL_bool.SDL_TRUE)
                continue;

            int instanceId = SDL.SDL_JoystickGetDeviceInstanceID(deviceIndex);
            if (_states.ContainsKey(instanceId))
                continue;

            IntPtr handle = SDL.SDL_GameControllerOpen(deviceIndex);
            if (handle == IntPtr.Zero)
            {
                Logger.Log($"SDL_GameControllerOpen failed for device {deviceIndex}: {SDL.SDL_GetError()}");
                continue;
            }

            Logger.Log($"SDL2 controller connected: {SDL.SDL_GameControllerName(handle)} " +
                       $"VID={SDL.SDL_GameControllerGetVendor(handle):X4} PID={SDL.SDL_GameControllerGetProduct(handle):X4}");

            _states[instanceId] = new ControllerState { Handle = handle };
        }
    }

    private bool PollOpenControllers()
    {
        bool anyActivity = false;
        List<int>? disconnected = null;

        foreach (var (instanceId, state) in _states)
        {
            if (SDL.SDL_GameControllerGetAttached(state.Handle) != SDL.SDL_bool.SDL_TRUE)
            {
                (disconnected ??= []).Add(instanceId);
                continue;
            }

            for (int i = 0; i < state.Buttons.Length; i++)
            {
                bool pressed = SDL.SDL_GameControllerGetButton(state.Handle, (SDL.SDL_GameControllerButton)i) != 0;
                if (pressed != state.Buttons[i])
                {
                    anyActivity = true;
                    state.Buttons[i] = pressed;
                }
            }

            for (int i = 0; i < state.Axes.Length; i++)
            {
                double value = SDL.SDL_GameControllerGetAxis(state.Handle, (SDL.SDL_GameControllerAxis)i) / (double)AxisMax;
                if (Math.Abs(value - state.Axes[i]) > AxisDeadzone)
                    anyActivity = true;
                state.Axes[i] = value;
            }
        }

        if (disconnected is not null)
        {
            foreach (var instanceId in disconnected)
            {
                SDL.SDL_GameControllerClose(_states[instanceId].Handle);
                _states.Remove(instanceId);
            }
        }

        return anyActivity;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var state in _states.Values)
            SDL.SDL_GameControllerClose(state.Handle);
        _states.Clear();

        if (_initialized)
            SDL.SDL_QuitSubSystem(SDL.SDL_INIT_GAMECONTROLLER);
    }
}
