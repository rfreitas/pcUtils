using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LgtvBrightness.Forms;

/// <summary>
/// Custom on-screen brightness indicator, styled after Windows' native
/// volume/brightness flyout (rounded dark card, icon + fill bar, auto-fades).
/// Windows has no public API to trigger the real OSD for this TV's
/// non-functional brightness slider (confirmed: simulating the hardware
/// brightness key changes nothing and shows nothing), so this draws its own.
/// Only wired to the hotkey path — see BrightnessService.HotkeyApplied.
///
/// Continuous fill, matching BacklightSliderWindow's track visually — deliberately
/// not step-quantized, so there's no segment-count-vs-hotkey-step mismatch to keep in sync.
/// </summary>
internal partial class BrightnessOsdWindow : Window
{
    private const double WindowWidth = 220;
    private const double TrackWidth = 140;
    private const int AutoHideMs = 1500;
    private const int FadeMs = 200;

    private readonly DispatcherTimer _hideTimer;

    public BrightnessOsdWindow()
    {
        InitializeComponent();

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AutoHideMs) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            FadeOutAndHide();
        };
    }

    public void ShowValue(int percent)
    {
        FillBar.Width = TrackWidth * Math.Clamp(percent, 0, 100) / 100.0;

        PositionOnScreen();

        BeginAnimation(OpacityProperty, null); // cancel any in-flight fade
        Opacity = 1;
        if (!IsVisible)
            Show();

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void FadeOutAndHide()
    {
        var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(FadeMs));
        anim.Completed += (_, _) => Hide();
        BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>Top-center of the primary screen. Deliberately simple (DIP math via
    /// SystemParameters, no manual DPI/SetWindowPos plumbing) since this is a
    /// cosmetic, best-effort indicator, not something requiring pixel precision.</summary>
    private void PositionOnScreen()
    {
        Left = (SystemParameters.PrimaryScreenWidth - WindowWidth) / 2;
        Top  = SystemParameters.PrimaryScreenHeight * 0.08;
    }
}
