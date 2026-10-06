using System.Windows.Media;
using RefreshRateOverlay.WPF.Services;

namespace RefreshRateOverlay.WPF.Tests;

/// <summary>
/// The core-parking row follows the same default+per-app, sync-dot shape as HDR:
/// these cover its checkbox, dot and RefreshCoreParkingLiveState on a real
/// (never Show()n) OverlayWindow.
/// </summary>
public class OverlayWindowCoreParkingTests
{
    private static OverlayWindow CreateWindow(bool disable = false, bool stored = false) =>
        new OverlayWindow(
            activeApp: "Desktop",
            availableRates: new List<int> { 60, 100, 120 },
            preselectRate: 60,
            hdrSupported: true,
            hdrEnabled: false,
            hasProfile: false,
            windowMode: WindowMode.Windowed,
            preselectDsxProfile: null,
            gsyncMode: null,
            appVrrState: null,
            runningApps: new List<string>(),
            storedRate: 60,
            storedHdr: false,
            disableCoreParking: disable,
            storedDisableCoreParking: stored);

    private static Color DotColor(OverlayWindow w) => ((SolidColorBrush)w.CoreParkingSyncDot.Fill).Color;

    [Fact]
    public void Row_IsAlwaysVisible_AndPreselectsTheGivenValue() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(disable: true, stored: true);

        Assert.Equal(System.Windows.Visibility.Visible, window.CoreParkingRow.Visibility);
        Assert.True(window.DisableCoreParking);
    });

    [Fact]
    public void MatchingStoredValue_DotStartsGreen() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(disable: true, stored: true);

        Assert.Equal(Brushes.LimeGreen.Color, DotColor(window));
    });

    [Fact]
    public void UserEdit_TurnsDotRed_AndMarksTouched() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(disable: false, stored: false);

        window.DisableCoreParkingCheckBox.IsChecked = true;

        Assert.True(window.CoreParkingSync.Touched);
        Assert.Equal(Brushes.Red.Color, DotColor(window));
    });

    [Fact]
    public void RefreshLive_Untouched_FollowsNewBaseline() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(disable: false, stored: false);

        window.RefreshCoreParkingLiveState(disabled: true);

        Assert.True(window.DisableCoreParking);
        Assert.True(window.CoreParkingSync.Stored);
        Assert.False(window.CoreParkingSync.Touched);
        Assert.Equal(Brushes.LimeGreen.Color, DotColor(window));
    });

    [Fact]
    public void RefreshLive_Touched_KeepsSelectionButMovesBaseline() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(disable: false, stored: false);
        window.DisableCoreParkingCheckBox.IsChecked = true; // user edit

        window.RefreshCoreParkingLiveState(disabled: false);

        Assert.True(window.DisableCoreParking);              // edit preserved
        Assert.False(window.CoreParkingSync.Stored);         // baseline moved
        Assert.Equal(Brushes.Red.Color, DotColor(window));   // checked != stored
    });
}
