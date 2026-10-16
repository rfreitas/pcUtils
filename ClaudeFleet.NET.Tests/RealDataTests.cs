using System.Diagnostics;
using System.IO;
using ClaudeFleet;
using Xunit;

/// <summary>Temp folder that cleans itself up.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClaudeFleetTests-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string Write(string relative, string content)
    {
        string full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public class SessionRegistryTests
{
    private const string Sample = """
        {"pid":49752,"sessionId":"b77a76cc-0036-4627-9c61-6587dc12b242","cwd":"C:\\Users\\ricfr\\Repos","startedAt":1792176972707,
         "procStart":"134366505681072514","version":"2.1.301","kind":"interactive","entrypoint":"cli",
         "messagingSocketPath":"\\\\.\\pipe\\LOCAL\\cc-msg-83a6","name":"repos-5e","status":"busy"}
        """;

    [Fact]
    public void Parse_reads_the_fields_the_app_needs()
    {
        var s = SessionRegistry.Parse(Sample)!;
        Assert.Equal(49752, s.Pid);
        Assert.Equal("repos-5e", s.Name);
        Assert.Equal(@"C:\Users\ricfr\Repos", s.Cwd);
        Assert.Equal(134366505681072514L, s.ProcStart);
        Assert.Equal(SessionStatus.Busy, s.Status);
        Assert.False(s.Headless);
    }

    [Theory]
    [InlineData("busy", "Busy")]
    [InlineData("shell", "Busy")]
    [InlineData("idle", "Idle")]
    [InlineData("something-new", "Idle")]
    public void Status_maps_known_values_and_defaults_to_idle(string raw, string expected) =>
        Assert.Equal(Enum.Parse<SessionStatus>(expected), SessionRegistry.Parse(Sample.Replace("\"busy\"", $"\"{raw}\""))!.Status);

    [Fact]
    public void Sdk_entrypoint_is_headless() =>
        Assert.True(SessionRegistry.Parse(Sample.Replace("\"cli\"", "\"sdk-cli\""))!.Headless);

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"name\":\"no pid\"}")]
    public void Parse_returns_null_for_unusable_files(string json) => Assert.Null(SessionRegistry.Parse(json));

    [Fact]
    public void Parse_tolerates_missing_optional_fields()
    {
        var s = SessionRegistry.Parse("{\"pid\":7,\"sessionId\":\"abcdef12-0000\"}")!;
        Assert.Equal("abcdef12", s.Name);
        Assert.Equal(SessionStatus.Idle, s.Status);
        Assert.Equal(0, s.ProcStart);
    }

    [Fact]
    public void ReadAll_keeps_only_sessions_whose_process_is_still_the_one_that_wrote_the_file()
    {
        using var dir = new TempDir();
        dir.Write("1.json", Sample.Replace("49752", "1").Replace("134366505681072514", "1000000000"));      // alive, same start
        dir.Write("2.json", Sample.Replace("49752", "2").Replace("134366505681072514", "1000000000"));      // PID reused (other start)
        dir.Write("3.json", Sample.Replace("49752", "3").Replace("134366505681072514", "1000000000"));      // process gone
        dir.Write("4.json", Sample.Replace("49752", "4").Replace("\"procStart\":\"134366505681072514\",", "")); // no procStart: PID check only
        dir.Write("1.abc123.key", "secret");
        dir.Write("junk.json", "{{{");

        long? Created(int pid) => pid switch { 1 => 1000000000L, 2 => 2000000000L + TimeSpan.TicksPerMinute, 4 => 5, _ => null };
        var found = SessionRegistry.ReadAll(dir.Path, Created).Select(s => s.Pid).OrderBy(p => p).ToArray();
        Assert.Equal(new[] { 1, 4 }, found);
    }
}

public class CpuSamplerTests
{
    private static ProcEntry P(int pid, long cpu, long create = 100) => new(pid, 1, "x.exe", create, cpu, 0);
    private const long Sec = TimeSpan.TicksPerSecond;

    [Fact]
    public void First_sample_has_no_baseline_and_reads_zero()
    {
        var s = new CpuSampler(4);
        Assert.Equal(0, s.Update([P(5, 10 * Sec)], 0)[5]);
    }

    [Fact]
    public void Percent_is_of_the_whole_machine()
    {
        var s = new CpuSampler(4);
        s.Update([P(5, 0)], 0);
        // 2 CPU-seconds in 1 s on 4 logical processors = 50 %
        Assert.Equal(50.0, s.Update([P(5, 2 * Sec)], Sec)[5], 6);
    }

    [Fact]
    public void A_reused_pid_does_not_inherit_the_old_process_history()
    {
        var s = new CpuSampler(2);
        s.Update([P(5, 100 * Sec, create: 100)], 0);
        Assert.Equal(0, s.Update([P(5, 1 * Sec, create: 999)], Sec)[5]);
    }

    [Fact]
    public void A_long_gap_restarts_the_baseline()
    {
        var s = new CpuSampler(1);
        s.Update([P(5, 0)], 0);
        Assert.Equal(0, s.Update([P(5, 50 * Sec)], 120 * Sec)[5]);
        Assert.Equal(100.0, s.Update([P(5, 51 * Sec)], 121 * Sec)[5], 6);
    }

    [Fact]
    public void CPU_time_going_backwards_never_gives_negative_percent()
    {
        var s = new CpuSampler(1);
        s.Update([P(5, 10 * Sec)], 0);
        Assert.Equal(0, s.Update([P(5, 5 * Sec)], Sec)[5]);
    }
}

public class ProcessTreeTests
{
    private static ProcEntry E(int pid, int parent, string name, long ws = 0) => new(pid, parent, name, CreateTime: pid, CpuTime: 0, WorkingSet: ws);
    private const string Chcp = @"C:\WINDOWS\System32\cmd.exe /d /s /c ""C:\WINDOWS\System32\chcp.com"" 65001 >nul & ""pwsh.exe"" -Command ...";
    private const string Launcher = @"""C:\Program Files\PowerShell\7\pwsh.exe"" -NoProfile -Command ""$__claudeCodeScript = $env:X""";

    private static readonly ProcEntry[] Entries =
    [
        E(10, 1, "claude.exe"),
        E(11, 10, "cmd.exe"), E(12, 11, "conhost.exe"), E(13, 11, "pwsh.exe"),
        E(14, 13, "rustup.exe"), E(15, 14, "cargo.exe"), E(16, 15, "rustc.exe", ws: 500),
        E(20, 10, "cmd.exe"), E(21, 20, "Whiteboard.exe"),
        E(30, 10, "cmd.exe"), E(31, 30, "pwsh.exe"), E(32, 31, "claude.exe"),   // a headless run: shown as its own session
        E(40, 10, "bash.exe"),
    ];

    private static string? Cmd(ProcEntry e) => e.Pid switch
    {
        11 or 30 => Chcp,
        13 or 31 => Launcher,
        14 => @"""C:\Users\x\.cargo\bin\cargo.exe"" build --release --bin fixture_viewer",
        15 => @"""C:\Users\x\.rustup\toolchains\stable\bin\cargo.exe"" build --release --bin fixture_viewer",
        16 => @"""C:\Users\x\.rustup\toolchains\stable\bin\rustc.exe"" --crate-name bevy_pbr --edition=2021 src/lib.rs",
        20 => "cmd /d /c whiteboard mcp",
        21 => @"""C:\Apps\Whiteboard.exe"" mcp",
        40 => @"""C:\Program Files\Git\bin\bash.exe"" -c ""source /c/Users/x/.claude/shell-snapshots/s.sh && eval 'cd /c/repo && sleep 100' < /dev/null && pwd -P >| /tmp/cwd""",
        _ => null,
    };

    private static IReadOnlyList<ProcView> Tree() =>
        ProcessTree.Build(10, Entries, new Dictionary<int, double> { [16] = 4.0, [15] = 0.5 }, Cmd, new HashSet<int> { 10, 32 });

    [Fact]
    public void Launcher_wrappers_and_console_hosts_are_hidden_and_other_sessions_excluded()
    {
        var roots = Tree();
        Assert.Equal(new[] { "cargo build --release --bin fixture_viewer", "cmd /d /c whiteboard mcp", "sleep 100" }, roots.Select(r => r.Label).ToArray());
        Assert.DoesNotContain(roots, r => r.Pid is 11 or 13 or 14 or 30 or 31 or 32);
    }

    [Fact]
    public void Children_keep_their_cpu_and_the_tree_sums_up()
    {
        var cargo = Tree()[0];
        Assert.Equal(15, cargo.Pid);
        var rustc = Assert.Single(cargo.Children);
        Assert.Equal("rustc bevy_pbr", rustc.Label);
        Assert.Equal(4.0, rustc.CpuPercent);
        Assert.Equal(4.5, cargo.TreeCpu, 6);
        Assert.Equal(500, cargo.TreeMemory);
    }

    [Fact]
    public void Mcp_servers_are_flagged_with_their_children()
    {
        var mcp = Tree()[1];
        Assert.True(mcp.IsMcp);
        Assert.True(Assert.Single(mcp.Children).IsMcp);
        Assert.False(Tree()[0].IsMcp);
    }

    [Fact]
    public void A_pid_reused_by_an_older_looking_parent_is_not_a_link()
    {
        var entries = new[] { new ProcEntry(10, 1, "claude.exe", 5, 0, 0), new ProcEntry(50, 10, "x.exe", 1 /* older than parent */, 0, 0) };
        Assert.Empty(ProcessTree.Build(10, entries, new Dictionary<int, double>(), _ => null, new HashSet<int> { 10 }));
    }

    [Theory]
    [InlineData(@"""C:\a b\cargo.exe"" run --release", "cargo.exe", "cargo run --release")]
    [InlineData(@"C:\tools\python.exe -I scan.py x", "python.exe", "python -I scan.py x")]
    [InlineData(@"""C:\x\rustc.exe"" --crate-name bevy_pbr --edition=2021", "rustc.exe", "rustc bevy_pbr")]
    [InlineData(null, "thing.exe", "thing.exe")]
    public void Label_drops_the_exe_path_and_extension(string? cmd, string image, string expected) =>
        Assert.Equal(expected, ProcessTree.Label(image, cmd));

    [Fact]
    public void Label_truncates_very_long_command_lines() =>
        Assert.True(ProcessTree.Label("x.exe", "x.exe " + new string('a', 500)).Length <= 140);

    [Fact]
    public void BashCommand_extracts_the_eval_body_without_the_cd_prefix() =>
        Assert.Equal("cargo test -p foo", ProcessTree.BashCommand("bash -c \"source s && eval 'cd /c/r && cargo test -p foo' < /dev/null && pwd -P\""));
}

public class AgentReaderTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 20, 0, 0, DateTimeKind.Utc);

    private static void Agent(TempDir d, string cwd, string sid, string id, string meta, TimeSpan age, string? firstLine = null)
    {
        string dir = Path.Combine("proj", AgentReader.Slug(cwd), sid, "subagents");
        d.Write(Path.Combine(dir, $"agent-{id}.meta.json"), meta);
        string jsonl = d.Write(Path.Combine(dir, $"agent-{id}.jsonl"), (firstLine ?? "{}") + "\n{}\n");
        File.SetLastWriteTimeUtc(jsonl, Now - age);
    }

    [Fact]
    public void Slug_replaces_every_non_alphanumeric_like_claude_does()
    {
        Assert.Equal("C--Users-ricfr-Repos", AgentReader.Slug(@"C:\Users\ricfr\Repos"));
        Assert.Equal("C--Users-ricfr-Repos-boardvr-bevy--claude-worktrees-board-yaw-view", AgentReader.Slug(@"C:\Users\ricfr\Repos\boardvr-bevy\.claude\worktrees\board-yaw-view"));
    }

    [Fact]
    public void Running_needs_a_busy_session_a_fresh_transcript_and_no_finished_flag()
    {
        using var d = new TempDir();
        string cwd = @"C:\r";
        Agent(d, cwd, "s1", "run", """{"agentType":"Explore","description":"look","model":"sonnet","requestShape":"background"}""", TimeSpan.FromSeconds(20),
              firstLine: """{"timestamp":"2026-10-16T19:57:00.000Z"}""");
        Agent(d, cwd, "s1", "fin", """{"agentType":"general-purpose","description":"done one","finished":true}""", TimeSpan.FromMinutes(2));
        Agent(d, cwd, "s1", "old", """{"agentType":"x","description":"ancient","finished":true}""", TimeSpan.FromHours(3));

        var agents = AgentReader.Read(Path.Combine(d.Path, "proj"), cwd, "s1", SessionStatus.Busy, Now);
        Assert.Equal(new[] { "run", "fin" }, agents.Select(a => a.AgentId).ToArray());   // running first; the 3 h old one is hidden
        Assert.Equal(AgentState.Running, agents[0].State);
        Assert.True(agents[0].Background);
        Assert.Equal(TimeSpan.FromMinutes(3), agents[0].Elapsed);                // from the first transcript line
        Assert.Equal(AgentState.Done, agents[1].State);
    }

    [Fact]
    public void An_idle_session_has_no_running_agents()
    {
        using var d = new TempDir();
        Agent(d, @"C:\r", "s1", "a", """{"agentType":"Explore","description":"x"}""", TimeSpan.FromSeconds(5));
        var a = Assert.Single(AgentReader.Read(Path.Combine(d.Path, "proj"), @"C:\r", "s1", SessionStatus.Idle, Now));
        Assert.Equal(AgentState.Done, a.State);
    }

    [Fact]
    public void Missing_folder_and_broken_meta_are_not_errors()
    {
        using var d = new TempDir();
        Assert.Empty(AgentReader.Read(Path.Combine(d.Path, "proj"), @"C:\r", "none", SessionStatus.Busy, Now));
        Agent(d, @"C:\r", "s1", "bad", "{{{", TimeSpan.Zero);
        Assert.Empty(AgentReader.Read(Path.Combine(d.Path, "proj"), @"C:\r", "s1", SessionStatus.Busy, Now));
    }
}

public class FleetCollectorTests
{
    private const long Sec = TimeSpan.TicksPerSecond;

    private static ProcEntry E(int pid, int parent, string name, long cpu, long ws = 1 << 20) => new(pid, parent, name, pid * 1000L, cpu, ws);

    private static string Json(int pid, string name, string status, string entry) =>
        $$"""{"pid":{{pid}},"sessionId":"{{pid:x8}}-0000","cwd":"C:\\r","startedAt":1792176972707,"procStart":"{{pid * 1000L}}","entrypoint":"{{entry}}","name":"{{name}}","status":"{{status}}"}""";

    /// <summary>Session A (100) runs cargo (103) under launcher wrappers and a headless session B (200) under A.</summary>
    private static (FleetCollector Collector, Func<int, long> Clock, TempDir Dir) Make()
    {
        var dir = new TempDir();
        dir.Write("sessions\\100.json", Json(100, "alpha", "busy", "cli"));
        dir.Write("sessions\\200.json", Json(200, "agent-b", "busy", "sdk-cli"));
        dir.Write("sessions\\300.json", Json(300, "gamma", "idle", "cli"));

        long t = 0;
        int call = 0;
        IReadOnlyList<ProcEntry> Capture()
        {
            call++;
            long cargoCpu = call == 1 ? 0 : 2 * Sec;   // 2 CPU-seconds in the second sample
            return
            [
                E(100, 1, "claude.exe", 0), E(101, 100, "cmd.exe", 0), E(102, 101, "pwsh.exe", 0), E(103, 102, "cargo.exe", cargoCpu),
                E(110, 100, "cmd.exe", 0), E(111, 110, "pwsh.exe", 0), E(200, 111, "claude.exe", 0),
                E(201, 200, "python.exe", 0),
                E(300, 1, "claude.exe", 0),
            ];
        }
        string? CmdLine(int pid) => pid switch
        {
            101 or 110 => @"cmd.exe /d /s /c ""chcp.com"" 65001 >nul & x",
            102 or 111 => @"pwsh.exe -Command ""$__claudeCodeScript""",
            103 => @"""C:\bin\cargo.exe"" build",
            200 => @"""C:\bin\claude.exe"" -p --model sonnet",
            201 => "python.exe scan.py",
            _ => null,
        };
        var c = new FleetCollector(Path.Combine(dir.Path, "sessions"), Path.Combine(dir.Path, "projects"), Capture, CmdLine,
            () => new DateTime(2026, 10, 16, 20, 0, 0, DateTimeKind.Utc).AddTicks(t), logicalProcessors: 4);
        return (c, step => t += step * Sec, dir);
    }

    [Fact]
    public void Headless_session_nests_under_the_session_that_started_it()
    {
        var (c, clock, dir) = Make();
        using var _ = dir;
        clock(0);
        var snap = c.Collect(detail: true);
        Assert.Equal(new[] { "alpha", "gamma" }, snap.Sessions.Select(s => s.Name).ToArray());   // busy first
        var alpha = snap.Sessions[0];
        var child = Assert.Single(alpha.Children);
        Assert.Equal("agent-b", child.Name);
        Assert.True(child.Headless);
        Assert.Equal("sonnet", child.Model);
        Assert.Equal(3, snap.Count);
        Assert.Equal(2, snap.Busy);
    }

    [Fact]
    public void Detail_mode_lists_processes_and_does_not_double_count_the_headless_tree()
    {
        var (c, clock, dir) = Make();
        using var _ = dir;
        c.Collect(detail: true);
        clock(1);
        var snap = c.Collect(detail: true);
        var alpha = snap.Sessions[0];
        var cargo = Assert.Single(alpha.Processes);
        Assert.Equal("cargo build", cargo.Label);
        Assert.Equal(50.0, cargo.CpuPercent, 6);                       // 2 CPU-s / (1 s x 4 CPUs)
        Assert.Equal("python scan.py", Assert.Single(alpha.Children[0].Processes).Label);
        Assert.Equal(50.0, snap.TotalCpu, 6);
    }

    [Fact]
    public void Light_mode_folds_the_subtree_into_the_session_and_gives_the_same_total()
    {
        var (c, clock, dir) = Make();
        using var _ = dir;
        c.Collect(detail: false);
        clock(1);
        var snap = c.Collect(detail: false);
        var alpha = snap.Sessions[0];
        Assert.Empty(alpha.Processes);
        Assert.Equal(50.0, alpha.OwnCpuPercent, 6);
        Assert.Equal(50.0, snap.TotalCpu, 6);
        Assert.Equal(0, snap.Sessions[1].TreeCpu);
    }

    [Fact]
    public void Signature_changes_with_rows_but_not_with_cpu()
    {
        var (c, clock, dir) = Make();
        using var _ = dir;
        c.Collect(detail: true);
        clock(1);
        string a = c.Collect(detail: true).Signature();
        clock(1);
        string b = c.Collect(detail: true).Signature();
        Assert.Equal(a, b);
        File.Delete(Path.Combine(dir.Path, "sessions", "300.json"));
        Assert.NotEqual(a, c.Collect(detail: true).Signature());
    }
}

public class ProcessSnapshotTests
{
    [Fact]
    public void Capture_sees_this_process_with_plausible_values()
    {
        var me = ProcessSnapshot.Capture().First(e => e.Pid == Environment.ProcessId);
        using var p = Process.GetCurrentProcess();
        Assert.Equal(p.ProcessName, Path.GetFileNameWithoutExtension(me.Name), ignoreCase: true);
        Assert.InRange(Math.Abs(me.CreateTime - p.StartTime.ToFileTime()), 0, TimeSpan.TicksPerSecond);
        Assert.InRange(me.WorkingSet, p.WorkingSet64 / 2, p.WorkingSet64 * 2);
        Assert.True(me.CpuTime > 0);
        Assert.NotEqual(0, me.ParentPid);
    }

    [Fact]
    public void CommandLine_of_this_process_names_the_test_host() =>
        Assert.Contains(Process.GetCurrentProcess().ProcessName, ProcessSnapshot.CommandLine(Environment.ProcessId) ?? "", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void CommandLine_of_a_missing_process_is_null() => Assert.Null(ProcessSnapshot.CommandLine(int.MaxValue - 3));

    [Fact]
    public void Real_sessions_collect_without_throwing()
    {
        var snap = new FleetCollector().Collect(detail: true);
        Assert.False(snap.IsMock);
        Assert.True(snap.TotalCpu >= 0);
    }
}
