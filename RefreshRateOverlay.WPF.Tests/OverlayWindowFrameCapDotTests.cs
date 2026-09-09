using System.Windows.Media;
using RefreshRateOverlay.WPF.Services;

namespace RefreshRateOverlay.WPF.Tests;

/// <summary>
/// Covers the Frame Cap row/dot — built the same way as App VRR's (see
/// OverlayWindowAppVrrDotTests remarks): no default/global scope, no
/// background reconciliation, baseline is whatever NVIDIA reported when the
/// dialog opened. Unlike every other synced control here, this one is a plain
/// TextBox (no dropdown — see OverlayWindow.xaml's FrameCapRow comment), so
/// these exercise free-typed numeric input directly rather than a fixed list.
/// </summary>
public class OverlayWindowFrameCapDotTests
{
    private static OverlayWindow CreateWindow(uint? frameCapFps) =>
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
            appVrrState: null,
            runningApps: new List<string>(),
            storedRate: 60,
            storedHdr: false,
            frameCapFps: frameCapFps);

    [Fact]
    public void FrameCapNull_RowStaysCollapsed_NoSyncedField() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(frameCapFps: null);
        Assert.Null(window.FrameCapSync);
    });

    [Fact]
    public void FrameCapProvided_DotStartsGreen_MatchesOpenTimeSnapshot() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(frameCapFps: 60);

        Assert.NotNull(window.FrameCapSync);
        Assert.Equal(60u, window.FrameCapSync!.Stored);
        Assert.Equal("60", window.FrameCapTextBox.Text);
        Assert.Equal(Brushes.LimeGreen.Color, ((SolidColorBrush)window.FrameCapSyncDot.Fill).Color);
    });

    [Fact]
    public void UserTypesNewValue_MarksTouched_DotTurnsRed() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(frameCapFps: 60);
        Assert.False(window.FrameCapSync!.Touched);

        window.FrameCapTextBox.Text = "144";

        Assert.True(window.FrameCapSync.Touched);
        Assert.Equal(144u, window.SelectedFrameCapFps);
        Assert.Equal(Brushes.Red.Color, ((SolidColorBrush)window.FrameCapSyncDot.Fill).Color);
    });

    [Fact]
    public void UserClearsText_RoundTripsToNotSet() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(frameCapFps: 60);

        window.FrameCapTextBox.Text = "";

        Assert.Equal(NvidiaGsyncService.FrameCapNotSet, window.SelectedFrameCapFps);
    });

    [Fact]
    public void UserTypesZero_RoundTripsAsExplicitZero_NotNotSet() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(frameCapFps: NvidiaGsyncService.FrameCapNotSet);

        window.FrameCapTextBox.Text = "0";

        Assert.Equal(0u, window.SelectedFrameCapFps);
    });
}
