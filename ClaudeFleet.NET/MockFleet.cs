namespace ClaudeFleet;

/// <summary>
/// Sample fleet for the mock UI, modelled on the real process dump of 2026-10-16 (plan "Measured findings").
/// <see cref="Tick"/> random-walks every CPU value so the live meter can be seen moving.
/// </summary>
internal sealed class MockFleet
{
    private const long MB = 1L << 20;
    private const string Repos = @"C:\Users\ricfr\Repos";

    private readonly Random _rng;
    private readonly Dictionary<int, double> _base = new();
    private readonly Dictionary<int, double> _cpu = new();

    public MockFleet(int seed = 7)
    {
        _rng = new Random(seed);
        // pid -> typical CPU (% of machine)
        foreach (var (pid, cpu) in new (int, double)[]
        {
            (5520, 0.8), (47164, 0.3), (31880, 4.1), (49912, 3.8), (31224, 3.9), (47900, 4.2), (37600, 3.5),
            (23996, 0.2), (54968, 4.0),
            (54536, 0.6), (20972, 0.1), (52936, 0.1), (50520, 6.5), (25916, 0.0), (13988, 2.2),
            (36132, 0.9),
            (49752, 1.1), (20056, 0.4),
        })
        {
            _base[pid] = cpu;
            _cpu[pid] = cpu;
        }
    }

    /// <summary>Moves every CPU value a step around its typical value.</summary>
    public void Tick()
    {
        foreach (var pid in _base.Keys)
        {
            double b = _base[pid];
            double next = _cpu[pid] + (_rng.NextDouble() - 0.5) * Math.Max(0.4, b * 0.8);
            _cpu[pid] = Math.Clamp(next, 0, b * 2.5 + 0.5);
        }
    }

    private double Cpu(int pid) => _cpu.TryGetValue(pid, out var v) ? v : 0;

    private ProcView P(int pid, string label, long mb, params ProcView[] children) =>
        new(pid, label, Cpu(pid), mb * MB, false, children);

    private static ProcView Mcp(int pid, string label) => new(pid, label, 0, 40 * MB, true, []);

    public FleetSnapshot Snapshot()
    {
        var headlessBrp = new SessionView(
            54536, "a81aa9b9-0000-0000-0000-000000000000", "agent-a81aa9b9ea982d6dc-08", $@"{Repos}\boardvr-bevy",
            SessionStatus.Busy, TimeSpan.FromMinutes(6), Headless: true, "sonnet", Cpu(54536), 310 * MB,
            [],
            [
                Mcp(20972, "bevy_brp_mcp"),
                P(52936, "cargo run --release --bin fixture_viewer --features brp -- virtual_ollie", 90,
                    P(50520, "fixture_viewer.exe virtual_ollie", 640)),
                P(25916, "bash scan.sh", 12,
                    P(13988, "python -I scan.py virtual_ollie", 85)),
            ],
            []);

        var headlessEval = new SessionView(
            36132, "e3e3e3e3-0000-0000-0000-000000000000", "eval-baseline-e3", $@"{Repos}\boardvr-bevy",
            SessionStatus.Busy, TimeSpan.FromMinutes(3), Headless: true, "sonnet", Cpu(36132), 280 * MB,
            [], [], []);

        var boardvr = new SessionView(
            5520, "1b04be00-0000-0000-0000-000000000000", "boardvr-bevy-f8", $@"{Repos}\boardvr-bevy",
            SessionStatus.Busy, TimeSpan.FromMinutes(40), Headless: false, null, Cpu(5520), 520 * MB,
            [
                new AgentView("a1f0", "Explore", "find grip clamp references", "sonnet", Background: false, AgentState.Running, TimeSpan.FromMinutes(2)),
                new AgentView("a2c4", "general-purpose", "review drive_arms force balance", "opus", Background: true, AgentState.Done, TimeSpan.FromMinutes(5)),
            ],
            [
                Mcp(55200, "whiteboard mcp"),
                P(47164, "cargo build --release --bin fixture_viewer", 120,
                    P(31880, "rustc bevy_core_pipeline", 410),
                    P(49912, "rustc bevy_sprite_render", 380),
                    P(31224, "rustc bevy_post_process", 350),
                    P(47900, "rustc bevy_pbr", 720),
                    P(37600, "rustc bevy_anti_alias", 300)),
                P(23996, "cargo test --release --lib", 60,
                    P(54968, "rustc bevy_pbr", 690)),
            ],
            [headlessBrp, headlessEval]);

        var repos5e = new SessionView(
            49752, "b77a76cc-0036-4627-9c61-6587dc12b242", "repos-5e", Repos,
            SessionStatus.Busy, TimeSpan.FromMinutes(2), Headless: false, null, Cpu(49752), 480 * MB,
            [],
            [Mcp(16640, "whiteboard mcp"), P(20056, "dotnet build ClaudeFleet.NET", 210)],
            []);

        SessionView Idle(int pid, string name, string cwd, TimeSpan age, params AgentView[] agents) =>
            new(pid, $"{pid:x8}-0000-0000-0000-000000000000", name, cwd, SessionStatus.Idle, age, false, null, 0, 300 * MB,
                agents, [Mcp(pid + 1, "whiteboard mcp")], []);

        return new FleetSnapshot(
        [
            boardvr,
            repos5e,
            Idle(39136, "repos-97", Repos, TimeSpan.FromHours(19),
                new AgentView("a0329", "general-purpose", "Opus review of config unification", "opus", true, AgentState.Done, TimeSpan.FromMinutes(8))),
            Idle(28576, "boardvr-bevy-94", $@"{Repos}\boardvr-bevy", TimeSpan.FromDays(1)),
            Idle(31788, "repos-bf", Repos, TimeSpan.FromDays(2)),
            Idle(4364, "repos-d3", Repos, TimeSpan.FromDays(2)),
            Idle(11568, "Ollie with jump", $@"{Repos}\boardvr-bevy", TimeSpan.FromDays(8)),
            Idle(9468, "board-yaw-view-a0", $@"{Repos}\boardvr-bevy\.claude\worktrees\board-yaw-view", TimeSpan.FromDays(8)),
        ], IsMock: true);
    }
}
