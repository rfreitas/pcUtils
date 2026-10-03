using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace SteamDxvk;

/// <summary>An entry in the Run as dropdown, coloured and annotated when that setup went badly last time.</summary>
internal sealed record RunChoice(RunMode Mode, string Text, Brush Brush)
{
    public override string ToString() => Text;
}

internal sealed record LaunchRequest(DateTime Utc, RunMode Mode, string Config);

/// <summary>
/// Running-game awareness: which games are running and on which API, whether that is what was asked for, and what happened to
/// finished runs (remembered per setup so the dropdown can warn before you repeat a crash).
/// </summary>
internal partial class MainWindow
{
    private static readonly Brush Bad = Frozen("#ff8fa3");   // crashed: pink from the repo palette, always paired with a glyph and words

    /// <summary>Past run results per game and setup. Replaceable so tests never write the real history.</summary>
    internal RunHistory History { get; set; } = new();

    /// <summary>How a finished run is judged. Replaceable so tests don't depend on this machine's crash logs and event log.</summary>
    internal Func<string, DateTime, DateTime, string?, CrashAssessment> AssessRun { get; set; } = (exe, start, end, dxvkDir) =>
        CrashDetector.Assess(exe, start, end, dxvkDir, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            CrashDetector.FindWindowsCrashEvent);

    private readonly Dictionary<int, LaunchRequest> _launches = [];
    private GameMonitor? _monitor;
    private DispatcherTimer? _monitorTimer;
    private bool _polling;

    // --------------------------------------------------------------------------- monitor

    internal void AttachMonitor(GameMonitor monitor)
    {
        _monitor = monitor;
        monitor.Started += session => Dispatcher.InvokeAsync(() => OnRunStarted(session));
        monitor.ApiChanged += session => Dispatcher.InvokeAsync(() => OnRunApiChanged(session));
        monitor.Ended += (session, endUtc) =>
        {
            // Judged here, on the polling thread: reading logs and the event log must not stall the UI.
            var scan = session.Scan;
            string? dxvkDir = scan.Exes.Select(e => e.Dir).Distinct().FirstOrDefault(d => DxvkInstaller.Status(d) is not null);
            string exe = session.ExePath.Length > 0 ? session.ExePath : scan.Primary?.Path ?? "";
            var assessment = AssessRun(exe, session.StartUtc, endUtc, dxvkDir);
            Dispatcher.InvokeAsync(() => OnRunEnded(session, endUtc, assessment));
        };
    }

    /// <summary>Starts watching for games every couple of seconds (real processes). Not used by tests.</summary>
    internal void StartMonitoring()
    {
        if (_monitorTimer is not null) return;
        if (_monitor is null) AttachMonitor(new GameMonitor(new WindowsProcessProbe()));
        _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _monitorTimer.Tick += async (_, _) => await PollMonitorAsync();
        _monitorTimer.Start();
    }

    private async Task PollMonitorAsync()
    {
        if (_polling || _monitor is null || _scanning) return;
        _polling = true;
        try
        {
            var scans = _scans.ToList();
            await Task.Run(() => _monitor.Poll(scans));
        }
        catch (Exception e) { Logger.Log($"Monitor poll failed: {e}"); }
        finally { _polling = false; }
    }

    /// <summary>One synchronous poll (tests, headless flags).</summary>
    internal void PollMonitor() => _monitor?.Poll(_scans.ToList());

    internal RunSession? RunningSession(int appId) => _monitor?.For(appId);

    private void OnRunStarted(RunSession session)
    {
        var scan = session.Scan;
        // A run started within a few minutes of a launch from this app carries what the app asked for; anything else was
        // started outside the app (Steam directly), so it got no flag: Default.
        if (_launches.TryGetValue(scan.AppId, out var launch) && session.StartUtc >= launch.Utc.AddSeconds(-5) && session.StartUtc <= launch.Utc.AddMinutes(3))
        {
            session.Requested = launch.Mode;
            session.Config = launch.Config;
        }
        else
        {
            session.Requested = RunMode.Default;
            session.Config = ConfigKey.Of(RunMode.Default, scan.Status);
        }
        Log($"{scan.Name} is running ({(session.Requested == RunMode.Default ? "no API requested" : $"requested {session.Requested}")}).");
        RefreshRows();
        UpdateActions();
    }

    private void OnRunApiChanged(RunSession session)
    {
        Log($"{session.Scan.Name}: running {session.Api.Label}.");
        if (ApiDetector.Mismatch(session.Requested, session.Api) is { } warning) Log($"WARNING: {warning}");
        RefreshRows();
        UpdateActions();
    }

    private void OnRunEnded(RunSession session, DateTime endUtc, CrashAssessment assessment)
    {
        var scan = session.Scan;
        History.Add(new RunRecord(scan.AppId, session.Config, assessment.Outcome, assessment.Detail, endUtc));
        string setup = ConfigKey.Describe(session.Config);
        Log(assessment.Outcome == RunOutcome.Ok
            ? $"{scan.Name} closed normally ({assessment.Detail}) on {setup}."
            : $"{(assessment.Outcome == RunOutcome.Crashed ? "CRASHED" : "EXITED EARLY")}: {scan.Name} on {setup}: {assessment.Detail}. Remembered for this setup.");
        RefreshRunChoices();
        RefreshRows();
        UpdateActions();
    }

    // ---------------------------------------------------------------- run as dropdown

    /// <summary>Rebuilds the Run as dropdown for the selected game: only modes its engine supports, bad setups flagged.</summary>
    private void RefreshRunChoices()
    {
        bool wasPopulating = _populating;
        _populating = true;   // setting the items fires SelectionChanged, which must not be treated as the user choosing
        try
        {
            var scan = _selected;
            var modes = EngineFlags.Available(scan?.Engine ?? "");
            var saved = scan is null ? RunMode.Default : Settings.GetRunMode(scan.AppId);
            var choices = modes.Select(mode => Choice(scan, mode)).ToList();
            RunBox.ItemsSource = choices;
            RunBox.SelectedItem = choices.First(c => c.Mode == (modes.Contains(saved) ? saved : RunMode.Default));
        }
        finally { _populating = wasPopulating; }
    }

    private RunChoice Choice(GameScan? scan, RunMode mode)
    {
        var problem = scan is null ? null : History.Problem(scan.AppId, ConfigKey.Of(mode, scan.Status));
        return problem switch
        {
            { Outcome: RunOutcome.Crashed } p => new(mode, $"{mode}   ✖ crashed {When(p.WhenUtc)}", Bad),
            { } p => new(mode, $"{mode}   ⚠ exited early {When(p.WhenUtc)}", Warn),
            _ => new(mode, mode.ToString(), Fg),
        };
    }

    private static string When(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("MMM d");
    }

    private RunMode SelectedRunMode() => (RunBox.SelectedItem as RunChoice)?.Mode ?? RunMode.Default;

    /// <summary>Same as picking it in the dropdown (saved for the game too). Used by the headless --launch flag.</summary>
    internal void SetRunMode(RunMode mode)
    {
        if (RunBox.Items.OfType<RunChoice>().FirstOrDefault(c => c.Mode == mode) is { } choice) RunBox.SelectedItem = choice;
    }

    // ------------------------------------------------------------------ status lines

    /// <summary>The selected setup's last bad result, if any (a later good run clears it).</summary>
    private RunRecord? SelectedProblem() =>
        _selected is { } scan ? History.Problem(scan.AppId, ConfigKey.Of(SelectedRunMode(), scan.Status)) : null;

    /// <summary>Fills the line under the game's title: running now (and on what), or how this setup last went.</summary>
    private void UpdateRunStatus()
    {
        RunStatusText.Inlines.Clear();
        RunStatusText.ToolTip = null;
        if (_selected is not { } scan) { RunStatusText.Visibility = Visibility.Collapsed; return; }

        void Add(string text, Brush brush, bool lineBreak = false)
        {
            if (lineBreak) RunStatusText.Inlines.Add(new System.Windows.Documents.LineBreak());
            RunStatusText.Inlines.Add(new System.Windows.Documents.Run(text) { Foreground = brush });
        }

        if (RunningSession(scan.AppId) is { } session)
        {
            if (session.Api.Api == GfxApi.None) Add("● Running · detecting the API ...", Dim);
            else
            {
                Add($"● Running · {session.Api.Label}", Good);
                RunStatusText.ToolTip = $"Evidence: {session.Api.Evidence}";
                if (ApiDetector.Mismatch(session.Requested, session.Api) is { } warning) Add("⚠ " + warning, Warn, lineBreak: true);
            }
        }
        // A run that went well needs no message: only a running game or a problem is worth a line.
        RunStatusText.Visibility = RunStatusText.Inlines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
