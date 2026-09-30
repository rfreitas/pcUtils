---
name: Build .NET App
description: Builds and tests any .NET app in this workspace (RefreshRateOverlay.WPF, AggressiveScreensaver.NET, LGTV_brightness.NET, ...). Stops any running instance first to avoid file-lock errors.
---

# Build .NET App

Use this skill when making changes to a C# project and needing to verify the build is healthy before reloading or shipping.

## Script

`CommonScripts/BuildDotNetApp.ps1` — generic script that auto-detects the exe name from the `.csproj` (same convention `ReloadDotNetApp.ps1` uses), stops any running instance, runs the project's test suite (`<ProjectDir>.Tests`, if that folder exists), then builds (Debug).

## Usage

```powershell
& ".\CommonScripts\BuildDotNetApp.ps1" -ProjectDir "RefreshRateOverlay.WPF"
```

Pass the project folder (relative to the repo root or absolute) — **the one containing the app's own `.csproj`, not the test project**. The script:
1. Reads `<AssemblyName>` from the `.csproj` and stops any running process with that name
2. Runs `dotnet test` against `<ProjectDir>.Tests` if that folder exists (skips with a message otherwise — not every project has tests yet)
3. Runs `dotnet build` on the app's `.csproj`
4. Prints a pass/fail summary for both steps

## Exit codes

| Code | Meaning |
|------|---------|
| 0    | Tests (if any) and build both passed |
| 1    | Tests failed and/or build failed |

## Notes
- Always stop the running instance before building — the output `.exe` is locked while the app runs
- Test project discovery is convention-based: `<ProjectDir>.Tests` sitting next to the app project (e.g. `RefreshRateOverlay.WPF` → `RefreshRateOverlay.WPF.Tests`). If a project's test folder doesn't follow that convention, run `dotnet test` on it directly instead of this script.
- This intentionally does NOT hook into plain `dotnet build` (no MSBuild test target on the app csproj) — `ReloadDotNetApp.ps1`'s fast iterate-and-launch loop, and any IDE's incremental build, both call `dotnet build` directly and would slow down considerably if every build ran the full test suite (some WPF tests spin up an STA dispatcher and take ~1s each). Use this script specifically when you want both checks in one pass.
