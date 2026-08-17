using System.Windows;
using RefreshRateOverlay.WPF.Services;

namespace RefreshRateOverlay.WPF.Tests;

/// <summary>
/// UI-layout tests: does the row for an unsupported/unavailable setting
/// actually collapse, not just its inner control? Regression coverage for the
/// "orphan dot on an empty row" bug — HdrRow's wrapping Grid didn't collapse
/// when only HdrCheckBox.Visibility was set, leaving the sync dot floating
/// alone.
/// </summary>
public class OverlayWindowLayoutTests
{
    private static OverlayWindow CreateWindow(bool hdrSupported, GsyncGlobalMode? gsyncMode) =>
        new OverlayWindow(
            activeApp: "Desktop",
            availableRates: new List<int> { 60, 100, 120 },
            preselectRate: 60,
            hdrSupported: hdrSupported,
            hdrEnabled: false,
            hasProfile: false,
            windowMode: WindowMode.Windowed,
            preselectDsxProfile: null,
            gsyncMode: gsyncMode,
            appVrrState: null,
            runningApps: new List<string>(),
            storedRate: 60,
            storedHdr: false);

    [Fact]
    public void HdrUnsupported_WholeRowCollapses() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(hdrSupported: false, gsyncMode: null);
        Assert.Equal(Visibility.Collapsed, window.HdrRow.Visibility);
    });

    [Fact]
    public void HdrSupported_RowVisible() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(hdrSupported: true, gsyncMode: null);
        Assert.Equal(Visibility.Visible, window.HdrRow.Visibility);
    });

    [Fact]
    public void GsyncModeNull_RowStaysCollapsed() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(hdrSupported: true, gsyncMode: null);
        Assert.Equal(Visibility.Collapsed, window.GsyncModeRow.Visibility);
    });

    [Fact]
    public void GsyncModeProvided_RowVisible() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(hdrSupported: true, gsyncMode: GsyncGlobalMode.Disabled);
        Assert.Equal(Visibility.Visible, window.GsyncModeRow.Visibility);
    });
}
