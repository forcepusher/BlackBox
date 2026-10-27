<#
.SYNOPSIS
  Long-running test: Sloperator makes a 2048 game in Unity, play-tests it in the editor through the
  desktop, fixes it until it works, then makes a Windows build and checks that too.
  It drives your actual mouse and keyboard for as long as the run takes: don't touch the PC.

  Everything lands in .dev\game2048\<time>-unity\: run.txt (the full log), shots\ (what the model
  saw), game\ (the Unity project and the build), summary.txt. Earlier Unity runs are moved to
  .dev\unity2048-archive\ first.

.EXAMPLE
  ./scripts/unity2048-test.ps1                         # server and model from Data/settings.json
  ./scripts/unity2048-test.ps1 -Model qwen3.8-27b@q6_k_xl -Hours 10
#>
param([string]$Root = "", [string]$Url = "", [string]$Model = "", [double]$Hours = 10,
  [string]$Unity = "C:\Program Files\Unity\Hub\Editor\6000.3.23f1\Editor\Unity.exe")
$repo = Split-Path $PSScriptRoot

# Keep this log window out of the agent's way: on another monitor if there is one (in run 4 the
# agent spent steps minimizing it on the main screen).
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace Harness -Name Win -MemberDefinition @'
[DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
[DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
'@
$other = [System.Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary } | Select-Object -First 1
if ($other) {
  $b = $other.WorkingArea
  [Harness.Win]::MoveWindow([Harness.Win]::GetConsoleWindow(), $b.X, $b.Y, $b.Width, $b.Height, $true) | Out-Null
}
# The portable SDK in .dev\dotnet when none is installed.
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -and (Test-Path "$repo\.dev\dotnet\dotnet.exe")) {
  $env:DOTNET_ROOT = "$repo\.dev\dotnet"
  $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
}
$cli = Join-Path $repo "src\Sloperator.Cli\bin\Debug\net10.0-windows\sloperator-cli.exe"
dotnet build (Join-Path $repo "src\Sloperator.Cli") -v q | Out-Null
# Earlier Unity runs move to .dev\unity2048-archive first, so the agent can't reuse their code.
$archive = Join-Path $repo ".dev\unity2048-archive"
Get-ChildItem (Join-Path $repo ".dev\game2048") -Directory -Filter *-unity -ErrorAction SilentlyContinue | ForEach-Object {
  New-Item -ItemType Directory -Force $archive | Out-Null
  Move-Item $_.FullName $archive
}
$out = Join-Path $repo (".dev\game2048\" + (Get-Date -Format "yyyyMMdd-HHmmss") + "-unity")
$game = Join-Path $out "game"
New-Item -ItemType Directory -Force $game | Out-Null
$cliArgs = @()
if ($Root) { $cliArgs += @("--root", $Root) }
if ($Url) { $cliArgs += @("--url", $Url) }
if ($Model) { $cliArgs += @("--model", $Model) }

$project = Join-Path $game "Unity2048"
$build = Join-Path $game "Build"
$task = "Make a 2048 game in Unity and make sure it really works. Use the Unity editor at $Unity (Unity 6.3 LTS). " +
  "Create the Unity project in the folder $project. " +
  "The game: a 4x4 board with the tiles and their numbers drawn in the game view, and the score shown. " +
  "Controls: the arrow keys slide the tiles, R starts a new game. When no move is left, the game says Game Over. " +
  "Then play-test it in the Unity editor's Play mode the way a person would: play with the arrow keys while watching the Game view. " +
  "Only real key presses and what you see on screen count as testing. " +
  "Play until the score reaches 128. Then check Game Over: press R and fill the board quickly until no move is left. " +
  "Along the way check that a new game starts with two tiles, that tiles stay inside the board, that tiles slide all the way and equal tiles merge once per move, " +
  "that exactly one new 2 or 4 appears after every move that changes the board and none after a move that changes nothing, " +
  "that the score goes up by the value of each merged tile, that the game says Game Over when no move is left, and that R starts a new game. " +
  "Whenever something does not work properly, fix it and test again. Keep going until everything works. " +
  "Then make a Windows build of the game into the folder $build, with the game's exe in that folder. " +
  "Start the build and check quickly that it works the same way, then close it and close Unity. " +
  "When you are done, tell me what you tested and what you fixed."

$log = Join-Path $out "run.txt"
$cliArgs += @("--yes", "--save-shots", (Join-Path $out "shots"), $task)
Write-Host "Running; log: $log"
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process $cli -ArgumentList ($cliArgs | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -NoNewWindow -PassThru `
  -RedirectStandardOutput $log -RedirectStandardError (Join-Path $out "stderr.txt")

# Show the log live in this window while the run goes on. Writing to the console never takes
# focus away from what the agent is doing.
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
$scripts = if (Test-Path $project) { Get-ChildItem (Join-Path $project "Assets") -Recurse -Filter *.cs -ErrorAction SilentlyContinue } else { @() }
$exe = if (Test-Path $build) { Get-ChildItem $build -Filter *.exe | Where-Object Name -notmatch 'UnityCrashHandler' } else { @() }
$summary = @(
  "Unity 2048 run $(Split-Path $out -Leaf)"
  "wall time    {0:hh\:mm\:ss}" -f $sw.Elapsed
  "outcome      $done"
  "model calls  $(($c | Select-String '^\S+ step [1-9]').Count)"
  "tool calls   $(($c | Select-String '^\[call\]').Count)  (recovered from text: $(($c | Select-String 'recovered from text').Count))"
  "resets       $(($c | Select-String 'Context reset:').Count)  (without a model-written note: $(($c | Select-String "couldn't write a handoff note").Count))"
  "reconnects   $(($c | Select-String 'Connection lost').Count)"
  "loop notes   $(($c | Select-String 'Note: (you made exactly|the screen has not changed)').Count)"
  "scripts      $(($scripts | ForEach-Object { '{0} ({1:N0} bytes)' -f $_.Name, $_.Length }) -join ', ')"
  "build        $(if ($exe) { ($exe | ForEach-Object FullName) -join ', ' } else { 'none' })"
)
$summary | Set-Content -Encoding utf8 (Join-Path $out "summary.txt")
$summary
