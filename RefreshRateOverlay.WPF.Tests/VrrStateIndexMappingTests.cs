using RefreshRateOverlay.WPF.Services;

namespace RefreshRateOverlay.WPF.Tests;

/// <summary>
/// Covers OverlayWindow's VrrStateToIndex/IndexToVrrState — VrrAppState.NotSet
/// (0xFFFFFFFE) doesn't sit next to the real driver values (0-4) in NVIDIA's
/// own enum, so AppVrrDropDown's index can't be a raw cast once Not Set gets
/// its own dropdown slot. Regression coverage for exactly that off-by-one:
/// Not Set occupies index 0, the four real states shift up by one.
/// </summary>
public class VrrStateIndexMappingTests
{
    [Theory]
    [InlineData(VrrAppState.NotSet, 0)]
    [InlineData(VrrAppState.Allow, 1)]
    [InlineData(VrrAppState.ForceOff, 2)]
    [InlineData(VrrAppState.DisAllow, 3)]
    [InlineData(VrrAppState.ULMB, 4)]
    [InlineData(VrrAppState.FixedRefresh, 5)]
    public void VrrStateToIndex_MapsEachStateToItsDropdownSlot(VrrAppState state, int expectedIndex) =>
        Assert.Equal(expectedIndex, OverlayWindow.VrrStateToIndex(state));

    [Theory]
    [InlineData(0, VrrAppState.NotSet)]
    [InlineData(1, VrrAppState.Allow)]
    [InlineData(2, VrrAppState.ForceOff)]
    [InlineData(3, VrrAppState.DisAllow)]
    [InlineData(4, VrrAppState.ULMB)]
    [InlineData(5, VrrAppState.FixedRefresh)]
    public void IndexToVrrState_MapsEachDropdownSlotBackToItsState(int index, VrrAppState expectedState) =>
        Assert.Equal(expectedState, OverlayWindow.IndexToVrrState(index));

    [Theory]
    [InlineData(VrrAppState.NotSet)]
    [InlineData(VrrAppState.Allow)]
    [InlineData(VrrAppState.ForceOff)]
    [InlineData(VrrAppState.DisAllow)]
    [InlineData(VrrAppState.ULMB)]
    [InlineData(VrrAppState.FixedRefresh)]
    public void RoundTrip_StateToIndexAndBack_ReturnsOriginalState(VrrAppState state) =>
        Assert.Equal(state, OverlayWindow.IndexToVrrState(OverlayWindow.VrrStateToIndex(state)));
}
