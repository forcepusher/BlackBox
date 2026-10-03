<#
.SYNOPSIS
  Long-running test: BlackBox writes its own Tetris, play-tests it through the desktop and fixes it
  until it works. Afterwards the game is played in headless Edge (tetris-check.mjs) to judge it.
  It drives your actual mouse and keyboard for as long as the run takes: don't touch the PC.

  Everything lands in .dev\tetris\<time>\: run.txt (the full log), shots\ (what the model saw),
  game\ (the files it wrote), check\ (the checker's screenshots and check.json), summary.txt.

.EXAMPLE
  ./scripts/tetris-test.ps1                         # server and model from Data/settings.json
  ./scripts/tetris-test.ps1 -Url http://host:1234/v1 -Model qwen3.8-27b@q4_k_s -Hours 6
#>
param([string]$Root = "", [string]$Url = "", [string]$Model = "", [double]$Hours = 4)
$repo = Split-Path $PSScriptRoot
$cli = Join-Path $repo "src\BlackBox.Cli\bin\Debug\net10.0-windows\blackbox-cli.exe"
dotnet build (Join-Path $repo "src\BlackBox.Cli") -v q | Out-Null
$out = Join-Path $repo (".dev\tetris\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$game = Join-Path $out "game"
New-Item -ItemType Directory -Force $game | Out-Null
$cliArgs = @()
if ($Root) { $cliArgs += @("--root", $Root) }
if ($Url) { $cliArgs += @("--url", $Url) }
if ($Model) { $cliArgs += @("--model", $Model) }

$task = "Write your own Tetris game and make sure it really works. Put it in the folder $game as a single file tetris.html " +
  "(plain HTML, CSS and JavaScript, no libraries, nothing to install). The game starts as soon as the page opens. " +
  "Controls: Left and Right arrows move, Up arrow rotates, Down arrow drops faster, Space drops the piece straight down, R restarts. " +
  "Then play-test it yourself through the desktop: open the file in Microsoft Edge and play it with the keyboard. " +
  "Check that pieces fall on their own, move, rotate, stop at the walls and on other pieces, that full lines are cleared and the score goes up, " +
  "that the game shows Game Over when the stack reaches the top, and that R starts a new game. " +
  "Whenever something does not work properly, fix the file, reload the page and test again. Keep going until everything works. " +
  "When you are done, close the browser tab you opened and tell me what you tested and what you fixed."

$log = Join-Path $out "run.txt"
$cliArgs += @("--yes", "--save-shots", (Join-Path $out "shots"), $task)
Write-Host "Running; log: $log"
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process $cli -ArgumentList ($cliArgs | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -NoNewWindow -PassThru `
  -RedirectStandardOutput $log -RedirectStandardError (Join-Path $out "stderr.txt")
if (-not $p.WaitForExit([int]($Hours * 3600 * 1000))) {
  Stop-Process $p -Force
  Add-Content $log "`n[done] TIMEOUT after $Hours h"
}
$sw.Stop()

$c = Get-Content $log
$done = ($c | Select-String '^\[done\]' | ForEach-Object Line) -join " | "
node (Join-Path $PSScriptRoot "tetris-check.mjs") (Join-Path $game "tetris.html") (Join-Path $out "check") *> (Join-Path $out "check.txt")
$summary = @(
  "Tetris run $(Split-Path $out -Leaf)"
  "wall time    {0:hh\:mm\:ss}" -f $sw.Elapsed
  "outcome      $done"
  "model calls  $(($c | Select-String '^\S+ step [1-9]').Count)"
  "tool calls   $(($c | Select-String '^\[call\]').Count)  (recovered from text: $(($c | Select-String 'recovered from text').Count))"
  "compactions  $(($c | Select-String 'Earlier steps summarized').Count)  (failed: $(($c | Select-String 'Could not summarize').Count))"
  "reconnects   $(($c | Select-String 'Connection lost').Count)"
  "loop notes   $(($c | Select-String 'Note: (you made exactly|the screen has not changed)').Count)"
  "game files   $((Get-ChildItem $game -Recurse -File | ForEach-Object { '{0} ({1:N0} bytes)' -f $_.Name, $_.Length }) -join ', ')"
  "checker:"
) + (Get-Content (Join-Path $out "check.txt") | ForEach-Object { "  $_" })
$summary | Set-Content -Encoding utf8 (Join-Path $out "summary.txt")
$summary
