<#
.SYNOPSIS
  Runs a few real desktop tasks through the headless CLI and prints a one-line score per task.
  It drives your actual mouse and keyboard: don't touch the PC while it runs.

  The tasks named mm-* need a second monitor and are skipped without one: the window to work in
  is on the other screen, or has to be moved there. Each of them checks the end state itself
  (the text in the window, the button pressed, the window maximized on screen 2) and prints
  PASS or FAIL.

.EXAMPLE
  ./scripts/agent-tasks.ps1                         # server and model from Data/settings.json
  ./scripts/agent-tasks.ps1 -Tasks mm-type,mm-click # some of them
  ./scripts/agent-tasks.ps1 -Root D:\bb-other       # another portable root with its own settings
  ./scripts/agent-tasks.ps1 -Url http://host:1234/v1 -Model qwen3.8-27b@q4_k_s
#>
param([string]$Root = "", [string]$Url = "", [string]$Model = "", [string[]]$Tasks = @())
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

# ---- windows and screens, in physical pixels as BlackBox sees them
if (-not ("AgentTasks.Win" -as [type])) {
  Add-Type -Namespace AgentTasks -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
[DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
[DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
[DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
[DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, System.Text.StringBuilder lParam);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
'@
}
[AgentTasks.Win]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
Add-Type -AssemblyName System.Windows.Forms, UIAutomationClient, UIAutomationTypes
# The primary first, then the others left to right: BlackBox numbers them the same way.
$screens = @([System.Windows.Forms.Screen]::AllScreens | Sort-Object { -not $_.Primary }, { $_.Bounds.X }, { $_.Bounds.Y } | ForEach-Object Bounds)

function Wait-Window([Diagnostics.Process]$p) {
  for ($i = 0; $i -lt 100 -and $p.MainWindowHandle -eq 0; $i++) { Start-Sleep -Milliseconds 100; $p.Refresh() }
  $p.MainWindowHandle
}
function Place-Window([IntPtr]$h, $screen, [int]$w = 900, [int]$hh = 600) {
  [AgentTasks.Win]::ShowWindow($h, 9) | Out-Null  # SW_RESTORE
  [AgentTasks.Win]::MoveWindow($h, $screen.X + 150, $screen.Y + 120, $w, $hh, $true) | Out-Null
}
function Screen-Of([IntPtr]$h) {
  $r = New-Object AgentTasks.Win+RECT
  [AgentTasks.Win]::GetWindowRect($h, [ref]$r) | Out-Null
  $cx = [int](($r.Left + $r.Right) / 2); $cy = [int](($r.Top + $r.Bottom) / 2)
  for ($i = 0; $i -lt $screens.Count; $i++) { if ($screens[$i].Contains($cx, $cy)) { return $i + 1 } }
  0
}
function Notepad-Text([Diagnostics.Process]$p) {
  # The text of Notepad's edit box, by WM_GETTEXT (its UI Automation element has no ValuePattern).
  $p.Refresh()
  $edit = [AgentTasks.Win]::FindWindowEx($p.MainWindowHandle, [IntPtr]::Zero, "Edit", $null)
  if ($edit -eq [IntPtr]::Zero) { return $null }
  $sb = New-Object System.Text.StringBuilder 65536
  [AgentTasks.Win]::SendMessage($edit, 0x000D, [IntPtr]65536, $sb) | Out-Null  # WM_GETTEXT
  $sb.ToString()
}
function Stop-EdgeProfile([string]$profile) {
  Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" | Where-Object { $_.CommandLine -like "*$profile*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

# A page whose button says in the window title how often it was pressed.
$page = Join-Path $out "press.html"
@'
<!DOCTYPE html><html><head><title>Press me test</title>
<style>body{font-family:sans-serif;display:flex;flex-direction:column;align-items:center;justify-content:center;height:90vh;background:#f4f4f4}
button{font-size:40px;padding:30px 60px}</style></head>
<body><h1>Second screen test</h1><button id="b">Press me</button><p id="o"></p>
<script>var n=0;document.getElementById('b').onclick=function(){n++;document.title='Pressed '+n;document.getElementById('o').textContent='Pressed '+n+' time(s)';};</script>
</body></html>
'@ | Set-Content -Encoding ascii $page
$edgeProfile = Join-Path $out "edge-profile"
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1

$all = [ordered]@{
  "files"   = @{ Prompt = "In the working folder, create a file named fruits.txt containing three lines: apple, banana, cherry. Then read the file back and tell me exactly what the second line says." }
  "calc"    = @{ Prompt = "Open the Windows Calculator app and use its buttons to calculate 128 times 47. Tell me the result it shows, then close the Calculator." }
  "hn"      = @{ Prompt = "Open Microsoft Edge, go to https://news.ycombinator.com and tell me the titles of the top 3 stories. Close the browser tab you opened when you are done." }
  "search"  = @{ Prompt = "Open Microsoft Edge, search the web for 'AvaloniaUI Avalonia GitHub', open the GitHub repository page of Avalonia, and tell me how many stars it has. Close the browser tab you opened when you are done." }
  # The window to work in is on the other screen, and the request doesn't say so.
  "mm-type" = @{
    Screens = 2
    Prompt = "A Notepad window is open on one of my monitors. Type 'Hello from the other screen' into it, then tell me which monitor it is on."
    Setup = { $script:np = Start-Process notepad -PassThru; Place-Window (Wait-Window $script:np) $screens[1] }
    Check = {
      $text = Notepad-Text $script:np
      if ($text -like "*Hello from the other screen*") { "PASS typed into the Notepad on screen 2" } else { "FAIL Notepad on screen 2 holds: $text" }
    }
    Cleanup = { Stop-Process -Id $script:np.Id -Force -ErrorAction SilentlyContinue }
  }
  # Found and clicked on the second screen.
  "mm-click" = @{
    Screens = 2
    Prompt = "There's a web page with a 'Press me' button on my second monitor. Click that button once, then tell me what the page says."
    Setup = {
      $s = $screens[1]
      Start-Process $edge -ArgumentList "--user-data-dir=`"$edgeProfile`"", "--no-first-run", "--no-default-browser-check", "--new-window",
        "--window-position=$($s.X + 100),$($s.Y + 80)", "--window-size=1200,800", "`"$page`"" | Out-Null
      Start-Sleep 5
    }
    Check = {
      $titles = @(Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" | Where-Object { $_.CommandLine -like "*$edgeProfile*" } |
        ForEach-Object { (Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue).MainWindowTitle } | Where-Object { $_ })
      if ($titles -match '^Pressed 1\b') { "PASS pressed once on screen 2" } elseif ($titles -match '^Pressed') { "FAIL pressed more than once: $titles" } else { "FAIL not pressed: $titles" }
    }
    Cleanup = { Stop-EdgeProfile $edgeProfile }
  }
  # A program that opens on the other screen, as programs do where they were last closed.
  "mm-open" = @{
    Screens = 2
    Prompt = "Open Notepad and write 'opened on the other monitor' in it."
    Setup = {
      # Notepad opens where its registry values say; the user's values are put back afterwards.
      $key = "HKCU:\Software\Microsoft\Notepad"
      $script:npSaved = Get-ItemProperty $key -ErrorAction SilentlyContinue | Select-Object iWindowPosX, iWindowPosY, iWindowPosDX, iWindowPosDY
      if (-not (Test-Path $key)) { New-Item $key -Force | Out-Null }
      $s = $screens[1]
      Set-ItemProperty $key iWindowPosX ($s.X + 200); Set-ItemProperty $key iWindowPosY ($s.Y + 150)
      Set-ItemProperty $key iWindowPosDX 900; Set-ItemProperty $key iWindowPosDY 600
      $script:started = Get-Date
    }
    Check = {
      $mine = @(Get-Process notepad -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -ge $script:started })
      $hit = $mine | Where-Object { (Notepad-Text $_) -like "*opened on the other monitor*" } | Select-Object -First 1
      if ($hit) { "PASS wrote in the Notepad that opened on screen $(Screen-Of $hit.MainWindowHandle)" }
      else { "FAIL $($mine.Count) Notepad(s) opened, none holds the text" }
    }
    Cleanup = {
      Get-Process notepad -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -ge $script:started } | Stop-Process -Force
      $key = "HKCU:\Software\Microsoft\Notepad"
      foreach ($v in "iWindowPosX", "iWindowPosY", "iWindowPosDX", "iWindowPosDY") {
        if ($script:npSaved -and $null -ne $script:npSaved.$v) { Set-ItemProperty $key $v $script:npSaved.$v } else { Remove-ItemProperty $key $v -ErrorAction SilentlyContinue }
      }
    }
  }
  # A window moved to the other screen and maximized there.
  "mm-move" = @{
    Screens = 2
    Prompt = "Move the Notepad window that is open to my second monitor and maximize it there."
    Setup = { $script:np = Start-Process notepad -PassThru; Place-Window (Wait-Window $script:np) $screens[0] }
    Check = {
      $h = $script:np.MainWindowHandle
      $on = Screen-Of $h; $max = [AgentTasks.Win]::IsZoomed($h)
      if ($on -eq 2 -and $max) { "PASS maximized on screen 2" } else { "FAIL on screen $on, maximized $max" }
    }
    Cleanup = { Stop-Process -Id $script:np.Id -Force -ErrorAction SilentlyContinue }
  }
}
$Tasks = @($Tasks | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$names = if ($Tasks.Count -gt 0) { $Tasks } else { @($all.Keys) }

foreach ($name in $names) {
  $t = $all[$name]
  if (-not $t) { Write-Warning "No task '$name'"; continue }
  if ($t.Screens -and $screens.Count -lt $t.Screens) { "{0,-8} skipped: needs {1} monitors, this PC has {2}" -f $name, $t.Screens, $screens.Count; continue }
  $log = Join-Path $out "$name.txt"
  if ($t.Setup) { & $t.Setup }
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $job = Start-Job -ScriptBlock { param($cli, $a, $task, $log, $shots) & $cli @a --yes --save-shots $shots $task *> $log } -ArgumentList $cli, $rootArgs, $t.Prompt, $log, (Join-Path $out "$name-shots")
  if (-not (Wait-Job $job -Timeout 600)) {
    Stop-Job $job
    # Only this run's CLI: another suite may be running its own.
    Get-CimInstance Win32_Process -Filter "Name='blackbox-cli.exe'" | Where-Object { $_.CommandLine -like "*$out*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
    Add-Content $log "`n[done] TIMEOUT after 600s"
  }
  Remove-Job $job -Force
  $check = if ($t.Check) { & $t.Check } else { "" }
  if ($t.Cleanup) { & $t.Cleanup }
  Get-Process win32calc, CalculatorApp -ErrorAction SilentlyContinue | Stop-Process -Force
  $c = Get-Content $log
  "{0,-8} {1,5:N0}s calls={2,3} errors={3,2} recovered={4,2} cut={5} | {6}{7}" -f $name, $sw.Elapsed.TotalSeconds,
    ($c | Select-String '^\[call\]').Count, ($c | Select-String '^\[result\].*(not valid|Missing|Unknown tool|Failed|Exit code [1-9])').Count,
    ($c | Select-String 'recovered from text').Count, ($c | Select-String 'thinking started repeating').Count,
    (($c | Select-String '^\[done\]' | Select -Last 1).Line), $(if ($check) { " | $check" } else { "" })
}
