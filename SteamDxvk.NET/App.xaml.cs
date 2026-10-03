using System.IO;
using System.Windows;
using Shared;

namespace SteamDxvk;

/// <summary>
/// Single-instance guard, then the window. Headless flags (repo convention, see Shared.NET/TRAY_APP_UX.md section 8)
/// run the real exe so an agent can verify it without a mouse:
///   --render out.png [game]   render the window (optionally with a game selected) to a PNG, then view it
///   --scan out.txt            write the library scan table
///   --selftest                open the window, scan, open every dropdown; exit 0/1
/// </summary>
public partial class App : Application
{
    private SingleInstanceGuard? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, ex) =>
        {
            Logger.Log($"DISPATCHER: {ex.Exception}");
            ex.Handled = true;
        };
        Logger.InstallGlobalHandlers();
        Logger.Log("Starting.");

        switch (e.Args)
        {
            case ["--render", var png, ..]:
                RunHeadless(() => Render(png, e.Args.Length > 2 ? e.Args[2] : null));
                return;
            case ["--scan", var txt]:
                RunHeadless(() => File.WriteAllText(txt, ScanReport.Format(LibraryScanner.ScanAll())));
                return;
            case ["--selftest"]:
                RunHeadless(SelfTest);
                return;
            case ["--fetch"]:
                RunHeadless(Fetch);
                return;
            case ["--launch", var launchGame, ..]:
                RunHeadless(() => LaunchGame(launchGame, e.Args.Length > 2 ? Enum.Parse<RunMode>(e.Args[2], ignoreCase: true) : null));
                return;
            case ["--check", var checkGame]:
                RunHeadless(() => CheckGame(checkGame));
                return;
            case ["--detect", var detectGame]:
                RunHeadless(() => DetectGame(detectGame));
                return;
            case ["--assess", var assessGame, var from, var to]:
                RunHeadless(() => AssessGame(assessGame, DateTime.Parse(from), DateTime.Parse(to)));
                return;
        }

        _instance = SingleInstanceGuard.TryAcquire(@"Global\SteamDxvk.NET", "SteamDxvk", replaceExisting: true, Logger.Log);
        if (_instance is null)
        {
            Shutdown();
            return;
        }
        new MainWindow().Show();
    }

    private static void Render(string png, string? game)
    {
        var window = new MainWindow { ReadOnly = true };   // looking must not change a game folder
        window.LoadScans(LibraryScanner.ScanAll());
        // One real poll, so a render shows what is running right now.
        window.AttachMonitor(new GameMonitor(new WindowsProcessProbe()));
        window.PollMonitor();
        SteamDxvk.MainWindow.Pump();
        if (game is not null && !window.SelectByName(game)) throw new InvalidOperationException($"no game matching '{game}'");
        window.RenderToPng(png);
    }

    /// <summary>
    /// The real Launch button path (verify/repair, Run as flag, Steam). A Run as value given here is saved for the game,
    /// exactly as if picked in the dropdown. Pumps the dispatcher while waiting because blocking on it would deadlock the awaits.
    /// </summary>
    private static void LaunchGame(string game, RunMode? mode)
    {
        var window = new MainWindow();
        window.LoadScans(LibraryScanner.ScanAll());
        if (!window.SelectByName(game)) throw new InvalidOperationException($"no game matching '{game}'");
        if (mode is { } m) window.SetRunMode(m);
        var launch = window.LaunchAsync();
        while (!launch.IsCompleted)
        {
            SteamDxvk.MainWindow.Pump();
            Thread.Sleep(20);
        }
        launch.GetAwaiter().GetResult();
        SteamDxvk.MainWindow.Pump();
        Logger.Log($"launch: '{window.Selected?.Name}' requested via Steam");
    }

    /// <summary>
    /// Runs the real crash judge over a past run (local times) and logs the verdict. Read-only: nothing is recorded.
    /// Lets the detector be checked against crashes that really happened.
    /// </summary>
    private static void AssessGame(string game, DateTime localStart, DateTime localEnd)
    {
        var scan = LibraryScanner.ScanAll().FirstOrDefault(s => s.Name.Contains(game, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException($"no game matching '{game}'");
        string exe = scan.Primary?.Path ?? throw new InvalidOperationException("no game exe found");
        string? dxvkDir = scan.Exes.Select(e => e.Dir).Distinct().FirstOrDefault(d => DxvkInstaller.Status(d) is not null);
        var a = CrashDetector.Assess(exe, localStart.ToUniversalTime(), localEnd.ToUniversalTime(), dxvkDir,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), CrashDetector.FindWindowsCrashEvent);
        Logger.Log($"assess: {scan.Name} {localStart:HH:mm:ss}-{localEnd:HH:mm:ss} => {a.Outcome}: {a.Detail}");
    }

    /// <summary>One poll of the real process list: is the game running, and on which API (with the evidence)?</summary>
    private static void DetectGame(string game)
    {
        var window = new MainWindow { ReadOnly = true };
        window.LoadScans(LibraryScanner.ScanAll());
        if (!window.SelectByName(game)) throw new InvalidOperationException($"no game matching '{game}'");
        window.AttachMonitor(new GameMonitor(new WindowsProcessProbe()));
        window.PollMonitor();
        SteamDxvk.MainWindow.Pump();
        var scan = window.Selected!;
        var session = window.RunningSession(scan.AppId);
        Logger.Log(session is null
            ? $"detect: {scan.Name} is not running"
            : $"detect: {scan.Name} running since {session.StartUtc.ToLocalTime():HH:mm:ss} as {session.Api.Label} (confirmed={session.Api.Confirmed}); {session.Api.Evidence}");
    }

    private static void CheckGame(string game)
    {
        var window = new MainWindow { ReadOnly = true };
        window.LoadScans(LibraryScanner.ScanAll());
        if (!window.SelectByName(game)) throw new InvalidOperationException($"no game matching '{game}'");
        window.CheckLastRun();
        SteamDxvk.MainWindow.Pump();
    }

    /// <summary>Downloads (or finds cached) both DXVK builds: the real network + extraction path, end to end.</summary>
    private static void Fetch()
    {
        // Task.Run: no dispatcher context on the worker, so blocking here can't deadlock the awaits inside.
        Task.Run(async () =>
        {
            foreach (var variant in new[] { DxvkVariant.Official, DxvkVariant.Async })
            {
                var (dir, version) = await DxvkBuilds.EnsureBuildAsync(variant, Logger.Log);
                foreach (var arch in new[] { "x32", "x64" })
                    foreach (var dll in DxvkInstaller.AllDlls)
                        if (!File.Exists(Path.Combine(dir, arch, dll))) throw new InvalidDataException($"{variant} {version}: missing {arch}/{dll}");
                Logger.Log($"fetch: {DxvkInstaller.Label(variant)} {version} OK at {dir}");
            }
        }).GetAwaiter().GetResult();
    }

    private static void SelfTest()
    {
        var window = new MainWindow { ReadOnly = true };
        window.Show();
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (window.IsScanning && DateTime.UtcNow < deadline)
        {
            SteamDxvk.MainWindow.Pump();
            Thread.Sleep(30);
        }
        if (window.IsScanning) throw new TimeoutException("scan did not finish");
        if (window.ScanCount == 0 && SteamLibrary.FindSteamPath() is not null) throw new InvalidOperationException("Steam is installed but no games were found");

        window.SelectByName("");   // first game: builds the detail panel from real data
        window.ExerciseControls();
        Logger.Log($"selftest: {window.ScanCount} games, selected '{window.Selected?.Name}', install enabled={window.DxvkEnabled}");
        window.Close();
    }

    /// <summary>Runs one action in the real app context (real manifest/DPI), exiting 0 or 1.</summary>
    private void RunHeadless(Action action)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try { action(); Logger.Log("headless OK"); Shutdown(0); }
        catch (Exception ex) { Logger.Log($"headless FAILED: {ex}"); Shutdown(1); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
