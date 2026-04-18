param (
    [Parameter(Mandatory=$true)]
    [string]$ScriptName
)

# Check if running as Admin to close elevated processes; if not, prompt to elevate
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Administrator privileges required to close $ScriptName."
    Write-Host "Prompting for elevation..."
    Start-Process powershell -ArgumentList "-WindowStyle Hidden -ExecutionPolicy Bypass -File `"$PSCommandPath`" -ScriptName `"$ScriptName`"" -Verb RunAs -Wait
    exit
}

Write-Host "Looking for AutoHotkey processes running '$ScriptName'..."

# Check the CommandLine of processes to exclusively find the one running our script
$ahkProcesses = Get-CimInstance Win32_Process -Filter "Name like 'AutoHotkey%'" | Where-Object { $_.CommandLine -match [regex]::Escape($ScriptName) }

if ($ahkProcesses) {
    foreach ($proc in $ahkProcesses) {
        Write-Host "Force closing process ID $($proc.ProcessId)..."
        Stop-Process -Id $proc.ProcessId -Force -ErrorAction SilentlyContinue
    }
    Write-Host "Successfully closed $($ahkProcesses.Count) process(es) running '$ScriptName'."
} else {
    Write-Host "No running instances of '$ScriptName' found."
}
