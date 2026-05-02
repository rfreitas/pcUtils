using System;
using System.Diagnostics;
using System.IO;

namespace Shared;

/// <summary>
/// Creates / removes a Task Scheduler entry so an app launches at logon
/// with highest privileges (matching a requireAdministrator manifest).
/// </summary>
internal static class StartupTaskService
{
    public static bool IsInstalled(string taskName)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe",
                $"/Query /TN \"{taskName}\" /FO LIST")
            {
                CreateNoWindow         = true,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool Install(string taskName, string exePath, string description, Action<string>? log = null)
    {
        string xml     = BuildTaskXml(taskName, exePath, description, Environment.UserName);
        string xmlFile = Path.Combine(Path.GetTempPath(), $"{taskName}_task.xml");
        try
        {
            File.WriteAllText(xmlFile, xml, System.Text.Encoding.Unicode);

            var psi = new ProcessStartInfo("schtasks.exe",
                $"/Create /TN \"{taskName}\" /XML \"{xmlFile}\" /F")
            {
                CreateNoWindow         = true,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };

            using var p = Process.Start(psi)!;
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(10000);

            if (p.ExitCode != 0)
            {
                log?.Invoke($"StartupTask.Install failed (exit {p.ExitCode}): {stderr.Trim()}");
                return false;
            }

            log?.Invoke($"StartupTask installed for \"{exePath}\".");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"StartupTask.Install exception: {ex.Message}");
            return false;
        }
        finally
        {
            try { File.Delete(xmlFile); } catch { /* best-effort */ }
        }
    }

    public static bool Uninstall(string taskName, Action<string>? log = null)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe",
                $"/Delete /TN \"{taskName}\" /F")
            {
                CreateNoWindow         = true,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };

            using var p = Process.Start(psi)!;
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(10000);

            if (p.ExitCode != 0)
            {
                log?.Invoke($"StartupTask.Uninstall failed (exit {p.ExitCode}): {stderr.Trim()}");
                return false;
            }

            log?.Invoke("StartupTask removed.");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"StartupTask.Uninstall exception: {ex.Message}");
            return false;
        }
    }

    private static string BuildTaskXml(string taskName, string exePath, string description, string userName)
    {
        string userDomain = string.IsNullOrEmpty(Environment.UserDomainName)
            ? Environment.MachineName
            : Environment.UserDomainName;

        string fullUser = $"{userDomain}\\{userName}";

        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>{description}</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{fullUser}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{fullUser}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{exePath}</Command>
      <WorkingDirectory>{AppContext.BaseDirectory.TrimEnd('\\', '/')}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
""";
    }
}
