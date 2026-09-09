using System.Windows.Media;
using RefreshRateOverlay.WPF.Services;

namespace RefreshRateOverlay.WPF.Tests;

/// <summary>
/// Regression coverage for a real production bug: clicking Apply never told
/// the overlay's own sync dots that the shown value had just become the new
/// truth, so a dot the user had just turned red by editing (Rate, App VRR,
/// Frame Cap, ...) stayed red after Apply — only closing and reopening the
/// dialog (which recomputes everything fresh) ever showed green, making a
/// perfectly successful Apply look like it hadn't taken effect.
///
/// RefreshLiveState/RefreshGsyncLiveState already existed and already worked
/// (see OverlayWindowLiveRefreshTests) — TrayApp's ApplyRequested handler
/// simply never called them. App VRR, Frame Cap, and the DSX controller
/// profile had no equivalent method to call AT ALL, since they get no
/// background reconciliation the other three do. These tests cover the
/// ReflectAppVrrApplied/ReflectFrameCapApplied/ReflectDsxApplied methods that
/// close that gap — TrayApp's Apply handler now calls RefreshLiveState/
/// RefreshGsyncLiveState plus these three, right next to each setting's
/// write, instead of leaving the "tell the dot it's saved" step out entirely
/// for three of six settings and silently relying on a background
/// reconciliation tick for the other three (routinely delayed several
/// seconds, or that might never fire again at all — see TrayApp.ReconcileAll/
/// OnDisplayChange's _settling guard).
/// </summary>
public class OverlayWindowReflectAppliedTests
{
    private static OverlayWindow CreateWindow(
        VrrAppState? appVrrState = null,
        uint? frameCapFps = null) =>
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
            storedHdr: false,
            frameCapFps: frameCapFps);

    [Fact]
    public void ReflectAppVrrApplied_TurnsRedDotGreen_AndUpdatesBaseline() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(appVrrState: VrrAppState.Allow);
        window.AppVrrDropDown.SelectedIndex = OverlayWindow.VrrStateToIndex(VrrAppState.ForceOff);
        Assert.Equal(Brushes.Red.Color, ((SolidColorBrush)window.AppVrrSyncDot.Fill).Color);

        window.ReflectAppVrrApplied(VrrAppState.ForceOff);

        Assert.Equal(VrrAppState.ForceOff, window.AppVrrSync!.Stored);
        Assert.Equal(Brushes.LimeGreen.Color, ((SolidColorBrush)window.AppVrrSyncDot.Fill).Color);
    });

    [Fact]
    public void ReflectAppVrrApplied_RowNeverShown_IsNoOp() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(appVrrState: null);

        window.ReflectAppVrrApplied(VrrAppState.Allow); // must not throw

        Assert.Null(window.AppVrrSync);
    });

    [Fact]
    public void ReflectFrameCapApplied_TurnsRedDotGreen_AndUpdatesBaseline() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(frameCapFps: 60);
        window.FrameCapTextBox.Text = "144";
        Assert.Equal(Brushes.Red.Color, ((SolidColorBrush)window.FrameCapSyncDot.Fill).Color);

        window.ReflectFrameCapApplied(144);

        Assert.Equal(144u, window.FrameCapSync!.Stored);
        Assert.Equal(Brushes.LimeGreen.Color, ((SolidColorBrush)window.FrameCapSyncDot.Fill).Color);
    });

    [Fact]
    public void ReflectFrameCapApplied_RowNeverShown_IsNoOp() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(frameCapFps: null);

        window.ReflectFrameCapApplied(60); // must not throw

        Assert.Null(window.FrameCapSync);
    });

    [Fact]
    public void ReflectDsxApplied_DsxNeverAvailable_IsNoOp() => StaTestHelper.Run(() =>
    {
        // DSX_Console isn't reachable in a test environment, so DsxSync never
        // gets constructed (see LoadDsxProfilesAsync) — same "no row, no
        // dot, must not throw" shape as the other two no-op tests above.
        var window = CreateWindow();

        window.ReflectDsxApplied("SomeProfile"); // must not throw

        Assert.Null(window.DsxSync);
    });
}
