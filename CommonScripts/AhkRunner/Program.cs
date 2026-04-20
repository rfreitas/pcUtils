using System.Diagnostics;

int timeoutMs = -1;
var scriptArgs = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--timeout" && i + 1 < args.Length)
    {
        timeoutMs = int.Parse(args[++i]);
    }
    else
    {
        scriptArgs.Add(args[i]);
    }
}

if (scriptArgs.Count == 0)
{
    Console.Error.WriteLine("Usage: ahkrun [--timeout <ms>] <script.ahk> [args...]");
    return 1;
}

string? ahkExe = FindAhkExe();
if (ahkExe == null)
{
    Console.Error.WriteLine("ahkrun: AutoHotkey64.exe not found");
    return 1;
}

// Wrap the target script in a temp file that installs an OnError handler.
// AHK runtime exceptions otherwise show a GUI dialog and never reach stderr.
string targetScript = Path.GetFullPath(scriptArgs[0]);
// Temp file must live next to the target so A_ScriptDir resolves correctly
// for any #Include directives inside the target that reference A_ScriptDir.
string tempScript = Path.Combine(
    Path.GetDirectoryName(targetScript)!,
    $"__ahkrun_{Guid.NewGuid():N}.ahk");

// Escape backslashes for AHK string literal
string escapedTarget = targetScript.Replace("\\", "\\\\");

File.WriteAllText(tempScript, $$"""
    #Warn All, StdOut
    __ahkrun_OnError(err, mode) {
        FileAppend("RUNTIME ERROR: " err.Message " [" err.File ":" err.Line "]`n", "*")
        ExitApp(1)
    }
    OnError(__ahkrun_OnError)
    #Include "{{escapedTarget}}"
    """);

// /ErrorStdOut must be unquoted — AHK does not recognise it when quoted.
// Script args beyond index 0 (the script path) are forwarded as-is.
var forwardedArgs = scriptArgs.Skip(1).Select(QuoteArg);
var arguments = $"/ErrorStdOut {QuoteArg(tempScript)} {string.Join(" ", forwardedArgs)}";

var psi = new ProcessStartInfo
{
    FileName = ahkExe,
    Arguments = arguments,
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    CreateNoWindow = true,
};

int exitCode;
try
{
    using var proc = Process.Start(psi)!;

    // Drain stdout and stderr concurrently to prevent deadlock
    var stdoutTask = proc.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
    var stderrTask = proc.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());

    if (timeoutMs >= 0)
    {
        bool exited = proc.WaitForExit(timeoutMs);
        if (!exited)
        {
            proc.Kill(entireProcessTree: true);
            await stdoutTask;
            await stderrTask;
            exitCode = 124;
            goto done;
        }
    }
    else
    {
        proc.WaitForExit();
    }

    await stdoutTask;
    await stderrTask;
    exitCode = proc.ExitCode;
}
finally
{
    File.Delete(tempScript);
}

done:
return exitCode;

static string QuoteArg(string arg)
{
    if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
        return arg;
    return '"' + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + '"';
}

static string? FindAhkExe()
{
    var candidates = new[]
    {
        @"C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe",
        @"C:\Program Files\AutoHotkey\AutoHotkey64.exe",
    };

    foreach (var c in candidates)
        if (File.Exists(c)) return c;

    foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
    {
        var full = Path.Combine(dir.Trim(), "AutoHotkey64.exe");
        if (File.Exists(full)) return full;
    }

    return null;
}
