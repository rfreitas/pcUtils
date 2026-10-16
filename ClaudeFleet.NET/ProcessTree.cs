using System.IO;
using System.Text.RegularExpressions;

namespace ClaudeFleet;

/// <summary>
/// Builds the process view under a session's <c>claude.exe</c>. Claude wraps every shell command in launcher
/// processes (cmd /c chcp, pwsh launcher, bash -c snapshot); those are hidden so the tree shows the real programs.
/// Pure: all inputs are passed in, so it is tested with fabricated snapshots.
/// </summary>
internal static class ProcessTree
{
    private const int MaxLabel = 140;

    public static IReadOnlyList<ProcView> Build(
        int rootPid, IReadOnlyList<ProcEntry> entries, IReadOnlyDictionary<int, double> cpu,
        Func<ProcEntry, string?> commandLine, ISet<int> sessionPids)
    {
        var kids = ChildMap(entries);

        IEnumerable<ProcView> Children(int pid, bool underMcp) =>
            !kids.TryGetValue(pid, out var list) ? []
            : list.Where(c => !IsNoise(c) && !sessionPids.Contains(c.Pid)).OrderBy(c => c.CreateTime).SelectMany(c => Make(c, underMcp));

        IEnumerable<ProcView> Make(ProcEntry e, bool underMcp)
        {
            string? cmd = commandLine(e);
            if (IsWrapper(e, cmd))
            {
                var inner = Children(e.Pid, underMcp).ToList();
                if (inner.Count > 0) return inner;
                // Its only job was to start another session (a headless run): that session is shown on its own.
                if (Subtree(e.Pid, kids, new HashSet<int>()).Any(c => sessionPids.Contains(c.Pid))) return [];
                return [new ProcView(e.Pid, WrapperLabel(e, cmd), Cpu(e.Pid), e.WorkingSet, underMcp, [])];
            }
            string label = Label(e.Name, cmd);
            bool mcp = underMcp || IsMcp(e.Name, cmd);
            return [new ProcView(e.Pid, label, Cpu(e.Pid), e.WorkingSet, mcp, Children(e.Pid, mcp).ToList())];
        }

        double Cpu(int pid) => cpu.TryGetValue(pid, out var v) ? v : 0;
        return Children(rootPid, false).ToList();
    }

    /// <summary>Parent PID to children. A parent must be older than its child, otherwise the PID was reused and the link is not real.</summary>
    public static Dictionary<int, List<ProcEntry>> ChildMap(IReadOnlyList<ProcEntry> entries)
    {
        var byPid = new Dictionary<int, ProcEntry>(entries.Count);
        foreach (var e in entries) byPid.TryAdd(e.Pid, e);
        var kids = new Dictionary<int, List<ProcEntry>>();
        foreach (var e in entries)
            if (e.ParentPid != e.Pid && byPid.TryGetValue(e.ParentPid, out var parent) && parent.CreateTime <= e.CreateTime)
                (kids.TryGetValue(e.ParentPid, out var l) ? l : kids[e.ParentPid] = new()).Add(e);
        return kids;
    }

    /// <summary>Every process below <paramref name="rootPid"/>, not descending into other sessions (they are counted on their own).</summary>
    public static IEnumerable<ProcEntry> Subtree(int rootPid, IReadOnlyDictionary<int, List<ProcEntry>> kids, ISet<int> sessionPids)
    {
        var stack = new Stack<int>();
        stack.Push(rootPid);
        while (stack.Count > 0)
            if (kids.TryGetValue(stack.Pop(), out var list))
                foreach (var c in list)
                {
                    if (sessionPids.Contains(c.Pid)) continue;
                    yield return c;
                    stack.Push(c.Pid);
                }
    }

    /// <summary>Console hosts carry no information.</summary>
    public static bool IsNoise(ProcEntry e) => e.Name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Launcher processes Claude puts around every shell command, plus the rustup proxy in front of cargo.</summary>
    public static bool IsWrapper(ProcEntry e, string? cmd)
    {
        string n = e.Name.ToLowerInvariant();
        cmd ??= "";
        return n switch
        {
            "cmd.exe"                    => cmd.Contains("chcp", StringComparison.OrdinalIgnoreCase) && cmd.Contains("65001"),
            "pwsh.exe" or "powershell.exe" => cmd.Contains("__claudeCodeScript", StringComparison.Ordinal),
            "bash.exe"                   => cmd.Contains("shell-snapshots", StringComparison.OrdinalIgnoreCase),
            "rustup.exe"                 => true,
            _                            => false,
        };
    }

    public static bool IsMcp(string name, string? cmd) =>
        name.Contains("mcp", StringComparison.OrdinalIgnoreCase) || (cmd?.Contains(" mcp", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>What a wrapper with nothing under it is running (e.g. a bash builtin or a pwsh cmdlet).</summary>
    private static string WrapperLabel(ProcEntry e, string? cmd)
    {
        if (e.Name.Equals("bash.exe", StringComparison.OrdinalIgnoreCase) && BashCommand(cmd) is { } b) return Shorten(b);
        return e.Name.StartsWith("pwsh", StringComparison.OrdinalIgnoreCase) ? "pwsh command" : e.Name.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The user's command inside Claude's <c>bash -c "…eval '&lt;command&gt;' &lt; /dev/null && pwd -P …"</c> wrapper.</summary>
    public static string? BashCommand(string? cmdline)
    {
        if (cmdline is null) return null;
        int start = cmdline.IndexOf("eval '", StringComparison.Ordinal);
        if (start < 0) return null;
        start += "eval '".Length;
        int end = cmdline.IndexOf("' < /dev/null", start, StringComparison.Ordinal);
        string cmd = (end < 0 ? cmdline[start..] : cmdline[start..end]).Trim();
        // Drop the leading "cd <dir> && " Claude adds so the program name comes first.
        cmd = Regex.Replace(cmd, @"^cd\s+\S+\s*&&\s*", "");
        return cmd.Length == 0 ? null : cmd;
    }

    /// <summary>"cargo build --release …" from a full command line: exe path and extension removed, whitespace collapsed.</summary>
    public static string Label(string imageName, string? cmdline)
    {
        if (string.IsNullOrWhiteSpace(cmdline)) return imageName;
        string rest;
        string exe;
        string trimmed = cmdline.Trim();
        if (trimmed[0] == '"')
        {
            int close = trimmed.IndexOf('"', 1);
            exe = close < 0 ? trimmed[1..] : trimmed[1..close];
            rest = close < 0 ? "" : trimmed[(close + 1)..];
        }
        else
        {
            int sp = trimmed.IndexOf(' ');
            exe = sp < 0 ? trimmed : trimmed[..sp];
            rest = sp < 0 ? "" : trimmed[sp..];
        }
        string baseName = Path.GetFileNameWithoutExtension(exe.Replace('/', '\\'));
        if (baseName.Length == 0) baseName = imageName;

        // rustc invocations are very long; the crate name is what matters.
        var crate = Regex.Match(rest, @"--crate-name\s+(\S+)");
        if (baseName.Equals("rustc", StringComparison.OrdinalIgnoreCase) && crate.Success) return $"rustc {crate.Groups[1].Value}";

        return Shorten(Regex.Replace($"{baseName} {rest}", @"\s+", " ").Trim());
    }

    private static string Shorten(string s)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length <= MaxLabel ? s : s[..(MaxLabel - 1)] + "…";
    }
}
