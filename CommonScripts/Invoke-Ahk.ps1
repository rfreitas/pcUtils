<#
.SYNOPSIS
    Runs an AutoHotkey v2 script via ahkrun.exe, piping all output (including
    compile errors, runtime exceptions, and warnings) to the console.

.PARAMETER ScriptPath
    Path to the .ahk script to execute.

.PARAMETER ScriptArgs
    Arguments forwarded to the AHK script.

.PARAMETER Timeout
    Optional timeout in milliseconds. Exits with code 124 on timeout.

.EXAMPLE
    .\CommonScripts\Invoke-Ahk.ps1 .\RefreshRateOverlay\RefreshRateOverlayCLI.ahk status
#>
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$ScriptPath,

    [Parameter(ValueFromRemainingArguments)]
    [string[]]$ScriptArgs,

    [int]$Timeout = 0
)

$ahkrun = Join-Path $PSScriptRoot "AhkRunner\bin\Release\net8.0\win-x64\publish\ahkrun.exe"

if (-not (Test-Path $ahkrun)) {
    Write-Error "ahkrun.exe not found at: $ahkrun`nBuild it with: dotnet publish -c Release CommonScripts/AhkRunner/AhkRunner.csproj"
    exit 1
}

$invokeArgs = @(Resolve-Path $ScriptPath)
if ($Timeout -gt 0) {
    $invokeArgs = @("--timeout", $Timeout) + $invokeArgs
}
$invokeArgs += $ScriptArgs

& $ahkrun @invokeArgs
exit $LASTEXITCODE
