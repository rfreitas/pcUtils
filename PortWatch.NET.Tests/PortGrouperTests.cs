using PortWatch;
using Xunit;

public class PortGrouperTests
{
    [Fact]
    public void Process_bound_on_v4_and_v6_is_listed_once()
    {
        var row = Assert.Single(PortGrouper.Group([
            new("TCP", 135, 2432, "svchost"),
            new("TCP", 135, 2432, "svchost"),
        ]));
        Assert.Equal([2432], row.Pids);
        Assert.Equal("135", PortGrouper.FormatPorts(row.Tcp.SelectMany(x => x.Ports)));
    }

    [Fact]
    public void Same_name_different_pids_collapse_to_one_row()
    {
        var row = Assert.Single(PortGrouper.Group([
            new("UDP", 5353, 1, "chrome"),
            new("UDP", 5355, 2, "Chrome"),
        ]));
        Assert.Equal([1, 2], row.Pids);
        Assert.Equal("5353, 5355", string.Join(", ", row.Udp.Select(x => x.Text)));
    }

    [Fact]
    public void A_process_appears_once_even_with_many_ports()
    {
        var rows = PortGrouper.Group(Enumerable.Range(49664, 7).Select(p => new PortEntry("TCP", p, 9, "svchost")));
        Assert.Equal("49664-49670", Assert.Single(Assert.Single(rows).Tcp).Text);
    }

    [Theory]
    [InlineData("135, 445", new[] { 135, 445 })]
    [InlineData("4-6", new[] { 6, 4, 5 })]
    [InlineData("1, 2", new[] { 1, 2 })]
    [InlineData("1-3, 9", new[] { 1, 2, 3, 9 })]
    [InlineData("", new int[0])]
    public void Ports_collapse_into_ranges(string expected, int[] ports) =>
        Assert.Equal(expected, PortGrouper.FormatPorts(ports));

    [Fact]
    public void Tcp_and_udp_stay_separate_on_one_row()
    {
        var row = Assert.Single(PortGrouper.Group([new("TCP", 3389, 9, "svchost"), new("UDP", 3389, 9, "svchost")]));
        Assert.Equal("3389", Assert.Single(row.Tcp).Text);
        Assert.Equal("3389", Assert.Single(row.Udp).Text);
    }

    [Fact]
    public void Rows_are_sorted_by_name()
    {
        var rows = PortGrouper.Group([new("TCP", 445, 4, "System"), new("TCP", 135, 5, "svchost"), new("TCP", 1, 6, "Apple")]);
        Assert.Equal(["Apple", "svchost", "System"], rows.Select(r => r.Name));
    }
}

public class SharingTests
{
    private static IReadOnlyList<ProcessRow> Rows() => PortGrouper.Group([
        new("UDP", 5353, 1, "chrome"),
        new("UDP", 5353, 2, "mDNSResponder"),
        new("UDP", 5353, 3, "simpro3"),
        new("TCP", 80, 4, "nginx"),
        new("TCP", 80, 5, "apache"),
        new("TCP", 9000, 6, "solo"),
    ]);

    [Fact]
    public void Counts_distinct_processes_per_port()
    {
        var counts = PortGrouper.ProcessCounts(Rows());
        Assert.Equal(3, counts[("UDP", 5353)]);
        Assert.Equal(1, counts[("TCP", 9000)]);
    }

    [Fact]
    public void OtherSharers_excludes_self()
    {
        var rows = Rows();
        var counts = PortGrouper.ProcessCounts(rows);
        var chrome = rows.Single(r => r.Name == "chrome");
        Assert.Equal(2, PortGrouper.OtherSharers("UDP", chrome.Udp[0], counts));
        Assert.Equal(0, PortGrouper.OtherSharers("TCP", rows.Single(r => r.Name == "solo").Tcp[0], counts));
    }

    [Fact]
    public void Same_name_processes_do_not_count_as_sharing()
    {
        var rows = PortGrouper.Group([new("UDP", 5353, 1, "chrome"), new("UDP", 5353, 2, "chrome")]);
        Assert.Equal(0, PortGrouper.OtherSharers("UDP", rows[0].Udp[0], PortGrouper.ProcessCounts(rows)));
    }

    [Fact]
    public void System_flag_propagates_to_the_row()
    {
        var rows = PortGrouper.Group([new("TCP", 135, 1, "svchost", IsSystem: true), new("TCP", 80, 2, "nginx")]);
        Assert.True(rows.Single(r => r.Name == "svchost").IsSystem);
        Assert.False(rows.Single(r => r.Name == "nginx").IsSystem);
    }

    [Theory]
    [InlineData("svchost", null, true)]
    [InlineData("lsass", null, true)]
    [InlineData("whatever", @"C:\Windows\System32oo.exe", true)]
    [InlineData("chrome", @"C:\Program Files\Google\Chrome\chrome.exe", false)]
    [InlineData("chrome", null, false)]
    public void Detects_system_processes(string name, string? path, bool expected) =>
        Assert.Equal(expected, PortScanner.IsSystemProcess(name, path));
}

public class PortScannerTests
{
    [Fact]
    public void Scan_finds_rpc_endpoint_mapper_on_135()
    {
        Assert.Contains(PortScanner.Scan(), e => e.Protocol == "TCP" && e.Port == 135 && e.ProcessName == "svchost");
    }
}
