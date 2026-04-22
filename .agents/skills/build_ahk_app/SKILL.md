---
name: Build AutoHotkey App
description: Validates an AHK app by running its tests (if any) then doing a startup check to catch compile errors and runtime exceptions.
---

# Build AutoHotkey App

Use `CommonScripts/BuildAhkApp.ps1` after making changes to an AHK app to verify it is healthy before reloading or shipping.

## What it does
1. **Tests** — runs all `test_*.ahk` files in `<AppFolder>\tests\` via `RunTests.ps1` (skipped if no `tests\` folder)
2. **Startup check** — runs the main `.ahk` via `DebugRunAhk.ps1` with a 3s timeout to catch compile errors and startup runtime exceptions

The main script is resolved as `<AppFolder>\<AppFolderName>.ahk`.

## Usage

```powershell
powershell -ExecutionPolicy Bypass -File ".\CommonScripts\BuildAhkApp.ps1" -AppFolder ".\<AppFolder>"
```

## Examples

```powershell
# Build AggressiveScreensaver
powershell -ExecutionPolicy Bypass -File ".\CommonScripts\BuildAhkApp.ps1" -AppFolder ".\AggressiveScreensaver"

# Build RefreshRateOverlay
powershell -ExecutionPolicy Bypass -File ".\CommonScripts\BuildAhkApp.ps1" -AppFolder ".\RefreshRateOverlay"
```

## Exit codes

| Code | Meaning |
|------|---------|
| 0    | Build passed |
| 1    | Tests failed or startup check failed |
