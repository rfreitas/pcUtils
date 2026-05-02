using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;
using AggressiveScreensaver.Parsing;
using AggressiveScreensaver.Services;

namespace AggressiveScreensaver.Services;

/// <summary>
/// Polls 'powercfg /requests' every 5 s and maintains formatted blocking strings.
/// Ported from GetPowerRequests() in index.ahk.
/// </summary>
internal sealed class PowercfgService : IDisposable
{
    // -------------------------------------------------------------------------
    // Dependencies
    // -------------------------------------------------------------------------
    private readonly IniStore _ini;
    private readonly System.Windows.Forms.Timer _timer;

    // -------------------------------------------------------------------------
    // Public state
    // -------------------------------------------------------------------------

    /// <summary>
    /// Comma-separated DISPLAY blockers (excluding blacklisted apps). Empty = none.
    /// </summary>
    public string BlockingScreenApps { get; private set; } = "";

    /// <summary>
    /// Comma-separated SYSTEM/AWAYMODE blockers. Empty = none.
    /// </summary>
    public string BlockingSleepApps { get; private set; } = "";

    /// <summary>Apps that have ever blocked DISPLAY (History). Case-insensitive.</summary>
    public Dictionary<string, bool> HistoryApps { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Apps the user chose to ignore (Blacklist). Case-insensitive.</summary>
    public Dictionary<string, bool> BlacklistedApps { get; } = new(StringComparer.OrdinalIgnoreCase);

    public long LastCheckTick { get; private set; }

    /// <summary>Raised on the UI thread after each poll completes.</summary>
    public event EventHandler? Updated;

    // -------------------------------------------------------------------------
    // Ctor
    // -------------------------------------------------------------------------
    public PowercfgService(IniStore ini)
    {
        _ini = ini;

        _timer = new System.Windows.Forms.Timer { Interval = 5000 };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start()
    {
        LoadHistoryAndBlacklist();
        Poll();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    /// <summary>Force an immediate poll (e.g., after blacklist change).</summary>
    public void Refresh() => Poll();

    // -------------------------------------------------------------------------
    // Core
    // -------------------------------------------------------------------------

    private void Poll()
    {
        if (!IsAdmin())
        {
            BlockingScreenApps = "";
            BlockingSleepApps  = "";
            Updated?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            string output = RunPowercfg();
            var parsed = PowercfgParser.Parse(output);

            // Process DISPLAY results
            var screenTexts = new List<string>();
            foreach (var app in parsed.Screen)
            {
                // Track history
                if (!HistoryApps.ContainsKey(app.Filename))
                {
                    HistoryApps[app.Filename] = true;
                    _ini.WriteString("History", app.Filename, "1");
                }

                // Filter by blacklist
                if (!BlacklistedApps.ContainsKey(app.Filename))
                    screenTexts.Add(app.Text);
            }

            // Process SLEEP results
            var sleepTexts = new List<string>();
            foreach (var app in parsed.Sleep)
                sleepTexts.Add(app.Text);

            BlockingScreenApps = BlockingFormatter.Format(screenTexts);
            BlockingSleepApps  = BlockingFormatter.Format(sleepTexts);
            LastCheckTick      = Environment.TickCount64;
        }
        catch (Exception ex)
        {
            Logger.LogException(ex);
        }

        Updated?.Invoke(this, EventArgs.Empty);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string RunPowercfg()
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = "powercfg.exe",
                Arguments              = "/requests",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            }
        };
        proc.Start();
        // Read stderr asynchronously to prevent pipe-buffer deadlock: if powercfg
        // writes to stderr while we block on ReadToEnd(stdout), the stderr buffer
        // fills, powercfg blocks, and the UI thread hangs indefinitely.
        var stderrTask = proc.StandardError.ReadToEndAsync();
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(10_000); // 10 s hard limit
        stderrTask.GetAwaiter().GetResult(); // drain stderr; result is discarded
        return output;
    }

    public static bool IsAdmin()
    {
        try
        {
            var identity  = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private void LoadHistoryAndBlacklist()
    {
        var history = _ini.ReadSection("History");
        foreach (var key in history.Keys)
            HistoryApps[key] = true;

        var blacklist = _ini.ReadSection("Blacklist");
        foreach (var key in blacklist.Keys)
            BlacklistedApps[key] = true;
    }

    public void Dispose() => _timer.Dispose();
}
