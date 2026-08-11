using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RefreshRateOverlay.WPF.Services;

internal enum PresentModeVerdict
{
    Optimal,     // Independent Flip (hardware or DWM-composed) — as fast as exclusive fullscreen
    Good,        // Other flip-model paths (Composed: Flip, Legacy Flip)
    Inefficient, // Composed: Copy — the old blit path, real overhead
    Unknown,
}

internal sealed record PresentModeResult(string RawMode, int SampleCount, PresentModeVerdict Verdict);

/// <summary>
/// Runs a short PresentMon capture against a target process and reports its
/// dominant DXGI presentation mode. Needs an elevated real-time ETW trace
/// session (confirmed empirically) — the app itself now runs elevated
/// (app.manifest: requireAdministrator) specifically so this can launch
/// PresentMon as a normal child process (inherits our token) with no extra
/// per-call UAC prompt. Runs automatically whenever the settings overlay
/// opens for a real app.
/// </summary>
internal static class PresentModeService
{
    private static readonly string ToolPath =
        Path.Combine(AppContext.BaseDirectory, "Tools", "PresentMon-2.5.1-x64.exe");

    public static async Task<PresentModeResult?> CaptureAsync(string processName, int seconds = 3)
    {
        if (!File.Exists(ToolPath))
        {
            Logger.Log($"PresentModeService: tool not found at '{ToolPath}'.");
            return null;
        }

        string outCsv = Path.Combine(Path.GetTempPath(), $"rro_presentmon_{Guid.NewGuid():N}.csv");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ToolPath,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--process_name");
            psi.ArgumentList.Add(processName);
            psi.ArgumentList.Add("--output_file");
            psi.ArgumentList.Add(outCsv);
            psi.ArgumentList.Add("--timed");
            psi.ArgumentList.Add(seconds.ToString());
            psi.ArgumentList.Add("--terminate_after_timed");
            psi.ArgumentList.Add("--no_console_stats");

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                Logger.Log("PresentModeService: Process.Start returned null.");
                return null;
            }

            await proc.WaitForExitAsync();

            if (!File.Exists(outCsv))
            {
                Logger.Log($"PresentModeService: no CSV produced for '{processName}' (not running, or no frames presented during the window).");
                return null;
            }

            return ParseCsv(outCsv);
        }
        catch (Exception ex)
        {
            Logger.LogException(ex);
            return null;
        }
        finally
        {
            try { if (File.Exists(outCsv)) File.Delete(outCsv); }
            catch { /* best effort cleanup */ }
        }
    }

    private static PresentModeResult? ParseCsv(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length < 2) return null;

        string[] header = lines[0].TrimStart('﻿').Split(',');
        int modeIdx = Array.IndexOf(header, "PresentMode");
        if (modeIdx < 0) return null;

        var counts = new Dictionary<string, int>();
        for (int i = 1; i < lines.Length; i++)
        {
            string[] cols = lines[i].Split(',');
            if (cols.Length <= modeIdx) continue;
            string mode = cols[modeIdx];
            counts[mode] = counts.GetValueOrDefault(mode) + 1;
        }

        if (counts.Count == 0) return null;

        var top = counts.OrderByDescending(kv => kv.Value).First();
        int total = counts.Values.Sum();

        return new PresentModeResult(top.Key, total, Classify(top.Key));
    }

    private static PresentModeVerdict Classify(string mode)
    {
        if (mode.Contains("Independent Flip", StringComparison.OrdinalIgnoreCase))
            return PresentModeVerdict.Optimal;
        if (mode.Contains("Copy", StringComparison.OrdinalIgnoreCase))
            return PresentModeVerdict.Inefficient;
        if (mode.Contains("Flip", StringComparison.OrdinalIgnoreCase))
            return PresentModeVerdict.Good;
        return PresentModeVerdict.Unknown;
    }

    public static string Recommendation(PresentModeResult result) => result.Verdict switch
    {
        PresentModeVerdict.Optimal =>
            "Optimal — running at near-exclusive-fullscreen efficiency. No change needed.",
        PresentModeVerdict.Good =>
            "Good — using a flip-model presentation path. No action needed.",
        PresentModeVerdict.Inefficient =>
            "Inefficient — on the slower composited-copy path. Try toggling Fullscreen/Borderless in the game's video settings.",
        _ =>
            "Unrecognized presentation mode.",
    };
}
