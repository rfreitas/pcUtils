using System.Windows.Media;
using RefreshRateOverlay.WPF.Services;

namespace RefreshRateOverlay.WPF.Tests;

/// <summary>
/// Component tests for OverlayWindow.RefreshLiveState/RefreshGsyncLiveState —
/// the methods TrayApp's HardwareChange reconcilers push into. Constructs a
/// real OverlayWindow (never Show()n) on an STA thread; asserts on real
/// control/dot state via the internal accessors (RateSync/HdrSync/GsyncSync),
/// reachable through InternalsVisibleTo.
/// </summary>
public class OverlayWindowLiveRefreshTests
{
    private static OverlayWindow CreateWindow(
        int preselectRate = 60,
        bool hdrSupported = true,
        bool hdrEnabled = false,
        bool hasProfile = false,
        GsyncGlobalMode? gsyncMode = null,
        int storedRate = 60,
        bool storedHdr = false) =>
        new OverlayWindow(
            activeApp: "Desktop", // avoids RunPresentationCheckAsync (PresentMon.exe)
            availableRates: new List<int> { 60, 100, 120 },
            preselectRate: preselectRate,
            hdrSupported: hdrSupported,
            hdrEnabled: hdrEnabled,
            hasProfile: hasProfile,
            windowMode: WindowMode.Windowed,
            preselectDsxProfile: null,
            gsyncMode: gsyncMode,
            storedRate: storedRate,
            storedHdr: storedHdr);

    [Fact]
    public void RefreshLiveState_RateUntouched_FollowsNewBaseline() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(preselectRate: 60, storedRate: 60);

        window.RefreshLiveState(currentRate: 100, currHdr: false);

        Assert.Equal(100, window.SelectedRate);
        Assert.Equal(100, window.RateSync.Stored);
        Assert.Equal(Brushes.LimeGreen.Color, ((SolidColorBrush)window.RateSyncDot.Fill).Color);
    });

    [Fact]
    public void RefreshLiveState_RateTouched_KeepsSelectionButUpdatesBaselineAndDot() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(preselectRate: 60, storedRate: 60);

        // Simulate the user picking a different rate before any background sync arrives.
        SetRateSelection(window, 120);
        Assert.True(window.RateSync.Touched);

        window.RefreshLiveState(currentRate: 100, currHdr: false);

        Assert.Equal(120, window.SelectedRate); // untouched by the background push
        Assert.Equal(100, window.RateSync.Stored); // baseline still moved
        Assert.Equal(Brushes.Red.Color, ((SolidColorBrush)window.RateSyncDot.Fill).Color); // 120 != 100
    });

    [Fact]
    public void RefreshLiveState_HdrUntouched_FollowsNewBaseline() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(hdrSupported: true, hdrEnabled: false, storedHdr: false);

        window.RefreshLiveState(currentRate: 60, currHdr: true);

        Assert.True(window.HdrEnabled);
        Assert.True(window.HdrSync.Stored);
    });

    [Fact]
    public void RefreshLiveState_HdrUnsupported_NeverThrows_DotStaysInactive() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(hdrSupported: false);

        // Should be a safe no-op for HDR (row hidden) — must not throw.
        window.RefreshLiveState(currentRate: 60, currHdr: true);

        Assert.False(window.HdrEnabled);
    });

    [Fact]
    public void RefreshGsyncLiveState_RowNeverShown_IsNoOp() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(gsyncMode: null);

        // Must not throw even though no G-SYNC row/dot exists.
        window.RefreshGsyncLiveState(GsyncGlobalMode.FullscreenAndWindowed);

        Assert.Null(window.SelectedGsyncMode);
    });

    [Fact]
    public void RefreshGsyncLiveState_Untouched_FollowsNewBaseline() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(gsyncMode: GsyncGlobalMode.Disabled);

        window.RefreshGsyncLiveState(GsyncGlobalMode.FullscreenAndWindowed);

        Assert.Equal(GsyncGlobalMode.FullscreenAndWindowed, window.SelectedGsyncMode);
        Assert.Equal(GsyncGlobalMode.FullscreenAndWindowed, window.GsyncSync!.Stored);
    });

    [Fact]
    public void RefreshGsyncLiveState_Touched_KeepsSelectionButUpdatesBaseline() => StaTestHelper.Run(() =>
    {
        var window = CreateWindow(gsyncMode: GsyncGlobalMode.Disabled);
        window.GsyncSync!.MarkTouched();

        window.RefreshGsyncLiveState(GsyncGlobalMode.FullscreenAndWindowed);

        Assert.Equal(GsyncGlobalMode.Disabled, window.SelectedGsyncMode); // untouched selection preserved
        Assert.Equal(GsyncGlobalMode.FullscreenAndWindowed, window.GsyncSync!.Stored); // baseline still moved
    });

    // -------------------------------------------------------------------------
    // Helpers — reach the XAML-named ComboBox/Ellipse fields (internal by
    // WPF's default field modifier, visible here via InternalsVisibleTo) to
    // drive a "user edit" the same way the real control would.
    // -------------------------------------------------------------------------

    private static void SetRateSelection(OverlayWindow window, int rate)
    {
        var dropdown = window.RateDropDown;
        for (int i = 0; i < dropdown.Items.Count; i++)
        {
            if (dropdown.Items[i] is string s && s == $"{rate} Hz")
            {
                dropdown.SelectedIndex = i;
                return;
            }
        }
        throw new InvalidOperationException($"{rate} Hz not present in RateDropDown for this test's availableRates.");
    }
}
