param (
    [Parameter(Mandatory=$true)]
    [string]$ScriptPath
)

# Elevate Reload wrapper early so both Close and Open operations happen in Admin scope
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Administrator privileges may be required to cleanly terminate $ScriptPath."
    Write-Host "Prompting for elevation..."
    Start-Process powershell -ArgumentList "-ExecutionPolicy Bypass -File `"$PSCommandPath`" -ScriptPath `"$ScriptPath`"" -Verb RunAs
    exit
}

$scriptName = Split-Path $ScriptPath -Leaf
$closeScript = Join-Path $PSScriptRoot "CloseAhkProgram.ps1"
$openScript = Join-Path $PSScriptRoot "OpenAhkProgram.ps1"

Write-Host "=============================================="
Write-Host " RELOADING SYSTEM: $scriptName"
Write-Host "=============================================="

# 1. Close existing process
& $closeScript -ScriptName $scriptName

# 2. Wait explicitly to prevent race conditions 
Start-Sleep -Seconds 1

# 3. Open it up freshly
& $openScript -ScriptPath $ScriptPath

Start-Sleep -Seconds 2
Write-Host "Reload complete successfully for $scriptName."
