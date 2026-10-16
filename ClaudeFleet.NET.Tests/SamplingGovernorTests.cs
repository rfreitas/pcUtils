using ClaudeFleet;
using Xunit;

public class SamplingGovernorTests
{
    private static FleetSnapshot Fleet(bool busy, double cpu) => new(
    [
        new SessionView(1, "s", "s", @"C:\r", busy ? SessionStatus.Busy : SessionStatus.Idle, TimeSpan.Zero, false, null, cpu, 0, [], [], []),
    ], IsMock: false);

    [Fact]
    public void Idle_and_quiet_from_the_start_never_runs()
    {
        var g = new SamplingGovernor();
        Assert.False(g.Observe(Fleet(busy: false, cpu: 0.2)));
        Assert.False(g.Running);
    }

    [Fact]
    public void A_busy_session_starts_it_and_two_quiet_samples_stop_it()
    {
        var g = new SamplingGovernor();
        Assert.True(g.Observe(Fleet(busy: true, cpu: 0)));
        Assert.True(g.Observe(Fleet(busy: false, cpu: 0.5)));    // first quiet sample: keep going (a build may be ramping)
        Assert.False(g.Observe(Fleet(busy: false, cpu: 0.5)));   // second: stop
    }

    [Fact]
    public void Cpu_alone_keeps_it_running_after_the_session_goes_idle()
    {
        var g = new SamplingGovernor();
        g.Observe(Fleet(busy: true, cpu: 0));
        for (int i = 0; i < 10; i++)
            Assert.True(g.Observe(Fleet(busy: false, cpu: 12)));   // background cargo build
        Assert.True(g.Observe(Fleet(busy: false, cpu: 0)));
        Assert.False(g.Observe(Fleet(busy: false, cpu: 0)));
    }

    [Fact]
    public void Activity_between_quiet_samples_resets_the_count()
    {
        var g = new SamplingGovernor();
        g.Observe(Fleet(busy: true, cpu: 0));
        g.Observe(Fleet(busy: false, cpu: 0));
        Assert.True(g.Observe(Fleet(busy: true, cpu: 0)));
        Assert.True(g.Observe(Fleet(busy: false, cpu: 0)));
        Assert.False(g.Observe(Fleet(busy: false, cpu: 0)));
    }

    [Fact]
    public void A_watcher_event_restarts_it_after_it_stopped()
    {
        var g = new SamplingGovernor();
        g.Observe(Fleet(busy: true, cpu: 0));
        g.Observe(Fleet(busy: false, cpu: 0));
        g.Observe(Fleet(busy: false, cpu: 0));
        Assert.False(g.Running);
        Assert.True(g.Observe(Fleet(busy: true, cpu: 0)));
    }
}
