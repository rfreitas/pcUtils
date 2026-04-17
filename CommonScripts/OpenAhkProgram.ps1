param (
    [Parameter(Mandatory=$true)]
    [string]$ScriptPath,
    [string]$AhkPath = "C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe"
)

Write-Host "Starting AutoHotkey script: $ScriptPath"

if (Test-Path $AhkPath) {
    if (Test-Path $ScriptPath) {
        Start-Process -FilePath $AhkPath -ArgumentList "`"$ScriptPath`""
        Write-Host "Started successfully."
    } else {
        Write-Warning "Could not find AutoHotkey script at: $ScriptPath"
    }
} else {
    Write-Warning "Could not find AutoHotkey executable at: $AhkPath"
}

Start-Sleep -Seconds 2
