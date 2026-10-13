<#
.SYNOPSIS
  Sysadmin benchmark: BlackBox gets a fresh Ubuntu server (the lab VM from sysadmin-vm.ps1) and
  a real ticket per task, with only the IP, the user name and a key, like a hired admin. After
  each run the VM is rebooted and scripts check the end state over SSH and from this PC: the
  game server answers over WebSocket, the site has HTTPS, password logins are really off, the
  firewall blocks what it should, and so on.

  Each task is a folder in scripts\sysadmin\tasks\: task.txt (the ticket; {ip}, {user}, {key}
  are filled in), setup.sh (run as root before BlackBox starts, with the task's files\ next to
  it), check.sh (root, on the VM, after the reboot) and check-host.ps1 (on this PC), both
  printing PASS/FAIL lines, and solution.sh, a reference fix that -SelfTest uses to prove the
  checks pass. Optional: vars.txt (KEY=value lines for the scripts, such as LAB_SUDO_PASSWORD
  when a task makes sudo ask for one; {KEY} in the ticket is filled in too), preamble.txt (in
  place of the standard opening, which says to log in as admin with the key), old\ (setup.sh and
  files for an "old server": a second VM at {ip2}, set up before BlackBox starts and switched off
  before the checks, for tasks that move things from one server to another), solution-host.ps1
  (the reference solution's part on this PC) and setup-host.ps1 (makes per-run files in $tdir\upload,
  which go to the VM with setup.sh). The scripts see LAB_IP, LAB_USER and a random TOKEN; the
  PowerShell ones also $token, $tdir and the lab helpers. Nothing of this is left on the VM while
  BlackBox works.

  Runs land in .dev\sysadmin\<time>\: summary.txt, and per task run.txt (the live log),
  setup.txt, check.txt and shots\. The VM is reset from its snapshot before every task.

.EXAMPLE
  ./scripts/sysadmin-test.ps1                              # every task, server and model from Data/settings.json
  ./scripts/sysadmin-test.ps1 -Tasks game-server,fix-website -Model qwen3.8-27b@q6_k_xl
  ./scripts/sysadmin-test.ps1 -SelfTest                    # reference solutions only: checks the checks, no model
  ./scripts/sysadmin-test.ps1 -Cli D:\old\blackbox-cli.exe # another build, e.g. to compare with a baseline
  ./scripts/sysadmin-test.ps1 -Recheck 20261014-041653 -Tasks game-server   # reboot and check a finished run's VM again
#>
param([string[]]$Tasks = @(), [string]$Url = "", [string]$Model = "", [string]$Root = "",
  [double]$Minutes = 75, [switch]$SelfTest, [string]$Cli = "", [string]$Recheck = "")
$ErrorActionPreference = "Stop"
. "$PSScriptRoot\sysadmin\lab.ps1"
$taskRoot = Join-Path $PSScriptRoot "sysadmin\tasks"
# A two-server task (one with an old\ folder) gets its "old server" on the next lab instance.
$LabIp2 = "127.0.0.$(3 + $LabInstance)"
$oldServer = Join-Path $PSScriptRoot "sysadmin\old-server.ps1"
function Invoke-OldServer([string[]]$arguments) {
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $oldServer -Instance ($LabInstance + 1) @arguments 2>&1
}

# Keep this log window out of the agent's screenshots: on another monitor if there is one.
if (-not $SelfTest) {
  Add-Type -AssemblyName System.Windows.Forms
  if (-not ("Harness.Win" -as [type])) {
    Add-Type -Namespace Harness -Name Win -MemberDefinition @'
[DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
[DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
'@
  }
  $other = [System.Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary } | Select-Object -First 1
  if ($other) {
    $b = $other.WorkingArea
    [Harness.Win]::MoveWindow([Harness.Win]::GetConsoleWindow(), $b.X, $b.Y, $b.Width, $b.Height, $true) | Out-Null
  }
}
$all = @(Get-ChildItem $taskRoot -Directory | Sort-Object Name)
# powershell -File passes "a,b" as one string.
$Tasks = @($Tasks | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
if ($Tasks.Count -gt 0) {
  $all = @($Tasks | ForEach-Object { $n = $_; $all | Where-Object Name -eq $n })
  if ($all.Count -ne $Tasks.Count) { throw "Unknown task in: $($Tasks -join ', ')" }
}
if (-not (Test-Path $LabBase)) { throw "No lab VM; build it first with ./scripts/sysadmin-vm.ps1" }

if ($Cli) {
  $cli = $Cli
} else {
  $cli = Join-Path $LabRepo "src\BlackBox.Cli\bin\Debug\net10.0-windows\blackbox-cli.exe"
  if (-not $SelfTest) {
    dotnet build (Join-Path $LabRepo "src\BlackBox.Cli") -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
  }
}
Write-Host "BlackBox: $cli"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$out = if ($Recheck) { Join-Path $LabRepo ".dev\sysadmin\$Recheck" } else { Join-Path $LabRepo (".dev\sysadmin\" + $stamp + $(if ($SelfTest) { "-selftest" } else { "" })) }
New-Item -ItemType Directory -Force $out | Out-Null

# BlackBox's copy of the admin key.
$key = Join-Path $out "key\admin_ed25519"
if (-not $Recheck) {
  New-Item -ItemType Directory -Force (Split-Path $key) | Out-Null
  Copy-Item $LabKey $key
  Copy-Item "$LabKey.pub" "$key.pub"
  Protect-LabKey $key
}

# If BlackBox calls ssh.exe itself, it adds the VM to the user's known_hosts; that entry is taken
# out again at the end (and known_hosts.old, which ssh-keygen -R overwrites, put back).
$userKnown = Join-Path $env:USERPROFILE ".ssh\known_hosts"
$userKnownOld = "$userKnown.old"
$savedOld = Join-Path $out "known_hosts.old.bak"
if (Test-Path $userKnownOld) { Copy-Item $userKnownOld $savedOld }

# BlackBox's own known_hosts: a rebuilt lab has new host keys, which BlackBox would rightly refuse.
$bbKnown = Join-Path $(if ($Root) { $Root } else { Join-Path $LabRepo ".dev" }) "Data\known_hosts"
if (Test-Path $bbKnown) {
  @(Get-Content $bbKnown | Where-Object { $_ -notlike "$LabIp *" -and $_ -notlike "$LabIp2 *" }) | Set-Content -Encoding ascii $bbKnown
}

$cliArgs = @()
if ($Root) { $cliArgs += @("--root", $Root) }
if ($Url) { $cliArgs += @("--url", $Url) }
if ($Model) { $cliArgs += @("--model", $Model) }
# QEMU's port forwarding accepts a connection on this PC before it knows whether the VM does, so
# the ticket says so: otherwise a bare connect test from the PC (Test-NetConnection) misleads.
$preamble = "Our Ubuntu server runs in a VM on this PC and is reachable at {ip}: SSH on port 22, and its other ports at {ip} too " +
  "(QEMU forwards them, so from this PC a TCP connect to any of them succeeds even when nothing answers on the VM; only a real reply counts). " +
  "Log in over SSH as {user} with the private key {key}; {user} has sudo rights. "

function Fill([string]$text) {
  $text = $text.Replace("{ip2}", $LabIp2).Replace("{ip}", $LabIp).Replace("{user}", $LabUser).Replace("{key}", $key).Replace("{monitor}", $LabHostSeenAs).Replace("{tdir}", $tdir)
  # A task's own values: {KEY} for each KEY=value line of its vars.txt.
  foreach ($line in ($extra -split "`n")) { if ($line -match '^(\w+)=(.*)$') { $text = $text.Replace("{$($Matches[1])}", $Matches[2].Trim()) } }
  $text
}
function Log([string]$text) { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $text) -ForegroundColor Yellow }

# Uploads the named files of a task (and its files\ folder) to /tmp/bb-task and runs one of them as root.
function Invoke-TaskScript([IO.DirectoryInfo]$task, [string]$script, [string]$vars, [int]$TimeoutSec = 900) {
  Invoke-LabSsh "rm -rf /tmp/bb-task && mkdir -p /tmp/bb-task" | Out-Null
  $send = @((Join-Path $task.FullName $script), $vars)
  $files = Join-Path $task.FullName "files"
  if (Test-Path $files) { $send += $files }
  # Files a task's setup-host.ps1 made for this run (for example the new users' public keys).
  $upload = Join-Path $tdir "upload"
  if (Test-Path $upload) { $send += $upload }
  Copy-LabFiles $send "/tmp/bb-task/"
  # sudo -S: a task can make sudo ask for a password (LAB_SUDO_PASSWORD in vars.env); without one it is ignored.
  $sudo = "printf '%s\n' `"`$LAB_SUDO_PASSWORD`" | sudo -S -p '' -E"
  $r = Invoke-LabSsh "cd /tmp/bb-task && sed -i 's/\r$//' $script vars.env && set -a && . ./vars.env && set +a && $sudo bash ./$script; rc=`$?; $sudo rm -rf /tmp/bb-task; exit `$rc" -TimeoutSec $TimeoutSec
  $r
}

$rows = @()
try {
  foreach ($task in $all) {
    $tdir = Join-Path $out $task.Name
    New-Item -ItemType Directory -Force $tdir | Out-Null
    $vars = Join-Path $tdir "vars.env"
    $extra = Join-Path $task.FullName "vars.txt"
    $extra = if (Test-Path $extra) { (Get-Content $extra) -join "`n" } else { "" }
    if ($Recheck) {
      # The VM still holds the run's end state; only the reboot and the checks are done again.
      $token = ((Get-Content $vars) -match '^TOKEN=' -replace '^TOKEN=', '') | Select-Object -First 1
    } else {
      $token = -join ((1..16) | ForEach-Object { '{0:x}' -f (Get-Random -Maximum 16) })
      # A task's own values (vars.txt, KEY=value lines) come after the common ones.
      [IO.File]::WriteAllText($vars, "LAB_IP=$LabIp`nLAB_GUEST_IP=$LabGuestIp`nLAB_HOST_SEEN_AS=$LabHostSeenAs`nLAB_USER=$LabUser`nTOKEN=$token`nLAB_SUDO_PASSWORD=`n$extra`n")
    }
    # Restart-LabVm needs it too, when the task makes sudo ask for a password.
    $script:LabSudoPassword = if ($extra -match '(?m)^LAB_SUDO_PASSWORD=(.*)$') { $Matches[1].Trim() } else { "" }
    $log = Join-Path $tdir "run.txt"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    if ($Recheck) { $sw.Stop() } else {

    Log "$($task.Name): resetting the lab VM"
    try { Reset-LabVm } catch { Log "reset failed ($($_.Exception.Message)); once more"; Reset-LabVm }
    $hasOld = Test-Path (Join-Path $task.FullName "old\setup.sh")
    if ($hasOld) {
      Log "$($task.Name): setting up the old server at $LabIp2"
      Invoke-OldServer @("-TaskDir", $task.FullName, "-Vars", $vars) | Set-Content -Encoding utf8 (Join-Path $tdir "setup-old.txt")
      if ($LASTEXITCODE -ne 0) {
        Log "$($task.Name): the old server's setup failed (see $tdir\setup-old.txt); next task"
        $rows += [pscustomobject]@{ Task = $task.Name; Result = "SETUP FAILED"; Checks = ""; Minutes = 0; Steps = 0; Tools = 0; Errors = 0; Done = "" }
        continue
      }
    }
    $hostSetup = Join-Path $task.FullName "setup-host.ps1"
    if (Test-Path $hostSetup) { & $hostSetup }
    $r = Invoke-TaskScript $task "setup.sh" $vars
    ($r.Out + $r.Err) | Set-Content -Encoding utf8 (Join-Path $tdir "setup.txt")
    if ($r.Code -ne 0) {
      Log "$($task.Name): setup failed (see $tdir\setup.txt); next task"
      $rows += [pscustomobject]@{ Task = $task.Name; Result = "SETUP FAILED"; Checks = ""; Minutes = 0; Steps = 0; Tools = 0; Errors = 0; Done = "" }
      continue
    }

    # A task can bring its own preamble (preamble.txt), e.g. one where root logs in with a password.
    $pre = Join-Path $task.FullName "preamble.txt"
    $pre = if (Test-Path $pre) { (Get-Content $pre -Raw).Trim() + " " } else { $preamble }
    $text = Fill ($pre + (Get-Content (Join-Path $task.FullName "task.txt") -Raw).Trim())
    Set-Content -Encoding utf8 (Join-Path $tdir "task.txt") $text
    $sw.Restart()
    if ($SelfTest) {
      Log "$($task.Name): reference solution"
      $hostSolution = Join-Path $task.FullName "solution-host.ps1"
      if (Test-Path $hostSolution) { & $hostSolution }
      $r = Invoke-TaskScript $task "solution.sh" $vars -TimeoutSec 1800
      ($r.Out + $r.Err) | Set-Content -Encoding utf8 $log
      Add-Content $log "`n[done] solution.sh exit code $($r.Code)"
    } else {
      Log "$($task.Name): BlackBox runs; log: $log"
      Write-Host $text
      $a = $cliArgs + @("--yes", "--save-shots", (Join-Path $tdir "shots"), $text)
      $p = Start-Process $cli -ArgumentList ($a | ForEach-Object { Format-LabArg $_ }) -NoNewWindow -PassThru `
        -RedirectStandardOutput $log -RedirectStandardError (Join-Path $tdir "stderr.txt")
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
    }
    $sw.Stop()
    # The old server is switched off, as the ticket says: nothing may depend on it.
    if ($hasOld) { Invoke-OldServer @("-Stop") | Out-Null }
    } # not -Recheck

    Log "$($task.Name): rebooting the VM and checking"
    $lines = @()
    if (Restart-LabVm) {
      $lines += "PASS admin can still log in after a reboot"
      $hostCheck = Join-Path $task.FullName "check-host.ps1"
      if (Test-Path $hostCheck) {
        try { $lines += @(& $hostCheck) } catch { $lines += "FAIL check-host.ps1 crashed: $($_.Exception.Message)" }
      }
      try {
        $r = Invoke-TaskScript $task "check.sh" $vars -TimeoutSec 600
        $lines += @(($r.Out -split "`n") | Where-Object { $_.Trim() })
        if ($r.Code -ne 0) { $lines += "FAIL check.sh exit code $($r.Code): $($r.Err.Trim())" }
      } catch {
        $lines += "FAIL check.sh could not run: $($_.Exception.Message)"
      }
    } else {
      $lines += "FAIL admin can still log in after a reboot"
    }
    $lines | Set-Content -Encoding utf8 (Join-Path $tdir "check.txt")
    $lines | ForEach-Object { Write-Host "  $_" -ForegroundColor $(if ($_ -like "PASS*") { "Green" } elseif ($_ -like "FAIL*") { "Red" } else { "Gray" }) }

    $c = @(Get-Content $log -ErrorAction SilentlyContinue)
    $passed = @($lines | Where-Object { $_ -like "PASS *" }).Count
    $failed = @($lines | Where-Object { $_ -like "FAIL *" }).Count
    $rows += [pscustomobject]@{
      Task = $task.Name
      Result = $(if ($failed -eq 0) { "OK" } else { "FAILED" })
      Checks = "$passed/$($passed + $failed)"
      Minutes = [Math]::Round($sw.Elapsed.TotalMinutes, 1)
      Steps = @($c | Select-String '^\S+ step [1-9]').Count
      Tools = @($c | Select-String '^\[call\]').Count
      Errors = @($c | Select-String '^\[result\].*(not valid|Missing|Unknown tool|Failed|Exit code [1-9]|Timed out)|\[exit code [1-9]').Count
      Done = (($c | Select-String '^\[done\]' | Select-Object -First 1).Line -replace '^\[done\]\s*', '')
    }
  }
} finally {
  # Leave the user's known_hosts as it was (the old server of a two-server task included).
  $added = @($LabIp, $LabIp2 | Where-Object { (Test-Path $userKnown) -and (Invoke-LabExe "ssh-keygen.exe" @("-F", $_, "-f", $userKnown)).Code -eq 0 })
  foreach ($ip in $added) { Invoke-LabExe "ssh-keygen.exe" @("-R", $ip, "-f", $userKnown) | Out-Null }
  if ($added.Count -gt 0) {
    if (Test-Path $savedOld) { Copy-Item $savedOld $userKnownOld -Force } else { Remove-Item $userKnownOld -ErrorAction SilentlyContinue }
  }
}

$summary = @("Sysadmin run $(Split-Path $out -Leaf)$(if ($Model) { " model $Model" })", "") +
  ($rows | Format-Table -AutoSize | Out-String -Width 220).Trim().Split("`n") +
  @("", "Failed checks:") +
  @($all | ForEach-Object { $n = $_.Name; Get-Content (Join-Path $out "$n\check.txt") -ErrorAction SilentlyContinue | Where-Object { $_ -like "FAIL *" } | ForEach-Object { "  ${n}: $_" } })
$summary | Set-Content -Encoding utf8 (Join-Path $out "summary.txt")
$summary
