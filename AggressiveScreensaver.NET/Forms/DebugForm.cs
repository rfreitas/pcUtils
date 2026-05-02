using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using AggressiveScreensaver.Services;

namespace AggressiveScreensaver.Forms;

/// <summary>
/// Read-only debug view showing admin status, last poll time, blocking lists,
/// and raw powercfg output. Ported from ShowPowerRequests() in index.ahk.
/// </summary>
internal sealed class DebugForm : Form
{
    private readonly PowercfgService _powercfg;

    public DebugForm(PowercfgService powercfg)
    {
        _powercfg = powercfg;

        Text            = "Power Requests Debug";
        Font            = new Font("Consolas", 9f);
        BackColor       = ColorTranslator.FromHtml("#2d2d2d");
        ForeColor       = Color.FromArgb(0xCC, 0xCC, 0xCC);
        FormBorderStyle = FormBorderStyle.Sizable;
        ClientSize      = new Size(640, 480);
        StartPosition   = FormStartPosition.CenterScreen;

        var textBox = new TextBox
        {
            Multiline   = true,
            ReadOnly    = true,
            ScrollBars  = ScrollBars.Vertical,
            BackColor   = ColorTranslator.FromHtml("#1e1e1e"),
            ForeColor   = Color.FromArgb(0xCC, 0xCC, 0xCC),
            Font        = new Font("Consolas", 9f),
            Dock        = DockStyle.Fill,
            WordWrap    = false,
        };
        Controls.Add(textBox);

        Load += (_, _) =>
        {
            bool isAdmin = IsAdmin();
            string timeSinceLast = _powercfg.LastCheckTick == 0
                ? "Never"
                : $"{(Environment.TickCount64 - _powercfg.LastCheckTick) / 1000}s ago";

            string rawOutput = RunPowercfg();

            textBox.Text =
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

    private static bool IsAdmin() => PowercfgService.IsAdmin();
}
