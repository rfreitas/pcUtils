# Publishes AggressiveScreensaver.NET as a single-file, self-contained win-x64 executable.
# Run from the repo root or from the AggressiveScreensaver.NET folder.

$ErrorActionPreference = "Stop"
$projectDir = $PSScriptRoot
$project    = Join-Path $projectDir "AggressiveScreensaver.NET.csproj"
$outDir     = Join-Path $projectDir "bin\Release\publish"

Write-Host "Publishing $project -> $outDir"

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    /p:PublishSingleFile=true `
    -o $outDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed (exit $LASTEXITCODE)."
    exit $LASTEXITCODE
}

$exe = Join-Path $outDir "AggressiveScreensaver.exe"
if (Test-Path $exe) {
    Write-Host "Published: $exe"
    Write-Host "File size: $([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB"
} else {
    Write-Error "Expected output not found: $exe"
    exit 1
}
