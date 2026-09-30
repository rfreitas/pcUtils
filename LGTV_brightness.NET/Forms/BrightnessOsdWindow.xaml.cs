using System;
using System.Windows;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
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

    // Subtle state tints for the fill: dimmer blue while the TV write is in flight,
    // soft blue once the TV accepted it, dull blue-grey if it failed.
    private static readonly WpfBrush PendingBrush = Frozen(0x6E, 0x94, 0xC2);
    private static readonly WpfBrush SyncedBrush  = Frozen(0x88, 0xBB, 0xFF);
    private static readonly WpfBrush FailedBrush  = Frozen(0x5E, 0x6B, 0x80);

    private static WpfBrush Frozen(byte r, byte g, byte b)
    {
        var br = new WpfSolidColorBrush(WpfColor.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

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

    /// <summary>Forces the native HWND creation + first layout/render pass ahead of time
    /// (at app startup, off the hot path) so the first real ShowValue() isn't the one
    /// paying that cost. Opacity 0 keeps the flash-through-Show/Hide invisible.</summary>
    public void WarmUp()
    {
        Opacity = 0;
        Show();
        Hide();
    }

    public void ShowValue(int percent)
    {
        FillBar.Width = TrackWidth * Math.Clamp(percent, 0, 100) / 100.0;
        FillBar.Background = PendingBrush;

        PositionOnScreen();

        BeginAnimation(OpacityProperty, null); // cancel any in-flight fade
        Opacity = 1;
        if (!IsVisible)
            Show();

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    public void SetSynced(bool ok) => FillBar.Background = ok ? SyncedBrush : FailedBrush;

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
