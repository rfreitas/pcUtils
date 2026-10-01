namespace AggressiveScreensaver.Services;

/// <summary>
/// Keeps the Windows screensaver timeout equal to AggressiveScreensaver's own blank threshold, so the two
/// are configured as one. The app is the source of truth, and it only writes the Windows setting while the
/// "Native Screensaver" item is ticked — an unticked screensaver is none of our business.
///
/// Operations are injected so the policy is unit-testable without touching the real OS setting.
/// </summary>
internal sealed class NativeScreensaverSync
{
    private readonly Func<bool>        _isActive;
    private readonly Func<int>         _getTimeoutSec;
    private readonly Func<int, bool>   _setTimeoutSec;
    private readonly Action<string>?   _log;

    public NativeScreensaverSync(
        Func<bool> isActive, Func<int> getTimeoutSec, Func<int, bool> setTimeoutSec, Action<string>? log = null)
    {
        _isActive      = isActive;
        _getTimeoutSec = getTimeoutSec;
        _setTimeoutSec = setTimeoutSec;
        _log           = log;
    }

    /// <summary>The real OS-backed instance.</summary>
    public static NativeScreensaverSync ForSystem(Action<string>? log) => new(
        NativeScreensaverService.IsActive,
        NativeScreensaverService.GetTimeoutSeconds,
        NativeScreensaverService.SetTimeoutSeconds,
        log);

    /// <summary>The app's threshold changed (slider) or the app just started: follow it if the native screensaver is on.</summary>
    public void OnThresholdChanged(int thresholdSec)
    {
        if (_isActive()) Apply(thresholdSec);
    }

    /// <summary>The user just ticked "Native Screensaver": match it to the app's threshold straight away.</summary>
    public void OnEnabled(int thresholdSec) => Apply(thresholdSec);

    private void Apply(int thresholdSec)
    {
        if (thresholdSec <= 0 || _getTimeoutSec() == thresholdSec) return;   // already in sync: no needless OS write

        bool ok = _setTimeoutSec(thresholdSec);
        _log?.Invoke($"Native screensaver timeout set to {thresholdSec}s to match the blank threshold.{(ok ? "" : " (failed)")}");
    }
}
