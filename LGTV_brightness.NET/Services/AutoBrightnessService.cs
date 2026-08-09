using System;

namespace LgtvBrightness.Services;

/// <summary>
/// Scales the backlight by the display refresh-rate ratio whenever the rate
/// changes (e.g. switching a game to 120 Hz should dim relative to 60 Hz).
/// Direct port of the AHK script's CheckRefreshRate().
/// </summary>
internal sealed class AutoBrightnessService
{
    private readonly BrightnessService _brightness;
    private int _lastRate;

    public bool Enabled { get; set; }

    public AutoBrightnessService(BrightnessService brightness) => _brightness = brightness;

    /// <summary>Establishes the baseline rate. Call at startup and whenever re-enabled.</summary>
    public void Initialize() => _lastRate = DisplayService.GetCurrentRate();

    public void OnDisplayChange()
    {
        if (!Enabled) return;

        int rate = DisplayService.GetCurrentRate();
        if (rate <= 0) return;

        if (_lastRate > 0 && rate != _lastRate)
        {
            double ratio = (double)_lastRate / rate;
            int newVal = (int)Math.Round(_brightness.Current * ratio);
            _ = _brightness.ApplyAsync(newVal);
        }

        _lastRate = rate;
    }
}
