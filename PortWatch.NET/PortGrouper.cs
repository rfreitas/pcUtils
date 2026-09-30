namespace PortWatch;

internal readonly record struct PortEntry(string Protocol, int Port, int Pid, string ProcessName, bool IsSystem = false);

/// <summary>A run of ports shown as one token: "135", or "49331-49333" (Ports holds every port in it).</summary>
internal sealed record PortSegment(string Text, IReadOnlyList<int> Ports);

/// <summary>One line per process: its PIDs and the port sets it is bound to.</summary>
internal sealed record ProcessRow(
    string Name, IReadOnlyList<int> Pids, bool IsSystem,
    IReadOnlyList<PortSegment> Tcp, IReadOnlyList<PortSegment> Udp);

/// <summary>
/// Pure grouping logic. Each process appears exactly once (same-name processes such as
/// several svchost instances collapse into one row with all PIDs), and its ports are
/// collapsed into ranges so the list stays short.
/// </summary>
internal static class PortGrouper
{
    public static IReadOnlyList<ProcessRow> Group(IEnumerable<PortEntry> entries) =>
        entries
            .GroupBy(e => e.ProcessName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProcessRow(
                g.Key,
                g.Select(e => e.Pid).Distinct().Order().ToList(),
                g.Any(e => e.IsSystem),
                Segments(g.Where(e => e.Protocol == "TCP").Select(e => e.Port)),
                Segments(g.Where(e => e.Protocol == "UDP").Select(e => e.Port))))
            .ToList();

    /// <summary>Sorted, de-duplicated; runs of 3+ consecutive ports become one "a-b" segment.</summary>
    public static IReadOnlyList<PortSegment> Segments(IEnumerable<int> ports)
    {
        var sorted = ports.Distinct().Order().ToList();
        var result = new List<PortSegment>();
        for (int i = 0; i < sorted.Count;)
        {
            int j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            if (j - i >= 2)
                result.Add(new PortSegment($"{sorted[i]}-{sorted[j]}", sorted.GetRange(i, j - i + 1)));
            else
                for (int k = i; k <= j; k++) result.Add(new PortSegment(sorted[k].ToString(), [sorted[k]]));
            i = j + 1;
        }
        return result;
    }

    public static string FormatPorts(IEnumerable<int> ports) => string.Join(", ", Segments(ports).Select(s => s.Text));

    /// <summary>For each (protocol, port): how many distinct processes are bound to it.</summary>
    public static Dictionary<(string Protocol, int Port), int> ProcessCounts(IEnumerable<ProcessRow> rows)
    {
        var counts = new Dictionary<(string, int), int>();
        foreach (var row in rows)
            foreach (var (proto, segs) in new[] { ("TCP", row.Tcp), ("UDP", row.Udp) })
                foreach (int port in segs.SelectMany(s => s.Ports))
                    counts[(proto, port)] = counts.GetValueOrDefault((proto, port)) + 1;
        return counts;
    }

    /// <summary>How many OTHER processes share the most-shared port in this segment.</summary>
    public static int OtherSharers(string protocol, PortSegment seg, Dictionary<(string Protocol, int Port), int> counts) =>
        seg.Ports.Max(p => counts.GetValueOrDefault((protocol, p), 1)) - 1;
}
