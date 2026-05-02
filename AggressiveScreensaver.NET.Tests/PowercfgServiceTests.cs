using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace AggressiveScreensaver.NET.Tests;

/// <summary>
/// Tests for the async stderr drain pattern used in PowercfgService.RunPowercfg().
/// RunPowercfg() is private, so these tests validate the pattern directly using
/// cmd.exe as a stand-in subprocess.
/// </summary>
public class PowercfgServiceTests
{
    // -------------------------------------------------------------------------
    // Helper: same pattern as RunPowercfg() — read stdout sync, drain stderr async
    // -------------------------------------------------------------------------

    private static string RunAndCapture(string fileName, string arguments, int timeoutMs = 5000)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = fileName,
                Arguments              = arguments,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            }
        };
        proc.Start();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(timeoutMs);
        stderrTask.GetAwaiter().GetResult();
        return output;
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void AsyncStderrDrain_StdoutReturnedCorrectly()
    {
        // A process that writes to stdout only — basic sanity check.
        string output = RunAndCapture("cmd.exe", "/c echo hello");
        Assert.Contains("hello", output);
    }

    [Fact]
    public void AsyncStderrDrain_DoesNotDeadlockWhenStderrHasOutput()
    {
        // A process that writes to BOTH stdout and stderr.
        // Without async stderr drain, ReadToEnd(stdout) would block once the
        // 4 KB pipe buffer fills — this test would hang/timeout.
        string output = RunAndCapture(
            "cmd.exe",
            // Write 200 lines to stderr, then one line to stdout
            "/c FOR /L %i IN (1,1,200) DO @echo stderr line %i 1>&2 & echo stdout_done");

        Assert.Contains("stdout_done", output);
    }

    [Fact]
    public void AsyncStderrDrain_CompletesWithinTimeout()
    {
        var sw = Stopwatch.StartNew();
        RunAndCapture("cmd.exe", "/c echo done");
        sw.Stop();

        // Should complete well within 2 seconds for a trivial process
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"Took {sw.ElapsedMilliseconds} ms — possible deadlock or slowness");
    }
}
