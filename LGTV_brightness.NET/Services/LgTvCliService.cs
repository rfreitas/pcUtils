using System;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LgtvBrightness.Services;

/// <summary>
/// Wraps LGTV Companion's command-line tool. Uses ArgumentList (not a single
/// Arguments string) so argument values like the JSON payload below never need
/// manual shell-quoting.
/// </summary>
internal static class LgTvCliService
{
    private const string CliPath = @"C:\Program Files\LGTV Companion\LGTVcli.exe";

    public static bool SetBacklight(int value)
    {
        var (_, err) = Run("-backlight", value.ToString());
        if (err.Length > 0)
            Logger.Log($"LGTVcli backlight set failed: {err}");
        return err.Length == 0;
    }

    public static int? GetBacklight()
    {
        var (outp, err) = Run("-ok", "backlight", "-get_system_settings", "picture", "[\"backlight\"]");
        if (err.Length > 0)
            Logger.Log($"LGTVcli backlight query failed: {err}");
        if (outp.Length == 0) return null;

        var m = Regex.Match(outp, @"\d+");
        return m.Success ? int.Parse(m.Value) : null;
    }

    private static (string Out, string Err) Run(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(CliPath)
            {
                CreateNoWindow         = true,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return (stdout.Trim(), stderr.Trim());
        }
        catch (Exception ex)
        {
            Logger.LogException(ex);
            return ("", ex.Message);
        }
    }
}
