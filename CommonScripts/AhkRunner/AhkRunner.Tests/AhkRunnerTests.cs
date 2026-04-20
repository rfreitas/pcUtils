using System.Diagnostics;
using Xunit;

namespace AhkRunner.Tests;

public class AhkRunnerTests
{
    // AppContext.BaseDirectory = AhkRunner/AhkRunner.Tests/bin/<config>/net8.0/
    // Four levels up lands at AhkRunner/
    private static readonly string AhkRunExe = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "bin", "Release", "net8.0", "win-x64", "publish", "ahkrun.exe"));

    private static readonly string ScriptsDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", ".."));

    private static async Task<(string Output, int ExitCode)> Run(
        string scriptFile, string[]? args = null, int timeoutMs = 0)
    {
        var psi = new ProcessStartInfo
        {
            FileName = AhkRunExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (timeoutMs > 0)
        {
            psi.ArgumentList.Add("--timeout");
            psi.ArgumentList.Add(timeoutMs.ToString());
        }

        psi.ArgumentList.Add(Path.Combine(ScriptsDir, scriptFile));
        foreach (var a in args ?? [])
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        var output = (await stdoutTask) + (await stderrTask);
        return (output, proc.ExitCode);
    }

    [Fact]
    public async Task NoArgs_PrintsUsage_ExitsOne()
    {
        var psi = new ProcessStartInfo
        {
            FileName = AhkRunExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        Assert.Equal(1, proc.ExitCode);
        Assert.Contains("Usage", stderr);
    }

    [Fact]
    public async Task CompileError_ExitsTwo_OutputContainsErrorDetails()
    {
        var (output, exitCode) = await Run("test_compile_error.ahk");

        Assert.Equal(2, exitCode);
        Assert.Contains("Missing", output);
        Assert.Contains("test_compile_error.ahk", output);
    }

    [Fact]
    public async Task RuntimeError_ExitsOne_OutputContainsRuntimeError()
    {
        var (output, exitCode) = await Run("test_runtime_error.ahk");

        Assert.Equal(1, exitCode);
        Assert.Contains("RUNTIME ERROR", output);
        Assert.Contains("intentional runtime error", output);
        Assert.Contains("test_runtime_error.ahk", output);
    }

    [Fact]
    public async Task RuntimeError_OutputBeforeThrow_IsIncluded()
    {
        var (output, _) = await Run("test_runtime_error.ahk");

        Assert.Contains("before error", output);
    }

    [Fact]
    public async Task Timeout_ExitsWithCode124()
    {
        var (_, exitCode) = await Run("test_timeout.ahk", timeoutMs: 500);

        Assert.Equal(124, exitCode);
    }

    [Fact]
    public async Task Timeout_OutputBeforeKill_IsIncluded()
    {
        var (output, _) = await Run("test_timeout.ahk", timeoutMs: 500);

        Assert.Contains("started", output);
    }
}
