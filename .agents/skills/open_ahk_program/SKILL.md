---
name: Open AutoHotkey Program
description: Starts a specified AutoHotkey script using the installed AHK v2 runtime. Validates paths before launching.
---

# Open AutoHotkey Program

Use this skill when you need to start a new AutoHotkey script process for testing, reloading, or regular operations. The `OpenAhkProgram.ps1` script performs sanity checks on the existence of the script and the AHK executable before firing it up.

## Execution

The scripts are located in `CommonScripts` relative to the root of the workspace. When executing, ensure you are either at the root directory or adjust the path dynamically based on your current working directory.

You **must** provide the `-ScriptPath` parameter.

**Required Execution (Starts a Specific Script):**
```powershell
powershell -ExecutionPolicy Bypass -File ".\CommonScripts\OpenAhkProgram.ps1" -ScriptPath ".\Relative\Path\To\YourScript.ahk"
```
