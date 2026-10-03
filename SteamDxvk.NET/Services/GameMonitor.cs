using System.IO;

namespace SteamDxvk;

/// <param name="StartUtc">Lazy: reading a process's start time costs a handle open, so it is only done for games.</param>
/// <param name="Modules">Lazy for the same reason; empty if access is denied.</param>
internal sealed record ProbedProcess(int Pid, string ExePath, Func<DateTime> StartUtc, Func<IReadOnlyList<ModuleInfo>> Modules);

internal interface IProcessProbe
{
    IReadOnlyList<ProbedProcess> Snapshot();
}

/// <summary>One continuous run of a game, from its first process appearing to its last one disappearing.</summary>
internal sealed class RunSession
{
    public required GameScan Scan { get; init; }
    public required DateTime StartUtc { get; init; }
    public HashSet<int> Pids { get; set; } = [];
    public string ExePath { get; set; } = "";
    public RunningApi Api { get; set; } = RunningApi.Unknown;

    /// <summary>What the app asked this run to be, and the setup it ran in. Set by whoever handles <see cref="GameMonitor.Started"/>.</summary>
    public RunMode Requested { get; set; }
    public string Config { get; set; } = "";
    internal int Polls { get; set; }
}

/// <summary>
/// Notices installed Steam games starting and stopping and works out which API each is really using.
/// Poll it on a timer; it keeps no thread of its own. Events fire on the polling thread.
/// </summary>
internal sealed class GameMonitor
{
    private readonly IProcessProbe _probe;
    private readonly Func<DateTime> _clock;
    private readonly string _windowsDir;
    private readonly Dictionary<int, RunSession> _sessions = [];

    public event Action<RunSession>? Started;
    public event Action<RunSession>? ApiChanged;
    public event Action<RunSession, DateTime>? Ended;

    public GameMonitor(IProcessProbe probe, Func<DateTime>? clock = null, string? windowsDir = null)
    {
        _probe = probe;
        _clock = clock ?? (() => DateTime.UtcNow);
        _windowsDir = windowsDir ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    }

    public IReadOnlyCollection<RunSession> Running => _sessions.Values;

    public RunSession? For(int appId) => _sessions.GetValueOrDefault(appId);

    public void Poll(IReadOnlyList<GameScan> scans)
    {
        var byGame = new Dictionary<int, (GameScan Scan, List<ProbedProcess> Processes)>();
        foreach (var process in _probe.Snapshot())
        {
            var scan = scans.FirstOrDefault(s => IsUnder(process.ExePath, s.Root));
            if (scan is null) continue;
            if (!byGame.TryGetValue(scan.AppId, out var entry)) byGame[scan.AppId] = entry = (scan, []);
            entry.Processes.Add(process);
        }

        foreach (var (appId, session) in _sessions.ToList())
        {
            if (byGame.ContainsKey(appId)) continue;
            _sessions.Remove(appId);
            Ended?.Invoke(session, _clock());
        }

        foreach (var (appId, (scan, processes)) in byGame)
        {
            if (!_sessions.TryGetValue(appId, out var session))
            {
                session = new RunSession { Scan = scan, StartUtc = processes.Min(p => p.StartUtc()) };
                _sessions[appId] = session;
                session.Pids = [.. processes.Select(p => p.Pid)];
                Started?.Invoke(session);
            }
            session.Pids = [.. processes.Select(p => p.Pid)];

            // Keep looking until the API is conclusive; afterwards re-check now and then in case the game switches.
            session.Polls++;
            if (session.Api.Confirmed && session.Polls % 5 != 0) continue;

            var api = Detect(scan, session, processes);
            if (api != session.Api)
            {
                session.Api = api;
                ApiChanged?.Invoke(session);
            }
        }
    }

    private RunningApi Detect(GameScan scan, RunSession session, List<ProbedProcess> processes)
    {
        // Game exes from the scan first: the launcher stub (SessionGame.exe) loads no graphics DLLs.
        var ordered = processes.OrderByDescending(p => scan.Exes.Any(e => e.Path.Equals(p.ExePath, StringComparison.OrdinalIgnoreCase)));

        GfxApi dxvkDevice = GfxApi.None;
        foreach (var dir in scan.Exes.Select(e => e.Dir).Distinct())
            if (DxvkInstaller.Status(dir) is not null && RunDiagnosis.Check(dir, session.StartUtc).Api is var api && api != GfxApi.None)
                dxvkDevice = api;

        RunningApi best = RunningApi.Unknown;
        foreach (var process in ordered)
        {
            IReadOnlyList<ModuleInfo> modules;
            try { modules = process.Modules(); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { continue; }
            if (modules.Count == 0) continue;

            session.ExePath = process.ExePath;
            var result = ApiDetector.Detect(modules, dxvkDevice, _windowsDir);
            if (result.Confirmed) return result;
            if (best.Api == GfxApi.None) best = result;
        }
        return best;
    }

    internal static bool IsUnder(string path, string root) =>
        path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
