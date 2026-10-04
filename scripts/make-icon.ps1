<#
.SYNOPSIS
  Draws the BlackBox logo (a charcoal box with an overhanging lid) and writes the app icon.

.DESCRIPTION
  Writes src/BlackBox.App/Assets/blackbox.ico (16-256 px, PNG entries) and blackbox.png (256 px).
  Each size is drawn on its own, 4x oversampled, so the small ones stay crisp.

.EXAMPLE
  ./scripts/make-icon.ps1
  ./scripts/make-icon.ps1 -Preview preview.png   # also writes a sheet of every size on dark and light
#>
param([string]$Preview)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot
$assets = Join-Path $root "src/BlackBox.App/Assets"
$sizes = 256, 128, 64, 48, 40, 32, 24, 20, 16

# The box in unit space, seen from the front corner and a little above.
$lidBottom = 0.72   # lower edge of the lid; the box itself runs from 0 up under the lid
$lidTop = 0.98
$overhang = 0.08

function Project([double]$x, [double]$y, [double]$z) {
    [System.Drawing.PointF]::new([float](($x - $y) * 0.866), [float](($x + $y) * 0.42 - $z))
}

function Color([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

function Draw-Box([int]$size) {
    $ss = 4
    $px = $size * $ss
    $big = [System.Drawing.Bitmap]::new($px, $px, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($big)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $a = -$overhang; $b = 1 + $overhang
    # Corners: back-left (a,b) is hidden; the front corner is (b,b).
    $lt = @{ L = Project $a $b $lidTop; F = Project $b $b $lidTop; R = Project $b $a $lidTop; B = Project $a $a $lidTop }
    $lb = @{ L = Project $a $b $lidBottom; F = Project $b $b $lidBottom; R = Project $b $a $lidBottom }
    $bt = @{ L = Project 0 1 $lidBottom; F = Project 1 1 $lidBottom; R = Project 1 0 $lidBottom }
    $bb = @{ L = Project 0 1 0; F = Project 1 1 0; R = Project 1 0 0 }

    # Fit the drawing into the square, leaving room for the rim and a hair more on the larger sizes.
    $all = @($lt.L, $lt.F, $lt.R, $lt.B, $lb.L, $lb.F, $lb.R, $bb.L, $bb.F, $bb.R)
    $minX = ($all | Measure-Object X -Minimum).Minimum; $maxX = ($all | Measure-Object X -Maximum).Maximum
    $minY = ($all | Measure-Object Y -Minimum).Minimum; $maxY = ($all | Measure-Object Y -Maximum).Maximum
    $edge = [Math]::Max(1.0, $size / 64.0)
    $margin = ($edge + $(if ($size -ge 48) { $size * 0.03 } else { 0 })) * $ss
    $scale = ($px - 2 * $margin) / [Math]::Max($maxX - $minX, $maxY - $minY)
    $offX = ($px - ($maxX - $minX) * $scale) / 2 - $minX * $scale
    $offY = ($px - ($maxY - $minY) * $scale) / 2 - $minY * $scale
    $m = [System.Drawing.Drawing2D.Matrix]::new([float]$scale, 0, 0, [float]$scale, [float]$offX, [float]$offY)
    $g.Transform = $m

    function Fill([string]$hex, [System.Drawing.PointF[]]$pts) {
        $brush = [System.Drawing.SolidBrush]::new((Color $hex))
        $g.FillPolygon($brush, $pts); $brush.Dispose()
    }
    # Lines are sized in output pixels so they stay one clean pixel at 16 px.
    function Line([string]$hex, [double]$width, [System.Drawing.PointF[]]$pts) {
        $pen = [System.Drawing.Pen]::new((Color $hex), [float]($width * $ss / $scale))
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Miter
        $g.DrawLines($pen, $pts); $pen.Dispose()
    }

    $bodyHull = [System.Drawing.PointF[]]@($bt.L, $bb.L, $bb.F, $bb.R, $bt.R)
    $lidHull = [System.Drawing.PointF[]]@($lt.B, $lt.R, $lb.R, $lb.F, $lb.L, $lt.L)

    # A thin grey rim around the whole shape so the dark box reads on a dark taskbar.
    $rim = [System.Drawing.Pen]::new((Color "#565B63"), [float](2 * $edge * $ss / $scale))
    $rim.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Miter
    $g.DrawPolygon($rim, $bodyHull); $g.DrawPolygon($rim, $lidHull); $rim.Dispose()

    # Box body: left face catches more light than the right one.
    Fill "#33373D" @($bt.L, $bt.F, $bb.F, $bb.L)
    Fill "#23262A" @($bt.F, $bt.R, $bb.R, $bb.F)
    # Shadow the lid casts on the body.
    if ($size -ge 24) {
        $z = $lidBottom - 0.1
        Fill "#1E2024" @($bt.L, $bt.F, (Project 1 1 $z), (Project 0 1 $z))
        Fill "#17191C" @($bt.F, $bt.R, (Project 1 0 $z), (Project 1 1 $z))
    }
    # Lid sides.
    Fill "#41454C" @($lt.L, $lt.F, $lb.F, $lb.L)
    Fill "#2D3035" @($lt.F, $lt.R, $lb.R, $lb.F)
    # Lid top with a soft highlight toward the back.
    $grad = [System.Drawing.Drawing2D.LinearGradientBrush]::new($lt.B, $lt.F, (Color "#858C96"), (Color "#5B6169"))
    $g.FillPolygon($grad, [System.Drawing.PointF[]]@($lt.B, $lt.L, $lt.F, $lt.R)); $grad.Dispose()

    # A light rim along the front of the lid top.
    Line "#B4BAC2" $edge @($lt.L, $lt.F, $lt.R)

    $g.Dispose()
    $out = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g2 = [System.Drawing.Graphics]::FromImage($out)
    $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g2.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $g2.DrawImage($big, [System.Drawing.Rectangle]::new(0, 0, $size, $size))
    $g2.Dispose(); $big.Dispose()
    $out
}

function Png-Bytes([System.Drawing.Bitmap]$bmp) {
    $ms = [IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    , $ms.ToArray()   # the comma keeps PowerShell from unrolling the bytes
}

$images = @{}
foreach ($s in $sizes) { $images[$s] = Draw-Box $s }

# ICO with PNG entries (Windows Vista and later read these at every size).
$pngs = foreach ($s in $sizes) { , [byte[]](Png-Bytes $images[$s]) }
$ms = [IO.MemoryStream]::new()
$w = [IO.BinaryWriter]::new($ms)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write([byte[]]$p) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $assets "blackbox.ico"), $ms.ToArray())
$images[256].Save((Join-Path $assets "blackbox.png"), [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "Wrote blackbox.ico ($($sizes -join ', ') px) and blackbox.png"

if ($Preview) {
    # Each size at 1x and blown up 4x (nearest neighbour), on the app's dark surface, the taskbar and white.
    $bgs = "#1A1C1F", "#101010", "#FFFFFF"
    $sheet = [System.Drawing.Bitmap]::new(1400, 3 * 300)
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    for ($r = 0; $r -lt 3; $r++) {
        $brush = [System.Drawing.SolidBrush]::new((Color $bgs[$r]))
        $g.FillRectangle($brush, 0, $r * 300, 1400, 300); $brush.Dispose()
        $x = 10
        foreach ($s in 16, 20, 24, 32, 48, 64) {
            $g.DrawImage($images[$s], $x, $r * 300 + 10, $s, $s)
            $g.DrawImage($images[$s], $x, $r * 300 + 30 + 64, $s * 3, $s * 3)
            $x += [Math]::Max($s * 3, 40) + 16
        }
        $g.DrawImage($images[256], $x + 20, $r * 300 + 22, 256, 256)
    }
    $g.Dispose()
    $sheet.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "Preview: $Preview"
}
