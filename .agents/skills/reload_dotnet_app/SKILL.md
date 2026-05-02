---
name: Reload .NET App
description: Stops the running AggressiveScreensaver.NET process, rebuilds it, and relaunches with UAC elevation.
---

# Reload .NET App

Use this skill after making code changes to a .NET WinForms app and wanting to test it live.

## Script

`CommonScripts/ReloadDotNetApp.ps1` — generic script that auto-detects the exe name from the `.csproj`, stops the running process, rebuilds (Debug), and relaunches with `-Verb RunAs`.

## Usage

```powershell
& ".\CommonScripts\ReloadDotNetApp.ps1" -ProjectDir "AggressiveScreensaver.NET"
```

Pass the project folder (relative to the repo root or absolute). The script:
1. Reads `<AssemblyName>` from the `.csproj`
2. Stops any process with that name
3. Runs `dotnet build` on the `.csproj`
4. Resolves the Debug output exe (handles both flat and RID sub-folder layouts)
5. Launches with `-Verb RunAs` for UAC elevation

## Notes
- `-Verb RunAs` is required — the app needs admin rights for powercfg/screensaver features
- Always stop before building — the output `.exe` is locked while the app runs (MSB3027)
- Logs are written to `%LOCALAPPDATA%\AggressiveScreensaver\AggressiveScreensaver.log`
- Native AVs (`0xc0000005`) bypass managed handlers — check Event Viewer if the app dies silently
