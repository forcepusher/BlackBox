<#
.SYNOPSIS
  Long-running test: BlackBox writes its own 2048, play-tests it through the desktop and fixes it
  until it works. 2048 is turn-based, so the model's seconds per step don't matter. Afterwards
  game2048-check.mjs plays the game in headless Edge and checks every move against the rules
  and that the tiles sit inside the board.
  It drives your actual mouse and keyboard for as long as the run takes: don't touch the PC.

  Everything lands in .dev\game2048\<time>\: run.txt (the full log), shots\ (what the model saw),
  game\ (the files it wrote), check\ (the checker's screenshots and check.json), summary.txt.

.EXAMPLE
  ./scripts/game2048-test.ps1                         # server and model from Data/settings.json
  ./scripts/game2048-test.ps1 -Url http://host:1234/v1 -Model qwen3.8-27b@q4_k_s -Hours 6
#>
param([string]$Root = "", [string]$Url = "", [string]$Model = "", [double]$Hours = 4)
$repo = Split-Path $PSScriptRoot
$cli = Join-Path $repo "src\BlackBox.Cli\bin\Debug\net10.0-windows\blackbox-cli.exe"
dotnet build (Join-Path $repo "src\BlackBox.Cli") -v q | Out-Null
$out = Join-Path $repo (".dev\game2048\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$game = Join-Path $out "game"
New-Item -ItemType Directory -Force $game | Out-Null
$cliArgs = @()
if ($Root) { $cliArgs += @("--root", $Root) }
if ($Url) { $cliArgs += @("--url", $Url) }
if ($Model) { $cliArgs += @("--model", $Model) }

$task = "Write your own 2048 game and make sure it really works. Put it in the folder $game as a single file 2048.html " +
  "(plain HTML, CSS and JavaScript, no libraries, nothing to install). Draw the 4x4 board with HTML elements, not a canvas, and show the score. " +
  "Controls: the arrow keys slide the tiles, R starts a new game. " +
  "Then play-test it the way a person would: open the file in Microsoft Edge, maximize the window, and play with the arrow keys while watching the board. " +
  "Do not use the developer tools, the console or scripts to test or change the game: only real key presses and what you see on screen count. " +
  "Play until a 128 tile appears. Then check Game Over: press R and fill the board quickly with big batches of moves, without checking each one, until no move is left. " +
  "Along the way check that a new game starts with two tiles, that tiles stay inside the board, that tiles slide all the way and equal tiles merge once per move, " +
  "that exactly one new 2 or 4 appears after every move that changes the board and none after a move that changes nothing, " +
  "that the score goes up by the value of each merged tile, that the game says Game Over when no move is left, and that R starts a new game. " +
  "Whenever something does not work properly, fix the file, reload the page and test again. Keep going until everything works. " +
  "When you are done, close the browser tab you opened and tell me what you tested and what you fixed."

$log = Join-Path $out "run.txt"
$cliArgs += @("--yes", "--save-shots", (Join-Path $out "shots"), $task)
Write-Host "Running; log: $log"
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process $cli -ArgumentList ($cliArgs | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -NoNewWindow -PassThru `
  -RedirectStandardOutput $log -RedirectStandardError (Join-Path $out "stderr.txt")

# Show the log live in this window while the run goes on (thinking, tool calls, reconnects,
# compactions). Writing to the console never takes focus away from what the agent is doing.
$limit = [TimeSpan]::FromHours($Hours)
$stream = [IO.FileStream]::new($log, 'Open', 'Read', 'ReadWrite')
$reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8)
while (-not $p.HasExited) {
  $text = $reader.ReadToEnd()
  if ($text) { Write-Host $text -NoNewline }
  if ($sw.Elapsed -gt $limit) {
    Stop-Process $p -Force
    $p.WaitForExit()
    Add-Content $log "`n[done] TIMEOUT after $Hours h"
    break
  }
  Start-Sleep -Milliseconds 300
}
Write-Host $reader.ReadToEnd()
$reader.Dispose()
$sw.Stop()

$c = Get-Content $log
$done = ($c | Select-String '^\[done\]' | ForEach-Object Line) -join " | "
node (Join-Path $PSScriptRoot "game2048-check.mjs") (Join-Path $game "2048.html") (Join-Path $out "check") *> (Join-Path $out "check.txt")
$summary = @(
  "2048 run $(Split-Path $out -Leaf)"
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
