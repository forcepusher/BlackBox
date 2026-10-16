<#
.SYNOPSIS
  Runs a few real desktop tasks through the headless CLI and prints a one-line score per task.
  It drives your actual mouse and keyboard: don't touch the PC while it runs.

.EXAMPLE
  ./scripts/agent-tasks.ps1                         # server and model from Data/settings.json
  ./scripts/agent-tasks.ps1 -Root D:\bb-other       # another portable root with its own settings
  ./scripts/agent-tasks.ps1 -Url http://host:1234/v1 -Model qwen3.8-27b@q4_k_s
#>
param([string]$Root = "", [string]$Url = "", [string]$Model = "")
$repo = Split-Path $PSScriptRoot
# The portable SDK in .dev\dotnet when none is installed.
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -and (Test-Path "$repo\.dev\dotnet\dotnet.exe")) {
  $env:DOTNET_ROOT = "$repo\.dev\dotnet"
  $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
}
$cli = Join-Path $repo "src\BlackBox.Cli\bin\Debug\net10.0-windows\blackbox-cli.exe"
if (-not (Test-Path $cli)) { dotnet build (Join-Path $repo "src\BlackBox.Cli") -v q | Out-Null }
$out = Join-Path $repo (".dev\agent-tasks\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Force $out | Out-Null
$rootArgs = @()
if ($Root) { $rootArgs = @("--root", $Root) }
if ($Url) { $rootArgs += @("--url", $Url) }
if ($Model) { $rootArgs += @("--model", $Model) }

$tasks = [ordered]@{
  "files"   = "In the working folder, create a file named fruits.txt containing three lines: apple, banana, cherry. Then read the file back and tell me exactly what the second line says."
  "calc"    = "Open the Windows Calculator app and use its buttons to calculate 128 times 47. Tell me the result it shows, then close the Calculator."
  "hn"      = "Open Microsoft Edge, go to https://news.ycombinator.com and tell me the titles of the top 3 stories. Close the browser tab you opened when you are done."
  "search"  = "Open Microsoft Edge, search the web for 'AvaloniaUI Avalonia GitHub', open the GitHub repository page of Avalonia, and tell me how many stars it has. Close the browser tab you opened when you are done."
}

foreach ($name in $tasks.Keys) {
  $log = Join-Path $out "$name.txt"
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $job = Start-Job -ScriptBlock { param($cli, $a, $task, $log, $shots) & $cli @a --yes --save-shots $shots $task *> $log } -ArgumentList $cli, $rootArgs, $tasks[$name], $log, (Join-Path $out "$name-shots")
  if (-not (Wait-Job $job -Timeout 600)) { Stop-Job $job; Get-Process blackbox-cli -ErrorAction SilentlyContinue | Stop-Process -Force; Add-Content $log "`n[done] TIMEOUT after 600s" }
  Remove-Job $job -Force
  Get-Process win32calc, CalculatorApp -ErrorAction SilentlyContinue | Stop-Process -Force
  $c = Get-Content $log
  "{0,-7} {1,5:N0}s calls={2,3} errors={3,2} recovered={4,2} cut={5} | {6}" -f $name, $sw.Elapsed.TotalSeconds,
    ($c | Select-String '^\[call\]').Count, ($c | Select-String '^\[result\].*(not valid|Missing|Unknown tool|Failed|Exit code [1-9])').Count,
    ($c | Select-String 'recovered from text').Count, ($c | Select-String 'thinking started repeating').Count,
    (($c | Select-String '^\[done\]' | Select -Last 1).Line)
}
