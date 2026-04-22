---
name: Run AutoHotkey Tests
description: Executes the test suite for any AHK app by auto-discovering all test_*.ahk files inside its tests/ subfolder.
---

# Run AutoHotkey Tests

Use `CommonScripts/RunTests.ps1` to run all tests for any app. Pass the app root folder — it auto-discovers every `test_*.ahk` inside `<AppFolder>\tests\` and runs each through `DebugRunAhk.ps1`.

## Running Tests

```powershell
# Run tests for AggressiveScreensaver
powershell -ExecutionPolicy Bypass -File ".\CommonScripts\RunTests.ps1" -AppFolder ".\AggressiveScreensaver"

# Run tests for any other app
powershell -ExecutionPolicy Bypass -File ".\CommonScripts\RunTests.ps1" -AppFolder ".\<AppFolder>"
```

## Creating New Tests
If modifying a script with new features, write corresponding test scenarios.
1. Place new test scripts named `test_[feature].ahk` in the app's `tests/` directory.
2. `#Include` the main script at the top.
3. Hook into dependencies if necessary (e.g. override global variables like `GetKeyStateFn` or define new mocks).
4. Do **not** use real system queries or execute blocking hardware checks directly in your tests.

No registration needed — `RunTests.ahk` picks up any `test_*.ahk` file automatically.
