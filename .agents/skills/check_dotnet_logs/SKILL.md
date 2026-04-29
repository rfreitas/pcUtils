---
name: Check .NET App Logs
description: Reads the AggressiveScreensaver.NET log file and checks for crash minidumps to diagnose errors or silent crashes.
---

# Check .NET App Logs

Use this skill when the app crashed, stopped unexpectedly, or behaves incorrectly.

## Run the diagnostics script

```powershell
powershell -ExecutionPolicy Bypass -File ".\CommonScripts\CheckDotNetLogs.ps1"
```

This checks **all** crash/log locations in order:
1. App log — `%LOCALAPPDATA%\AggressiveScreensaver\AggressiveScreensaver.log`
2. WER dumps — `%LOCALAPPDATA%\CrashDumps\AggressiveScreensaver*.dmp` (auto-collected by Windows for every unhandled crash)
3. WER Report Archive — `%LOCALAPPDATA%\Microsoft\Windows\WER\ReportArchive\`
4. WER Report Queue — pending, not yet submitted
5. Event Viewer — `Application Error` provider (native AVs, fault offset)
6. Event Viewer — `.NET Runtime` provider (managed unhandled exceptions with stack trace)

For WER dumps it automatically runs `dotnet-dump analyze --command clrstack` on the most recent one.

## What the app log levels mean

| Prefix | Source |
|--------|--------|
| `Starting.` | Normal startup |
| `HANDLED ERROR` | Caught exception (non-fatal) |
| `CRITICAL ERROR` | `Application.ThreadException` — WinForms UI thread |
| `UNHANDLED CLR EXCEPTION` | `AppDomain.UnhandledException` — any managed thread |

## Notes

- Native AVs (`0xc0000005`) bypass all managed handlers in .NET 8 — they show up only in Event Viewer and WER dumps, never in the app log
- If `dotnet-dump` is not installed: `dotnet tool install -g dotnet-dump`
- The `.NET Runtime` Event Viewer source often has a full managed stack trace for managed crashes even without a dump

