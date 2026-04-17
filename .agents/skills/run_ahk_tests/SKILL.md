---
name: Run AutoHotkey Tests
description: Executes the test suite for the AggressiveScreensaver to validate pure functions and mocked hardware.
---

# Run AutoHotkey Tests

This skill helps you validate logic changes to the `AggressiveScreensaver.ahk` workspace using the centralized test runner located in the `AggressiveScreensaver/tests/` directory.

## Testing Standards
The workspace follows specific testing guidelines outlined in `.github/copilot-instructions.md`. Tests heavily utilize **Dependency Injection** through functions like `GetKeyStateFn` and `GetTimeMsFn` to mock hardware boundaries.

## Running Tests
To run the automated tests and see console output naturally printed, use the following PowerShell command:

```powershell
Start-Process -FilePath "C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe" -ArgumentList "C:\Users\ricfr\Documents\AutoHotkey\AggressiveScreensaver\tests\run_all_tests.ahk" -NoNewWindow -Wait
```

## Creating New Tests
If modifying the main script with new features, you must write corresponding test scenarios.
1. Place new test scripts named `test_[feature].ahk` in the `AggressiveScreensaver/tests/` directory.
2. `#Include ..\AggressiveScreensaver.ahk` at the top.
3. Hook into dependencies if necessary (e.g. override global variables like `GetKeyStateFn` or define new mocks).
4. Do **not** use real system queries or execute blocking hardware checks directly in your tests.
5. Finally, append your new test filename to the `testsToRun` array inside `AggressiveScreensaver/tests/run_all_tests.ahk`.
