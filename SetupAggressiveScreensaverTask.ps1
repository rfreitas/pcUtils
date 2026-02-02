$TaskName = "AggressiveScreensaver_AutoStart"
$AhkPath = "C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe"
$ScriptPath = "c:\Users\ricfr\Documents\AutoHotkey\AggressiveScreensaver.ahk"
$WorkDir = "c:\Users\ricfr\Documents\AutoHotkey"

Write-Host "Setting up Scheduled Task: $TaskName"
Write-Host "AHK Path: $AhkPath"
Write-Host "Script Path: $ScriptPath"

# Action: Run AutoHotkey with the script
$Action = New-ScheduledTaskAction -Execute $AhkPath -Argument "`"$ScriptPath`"" -WorkingDirectory $WorkDir

# Trigger: At Logon
$Trigger = New-ScheduledTaskTrigger -AtLogOn

# settings: Allow starting if on battery, don't stop if on battery, wake to run
$Settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Days 0) -Priority 4

# Principal: Run with highest privileges (Admin) for the current user
$Principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest

# Register
try {
    Register-ScheduledTask -TaskName $TaskName -Action $Action -Trigger $Trigger -Principal $Principal -Settings $Settings -Force
    Write-Host "Task registered successfully."
    
    # Attempt to start it immediately to verify
    Start-ScheduledTask -TaskName $TaskName
    Write-Host "Task started."
}
catch {
    Write-Error "Failed to register task. Ensure you are running this setup script as Administrator."
    Write-Error $_
}
