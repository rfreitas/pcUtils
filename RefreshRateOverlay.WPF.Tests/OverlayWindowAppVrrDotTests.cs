using System.Windows.Media;
using RefreshRateOverlay.WPF.Services;

namespace RefreshRateOverlay.WPF.Tests;

/// <summary>
/// Covers the App VRR sync dot added alongside the "Not Set" dropdown entry —
/// regression coverage for App VRR previously having no dot at all (see
/// OverlayWindow's AppVrrSyncDot/AppVrrRow remarks). Unlike Rate/HDR/GsyncMode,
/// there's no background reconciliation for this setting, so its baseline is
/// whatever NVIDIA reported when the dialog opened, not a resolved
/// profile-or-default — these tests exercise that "matches open-time snapshot"
/// semantics specifically, not the full reconciliation shape.
/// </summary>
public class OverlayWindowAppVrrDotTests
{
    private static OverlayWindow CreateWindow(VrrAppState? appVrrState) =>
        new OverlayWindow(
            activeApp: "Desktop",
            availableRates: new List<int> { 60, 100, 120 },
            preselectRate: 60,
            hdrSupported: false,
            hdrEnabled: false,
            hasProfile: true,
            windowMode: WindowMode.Windowed,
            preselectDsxProfile: null,
            gsyncMode: null,
            appVrrState: appVrrState,
            runningApps: new List<string>(),
            storedRate: 60,
            storedHdr: false);

    [Fact]
    public void AppVrrStateNull_RowStaysCollapsed_NoSyncedField() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(appVrrState: null);
        Assert.Null(window.AppVrrSync);
    });

    [Fact]
    public void AppVrrStateProvided_DotStartsGreen_MatchesOpenTimeSnapshot() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(appVrrState: VrrAppState.Allow);

        Assert.NotNull(window.AppVrrSync);
        Assert.Equal(VrrAppState.Allow, window.AppVrrSync!.Stored);
        Assert.Equal(Brushes.LimeGreen.Color, ((SolidColorBrush)window.AppVrrSyncDot.Fill).Color);
    });

    [Fact]
    public void UserChangesDropdown_MarksTouched_DotTurnsRed() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(appVrrState: VrrAppState.NotSet);
        Assert.False(window.AppVrrSync!.Touched);

        window.AppVrrDropDown.SelectedIndex = OverlayWindow.VrrStateToIndex(VrrAppState.ForceOff);

        Assert.True(window.AppVrrSync.Touched);
        Assert.Equal(VrrAppState.ForceOff, window.SelectedAppVrrState);
    });

    [Fact]
    public void NotSetIsSelectable_RoundTripsThroughSelectedAppVrrState() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(appVrrState: VrrAppState.Allow);

        window.AppVrrDropDown.SelectedIndex = OverlayWindow.VrrStateToIndex(VrrAppState.NotSet);

        Assert.Equal(VrrAppState.NotSet, window.SelectedAppVrrState);
    });
}
