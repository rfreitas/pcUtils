# Agent Instructions for AutoHotkey

These instructions define how agents should handle AutoHotkey (AHK) scripts in this workspace.

## 1. Script Validation
**ALWAYS** validate script syntax before attempting to run or reload a script. AutoHotkey v2 provides a `/Validate` switch that checks for errors without executing the code.

**Command Pattern:**
```powershell
& "C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe" /Validate "path\to\script.ahk"
```

- **If it outputs text:** There are syntax errors. Fix them.
- **If it outputs nothing:** The syntax is valid.

## 2. Reloading Scripts with Error Checking
After validation, reload the script. Capture potential runtime errors by using the `/ErrorStdOut` switch.

**Command Pattern:**
```powershell
# Kill existing instance and restart
Get-Process AutoHotkey* -ErrorAction SilentlyContinue | Stop-Process -Force;
& "C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe" /ErrorStdOut "path\to\script.ahk"
```

## 3. Startup Links for New Scripts
When creating a **NEW** script (excluding temporary debug scripts), **ALWAYS** offer to create a startup shortcut for the user.

**Startup Folder Path:**
`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup`

**Procedure:**
1.  Ask the user if they want the script to run on startup.
2.  If yes, create a `.lnk` file in the Startup folder pointing to the script.
    - Uses PowerShell to create the shortcut:
    ```powershell
    $s = (New-Object -ComObject WScript.Shell).CreateShortcut("$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\ScriptName.lnk")
    $s.TargetPath = "path\to\script.ahk"
    $s.Save()
    ```
