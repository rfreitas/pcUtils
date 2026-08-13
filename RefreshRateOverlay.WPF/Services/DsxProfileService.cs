using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace RefreshRateOverlay.WPF.Services;

internal sealed record DsxDevice(string MacAddress, string ActiveProfile, string DeviceType, string Name);

/// <summary>
/// Drives DSX's console companion app (DSX_Console.exe, under the Steam install
/// of app 1812620) to read and switch the controller-emulation profile DSX
/// applies to connected controllers. DSX itself must already be running — the
/// console is just a scriptable client that redirects commands into it. Every
/// call passes /silent: a bare invocation leaves a visible "DSX Console" window
/// resident (it becomes the singleton instance subsequent commands redirect
/// into), and the console has no usable stdout anyway, so /outfile is used to
/// read results back.
/// </summary>
internal static class DsxProfileService
{
    private static readonly Lazy<string?> ToolPath = new(LocateConsoleExe);

    public static bool IsAvailable => ToolPath.Value is not null;

    public static async Task<List<DsxDevice>> ListDevicesAsync()
    {
        string[] lines = await RunAsync("/listDevices");
        var devices = new List<DsxDevice>();
        foreach (string line in lines.Skip(1)) // skip "Connected Devices:" header
        {
            string[] parts = line.Split(" - ");
            if (parts.Length < 6) continue;
            devices.Add(new DsxDevice(parts[0].Trim(), parts[1].Trim(), parts[4].Trim(), parts[5].Trim()));
        }
        return devices;
    }

    public static async Task<List<string>> ListProfilesAsync()
    {
        string[] lines = await RunAsync("/listProfiles");
        return lines.Skip(1) // skip "Available Profiles:" header
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
    }

    public static async Task<bool> ChangeProfileAsync(string macAddress, string profile)
    {
        string[] lines = await RunAsync("/changeProfile", macAddress, profile);
        string result = string.Join(" ", lines);
        bool ok = result.Length > 0 && !result.StartsWith("Error", StringComparison.OrdinalIgnoreCase);
        Logger.Log($"DsxProfileService: changeProfile(mac='{macAddress}', profile='{profile}') -> '{result}' (ok={ok})");
        return ok;
    }

    private static async Task<string[]> RunAsync(params string[] args)
    {
        string? tool = ToolPath.Value;
        if (tool is null) return Array.Empty<string>();

        string outFile = Path.Combine(Path.GetTempPath(), $"rro_dsx_{Guid.NewGuid():N}.txt");
        try
        {
            var psi = new ProcessStartInfo { FileName = tool, UseShellExecute = false, CreateNoWindow = true };
            foreach (string a in args) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add($"/outfile:{outFile}");
            psi.ArgumentList.Add("/silent");

            using var proc = Process.Start(psi);
            if (proc is null) return Array.Empty<string>();
            await proc.WaitForExitAsync();

            // Small grace window: the console redirects into a resident DSX
            // instance and can exit a beat before that instance finishes
            // writing the result file.
            for (int i = 0; i < 20 && !File.Exists(outFile); i++)
                await Task.Delay(100);

            return File.Exists(outFile) ? await File.ReadAllLinesAsync(outFile) : Array.Empty<string>();
        }
        catch (Exception ex)
        {
            Logger.LogException(ex);
            return Array.Empty<string>();
        }
        finally
        {
            try { if (File.Exists(outFile)) File.Delete(outFile); }
            catch { /* best effort cleanup */ }
        }
    }

    private static string? LocateConsoleExe()
    {
        foreach (string lib in GetSteamLibraryPaths())
        {
            string dsxDir = Path.Combine(lib, "steamapps", "common", "DSX");
            if (!Directory.Exists(dsxDir)) continue;
            string? found = Directory.EnumerateFiles(dsxDir, "DSX_Console.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (found is not null) return found;
        }
        return null;
    }

    private static IEnumerable<string> GetSteamLibraryPaths()
    {
        string? steamPath =
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string
            ?? Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (steamPath is null) yield break;

        yield return steamPath;

        // Additional Steam library folders (other drives) are listed here.
        string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
            yield return m.Groups[1].Value.Replace(@"\\", @"\");
    }
}
