#Requires -Version 5.1
<#
.SYNOPSIS
    Converts tray.svg → tray.ico using ImageMagick (winget: ImageMagick.ImageMagick).

.PARAMETER SvgPath
    Source SVG. Default: tray.svg next to this script.

.PARAMETER IcoPath
    Output ICO. Default: tray.ico next to this script.

.EXAMPLE
    .\Generate-TrayIco.ps1
#>
param(
    [string]$SvgPath = (Join-Path $PSScriptRoot "tray.svg"),
    [string]$IcoPath = (Join-Path $PSScriptRoot "tray.ico")
)

if (-not (Get-Command magick -ErrorAction SilentlyContinue)) {
    Write-Error "ImageMagick not found. Install with: winget install ImageMagick.ImageMagick"
    exit 1
}

magick -background none $SvgPath -define icon:auto-resize=48,32,24,16 $IcoPath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Saved tray.ico → $IcoPath"
