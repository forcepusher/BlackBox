param([int]$Round = 1)
# Pictures of every file of a round: an SVG drawn by Sloperator's renderer (<id>.png) and by Edge
# (<id>.edge.png); an HTML page by Edge (<id>.png). Edge runs headless with a profile of its own.
$here = Split-Path $MyInvocation.MyCommand.Path
$env:DOTNET_ROOT = 'C:\Repos\Sloperator\.dev\dotnet'
$out = "$here\out"
$edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
$asks = Get-Content "$here\asks.json" -Raw | ConvertFrom-Json
function Edge($file, $png, $w, $h) {
  Remove-Item $png -ErrorAction SilentlyContinue
  $url = 'file:///' + ($file -replace '\\', '/')
  $p = Start-Process $edge -ArgumentList @('--headless=new', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1',
    "--window-size=$w,$h", "--user-data-dir=$here\edge-profile", '--no-first-run', '--default-background-color=ffffffff', "--screenshot=$png", $url) -PassThru -WindowStyle Hidden
  $p.WaitForExit(60000) | Out-Null
}
$svgArgs = @()
foreach ($a in $asks) {
  $svg = "$out\$($a.id)-svg-r$Round.svg"; $html = "$out\$($a.id)-html-r$Round.html"
  if (Test-Path $svg) { $svgArgs += $svg, ($svg -replace '\.svg$', '.png'); Edge $svg ($svg -replace '\.svg$', '.edge.png') $a.w $a.h }
  if (Test-Path $html) { Edge $html ($html -replace '\.html$', '.png') $a.w $a.h }
}
if ($svgArgs) { & 'C:\Repos\Sloperator\.dev\wt\svg-render\bin\Release\net10.0\svg-render.exe' @svgArgs }
Add-Type -AssemblyName System.Drawing
Get-ChildItem "$out\*-r$Round*.png" | % { $i = [Drawing.Image]::FromFile($_.FullName); "$($_.Name) $($i.Width)x$($i.Height)"; $i.Dispose() }
