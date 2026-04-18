param (
    [Parameter(Mandatory=$true)]
    [string]$ScriptPath
)

# Convert relative path to absolute path before any process elevation
if (-not [System.IO.Path]::IsPathRooted($ScriptPath)) {
    $ScriptPath = "$((Resolve-Path $ScriptPath -ErrorAction Stop).Path)"
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
