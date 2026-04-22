param(
    [Parameter(Mandatory=$true)]
    [string]$AppFolder
)

$appName    = Split-Path $AppFolder -Leaf
$debugRun   = Join-Path $PSScriptRoot "DebugRunAhk.ps1"
$runTests   = Join-Path $PSScriptRoot "RunTests.ps1"
$mainScript = Join-Path $AppFolder "index.ahk"
$testsDir   = Join-Path $AppFolder "tests"

Write-Host ""
Write-Host "=============================================="
Write-Host " BUILD: $appName"
Write-Host "=============================================="

# 1. Validate main script exists
if (-not (Test-Path $mainScript)) {
    Write-Error "Main script not found: $mainScript"
    exit 1
}

# 2. Run tests if tests/ folder exists
if (Test-Path $testsDir) {
    Write-Host ""
    Write-Host "--- Step 1: Running Tests ---"
    & $runTests -AppFolder $AppFolder
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Warning "BUILD FAILED: Tests failed."
        exit 1
    }
} else {
    Write-Host ""
    Write-Host "--- Step 1: No tests/ folder found, skipping tests ---"
}

# 3. Debug run to catch compile errors and startup runtime exceptions
Write-Host ""
Write-Host "--- Step 2: Startup Check (compile + runtime) ---"
& $debugRun -Timeout 3000 $mainScript
$startupCode = $LASTEXITCODE

# Exit code 124 = timeout = script started fine and kept running (expected for GUI apps)
if ($startupCode -eq 124 -or $startupCode -eq 0) {
    Write-Host "Startup check passed."
} else {
    Write-Host ""
    Write-Warning "BUILD FAILED: Startup check failed (ExitCode: $startupCode)."
    exit 1
}

Write-Host ""
Write-Host "=============================================="
Write-Host " BUILD PASSED: $appName"
Write-Host "=============================================="
exit 0
