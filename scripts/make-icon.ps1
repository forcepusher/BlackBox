<#
.SYNOPSIS
  Draws the blob logo (docs/logo/blob.svg) and writes the app icon.

.DESCRIPTION
  Writes src/Sloperator.App/Assets/sloperator.ico (16-256 px, PNG entries).
  Each size is drawn on its own, 4x oversampled, so the small ones stay crisp.
  The icon's blob has a soft drop shadow, so its pale yellow keeps its edge on a light taskbar or
  Explorer; on a dark one the shadow melts into the background and it looks as it did without.
  The app's own sloperator.png (256 px, the SVG as it is) has no shadow: the app is dark, and its
  still blob must line up with the party blob GIF. -Edge none draws that.

.EXAMPLE
  ./scripts/make-icon.ps1
  ./scripts/make-icon.ps1 -Edge outline -Out preview   # a thin dark outline instead, written elsewhere
#>
param(
    [ValidateSet("outline", "shadow", "none")] [string]$Edge = "shadow",
    [string]$Out
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot
$assets = if ($Out) { New-Item -ItemType Directory -Force $Out | Select-Object -ExpandProperty FullName } else { Join-Path $root "src/Sloperator.App/Assets" }
$sizes = 256, 128, 64, 48, 40, 32, 24, 20, 16
$svg = [xml][IO.File]::ReadAllText((Join-Path $root "docs/logo/blob.svg"))
$view = 128.0   # the SVG's viewBox is 0 0 128 128

function Color([string]$hex, [int]$alpha = 255) { [System.Drawing.Color]::FromArgb($alpha, [System.Drawing.ColorTranslator]::FromHtml($hex)) }

# The SVG's paths use absolute M, L, C and Z only.
function Path-Of([string]$d) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $at = $null
    foreach ($m in [regex]::Matches($d, '([MLCZ])([^MLCZ]*)')) {
        $n = @([regex]::Matches($m.Groups[2].Value, '-?[\d.]+') | ForEach-Object { [float]$_.Value })
        switch ($m.Groups[1].Value) {
            "M" { $path.StartFigure(); $at = [System.Drawing.PointF]::new($n[0], $n[1]) }
            "L" { $to = [System.Drawing.PointF]::new($n[0], $n[1]); $path.AddLine($at, $to); $at = $to }
            "C" {
                for ($i = 0; $i -lt $n.Count; $i += 6) {
                    $to = [System.Drawing.PointF]::new($n[$i + 4], $n[$i + 5])
                    $path.AddBezier($at, [System.Drawing.PointF]::new($n[$i], $n[$i + 1]), [System.Drawing.PointF]::new($n[$i + 2], $n[$i + 3]), $to)
                    $at = $to
                }
            }
            "Z" { $path.CloseFigure() }
        }
    }
    $path
}

function Draw-Blob([int]$size, [string]$edge) {
    $ss = 4
    $px = $size * $ss
    $big = [System.Drawing.Bitmap]::new($px, $px, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($big)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # The outline is one output pixel up to 64 px, then grows a little so 256 px still shows it.
    $line = if ($edge -eq "outline") { [Math]::Max(1.0, $size / 85.0) } else { 0 }
    $shadow = if ($edge -eq "shadow") { [Math]::Max(1.0, $size / 24.0) } else { 0 }
    if ($edge -eq "none") {
        # As the SVG has it, so the app's still blob lines up with the party blob GIF.
        $sx = $sy = $px / $view; $dx = $dy = 0
    }
    else {
        # The blob shrinks so blob and edge together fill the square side to side, and its top and
        # bottom (with the edge) land on whole pixels: the flat crown and base stay sharp lines.
        $bounds = (Path-Of ($svg.svg.path | Select-Object -First 1).d).GetBounds()
        $padX = $line + $shadow; $padTop = $line + $shadow / 2; $padBottom = $line + 1.5 * $shadow
        $sx = ($size - 2 * $padX) / $bounds.Width
        $tall = [Math]::Round($bounds.Height * $sx + $padTop + $padBottom)
        $sy = ($tall - $padTop - $padBottom) / $bounds.Height
        $top = [Math]::Floor(($size - $tall) / 2)
        $dx = ($padX - $bounds.X * $sx) * $ss; $dy = ($top + $padTop - $bounds.Y * $sy) * $ss
        $sx *= $ss; $sy *= $ss
    }
    $g.Transform = [System.Drawing.Drawing2D.Matrix]::new([float]$sx, 0, 0, [float]$sy, [float]$dx, [float]$dy)
    $unit = $ss / $sx   # one output pixel in SVG units

    $body = $null
    foreach ($node in $svg.svg.ChildNodes) {
        if ($node.LocalName -eq "path") { $shape = Path-Of $node.d }
        elseif ($node.LocalName -eq "ellipse") {
            $cx = [float]$node.cx; $cy = [float]$node.cy; $rx = [float]$node.rx; $ry = [float]$node.ry
            $shape = [System.Drawing.Drawing2D.GraphicsPath]::new()
            $shape.AddEllipse($cx - $rx, $cy - $ry, 2 * $rx, 2 * $ry)
            if ($node.transform -match 'rotate\(([-\d.]+)') {
                $turn = [System.Drawing.Drawing2D.Matrix]::new()
                $turn.RotateAt([float]$Matches[1], [System.Drawing.PointF]::new($cx, $cy))
                $shape.Transform($turn)
            }
        }
        else { continue }

        if (-not $body) {
            # The first shape is the blob's body: its edge goes under it.
            $body = $shape
            if ($line) {
                $pen = [System.Drawing.Pen]::new((Color "#2F2F2F"), [float](2 * $line * $unit))
                $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
                $g.DrawPath($pen, $shape); $pen.Dispose()
            }
            if ($shadow) {
                # Rings of faint black, wider ones fainter, so the shadow fades out; a touch lower than the blob.
                $state = $g.Save()
                $g.TranslateTransform(0, [float]($shadow * $unit / 2))
                $steps = 8
                for ($i = $steps; $i -ge 1; $i--) {
                    $pen = [System.Drawing.Pen]::new((Color "#000000" 22), [float](2 * $shadow * $unit * $i / $steps))
                    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
                    $g.DrawPath($pen, $shape); $pen.Dispose()
                }
                $g.Restore($state)
            }
        }
        $brush = [System.Drawing.SolidBrush]::new((Color $node.fill))
        $g.FillPath($brush, $shape); $brush.Dispose()
    }

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

# ICO with PNG entries (Windows Vista and later read these at every size).
$pngs = foreach ($s in $sizes) { , [byte[]](Png-Bytes (Draw-Blob $s $Edge)) }
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
[IO.File]::WriteAllBytes((Join-Path $assets "sloperator.ico"), $ms.ToArray())
Write-Host "Wrote sloperator.ico ($($sizes -join ', ') px, $Edge) to $assets"
