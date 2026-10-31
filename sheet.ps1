# One sheet per ask: SVG r1 | SVG r2 | HTML r1 | HTML r2, each scaled to the same width, a label over each.
$here = Split-Path $MyInvocation.MyCommand.Path
$out = "$here\out"
Add-Type -AssemblyName System.Drawing
$asks = Get-Content "$here\asks.json" -Raw | ConvertFrom-Json
foreach ($a in $asks) {
  $cells = @(@("$($a.id)-svg-r1", "SVG, round 1"), @("$($a.id)-svg-r2", "SVG, round 2 (after seeing it)"), @("$($a.id)-html-r1", "HTML, round 1"), @("$($a.id)-html-r2", "HTML, round 2 (after seeing it)"))
  $cw = if ($a.w -lt 600) { 390 } else { 720 }
  $ch = [int]($a.h * $cw / $a.w)
  $pad = 16; $lab = 34
  $sheet = New-Object Drawing.Bitmap ([int](4 * $cw + 5 * $pad)), ([int]($ch + $lab + 2 * $pad))
  $g = [Drawing.Graphics]::FromImage($sheet); $g.Clear([Drawing.Color]::FromArgb(22, 22, 22))
  $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'AntiAliasGridFit'
  $font = New-Object Drawing.Font 'Segoe UI', 13, ([Drawing.FontStyle]::Bold), ([Drawing.GraphicsUnit]::Pixel)
  for ($i = 0; $i -lt 4; $i++) {
    $x = $pad + $i * ($cw + $pad)
    $g.DrawString("$($a.id): $($cells[$i][1])", $font, [Drawing.Brushes]::Gainsboro, $x, $pad)
    $png = "$out\$($cells[$i][0]).png"
    if (Test-Path $png) { $img = [Drawing.Image]::FromFile($png); $g.DrawImage($img, $x, $pad + $lab, $cw, $ch); $img.Dispose() }
    else { $g.DrawString("no picture", $font, [Drawing.Brushes]::OrangeRed, $x, $pad + $lab + 20) }
    $g.DrawRectangle((New-Object Drawing.Pen ([Drawing.Color]::FromArgb(70, 70, 70))), $x - 1, $pad + $lab - 1, $cw + 1, $ch + 1)
  }
  $sheet.Save("$here\sheets\$($a.id)-sheet.png", [Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $sheet.Dispose()
  "$($a.id)-sheet.png"
}
