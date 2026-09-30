# BuildDotNetApp.ps1
# Stops a running .NET app, runs its test suite, then builds it (Debug).
# Generic across every .NET project in this repo — same auto-detection
# approach as ReloadDotNetApp.ps1 (AssemblyName read from the .csproj), plus
# convention-based test-project discovery (<ProjectDir>.Tests, the naming
# every project here already follows: RefreshRateOverlay.WPF.Tests,
# AggressiveScreensaver.NET.Tests, ...).
#
# Usage:
#   .\CommonScripts\BuildDotNetApp.ps1 -ProjectDir "RefreshRateOverlay.WPF"
#
# Exit codes:
#   0 - tests (if any) and build both passed
#   1 - tests failed and/or build failed

param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectDir
)

$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------------------
# Resolve paths
# ---------------------------------------------------------------------------
if (-not [System.IO.Path]::IsPathRooted($ProjectDir)) {
    $ProjectDir = Join-Path $PSScriptRoot ".." $ProjectDir
}
$ProjectDir = Resolve-Path $ProjectDir

$csproj = Get-ChildItem $ProjectDir -Filter "*.csproj" | Select-Object -First 1
if (-not $csproj) {
    Write-Error "No .csproj found in: $ProjectDir"
    exit 1
}

# Convention across this repo: the test project sits next to the app project
# as "<ProjectDir>.Tests" (not always present — e.g. TaskbarReveal.NET has
# no test project yet).
$testProjectDir = "$ProjectDir.Tests"
$hasTests = Test-Path $testProjectDir

# ---------------------------------------------------------------------------
# Read AssemblyName from .csproj (same fallback ReloadDotNetApp.ps1 uses)
# ---------------------------------------------------------------------------
[xml]$proj = Get-Content $csproj.FullName
$assemblyName = $proj.Project.PropertyGroup.AssemblyName |
    Where-Object { $_ } | Select-Object -First 1
if (-not $assemblyName) {
    $assemblyName = (Split-Path $ProjectDir -Leaf) -replace '\.NET$', '' -replace '\.Net$', ''
}

Write-Host "=============================================="
Write-Host " BUILD + TEST: $assemblyName"
Write-Host "=============================================="

# ---------------------------------------------------------------------------
# 1. Stop running instance — the output .exe is locked while the app runs
# ---------------------------------------------------------------------------
Write-Host "`n[1/3] Stopping running instance..."
$runningProc = Get-Process -Name $assemblyName -ErrorAction SilentlyContinue | Select-Object -First 1
if ($runningProc) {
    try {
        Stop-Process -Id $runningProc.Id -Force -ErrorAction Stop
    } catch {
        # Same elevated-process fallback as ReloadDotNetApp.ps1.
        Write-Host "  Access denied — requesting elevation to stop it..."
        Start-Process powershell -Verb RunAs -Wait -ArgumentList @(
            "-NoProfile", "-Command",
            "Stop-Process -Id $($runningProc.Id) -Force -ErrorAction SilentlyContinue"
        )
    }
    Start-Sleep -Milliseconds 400
}

# ---------------------------------------------------------------------------
# 2. Tests
# ---------------------------------------------------------------------------
$testsExitCode = 0
if ($hasTests) {
    Write-Host "`n[2/3] Running tests: $testProjectDir"
    dotnet test $testProjectDir --verbosity normal
    $testsExitCode = $LASTEXITCODE
} else {
    Write-Host "`n[2/3] No test project found at $testProjectDir — skipping."
}

# ---------------------------------------------------------------------------
# 3. Build
# ---------------------------------------------------------------------------
Write-Host "`n[3/3] Building $($csproj.Name)..."
dotnet build $csproj.FullName -c Debug 2>&1 | Select-Object -Last 6
$buildExitCode = $LASTEXITCODE

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
Write-Host "`n=============================================="
Write-Host " RESULTS: $assemblyName"
Write-Host "   Tests: $(if (-not $hasTests) { 'skipped' } elseif ($testsExitCode -eq 0) { 'passed' } else { 'FAILED' })"
Write-Host "   Build: $(if ($buildExitCode -eq 0) { 'passed' } else { 'FAILED' })"
Write-Host "=============================================="

if ($testsExitCode -ne 0 -or $buildExitCode -ne 0) { exit 1 } else { exit 0 }
