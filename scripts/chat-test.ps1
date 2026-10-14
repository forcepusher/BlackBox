<#
.SYNOPSIS
  Chat stand-in benchmark: BlackBox answers people for the user in chat apps, through the
  screen, mouse and keyboard, the way a person would. The chat lab (tests\BlackBox.ChatLab)
  serves local look-alikes of a Telegram-style messenger (Pigeon), a social network
  (Friendspace), a shop's support chat (GadgetHub) and a marketplace (SwapIt), with scripted
  people on the other side who answer, haggle, ask for a login code or a card number. Nobody
  real is ever messaged: the sites run on this PC and open in a browser profile of their own.

  Per scenario: the lab starts, Edge opens the site maximized, BlackBox gets the request in the
  user's words, and when it is done (or the time is up) its final reply goes to the lab, which
  checks the log of everything that was written: the facts each person was told, what must
  never be sent (codes, card numbers, the home address), chats that needed no answer, threads,
  timing, duplicates, junk such as escaped line breaks, and the report back to the user.

  It drives your mouse and keyboard while BlackBox runs: don't touch the PC. It refuses to run
  while a real messenger or mail app is open, so that a confused run can't write to real people.

  Runs land in .dev\chat\<time>\: summary.txt, and per scenario run.txt (the live log),
  task.txt, report.txt (BlackBox's final reply), check.txt, transcript.txt (every chat as the
  people saw it), events.jsonl (the lab's log) and shots\.

.EXAMPLE
  ./scripts/chat-test.ps1                                     # every scenario, server and model from Data/settings.json
  ./scripts/chat-test.ps1 -Scenarios inbox,haggle -Url http://host:1234/v1 -Model qwen3.8-27b@q6_k_xl
  ./scripts/chat-test.ps1 -SelfTest                           # the reference stand-in instead of BlackBox: checks the checks, no model or browser
  ./scripts/chat-test.ps1 -Cli D:\old\blackbox-cli.exe        # another build, e.g. to compare with a baseline
#>
param([string[]]$Scenarios = @(), [string]$Url = "", [string]$Model = "", [string]$Root = "",
  [double]$Minutes = 40, [switch]$SelfTest, [string]$Cli = "", [int]$Port = 5170)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot

function Log([string]$text) { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $text) -ForegroundColor Yellow }

# ---- nothing real may be within reach of the agent
if (-not $SelfTest) {
  $messengers = 'Telegram', 'Discord', 'DiscordPTB', 'DiscordCanary', 'WhatsApp', 'Signal', 'slack', 'Teams', 'ms-teams', 'Viber', 'Skype',
    'Element', 'Messenger', 'Zoom', 'Kotatogram', 'AyuGram', '64Gram', 'Unigram', 'Thunderbird', 'OUTLOOK', 'olk', 'Mailspring',
    'Ferdium', 'Franz', 'Rambox', 'Beeper', 'WeChat', 'Line', 'KakaoTalk', 'ICQ', 'Wire', 'Threema'
  $open = @(Get-Process | Where-Object { $messengers -contains $_.ProcessName } | ForEach-Object ProcessName | Sort-Object -Unique)
  # Web versions in a browser window, as far as the window's title shows them (VKontakte and
  # Yandex as their Russian titles spell them, in regex escapes so this file stays ASCII).
  $sites = '\b(Telegram|WhatsApp|Discord|Messenger|Gmail|Outlook|Slack|VK|\u0412\u041a\u043e\u043d\u0442\u0430\u043a\u0442\u0435|Instagram|Facebook|LinkedIn|Signal|Mail\.ru|\u042f\u043d\u0434\u0435\u043a\u0441)\b'
  $open += @(Get-Process -Name msedge, chrome, firefox, opera, brave, vivaldi, browser -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowTitle -match $sites } | ForEach-Object { "$($_.ProcessName): $($_.MainWindowTitle)" })
  if ($open.Count -gt 0) {
    throw "A real messenger or mail app is open, and BlackBox could write to real people there by mistake. Close it first: $($open -join '; ')"
  }
}

# ---- builds (with the portable SDK in .dev\dotnet when none is installed)
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -and (Test-Path "$repo\.dev\dotnet\dotnet.exe")) {
  $env:DOTNET_ROOT = "$repo\.dev\dotnet"
  $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
}
$lab = Join-Path $repo "tests\BlackBox.ChatLab\bin\Debug\net10.0\BlackBox.ChatLab.exe"
dotnet build (Join-Path $repo "tests\BlackBox.ChatLab") -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "The chat lab didn't build" }
if (-not $SelfTest) {
  if ($Cli) { $cliExe = $Cli } else {
    $cliExe = Join-Path $repo "src\BlackBox.Cli\bin\Debug\net10.0-windows\blackbox-cli.exe"
    dotnet build (Join-Path $repo "src\BlackBox.Cli") -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "BlackBox didn't build" }
  }
  Write-Host "BlackBox: $cliExe"
}

$known = @(& $lab --list | ForEach-Object { ($_ -split '\s+')[0] })
$apps = @{}
& $lab --list | ForEach-Object { $f = $_ -split '\s+'; $apps[$f[0]] = $f[1] }
# powershell -File passes "a,b" as one string.
$Scenarios = @($Scenarios | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$run = if ($Scenarios.Count -gt 0) { $Scenarios } else { $known }
$unknown = @($run | Where-Object { $known -notcontains $_ })
if ($unknown.Count -gt 0) { throw "Unknown scenario(s): $($unknown -join ', '). Known: $($known -join ', ')" }

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $repo (".dev\chat\" + $stamp + $(if ($SelfTest) { "-selftest" } else { "" }))
New-Item -ItemType Directory -Force $out | Out-Null

$cliArgs = @()
if ($Root) { $cliArgs += @("--root", $Root) }
if ($Url) { $cliArgs += @("--url", $Url) }
if ($Model) { $cliArgs += @("--model", $Model) }

# ---- the browser: Edge with a profile of its own, so none of the user's sign-ins are in reach
$edgeExe = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
  Where-Object { Test-Path $_ } | Select-Object -First 1
$profileDir = Join-Path $repo ".dev\chat\edge-profile"
if (-not ("ChatTest.Win" -as [type])) {
  Add-Type -Namespace ChatTest -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
[DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
[DllImport("user32.dll")] public static extern bool AttachThreadInput(uint from, uint to, bool attach);
[DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
'@
}
# Other windows are minimized while BlackBox works, so that it sees only the site (and can't type
# into another app by mistake); they come back when the run is over.
$shell = New-Object -ComObject Shell.Application

# Brings a window to the front. Windows lets only the app in front do that, so this script borrows
# the front window's input queue for the moment it takes.
function Show-InFront([IntPtr]$h) {
  [ChatTest.Win]::ShowWindow($h, 3) | Out-Null   # maximized
  $front = [ChatTest.Win]::GetForegroundWindow()
  $theirs = [ChatTest.Win]::GetWindowThreadProcessId($front, [IntPtr]::Zero)
  $mine = [ChatTest.Win]::GetCurrentThreadId()
  $attached = $theirs -ne 0 -and $theirs -ne $mine -and [ChatTest.Win]::AttachThreadInput($mine, $theirs, $true)
  try {
    [ChatTest.Win]::BringWindowToTop($h) | Out-Null
    [ChatTest.Win]::SetForegroundWindow($h) | Out-Null
  } finally {
    if ($attached) { [ChatTest.Win]::AttachThreadInput($mine, $theirs, $false) | Out-Null }
  }
  [ChatTest.Win]::GetForegroundWindow() -eq $h
}

function Get-LabEdge {
  # Only this profile's processes: the user's own Edge is never touched.
  @(Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($profileDir) })
}

function Stop-LabEdge {
  $main = Get-LabEdge | Where-Object { $_.CommandLine -notmatch '--type=' }
  foreach ($m in $main) { try { (Get-Process -Id $m.ProcessId -ErrorAction Stop).CloseMainWindow() | Out-Null } catch { } }
  for ($i = 0; $i -lt 20 -and (Get-LabEdge).Count -gt 0; $i++) { Start-Sleep -Milliseconds 250 }
  Get-LabEdge | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

function Start-LabEdge([string]$address) {
  Stop-LabEdge
  # Start on the page, not on what the last scenario left open.
  foreach ($d in "Default\Sessions", "Default\Current Session", "Default\Current Tabs", "Default\Last Session", "Default\Last Tabs") {
    Remove-Item -Recurse -Force (Join-Path $profileDir $d) -ErrorAction SilentlyContinue
  }
  $edgeArgs = @("--user-data-dir=`"$profileDir`"", "--no-first-run", "--no-default-browser-check", "--disable-sync", "--hide-crash-restore-bubble",
    "--disable-features=msEdgeSidebarV2,msUndersideButton,EdgeCollections", "--start-maximized", "--new-window", $address)
  $shell.MinimizeAll()
  Start-Sleep -Milliseconds 500
  Start-Process $edgeExe -ArgumentList $edgeArgs | Out-Null
  # Its window in front, maximized: the agent's first screenshot shows the site.
  for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 250
    $main = Get-LabEdge | Where-Object { $_.CommandLine -notmatch '--type=' } | ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue } |
      Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($main -and (Show-InFront $main.MainWindowHandle)) { return }
  }
  Log "the lab's Edge window may not be in front"
}

function Invoke-Lab([string]$method, [string]$path, [string]$body = "") {
  $p = @{ Uri = "http://127.0.0.1:$Port$path"; Method = $method; UseBasicParsing = $true; TimeoutSec = 30 }
  if ($body) { $p.Body = [Text.Encoding]::UTF8.GetBytes($body); $p.ContentType = "text/plain; charset=utf-8" }
  $r = Invoke-WebRequest @p
  # The body as UTF-8 whatever the response headers say (PowerShell 5 guesses otherwise).
  [Text.Encoding]::UTF8.GetString($r.RawContentStream.ToArray())
}

# The model's last words in a run log: the [say] text of the last step that has any.
function Get-FinalReply([string]$log) {
  if (-not (Test-Path $log)) { return "" }
  $steps = [IO.File]::ReadAllText($log, [Text.Encoding]::UTF8) -split '(?m)^\u2500\u2500 step '
  for ($i = $steps.Count - 1; $i -ge 1; $i--) {
    if ($steps[$i] -match '(?s)\[say\] \d\d:\d\d:\d\d (.*?)(\r?\n\[(time|call|tool|notice)\]|\s*$)' -and $Matches[1].Trim()) { return $Matches[1].Trim() }
  }
  ""
}

# One scenario with BlackBox: the lab, the browser, the run, and the checks. Returns the check lines.
function Invoke-Scenario([string]$name, [string]$sdir, [Diagnostics.Stopwatch]$sw) {
  Log "${name}: starting the lab"
  $labProc = Start-Process $lab -ArgumentList @("--scenario", $name, "--out", "`"$sdir`"", "--port", $Port) -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $sdir "lab.txt") -RedirectStandardError (Join-Path $sdir "lab-errors.txt")
  $p = $null
  try {
    $up = $false
    for ($i = 0; $i -lt 60 -and -not $up; $i++) {
      Start-Sleep -Milliseconds 250
      try { Invoke-Lab GET "/api/health" | Out-Null; $up = $true } catch { }
    }
    if (-not $up) { throw "The chat lab didn't start (see $sdir\lab-errors.txt)" }
    $app = $apps[$name]
    # Each site its own origin (browsers send *.localhost to this PC).
    Start-LabEdge "http://$app.localhost:$Port/"
    $seen = $false
    for ($i = 0; $i -lt 60 -and -not $seen; $i++) {
      Start-Sleep -Milliseconds 500
      $seen = (Invoke-Lab GET "/api/health" | ConvertFrom-Json).pageSeen
    }
    if (-not $seen) { Log "${name}: the page didn't load in Edge; trying the plain address"; Start-LabEdge "http://127.0.0.1:$Port/$app" }
    Start-Sleep -Seconds 2
    $task = (Invoke-Lab POST "/api/start" | ConvertFrom-Json).task
    $log = Join-Path $sdir "run.txt"
    Log "${name}: BlackBox runs; log: $log"
    Write-Host $task
    $sw.Restart()
    $a = $cliArgs + @("--yes", "--save-shots", (Join-Path $sdir "shots"), $task)
    $p = Start-Process $cliExe -ArgumentList ($a | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -NoNewWindow -PassThru `
      -RedirectStandardOutput $log -RedirectStandardError (Join-Path $sdir "stderr.txt")
    # The log, live in this window; writing to the console never takes focus from the agent.
    $limit = [TimeSpan]::FromMinutes($Minutes)
    while (-not (Test-Path $log)) { Start-Sleep -Milliseconds 200 }
    $reader = [IO.StreamReader]::new([IO.FileStream]::new($log, 'Open', 'Read', 'ReadWrite'), [Text.Encoding]::UTF8)
    while (-not $p.HasExited) {
      $chunk = $reader.ReadToEnd()
      if ($chunk) { Write-Host $chunk -NoNewline }
      if ($sw.Elapsed -gt $limit) {
        Stop-Process $p -Force
        $p.WaitForExit()
        Add-Content $log "`n[done] TIMEOUT after $Minutes min"
        break
      }
      Start-Sleep -Milliseconds 300
    }
    Write-Host $reader.ReadToEnd()
    $reader.Dispose()
    $sw.Stop()
    @((Invoke-Lab POST "/api/finish" (Get-FinalReply $log)) -split "`n" | Where-Object { $_.Trim() })
  } finally {
    # Whatever happened (Ctrl+C included), no BlackBox is left running, and the browser and the lab go away.
    if ($p -and -not $p.HasExited) { Stop-Process $p -Force -ErrorAction SilentlyContinue }
    Stop-LabEdge
    try { Invoke-Lab POST "/api/quit" | Out-Null } catch { }
    if (-not $labProc.WaitForExit(10000)) { Stop-Process $labProc -Force -ErrorAction SilentlyContinue }
  }
}

$rows = @()
try {
  foreach ($name in $run) {
    $sdir = Join-Path $out $name
    New-Item -ItemType Directory -Force $sdir | Out-Null
    $sw = [Diagnostics.Stopwatch]::StartNew()
    if ($SelfTest) {
      Log "${name}: reference stand-in"
      $lines = @(& $lab --scenario $name --out $sdir --selftest --speed 20 2>&1 | ForEach-Object { "$_" })
    } else {
      $lines = Invoke-Scenario $name $sdir $sw
    }
    $sw.Stop()
    $lines | ForEach-Object { Write-Host "  $_" -ForegroundColor $(if ($_ -like "PASS*") { "Green" } elseif ($_ -like "FAIL*") { "Red" } else { "Gray" }) }
    $c = @(Get-Content (Join-Path $sdir "run.txt") -Encoding utf8 -ErrorAction SilentlyContinue)
    $passed = @($lines | Where-Object { $_ -like "PASS *" }).Count
    $failed = @($lines | Where-Object { $_ -like "FAIL *" }).Count
    $rows += [pscustomobject]@{
      Scenario = $name
      Result = $(if ($failed -eq 0 -and $passed -gt 0) { "OK" } else { "FAILED" })
      Checks = "$passed/$($passed + $failed)"
      Minutes = [Math]::Round($sw.Elapsed.TotalMinutes, 1)
      Steps = @($c | Select-String '^\S+ step [1-9]').Count
      Tools = @($c | Select-String '^\[call\]').Count
      Errors = @($c | Select-String '^\[result\].*(not valid|Missing|Unknown tool|Failed|Exit code [1-9]|Timed out)').Count
      Done = (($c | Select-String '^\[done\]' | Select-Object -First 1).Line -replace '^\[done\]\s*', '')
    }
  }
} finally {
  # The user's windows as they were.
  if (-not $SelfTest) { $shell.UndoMinimizeAll() }
}

$summary = @("Chat run $(Split-Path $out -Leaf)$(if ($Model) { " model $Model" })", "") +
  @(($rows | Format-Table -AutoSize | Out-String -Width 220).Trim() -split '\r?\n') +
  @("", "Failed checks:") +
  @($run | ForEach-Object { $n = $_; Get-Content (Join-Path $out "$n\check.txt") -Encoding utf8 -ErrorAction SilentlyContinue | Where-Object { $_ -like "FAIL *" } | ForEach-Object { "  ${n}: $_" } })
$summary | Set-Content -Encoding utf8 (Join-Path $out "summary.txt")
$summary
