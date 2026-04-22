---
name: Run AHK Script
description: Executes any AutoHotkey v2 script and pipes compile errors, runtime exceptions, and warnings to the console — no dialogs.
---

# Run AHK Script

Use this skill whenever you need to run an AHK script and capture its output, including errors and warnings. This replaces `Start-Process` and direct `AutoHotkey64.exe` invocations, which either suppress output or show GUI dialogs for errors.

## How it works

`CommonScripts/DebugRunAhk.ps1` calls `CommonScripts/AhkRunner/bin/Release/net8.0/win-x64/publish/ahkrun.exe`, which:
- Injects an `OnError` handler so runtime exceptions print to stdout instead of showing a dialog
- Sets `#Warn All, StdOut` so warnings print to stdout instead of showing a dialog
- Passes `/ErrorStdOut` to AHK so compile errors go to stderr
- Forwards the exit code (0 = success, 1 = runtime error, 2 = compile error, 124 = timeout)

## Usage

```powershell
# Basic run
powershell -ExecutionPolicy Bypass -File ".\CommonScripts\DebugRunAhk.ps1" ".\Path\To\Script.ahk" [script args...]

# With timeout (ms)
powershell -ExecutionPolicy Bypass -File ".\CommonScripts\DebugRunAhk.ps1" -Timeout 5000 ".\Path\To\Script.ahk" [script args...]
```

## Examples

```powershell
# Run CLI status check
powershell -ExecutionPolicy Bypass -File ".\CommonScripts\DebugRunAhk.ps1" ".\RefreshRateOverlay\RefreshRateOverlayCLI.ahk" status

# Run HDR sync test with 10s timeout
powershell -ExecutionPolicy Bypass -File ".\CommonScripts\DebugRunAhk.ps1" -Timeout 10000 ".\RefreshRateOverlay\RefreshRateOverlayCLI.ahk" test-hdr-sync
```

## Exit codes

| Code | Meaning |
|------|---------|
| 0    | Success |
| 1    | Runtime exception (message printed to stdout) |
| 2    | Compile/syntax error (message printed to stderr) |
| 124  | Timed out |

## Prerequisites

`ahkrun.exe` is built automatically when running tests:
```powershell
dotnet test -c Release ".\CommonScripts\AhkRunner\AhkRunner.sln"
```

Or build it manually:
```powershell
dotnet publish -c Release ".\CommonScripts\AhkRunner\AhkRunner.csproj"
```
