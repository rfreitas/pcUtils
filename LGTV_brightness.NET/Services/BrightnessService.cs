using System;
using System.Threading;
using System.Threading.Tasks;

namespace LgtvBrightness.Services;

/// <summary>
/// Tracks the current backlight value, talks to the TV via LgTvCliService, and
/// debounces slider drags. CLI calls run on the thread pool (they're a local
/// process spawn + a network round-trip to the TV) so the UI thread never blocks;
/// <see cref="Changed"/> is always marshaled back onto the thread that
/// constructed this service (the UI thread).
/// </summary>
internal sealed class BrightnessService
{
    public const int Step = 10;
    private const int SyncEveryMs = 15000;
    private const int SliderDebounceMs = 150;

    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

    private int _cur = 50;
    private long _lastSyncTick;
    private int _pendingVal;
    private System.Threading.Timer? _debounce;

    public int Current => _cur;

    /// <summary>Fired on the UI thread whenever the tracked value changes.</summary>
    public event Action<int>? Changed;

    /// <summary>Fired on the UI thread only after a hotkey-driven adjustment (not
    /// slider drags or tray sync) — drives the on-screen brightness OSD.</summary>
    public event Action<int>? HotkeyApplied;

    /// <summary>Fired on the UI thread once the TV write for the latest hotkey press finishes
    /// (true = TV accepted it, false = failed). Superseded presses don't report.</summary>
    public event Action<bool>? HotkeySynced;

    private int _adjustSeq;

    public async Task SyncAsync(bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force && now - _lastSyncTick < SyncEveryMs)
            return;

        int? val = await Task.Run(LgTvCliService.GetBacklight);
        _lastSyncTick = now;
        if (val is int v)
        {
            _cur = Clamp(v);
            Raise(Changed, _cur);
        }
    }

    public async Task<bool> ApplyAsync(int value)
    {
        _cur = Clamp(value);
        Raise(Changed, _cur);
        int v = _cur;
        return await Task.Run(() => LgTvCliService.SetBacklight(v));
    }

    /// <summary>Adjusts by +/- Step. Mirrors the AHK hotkey path: an opportunistic
    /// (interval-gated) sync first, then apply relative to the tracked value.</summary>
    public async Task AdjustAsync(int direction)
    {
        // Show the OSD from the tracked value right away — the sync and the TV write
        // are CLI process spawns + network round-trips, and awaiting them first is what
        // made the OSD feel laggy. If the sync corrects the value, the OSD is updated below.
        int seq = Interlocked.Increment(ref _adjustSeq);
        int optimistic = Clamp(_cur + direction * Step);
        Raise(HotkeyApplied, optimistic);

        await SyncAsync(force: false);

        int target = Clamp(_cur + direction * Step);
        if (target != optimistic)
            Raise(HotkeyApplied, target);

        bool ok = await ApplyAsync(target);
        if (seq == Volatile.Read(ref _adjustSeq))
            Raise(HotkeySynced, ok);
    }

    /// <summary>
    /// Called on every slider-drag tick. Updates the tracked value immediately for
    /// responsive UI, then sends to the TV 150 ms after the last movement.
    /// </summary>
    public void QueueSliderValue(int value)
    {
        _pendingVal = Clamp(value);
        Raise(Changed, _pendingVal);

        _debounce?.Dispose();
        _debounce = new System.Threading.Timer(_ =>
        {
            if (_pendingVal != _cur)
                _ = ApplyAsync(_pendingVal);
        }, null, SliderDebounceMs, Timeout.Infinite);
    }

    private void Raise(Action<bool>? evt, bool value)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
            evt?.Invoke(value);
        else
            _uiContext.Post(_ => evt?.Invoke(value), null);
    }

    private void Raise(Action<int>? evt, int value)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
            evt?.Invoke(value);
        else
            _uiContext.Post(_ => evt?.Invoke(value), null);
    }

    private static int Clamp(int v) => Math.Clamp(v, 0, 100);
}
