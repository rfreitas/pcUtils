using RefreshRateOverlay.Services;

namespace RefreshRateOverlay.NET.Tests;

public class DisplayServiceTests
{
    [Fact]
    public void GetCurrentRate_ReturnsPositiveValue()
    {
        int rate = DisplayService.GetCurrentRate();
        Assert.True(rate > 0, $"Expected positive rate, got {rate}");
    }

    [Fact]
    public void GetAvailableRates_ReturnsNonEmptyList()
    {
        var rates = DisplayService.GetAvailableRates();
        Assert.NotEmpty(rates);
    }

    [Fact]
    public void GetAvailableRates_ContainsCurrentRate()
    {
        int current = DisplayService.GetCurrentRate();
        var available = DisplayService.GetAvailableRates();
        Assert.Contains(current, available);
    }

    [Fact]
    public void GetAvailableRates_IsSortedDescending()
    {
        var rates = DisplayService.GetAvailableRates();
        for (int i = 1; i < rates.Count; i++)
            Assert.True(rates[i - 1] >= rates[i],
                $"Not descending at index {i}: {rates[i - 1]} < {rates[i]}");
    }

    [Fact]
    public void GetAvailableRates_AllRatesPositive()
    {
        foreach (int r in DisplayService.GetAvailableRates())
            Assert.True(r > 0, $"Rate {r} should be positive");
    }

    // New: round-trip set – only runs if current rate equals itself after re-apply
    // (avoids actually changing monitor during tests by setting to the current value)
    [Fact]
    public void SetRate_CurrentRate_ReturnsTrueWithoutChanging()
    {
        int current = DisplayService.GetCurrentRate();
        // SetRate skips the call if rate == _currentRate... but DisplayService
        // itself is stateless, so calling with the current rate is safe.
        bool ok = DisplayService.SetRate(current);
        Assert.True(ok, "Setting the already-active rate should succeed");
        Assert.Equal(current, DisplayService.GetCurrentRate());
    }
}
