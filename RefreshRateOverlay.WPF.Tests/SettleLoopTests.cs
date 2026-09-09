using System.Threading;
using System.Threading.Tasks;
using RefreshRateOverlay.WPF.Services;

namespace RefreshRateOverlay.WPF.Tests;

/// <summary>
/// Covers SettleLoop.RunAsync — the generic push-then-confirm loop shared by
/// TrayApp's Rate/HDR settle (SettleAsync) and G-SYNC's enforcement path
/// (EnforceGsyncAsync). `delay` is always stubbed to complete instantly so
/// these run fast regardless of the windowMs/pollMs passed in — only call
/// counts and the return value are under test, never real timing.
/// </summary>
public class SettleLoopTests
{
    private static Task NoDelay(int ms, CancellationToken ct) => Task.CompletedTask;

    [Fact]
    public async Task ConvergesOnFirstPoll_ReturnsTrue_NeverPushes()
    {
        int pushCount = 0;
        bool result = await SettleLoop.RunAsync(
            isDrifted: () => false,
            push: () => pushCount++,
            CancellationToken.None, windowMs: 1000, pollMs: 100, delay: NoDelay);

        Assert.True(result);
        Assert.Equal(0, pushCount);
    }

    [Fact]
    public async Task DriftsTwiceThenConverges_ReturnsTrue_PushesTwice()
    {
        int checks = 0, pushCount = 0;
        bool result = await SettleLoop.RunAsync(
            isDrifted: () => ++checks <= 2, // drifted for the first 2 checks, converged on the 3rd
            push: () => pushCount++,
            CancellationToken.None, windowMs: 1000, pollMs: 100, delay: NoDelay);

        Assert.True(result);
        Assert.Equal(2, pushCount);
        Assert.Equal(3, checks);
    }

    [Fact]
    public async Task NeverConverges_ReturnsFalse_StopsAtWindowEdge()
    {
        int pushCount = 0;
        bool result = await SettleLoop.RunAsync(
            isDrifted: () => true,
            push: () => pushCount++,
            CancellationToken.None, windowMs: 500, pollMs: 100, delay: NoDelay);

        Assert.False(result);
        Assert.Equal(5, pushCount); // 5 loop iterations (500/100) each push once, final check pushes nothing more
    }

    [Fact]
    public async Task AlreadyCancelledToken_ReturnsFalse_NeverChecksOrPushes()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        bool isDriftedCalled = false;
        bool result = await SettleLoop.RunAsync(
            isDrifted: () => { isDriftedCalled = true; return true; },
            push: () => Assert.Fail("push should never be called"),
            cts.Token, windowMs: 1000, pollMs: 100, delay: NoDelay);

        Assert.False(result);
        Assert.False(isDriftedCalled);
    }

    [Fact]
    public async Task DelayThrowsOperationCanceled_ReturnsFalse()
    {
        bool result = await SettleLoop.RunAsync(
            isDrifted: () => true,
            push: () => Assert.Fail("push should never be called"),
            CancellationToken.None, windowMs: 1000, pollMs: 100,
            delay: (_, _) => throw new TaskCanceledException());

        Assert.False(result);
    }

    [Fact]
    public async Task DefaultDelay_ActuallyWaits()
    {
        // No delay override — exercises the real Task.Delay path with tiny
        // real timings, confirming the default parameter itself works.
        int pushCount = 0;
        bool result = await SettleLoop.RunAsync(
            isDrifted: () => pushCount == 0,
            push: () => pushCount++,
            CancellationToken.None, windowMs: 200, pollMs: 10);

        Assert.True(result);
        Assert.Equal(1, pushCount);
    }
}
