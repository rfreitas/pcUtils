using System;
using System.Diagnostics;
using System.Windows;
using AggressiveScreensaver.Services;

namespace AggressiveScreensaver.Forms;

/// <summary>
/// Read-only debug view showing admin status, last poll time, blocking lists,
/// and raw powercfg output.
/// </summary>
internal partial class DebugWindow : Window
{
    private readonly PowercfgService _powercfg;

    public DebugWindow(PowercfgService powercfg)
    {
        InitializeComponent();
        _powercfg = powercfg;

        Loaded += (_, _) =>
        {
            bool isAdmin = PowercfgService.IsAdmin();
            string timeSinceLast = _powercfg.LastCheckTick == 0
                ? "Never"
                : $"{(Environment.TickCount64 - _powercfg.LastCheckTick) / 1000}s ago";

            string rawOutput = RunPowercfg();

            OutputText.Text =
                $"Running as Admin: {(isAdmin ? "YES" : "NO")}\r\n" +
                $"Last Auto-Check: {timeSinceLast}\r\n" +
                $"Screen Blocked: {(string.IsNullOrEmpty(_powercfg.BlockingScreenApps) ? "None" : _powercfg.BlockingScreenApps)}\r\n" +
                $"Sleep Blocked: {(string.IsNullOrEmpty(_powercfg.BlockingSleepApps) ? "None" : _powercfg.BlockingSleepApps)}\r\n\r\n" +
                $"Raw powercfg output:\r\n{(string.IsNullOrEmpty(rawOutput) ? "(empty)" : rawOutput)}";
        };
    }

    private static string RunPowercfg()
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = "powercfg.exe",
                    Arguments              = "/requests",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow         = true,
                }
            };
            proc.Start();
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10_000);
            return output;
        }
        catch (Exception ex)
        {
            Logger.LogException(ex);
            return $"(error: {ex.Message})";
        }
    }
}
