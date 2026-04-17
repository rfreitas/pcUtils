---
name: Reload AutoHotkey Program
description: Safely restarts a specified AutoHotkey script using the CommonScripts. Automatically handles UAC elevation so the new instance is properly launched with Admin rights.
---

# Reload AutoHotkey Program

Use this skill when you make logic changes to a script and need to test them by running fresh. The `ReloadAhkProgram.ps1` wrapper automatically stops the existing instances of your targeted file and launches the script anew.

## Execution

The script is located in `CommonScripts` relative to the root of the workspace. When executing, ensure you are either at the root directory or adjust the path dynamically based on your current working directory. 

You **must** provide the `-ScriptPath` parameter.

**Required Execution (Reloads a Specific Script):**
```powershell
powershell -ExecutionPolicy Bypass -File ".\CommonScripts\ReloadAhkProgram.ps1" -ScriptPath ".\Relative\Path\To\YourScript.ahk"
```

## Important Notes
- **Administrator Elevation:** The script checks for Administrator privileges early. If it is not already elevated, it will prompt for UAC to safely re-launch itself so that both the process termination and process initialization steps run seamlessly with full permissions.
