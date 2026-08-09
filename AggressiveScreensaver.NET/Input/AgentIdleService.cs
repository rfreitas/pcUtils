using System;
using System.Drawing;
using System.Windows.Forms;
using AggressiveScreensaver.Services;

namespace AggressiveScreensaver.Input;

/// <summary>
/// Combines all input sources to compute "Agent Idle" — the smart idle timer
/// that only resets on genuine physical activity (not jiggler/simulated input).
///
/// Ticks every 100 ms on the UI thread via a WinForms Timer.
/// Ported from UpdateAgentIdle() in index.ahk.
/// </summary>
internal sealed class AgentIdleService : IDisposable
{
    // -------------------------------------------------------------------------
    // Dependencies
    // -------------------------------------------------------------------------
    private readonly IdleTimers            _idle;
    private readonly GameControllerMonitor _controllers;
    private readonly System.Windows.Forms.Timer _timer;

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------
    private long  _lastActivityTick;
    private Point _lastMousePos;
    private bool  _hooksInstalled;

    // -------------------------------------------------------------------------
    // Public state
    // -------------------------------------------------------------------------
    public int AgentIdleSeconds { get; private set; }

    /// <summary>Raised every 100 ms after idle is recalculated.</summary>
    public event EventHandler? Ticked;

    // -------------------------------------------------------------------------
    // Ctor
    // -------------------------------------------------------------------------
    public AgentIdleService(IdleTimers idle, GameControllerMonitor controllers)
    {
        _idle        = idle;
        _controllers = controllers;

        _lastActivityTick = Environment.TickCount64;

        _timer = new System.Windows.Forms.Timer { Interval = 100 };
        _timer.Tick += (_, _) => Update();
    }

    public void Start() => _timer.Start();
    public void Stop()  => _timer.Stop();

    // -------------------------------------------------------------------------
    // Core logic
    // -------------------------------------------------------------------------

    private void Update()
    {
        // Install hooks once on the message-loop thread
        if (!_hooksInstalled)
        {
            _idle.Install();
            _lastMousePos = Cursor.Position;
            _hooksInstalled = true;
        }

        // --- Mouse delta check (ignore jiggles < 5 pixels) ---
        Point curr = Cursor.Position;
        double dist = Math.Sqrt(
            Math.Pow(curr.X - _lastMousePos.X, 2) +
            Math.Pow(curr.Y - _lastMousePos.Y, 2));

        if (dist > 5)
        {
            _lastActivityTick = Environment.TickCount64;
            _lastMousePos = curr;
        }

        // --- Keyboard: infer from PhysicalIdleMs ---
        // If physical idle is very low AND the mouse didn't move, it must be a
        // keypress or mouse button click.
        if (_idle.PhysicalIdleMs < 50 && dist <= 5)
        {
            _lastActivityTick = Environment.TickCount64;
        }

        // --- Controller (Xbox, DualSense/DualSense Edge, any HID gamepad) ---
        try
        {
            if (_controllers.HasActivity())
                _lastActivityTick = Environment.TickCount64;
        }
        catch (Exception ex)
        {
            Logger.LogException(ex);
        }

        AgentIdleSeconds = (int)((Environment.TickCount64 - _lastActivityTick) / 1000);
        Ticked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Resets the activity timer to now (called by overlay cleanup so we don't
    /// immediately re-blank after a wake/display change).
    /// </summary>
    public void ResetActivity()
    {
        _lastActivityTick = Environment.TickCount64;
        _lastMousePos = Cursor.Position;
    }

    public void Dispose()
    {
        _timer.Dispose();
        _idle.Dispose();
        _controllers.Dispose();
    }
}
