param (
    [Parameter(Mandatory=$true)]
    [string]$ScriptPath
)

$scriptName = Split-Path (Split-Path $ScriptPath -Parent) -Leaf
$closeScript = Join-Path $PSScriptRoot "CloseAhkProgram.ps1"
$openScript = Join-Path $PSScriptRoot "OpenAhkProgram.ps1"

Write-Host "=============================================="
Write-Host " RELOADING SYSTEM: $scriptName"
Write-Host "=============================================="

# 1. Close existing process
& $closeScript -ScriptPath $ScriptPath

# 2. Wait explicitly to prevent race conditions 
Start-Sleep -Milliseconds 500

# 3. Open it up freshly
& $openScript -ScriptPath $ScriptPath
$openExitCode = $LASTEXITCODE

if ($openExitCode -ne 0) {
    Write-Warning "Reload stopped due to an error during the Open phase."
    exit $openExitCode
}

Write-Host "Reload complete successfully for $scriptName."
