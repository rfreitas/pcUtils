---
name: Close AutoHotkey Program
description: Safely and forcefully closes a specific running AutoHotkey script process. Automatically handles UAC elevation for background admin processes.
---

# Close AutoHotkey Program

When you need to stop or restart a running AutoHotkey script, use this skill. The `CloseAhkProgram.ps1` script specifically targets the AutoHotkey runtime matching your desired script's filename, avoiding the accidental closure of unrelated AHK programs.

## Execution

The scripts are located in `CommonScripts` relative to the root of the workspace. When executing, ensure you are either at the root directory or adjust the path dynamically based on your current working directory.

You **must** provide the `-ScriptName` parameter.

**Required Execution (Closes a Specific Script):**
```powershell
powershell -ExecutionPolicy Bypass -File ".\CommonScripts\CloseAhkProgram.ps1" -ScriptName "YourScript.ahk"
```

## Important Notes
- **Administrator Elevation:** If the script determines it needs Administrator privileges to close the process, it will automatically prompt the user with a UAC window to securely elevate itself.
