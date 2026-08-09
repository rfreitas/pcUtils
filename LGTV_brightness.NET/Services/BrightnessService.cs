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
            RaiseChanged(_cur);
        }
    }

    public async Task ApplyAsync(int value)
    {
        _cur = Clamp(value);
        RaiseChanged(_cur);
        await Task.Run(() => LgTvCliService.SetBacklight(_cur));
    }

    /// <summary>Adjusts by +/- Step. Mirrors the AHK hotkey path: an opportunistic
    /// (interval-gated) sync first, then apply relative to the tracked value.</summary>
    public async Task AdjustAsync(int direction)
    {
        await SyncAsync(force: false);
        await ApplyAsync(_cur + direction * Step);
    }

    /// <summary>
    /// Called on every slider-drag tick. Updates the tracked value immediately for
    /// responsive UI, then sends to the TV 150 ms after the last movement.
    /// </summary>
    public void QueueSliderValue(int value)
    {
        _pendingVal = Clamp(value);
        RaiseChanged(_pendingVal);

        _debounce?.Dispose();
        _debounce = new System.Threading.Timer(_ =>
        {
            if (_pendingVal != _cur)
                _ = ApplyAsync(_pendingVal);
        }, null, SliderDebounceMs, Timeout.Infinite);
    }

    private void RaiseChanged(int value)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
            Changed?.Invoke(value);
        else
            _uiContext.Post(_ => Changed?.Invoke(value), null);
    }

    private static int Clamp(int v) => Math.Clamp(v, 0, 100);
}
