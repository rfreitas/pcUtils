---
name: Reload .NET App
description: Stops the running AggressiveScreensaver.NET process, rebuilds it, and relaunches with UAC elevation.
---

# Reload .NET App

Use this skill after making code changes to AggressiveScreensaver.NET and wanting to test them live.

## Execution

Run all three steps in sequence — stop, build, launch.

### 1. Stop the running instance
```powershell
Stop-Process -Name "AggressiveScreensaver" -Force -ErrorAction SilentlyContinue
```

### 2. Rebuild (Debug)
```powershell
dotnet build AggressiveScreensaver.NET/ -c Debug 2>&1 | Select-Object -Last 4
```

### 3. Relaunch with elevation
```powershell
Start-Process "AggressiveScreensaver.NET\bin\Debug\net8.0-windows\win-x64\AggressiveScreensaver.exe" -Verb RunAs
```

## One-liner
```powershell
Stop-Process -Name "AggressiveScreensaver" -Force -ErrorAction SilentlyContinue; dotnet build AggressiveScreensaver.NET/ -c Debug 2>&1 | Select-Object -Last 4; Start-Process "AggressiveScreensaver.NET\bin\Debug\net8.0-windows\win-x64\AggressiveScreensaver.exe" -Verb RunAs
```

## Notes
- `-Verb RunAs` is required — the app needs admin rights for its screensaver/power management features
- Always stop first; building while the process is running causes file-lock MSB3027 errors
- Logs are written to `%LOCALAPPDATA%\AggressiveScreensaver\AggressiveScreensaver.log`
- Native AVs (`0xc0000005`) are not caught by managed handlers — check Event Viewer if the app dies silently
