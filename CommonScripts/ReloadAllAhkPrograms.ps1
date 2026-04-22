# Self-elevate once so all child scripts skip individual UAC prompts
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell -ArgumentList "-ExecutionPolicy Bypass -File `"$PSCommandPath`"" -Verb RunAs -Wait
    exit
}

$rootDir = Split-Path $PSScriptRoot -Parent
$reloadScript = Join-Path $PSScriptRoot "ReloadAhkProgram.ps1"

$scripts = @(
    "AggressiveScreensaver\AggressiveScreensaver.ahk",
    "LGTV_brightness.ahk",
    "RevealTaskbar.ahk",
    "RefreshRateOverlay\RefreshRateOverlay.ahk"
)

foreach ($script in $scripts) {
    $fullPath = Join-Path $rootDir $script
    Write-Host ""
    & $reloadScript -ScriptPath $fullPath
}

Write-Host ""
Write-Host "=============================================="
Write-Host " ALL AHK PROGRAMS RELOADED"
Write-Host "=============================================="
