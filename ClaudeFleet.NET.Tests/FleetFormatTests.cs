using ClaudeFleet;
using Xunit;

public class FleetFormatTests
{
    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.9, 0)]
    [InlineData(1.0, 1)]
    [InlineData(4.9, 1)]
    [InlineData(5.0, 2)]
    [InlineData(20.0, 3)]
    [InlineData(40.0, 4)]
    [InlineData(50.0, 5)]
    [InlineData(100.0, 5)]
    public void MeterLevel_buckets_machine_cpu(double cpu, int level) =>
        Assert.Equal(level, FleetFormat.MeterLevel(cpu));

    [Theory]
    [InlineData(5, "5s")]
    [InlineData(150, "2m")]
    [InlineData(3 * 3600 + 5, "3h")]
    [InlineData(8 * 86400, "8d")]
    public void Age_uses_largest_whole_unit(int seconds, string expected) =>
        Assert.Equal(expected, FleetFormat.Age(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(@"C:\Users\me\Repos", "Repos")]
    [InlineData(@"C:\Users\me\Repos\boardvr-bevy", "boardvr-bevy")]
    [InlineData(@"C:\Users\me\Repos\boardvr-bevy\.claude\worktrees\board-yaw-view", "boardvr-bevy \u2387 board-yaw-view")]
    [InlineData(@"D:\elsewhere\proj", @"D:\elsewhere\proj")]
    public void ShortCwd_is_relative_to_repos_and_names_worktrees(string cwd, string expected) =>
        Assert.Equal(expected, FleetFormat.ShortCwd(cwd, @"C:\Users\me\Repos\"));

    [Fact]
    public void Memory_switches_to_gigabytes()
    {
        Assert.Equal("840 MB", FleetFormat.Memory(840L << 20));
        Assert.Equal("1.5 GB", FleetFormat.Memory(1536L << 20));
    }

    [Fact]
    public void Tooltip_fits_notifyicon_limit_and_names_the_busiest_session()
    {
        var snap = new MockFleet().Snapshot();
        string tip = FleetFormat.Tooltip(snap);
        Assert.True(tip.Length <= 127, tip);
        Assert.Contains("boardvr-bevy-f8", tip);
        Assert.Contains("(mock)", tip);
    }

    [Fact]
    public void Snapshot_counts_include_headless_children()
    {
        var snap = new MockFleet().Snapshot();
        Assert.Equal(10, snap.Count);          // 8 registered top-level + 2 headless
        Assert.Equal(4, snap.Busy);            // boardvr-bevy-f8, repos-5e, 2 headless
        Assert.Equal(3, snap.RunningAgents);   // Explore + 2 busy headless sessions
    }
}
