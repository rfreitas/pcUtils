using System.Threading;
using System.Windows;

namespace PortWatch;

/// <summary>Single-instance guard, then hands off to TrayApp.</summary>
public partial class App : System.Windows.Application
{
    private Mutex?   _mutex;
    private TrayApp? _trayApp;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // NotifyIcon events run on the WinForms side; without this, their exceptions show a
        // modal "Unhandled exception" dialog instead of reaching the log.
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, ex) => Logger.Log($"WINFORMS: {ex.Exception}");

        DispatcherUnhandledException += (_, ex) =>
        {
            Logger.Log($"DISPATCHER: {ex.Exception}");
            ex.Handled = true;
        };
        Logger.InstallGlobalHandlers();
        Logger.Log("Starting.");

        if (e.Args.Length >= 2 && e.Args[0] == "--render")
        {
            RunHeadless(() =>
            {
                var popup = new HoverPopup();
                popup.SetRows(PortGrouper.Group(PortScanner.Scan()));
                // optional third arg, e.g. "UDP:5353": render the state while hovering that port
                if (e.Args.Length >= 3 && e.Args[2].Split(':') is [var proto, var port])
                    popup.HighlightPort(proto, int.Parse(port));
                popup.RenderToPng(e.Args[1]);
            });
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--capture")
        {
            // Real hover path, then snapshot the live window at 0/0.5/1/2 s to see what the user sees.
            var tray = new TrayApp();
            tray.PostTrayMouseMoves(40);   // what a real hover delivers: a burst, not one event
            int n = 0;
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            t.Tick += (_, _) =>
            {
                try
                {
                    Logger.Log($"capture {n}: visible={tray.PopupVisible}");
                    if (tray.PopupVisible) tray.Popup.CaptureWindowPng(e.Args[1].Replace(".png", $"_{n}.png"));
                }
                catch (Exception ex) { Logger.Log($"capture failed: {ex}"); }
                if (++n == 4) { t.Stop(); tray.Dispose(); Shutdown(0); }
            };
            t.Start();
            return;
        }
        if (e.Args.Length >= 1 && e.Args[0] == "--placement")
        {
            // Hover at several tray-icon positions; fail if the popup ever overlaps the taskbar.
            RunHeadless(() =>
            {
                using var tray = new TrayApp();
                var screen = System.Windows.Forms.Screen.PrimaryScreen!;
                int taskbarTop = Shared.TrayPopupPlacement.TaskbarTop(screen);
                Logger.Log($"placement: screen={screen.Bounds} work={screen.WorkingArea} taskbarTop={taskbarTop}");
                foreach (var cursor in new[]
                {
                    new System.Drawing.Point(screen.Bounds.Right - 100, screen.Bounds.Bottom - 20),  // tray icon on the bar
                    new System.Drawing.Point(screen.Bounds.Right - 60,  taskbarTop - 150),           // overflow flyout
                    new System.Drawing.Point(screen.Bounds.Right - 5,   screen.Bounds.Bottom - 2),   // far corner
                })
                {
                    tray.SimulateHoverAt(cursor);
                    System.Windows.Forms.Application.DoEvents();
                    System.Threading.Thread.Sleep(150);
                    System.Windows.Forms.Application.DoEvents();
                    var r = tray.Popup.WindowRectPx();
                    Logger.Log($"placement: cursor={cursor} rect={r} bottom={r.Bottom} (limit {taskbarTop})");
                    if (r.Bottom > taskbarTop) throw new InvalidOperationException($"popup bottom {r.Bottom} is under the taskbar (top {taskbarTop})");
                }
            });
            return;
        }
        if (e.Args.Length >= 1 && e.Args[0] == "--selftest")
        {
            RunHeadless(() =>
            {
                using var tray = new TrayApp();
                tray.SimulateHover();
                if (!tray.PopupVisible) throw new InvalidOperationException("popup not visible after hover");
            });
            return;
        }

        _mutex = new Mutex(initiallyOwned: true, @"Local\PortWatch", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        _trayApp = new TrayApp();
    }

    /// <summary>Runs one action in the real app context (real manifest/DPI), exiting 0 or 1.</summary>
    private void RunHeadless(Action action)
    {
        try { action(); Logger.Log("headless OK"); Shutdown(0); }
        catch (Exception ex) { Logger.Log($"headless FAILED: {ex}"); Shutdown(1); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayApp?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
