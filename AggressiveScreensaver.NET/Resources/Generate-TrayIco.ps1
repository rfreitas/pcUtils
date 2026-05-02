#Requires -Version 5.1
<#
.SYNOPSIS
    Generates tray.ico from the GDI+ drawing that matches tray.svg.

.DESCRIPTION
    Renders the AggressiveScreensaver tray icon at 16, 24, 32, and 48 px
    (white strokes on transparent) and packs them into a multi-size ICO.

    Run from the Resources\ directory, or pass -OutPath explicitly.

.PARAMETER Sizes
    Array of pixel sizes to embed. Default: 16, 24, 32, 48.

.PARAMETER OutPath
    Destination ICO file. Default: tray.ico next to this script.

.EXAMPLE
    .\Generate-TrayIco.ps1
    .\Generate-TrayIco.ps1 -Sizes 16,32 -OutPath C:\temp\out.ico
#>
param(
    [int[]]$Sizes   = @(16, 24, 32, 48),
    [string]$OutPath = (Join-Path $PSScriptRoot "tray.ico")
)

Add-Type -AssemblyName System.Drawing

# ---------------------------------------------------------------------------
# Design constants (all in a 32-unit coordinate space; scaled by $s = Size/32)
# ---------------------------------------------------------------------------
# Monitor body   : rect (1,1) 30×20
# Stand neck     : line (16,21)-(16,25.5)
# Stand base     : line (8,27)-(24,27)
# Power circle   : centre (16, 12.25), radius 5
#   Arc gap      : 60° centred at 12-o'clock → start 300°, sweep 300° (GDI+)
#   Stem         : (16, 4.75)-(16, 7.25)   [top of circle = 12.25-5 = 7.25]

function New-IconBitmap {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size,
               [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode   = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s    = $Size / 32.0
    # Stroke width: 1 px at 16, 1.5 at 24, 2 at 32, 2.5 at 48
    $penW = [float][Math]::Max(1.0, [Math]::Min(2.5, $Size / 16.0))
    $pen  = New-Object System.Drawing.Pen([System.Drawing.Color]::White, $penW)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    # Monitor body
    $mL = [float](1.0  * $s); $mT = [float](1.0  * $s)
    $mW = [float](30.0 * $s); $mH = [float](20.0 * $s)
    $g.DrawRectangle($pen, $mL, $mT, $mW, $mH)

    $cx = [float](16.0 * $s)   # horizontal centre
    $mB = [float]($mT + $mH)   # bottom of monitor body

    # Stand
    $g.DrawLine($pen, $cx, [float]($mB + 0.5 * $s), $cx, [float](25.5 * $s))
    $g.DrawLine($pen, [float](8.0 * $s), [float](27.0 * $s),
                      [float](24.0 * $s), [float](27.0 * $s))

    # Power arc — 300° sweep, 60° gap at 12-o'clock
    $pr  = [float](5.0   * $s)
    $pcy = [float](12.25 * $s)
    $g.DrawArc($pen,
        [float]($cx  - $pr), [float]($pcy - $pr),
        [float](2.0  * $pr), [float](2.0  * $pr),
        300.0, 300.0)

    # Power stem
    $g.DrawLine($pen,
        $cx, [float](4.75 * $s),
        $cx, [float](($pcy - $pr) + $penW * 0.5))

    $pen.Dispose(); $g.Dispose()
    return $bmp
}

function Save-AsIco {
    param([int[]]$Sizes, [string]$Path)

    $pngData = @()
    foreach ($sz in $Sizes) {
        $bmp = New-IconBitmap -Size $sz
        $ms  = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $pngData += , $ms.ToArray()
        $ms.Dispose()
    }

    $out = New-Object System.IO.MemoryStream
    $bw  = New-Object System.IO.BinaryWriter($out)

    # ICO file header
    $bw.Write([uint16]0)              # reserved
    $bw.Write([uint16]1)              # type = ICO
    $bw.Write([uint16]$Sizes.Count)   # image count

    # Directory entries (16 bytes each)
    $offset = [uint32](6 + 16 * $Sizes.Count)
    for ($i = 0; $i -lt $Sizes.Count; $i++) {
        $dim = if ($Sizes[$i] -ge 256) { [byte]0 } else { [byte]$Sizes[$i] }
        $bw.Write($dim)                      # width
        $bw.Write($dim)                      # height
        $bw.Write([byte]0)                   # colour count (0 = true-colour)
        $bw.Write([byte]0)                   # reserved
        $bw.Write([uint16]1)                 # planes
        $bw.Write([uint16]32)                # bit depth
        $bw.Write([uint32]$pngData[$i].Length)
        $bw.Write([uint32]$offset)
        $offset += [uint32]$pngData[$i].Length
    }

    foreach ($data in $pngData) { $bw.Write($data) }

    $bw.Flush()
    [System.IO.File]::WriteAllBytes($Path, $out.ToArray())
    $bw.Dispose(); $out.Dispose()
    Write-Host "Saved $($Sizes.Count)-size ICO → $Path"
}

Save-AsIco -Sizes $Sizes -Path $OutPath
