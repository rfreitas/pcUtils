using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Text.RegularExpressions;

namespace SteamDxvk;

internal sealed record CrashAssessment(RunOutcome Outcome, string Detail);

/// <summary>
/// Decides how a finished run went, from evidence the system leaves behind. Evidence, strongest first: an Unreal crash report
/// written during the run; Windows' own crash event for the exe; DXVK's log showing a D3D12 device it can't serve; and a very
/// short run with none of those (typically an error dialog, then quit: seen with Session on -vulkan, no crash report at all).
/// </summary>
internal static partial class CrashDetector
{
    public const double EarlyExitSeconds = 45;

    [GeneratedRegex(@"^(?<project>.+?)-(Win64|Win32|WinGDK)-", RegexOptions.IgnoreCase)]
    private static partial Regex UnrealExeName();

    [GeneratedRegex(@"Faulting module name:\s*(?<module>[^,\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FaultingModule();

    /// <param name="windowsCrashEvent">(exe file name, start, end) to a description of a Windows crash event, or null. Injectable for tests.</param>
    public static CrashAssessment Assess(string exePath, DateTime startUtc, DateTime endUtc, string? dxvkDir,
        string localAppData, Func<string, DateTime, DateTime, string?> windowsCrashEvent)
    {
        if (UnrealCrash(exePath, startUtc, localAppData) is { } unreal)
            return new(RunOutcome.Crashed, $"Unreal crash report: {unreal}");

        if (windowsCrashEvent(Path.GetFileName(exePath), startUtc, endUtc) is { } windows)
            return new(RunOutcome.Crashed, windows);

        if (dxvkDir is not null && RunDiagnosis.Check(dxvkDir, startUtc).D3D12Device)
            return new(RunOutcome.Crashed, "the game created a D3D12 device, which DXVK can't serve");

        double seconds = (endUtc - startUtc).TotalSeconds;
        return seconds < EarlyExitSeconds
            ? new(RunOutcome.ExitedEarly, $"exited after {seconds:0}s with no crash report (usually an error dialog, then quit)")
            : new(RunOutcome.Ok, $"ran for {Duration(seconds)}");
    }

    /// <summary>The ErrorMessage of an Unreal crash context written since the run started, or null.</summary>
    internal static string? UnrealCrash(string exePath, DateTime startUtc, string localAppData)
    {
        var match = UnrealExeName().Match(Path.GetFileName(exePath));
        if (!match.Success) return null;
        string crashes = Path.Combine(localAppData, match.Groups["project"].Value, "Saved", "Crashes");
        if (!Directory.Exists(crashes)) return null;

        try
        {
            var context = Directory.EnumerateFiles(crashes, "CrashContext.runtime-xml", SearchOption.AllDirectories)
                .Where(f => File.GetLastWriteTimeUtc(f) >= startUtc.AddSeconds(-5))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (context is null) return null;
            var m = Regex.Match(File.ReadAllText(context), @"<ErrorMessage>(?<m>.*?)</ErrorMessage>", RegexOptions.Singleline);
            string message = m.Success ? Regex.Replace(m.Groups["m"].Value.Trim(), @"\s+", " ") : "(no message)";
            return message.Length > 160 ? message[..160] + "..." : message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Windows' Application log, "Application Error" / WER entries naming this exe, from the run's start to a minute after its end.</summary>
    public static string? FindWindowsCrashEvent(string exeFileName, DateTime startUtc, DateTime endUtc)
    {
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, EventQuery(startUtc, endUtc)));
            for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    string text = record.FormatDescription() ?? "";
                    if (!text.Contains(exeFileName, StringComparison.OrdinalIgnoreCase)) continue;
                    var module = FaultingModule().Match(text);
                    return module.Success ? $"Windows logged a crash (faulting module {module.Groups["module"].Value.Trim()})" : "Windows logged a crash";
                }
            }
        }
        catch (Exception e) when (e is EventLogException or UnauthorizedAccessException or InvalidOperationException) { }
        return null;
    }

    /// <summary>XPath for crash entries from the run's start to a minute after its end. Internal so a test can prove it is valid.</summary>
    internal static string EventQuery(DateTime startUtc, DateTime endUtc)
    {
        static string At(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.000Z");
        return "*[System[(Provider[@Name='Application Error'] or Provider[@Name='Windows Error Reporting']) and " +
               $"TimeCreated[@SystemTime>='{At(startUtc)}' and @SystemTime<='{At(endUtc.AddSeconds(60))}']]]";
    }

    private static string Duration(double seconds) =>
        seconds < 120 ? $"{seconds:0}s" : seconds < 7200 ? $"{seconds / 60:0} min" : $"{seconds / 3600:0.#} h";
}
