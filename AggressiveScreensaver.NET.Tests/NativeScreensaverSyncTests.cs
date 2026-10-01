using AggressiveScreensaver.Services;

namespace AggressiveScreensaver.NET.Tests;

public class NativeScreensaverSyncTests
{
    /// <summary>Fake OS: a screensaver flag + timeout, recording every write.</summary>
    private sealed class FakeOs
    {
        public bool Active;
        public int  Timeout = 60;
        public readonly List<int> Writes = new();
        public bool SetSucceeds = true;

        public NativeScreensaverSync Sync(List<string>? log = null) => new(
            () => Active, () => Timeout,
            sec => { Writes.Add(sec); if (SetSucceeds) Timeout = sec; return SetSucceeds; },
            log is null ? null : log.Add);
    }

    [Fact]
    public void ThresholdChange_WhileNativeIsOn_WritesTheTimeout()
    {
        var os = new FakeOs { Active = true, Timeout = 60 };

        os.Sync().OnThresholdChanged(120);

        Assert.Equal([120], os.Writes);
        Assert.Equal(120, os.Timeout);
    }

    [Fact]
    public void ThresholdChange_WhileNativeIsOff_LeavesWindowsAlone()
    {
        var os = new FakeOs { Active = false, Timeout = 60 };

        os.Sync().OnThresholdChanged(120);

        Assert.Empty(os.Writes);
        Assert.Equal(60, os.Timeout);
    }

    [Fact]
    public void Enabling_MatchesTheTimeoutEvenThoughTheFlagIsNotYetReflected()
    {
        var os = new FakeOs { Active = false, Timeout = 600 };

        os.Sync().OnEnabled(30);

        Assert.Equal([30], os.Writes);
    }

    [Fact]
    public void AlreadyInSync_DoesNotWriteTheOsAgain()
    {
        var os = new FakeOs { Active = true, Timeout = 60 };

        os.Sync().OnThresholdChanged(60);
        os.Sync().OnEnabled(60);

        Assert.Empty(os.Writes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveThreshold_IsIgnored(int threshold)
    {
        var os = new FakeOs { Active = true, Timeout = 60 };

        os.Sync().OnThresholdChanged(threshold);

        Assert.Empty(os.Writes);
    }

    [Fact]
    public void FailedWrite_IsLoggedAsFailed()
    {
        var log = new List<string>();
        var os = new FakeOs { Active = true, Timeout = 60, SetSucceeds = false };

        os.Sync(log).OnThresholdChanged(120);

        Assert.Contains(log, l => l.Contains("120s") && l.Contains("failed"));
    }

    [Fact]
    public void SuccessfulWrite_IsLoggedWithoutFailure()
    {
        var log = new List<string>();
        var os = new FakeOs { Active = true, Timeout = 60 };

        os.Sync(log).OnThresholdChanged(120);

        Assert.Contains(log, l => l.Contains("120s") && !l.Contains("failed"));
    }

    [Fact]
    public void RepeatedSliderSteps_OnlyWriteWhenTheValueActuallyChanges()
    {
        var os = new FakeOs { Active = true, Timeout = 60 };
        var sync = os.Sync();

        sync.OnThresholdChanged(120);
        sync.OnThresholdChanged(120);
        sync.OnThresholdChanged(180);

        Assert.Equal([120, 180], os.Writes);
    }
}
