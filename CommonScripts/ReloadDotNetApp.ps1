# ReloadDotNetApp.ps1
# Stops a running .NET app, rebuilds it (Debug), and relaunches with elevation.
#
# Usage:
#   .\CommonScripts\ReloadDotNetApp.ps1 -ProjectDir "AggressiveScreensaver.NET"
#
# The exe name is auto-detected from the <AssemblyName> in the .csproj.
# If not found, it falls back to the project folder name (without ".NET" suffix).
#
# Assumes the standard Debug output layout:
#   <ProjectDir>\bin\Debug\<TargetFramework>\<RuntimeIdentifier>\<AssemblyName>.exe
# For projects without a RuntimeIdentifier:
#   <ProjectDir>\bin\Debug\<TargetFramework>\<AssemblyName>.exe

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

# ---------------------------------------------------------------------------
# Read AssemblyName, TargetFramework, RuntimeIdentifier from .csproj
# ---------------------------------------------------------------------------
[xml]$proj = Get-Content $csproj.FullName

$assemblyName = $proj.Project.PropertyGroup.AssemblyName |
    Where-Object { $_ } | Select-Object -First 1

if (-not $assemblyName) {
    # Fall back: strip trailing ".NET" or ".Net" from the folder name
    $assemblyName = (Split-Path $ProjectDir -Leaf) -replace '\.NET$', '' -replace '\.Net$', ''
}

$targetFramework = $proj.Project.PropertyGroup.TargetFramework |
    Where-Object { $_ } | Select-Object -First 1

$runtimeId = $proj.Project.PropertyGroup.RuntimeIdentifier |
    Where-Object { $_ } | Select-Object -First 1

# ---------------------------------------------------------------------------
# Locate exe — prefer RID sub-folder, fall back to flat layout
# ---------------------------------------------------------------------------
$exePath = $null

if ($targetFramework -and $runtimeId) {
    $candidate = Join-Path $ProjectDir "bin\Debug\$targetFramework\$runtimeId\$assemblyName.exe"
    if (Test-Path $candidate) { $exePath = $candidate }
}

if (-not $exePath -and $targetFramework) {
    $candidate = Join-Path $ProjectDir "bin\Debug\$targetFramework\$assemblyName.exe"
    if (Test-Path $candidate) { $exePath = $candidate }
}

if (-not $exePath) {
    # Glob search as last resort
    $found = Get-ChildItem (Join-Path $ProjectDir "bin\Debug") `
        -Recurse -Filter "$assemblyName.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($found) { $exePath = $found.FullName }
}

if (-not $exePath) {
    Write-Warning "Exe not found — building first to produce output."
}

# ---------------------------------------------------------------------------
# 1. Stop running instance
# ---------------------------------------------------------------------------
Write-Host "=============================================="
Write-Host " RELOADING: $assemblyName"
Write-Host "=============================================="

Write-Host "`n[1/3] Stopping running instance..."
$runningProc = Get-Process -Name $assemblyName -ErrorAction SilentlyContinue | Select-Object -First 1
if ($runningProc) {
    try {
        Stop-Process -Id $runningProc.Id -Force -ErrorAction Stop
    } catch {
        # Access denied means the running instance is elevated (e.g. an
        # app.manifest requireAdministrator app) and this shell isn't — a plain
        # Stop-Process can't open a handle to it. Escalate via a one-shot
        # elevated helper instead of silently giving up, which used to leave
        # the exe locked and the build failing right after.
        Write-Host "  Access denied — requesting elevation to stop it..."
        Start-Process powershell -Verb RunAs -Wait -ArgumentList @(
            "-NoProfile", "-Command",
            "Stop-Process -Id $($runningProc.Id) -Force -ErrorAction SilentlyContinue"
        )
    }
}
Start-Sleep -Milliseconds 400

# ---------------------------------------------------------------------------
# 2. Build
# ---------------------------------------------------------------------------
Write-Host "`n[2/3] Building $($csproj.Name)..."
dotnet build $csproj.FullName -c Debug 2>&1 | Select-Object -Last 6
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed (exit $LASTEXITCODE). Reload aborted."
    exit $LASTEXITCODE
}

# ---------------------------------------------------------------------------
# 3. Re-resolve exe path after build (in case it didn't exist before)
# ---------------------------------------------------------------------------
if (-not $exePath -or -not (Test-Path $exePath)) {
    $exePath = $null

    if ($targetFramework -and $runtimeId) {
        $candidate = Join-Path $ProjectDir "bin\Debug\$targetFramework\$runtimeId\$assemblyName.exe"
        if (Test-Path $candidate) { $exePath = $candidate }
    }
    if (-not $exePath -and $targetFramework) {
        $candidate = Join-Path $ProjectDir "bin\Debug\$targetFramework\$assemblyName.exe"
        if (Test-Path $candidate) { $exePath = $candidate }
    }
    if (-not $exePath) {
        $found = Get-ChildItem (Join-Path $ProjectDir "bin\Debug") `
            -Recurse -Filter "$assemblyName.exe" -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($found) { $exePath = $found.FullName }
    }
}

if (-not $exePath) {
    Write-Error "Could not locate $assemblyName.exe after build."
    exit 1
}

# ---------------------------------------------------------------------------
# 4. Relaunch with elevation
# ---------------------------------------------------------------------------
Write-Host "`n[3/3] Launching: $exePath"
Start-Process $exePath -Verb RunAs

Write-Host "`nReload complete."
