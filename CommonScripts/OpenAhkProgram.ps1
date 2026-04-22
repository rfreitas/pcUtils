param (
    [Parameter(Mandatory=$true)]
    [string[]]$ScriptPaths,
    [string]$AhkPath = "C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe"
)

if (-not (Test-Path $AhkPath)) {
    Write-Warning "Could not find AutoHotkey executable at: $AhkPath"
    exit 1
}

foreach ($ScriptPath in $ScriptPaths) {
    Write-Host "Starting AutoHotkey script: $ScriptPath"
    if (Test-Path $ScriptPath) {
        Start-Process -FilePath $AhkPath -ArgumentList "`"$ScriptPath`""
        Write-Host "Started successfully."
    } else {
        Write-Warning "Could not find AutoHotkey script at: $ScriptPath"
        exit 1
    }
}
