param(
    [Parameter(Mandatory=$true)]
    [string]$AppFolder
)

$appName    = Split-Path $AppFolder -Leaf
$debugRun   = Join-Path $PSScriptRoot "DebugRunAhk.ps1"
$testsDir   = Join-Path $AppFolder "tests"

if (-not (Test-Path $testsDir)) {
    Write-Host "No tests/ folder found in: $AppFolder"
    exit 0
}

$testFiles = Get-ChildItem -Path $testsDir -Filter "test_*.ahk" | Sort-Object Name

if ($testFiles.Count -eq 0) {
    Write-Host "No test files found in: $testsDir"
    exit 0
}

Write-Host ""
Write-Host "========================================="
Write-Host "Test Suite: $appName"
Write-Host "========================================="
Write-Host ""

$totalPassed = 0
$totalFailed = 0

foreach ($testFile in $testFiles) {
    Write-Host ">>> RUNNING: $($testFile.Name)"
    Write-Host "-----------------------------------------"

    & $debugRun -Timeout 30000 $testFile.FullName
    $exitCode = $LASTEXITCODE

    if ($exitCode -eq 0) {
        $totalPassed++
        Write-Host ""
        Write-Host "[PASSED]"
    } else {
        $totalFailed++
        Write-Host ""
        Write-Host "[FAILED] (ExitCode: $exitCode)"
    }

    Write-Host "-----------------------------------------"
    Write-Host ""
}

Write-Host "========================================="
Write-Host "RESULTS: $totalPassed passed, $totalFailed failed"
Write-Host "========================================="

if ($totalFailed -gt 0) { exit 1 } else { exit 0 }
