$rootDir    = Split-Path $PSScriptRoot -Parent
$closeScript = Join-Path $PSScriptRoot "CloseAhkProgram.ps1"
$openScript  = Join-Path $PSScriptRoot "OpenAhkProgram.ps1"

$scripts = @(
    "AggressiveScreensaver\index.ahk",
    "LGTV_brightness\index.ahk",
    "RevealTaskbar\index.ahk",
    "RefreshRateOverlay\index.ahk"
)

$fullPaths = $scripts | ForEach-Object { Join-Path $rootDir $_ }

# Step 1: Close all in one elevated call
Write-Host "Closing all AHK apps..."
& $closeScript -ScriptPaths $fullPaths

# Step 2: Wait for processes to fully exit
Start-Sleep -Milliseconds 500

# Step 3: Open all
Write-Host ""
& $openScript -ScriptPaths $fullPaths

Write-Host ""
Write-Host "=============================================="
Write-Host " ALL AHK PROGRAMS RELOADED"
Write-Host "=============================================="
