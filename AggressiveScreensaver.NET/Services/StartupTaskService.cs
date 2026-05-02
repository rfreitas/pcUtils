using System;
using System.Diagnostics;
using System.IO;

namespace AggressiveScreensaver.Services;

/// <summary>
/// Creates / removes a Task Scheduler entry so the app launches at logon
/// with highest privileges (matching the requireAdministrator manifest).
/// </summary>
internal static class StartupTaskService
{
    private const string TaskName = "AggressiveScreensaver";

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    public static bool IsInstalled()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe",
                $"/Query /TN \"{TaskName}\" /FO LIST")
            {
                CreateNoWindow        = true,
                UseShellExecute       = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Logger.Log($"StartupTask.IsInstalled error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Creates an ONLOGON task that runs the current exe with highest privileges.
    /// Requires the process to already be elevated (requireAdministrator manifest).
    /// </summary>
    public static bool Install()
    {
        string exePath = Path.Combine(AppContext.BaseDirectory, "AggressiveScreensaver.exe");

        // Build the XML-based task to get full control over RunLevel.
        // schtasks /Create with /RL HIGHEST is simpler but doesn't always persist
        // the run level reliably; an inline XML definition is the safest path.
        string xml = BuildTaskXml(exePath, Environment.UserName);

        string xmlFile = Path.Combine(Path.GetTempPath(), "AggressiveScreensaver_task.xml");
        try
        {
            File.WriteAllText(xmlFile, xml, System.Text.Encoding.Unicode);

            var psi = new ProcessStartInfo("schtasks.exe",
                $"/Create /TN \"{TaskName}\" /XML \"{xmlFile}\" /F")
            {
                CreateNoWindow        = true,
                UseShellExecute       = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };

            using var p = Process.Start(psi)!;
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(10000);

            if (p.ExitCode != 0)
            {
                Logger.Log($"StartupTask.Install failed (exit {p.ExitCode}): {stderr.Trim()}");
                return false;
            }

            Logger.Log($"StartupTask installed for \"{exePath}\".");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"StartupTask.Install exception: {ex.Message}");
            return false;
        }
        finally
        {
            try { File.Delete(xmlFile); } catch { /* best-effort */ }
        }
    }

    public static bool Uninstall()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe",
                $"/Delete /TN \"{TaskName}\" /F")
            {
                CreateNoWindow        = true,
                UseShellExecute       = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };

            using var p = Process.Start(psi)!;
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(10000);

            if (p.ExitCode != 0)
            {
                Logger.Log($"StartupTask.Uninstall failed (exit {p.ExitCode}): {stderr.Trim()}");
                return false;
            }

            Logger.Log("StartupTask removed.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"StartupTask.Uninstall exception: {ex.Message}");
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Task XML builder
    // -------------------------------------------------------------------------

    private static string BuildTaskXml(string exePath, string userName)
    {
        // Use the current user's domain\name so the task runs under the same account.
        string userDomain = string.IsNullOrEmpty(Environment.UserDomainName)
            ? Environment.MachineName
            : Environment.UserDomainName;

        string fullUser = $"{userDomain}\\{userName}";

        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Launches AggressiveScreensaver at logon with administrator privileges.</Description>
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
