param (
    [Parameter(Mandatory=$true)]
    [string[]]$ScriptPaths
)

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    # Serialize paths as a single comma-joined argument and re-launch elevated
    $joined = $ScriptPaths -join "|"
    Start-Process powershell -ArgumentList "-ExecutionPolicy Bypass -File `"$PSCommandPath`" -ScriptPaths `"$joined`"" -Verb RunAs -Wait
    exit
}

# Handle pipe-joined string (when re-launched elevated with single string arg)
if ($ScriptPaths.Count -eq 1 -and $ScriptPaths[0] -match '\|') {
    $ScriptPaths = $ScriptPaths[0] -split '\|'
}

foreach ($scriptPath in $ScriptPaths) {
    Write-Host "Looking for: $scriptPath"
    $procs = Get-CimInstance Win32_Process -Filter "Name like 'AutoHotkey%'" |
             Where-Object { $_.CommandLine -match [regex]::Escape($scriptPath) }
    if ($procs) {
        foreach ($proc in $procs) {
            Write-Host "  Closing PID $($proc.ProcessId)..."
            Stop-Process -Id $proc.ProcessId -Force -ErrorAction SilentlyContinue
        }
    } else {
        Write-Host "  Not running."
    }
}
