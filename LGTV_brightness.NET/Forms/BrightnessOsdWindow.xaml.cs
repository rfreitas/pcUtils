using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LgtvBrightness.Forms;

/// <summary>
/// Custom on-screen brightness indicator, styled after Windows' native
/// volume/brightness flyout (rounded dark card, icon + segmented bar,
/// auto-fades). Windows has no public API to trigger the real OSD for this
/// TV's non-functional brightness slider (confirmed: simulating the hardware
/// brightness key changes nothing and shows nothing), so this draws its own.
/// Only wired to the hotkey path — see BrightnessService.HotkeyApplied.
/// </summary>
internal partial class BrightnessOsdWindow : Window
{
    private const int SegmentCount = 12;
    private const double WindowWidth = 220;
    private const int AutoHideMs = 1500;
    private const int FadeMs = 200;

    private static readonly SolidColorBrush FilledBrush   = new(Colors.White);
    private static readonly SolidColorBrush UnfilledBrush = new(System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55));

    private readonly Border[] _segments = new Border[SegmentCount];
    private readonly DispatcherTimer _hideTimer;

    public BrightnessOsdWindow()
    {
        InitializeComponent();

        for (int i = 0; i < SegmentCount; i++)
        {
            var seg = new Border
            {
                Width = 10,
                Height = 18,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0),
                Background = UnfilledBrush,
            };
            _segments[i] = seg;
            SegmentsPanel.Children.Add(seg);
        }

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AutoHideMs) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            FadeOutAndHide();
        };
    }

    public void ShowValue(int percent)
    {
        int filled = (int)Math.Round(SegmentCount * Math.Clamp(percent, 0, 100) / 100.0);
        for (int i = 0; i < SegmentCount; i++)
            _segments[i].Background = i < filled ? FilledBrush : UnfilledBrush;

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
