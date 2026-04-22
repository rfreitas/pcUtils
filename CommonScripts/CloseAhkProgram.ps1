param (
    [Parameter(Mandatory=$true)]
    [string[]]$ScriptPaths
)

# Check if running as Admin to close elevated processes; if not, prompt to elevate
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    $serialized = $ScriptPaths -join "|"
    Start-Process powershell -ArgumentList "-WindowStyle Hidden -ExecutionPolicy Bypass -File `"$PSCommandPath`" -ScriptPaths `"$serialized`"" -Verb RunAs -Wait
    exit
}

# When re-launched elevated, ScriptPaths may arrive as a single pipe-delimited string
if ($ScriptPaths.Count -eq 1 -and $ScriptPaths[0] -match '\|') {
    $ScriptPaths = $ScriptPaths[0] -split '\|'
}

foreach ($ScriptPath in $ScriptPaths) {
    Write-Host "Looking for AutoHotkey processes running '$ScriptPath'..."
    $ahkProcesses = Get-CimInstance Win32_Process -Filter "Name like 'AutoHotkey%'" | Where-Object { $_.CommandLine -match [regex]::Escape($ScriptPath) }

    if ($ahkProcesses) {
        foreach ($proc in $ahkProcesses) {
            Write-Host "Force closing process ID $($proc.ProcessId)..."
            Stop-Process -Id $proc.ProcessId -Force -ErrorAction SilentlyContinue
        }
        Write-Host "Closed $($ahkProcesses.Count) process(es) for '$ScriptPath'."
    } else {
        Write-Host "No running instances of '$ScriptPath' found."
    }
}
