---
name: Build .NET App
description: Builds and tests a .NET WinForms app in this workspace (e.g. AggressiveScreensaver.NET). Stops any running instance first to avoid file-lock errors.
---

# Build .NET App

Use this skill when making changes to a C# project and needing to verify the build is healthy before reloading or shipping.

## Steps

1. **Kill any running instance** — prevents file-lock errors on the output `.exe`
2. **Run tests** — `dotnet test` the matching `.Tests` project
3. **Build** — `dotnet build` the app project

## Commands

### Kill running instance
```powershell
Stop-Process -Name "AggressiveScreensaver" -Force -ErrorAction SilentlyContinue
```

### Run tests
```powershell
dotnet test AggressiveScreensaver.NET.Tests/ --verbosity normal
```

### Build (Debug)
```powershell
dotnet build AggressiveScreensaver.NET/ -c Debug 2>&1 | Select-Object -Last 6
```

### Build + Publish (Release — single-file exe)
```powershell
& ".\AggressiveScreensaver.NET\publish.ps1"
```

## Exit codes

| Code | Meaning |
|------|---------|
| 0    | Build/tests passed |
| 1    | Build failed or tests failed |

## Notes
- Always kill the running process before building — the output `.exe` is locked while the app runs
- The `.Tests` project is `AggressiveScreensaver.NET.Tests/`
- Test results include `PowercfgParserTests`, `BlockingFormatterTests`, and `SliderRenderTest`
- `SliderRenderTest` saves a screenshot to `AggressiveScreensaver.NET.Tests/slider_render.png` for visual inspection
