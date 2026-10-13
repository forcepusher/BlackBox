<#
  Shared helpers for the sysadmin lab: the Ubuntu VM BlackBox administers over SSH in
  sysadmin-test.ps1, built by sysadmin-vm.ps1. Dot-source it: . "$PSScriptRoot\sysadmin\lab.ps1"

  The lab is portable: QEMU runs from .dev\sysadmin-lab\qemu\ (no install, no driver, no admin
  rights, no Windows feature), in software emulation. The VM's network is QEMU's own user-mode
  network: the VM is 10.0.2.15, it reaches the internet through QEMU, and its ports are
  forwarded to $LabIp on this PC, a loopback address no other computer can reach. Seen from
  inside the VM, connections from this PC come from 10.0.2.2.

  Disks: the downloaded cloud image stays as it is; base.qcow2 on top of it is the clean server
  (made once by sysadmin-vm.ps1); every task runs on a fresh task disk on top of base.qcow2,
  thrown away afterwards, so a reset takes a second.

  $env:LAB_INSTANCE = 1 (2, ...) runs another lab VM next to the first, at 127.0.0.3 (.4, ...),
  for instance to check the checks while BlackBox works on the first.
  Keep this file ASCII: Windows PowerShell 5.1 reads a BOM-less script as ANSI.
#>
$LabRepo = Split-Path (Split-Path $PSScriptRoot)
$LabDir = Join-Path $LabRepo ".dev\sysadmin-lab"
$LabInstance = if ($env:LAB_INSTANCE) { [int]$env:LAB_INSTANCE } else { 0 }
$LabIp = "127.0.0.$(2 + $LabInstance)"
$LabGuestIp = "10.0.2.15"
$LabHostSeenAs = "10.0.2.2"
$LabUser = "admin"
$LabKey = Join-Path $LabDir "key\admin_ed25519"
$LabKnownHosts = Join-Path $LabDir "known_hosts"
$LabQemu = Join-Path $LabDir "qemu\qemu-system-x86_64.exe"
$LabQemuImg = Join-Path $LabDir "qemu\qemu-img.exe"
$LabImage = Join-Path $LabDir "image\noble-server-cloudimg-amd64.img"
$LabBase = Join-Path $LabDir "disks\base.qcow2"
$LabSuffix = if ($LabInstance) { "-$LabInstance" } else { "" }
$LabTaskDisk = Join-Path $LabDir "disks\task$LabSuffix.qcow2"
$LabSerial = Join-Path $LabDir "serial$LabSuffix.log"
$LabPidFile = Join-Path $LabDir "qemu$LabSuffix.pid"
# Every port a task may need from outside, forwarded from $LabIp to the VM.
$LabPorts = @(22, 80, 443, 3000, 3306, 5432, 6379, 8000, 8080, 8443, 9000, 9090, 9100, 31337)

# Quotes one argument the way CommandLineToArgvW reads it back (Windows PowerShell 5.1 has no
# ProcessStartInfo.ArgumentList, and its own native-argument passing mangles embedded quotes).
function Format-LabArg([string]$a) {
  if ($a.Length -gt 0 -and $a -notmatch '[\s"]') { return $a }
  $sb = New-Object Text.StringBuilder
  [void]$sb.Append('"')
  $slashes = 0
  $bs = [char]92
  foreach ($c in $a.ToCharArray()) {
    if ($c -eq $bs) { $slashes++; continue }
    if ($c -eq '"') { [void]$sb.Append($bs, 2 * $slashes + 1); [void]$sb.Append('"') }
    else { if ($slashes) { [void]$sb.Append($bs, $slashes) }; [void]$sb.Append($c) }
    $slashes = 0
  }
  if ($slashes) { [void]$sb.Append($bs, 2 * $slashes) }
  [void]$sb.Append('"')
  $sb.ToString()
}

# Runs a program and returns @{ Code; Out; Err } without PowerShell touching its streams.
function Invoke-LabExe([string]$exe, [string[]]$arguments, [int]$TimeoutSec = 600, [string]$StdIn = $null) {
  $psi = New-Object Diagnostics.ProcessStartInfo $exe
  $psi.Arguments = ($arguments | ForEach-Object { Format-LabArg $_ }) -join ' '
  $psi.UseShellExecute = $false
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $psi.RedirectStandardInput = $true
  $psi.CreateNoWindow = $true
  $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
  $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
  $p = [Diagnostics.Process]::Start($psi)
  $outTask = $p.StandardOutput.ReadToEndAsync()
  $errTask = $p.StandardError.ReadToEndAsync()
  if ($StdIn) { $p.StandardInput.Write($StdIn) }
  $p.StandardInput.Close()
  if (-not $p.WaitForExit($TimeoutSec * 1000)) {
    try { $p.Kill() } catch { }
    return @{ Code = -1; Out = $outTask.Result; Err = "timed out after $TimeoutSec s" }
  }
  @{ Code = $p.ExitCode; Out = $outTask.Result; Err = $errTask.Result }
}

$LabSshOptions = @("-i", $LabKey, "-o", "BatchMode=yes", "-o", "ConnectTimeout=8", "-o", "StrictHostKeyChecking=no",
  "-o", "UserKnownHostsFile=$LabKnownHosts", "-o", "LogLevel=ERROR", "-o", "ServerAliveInterval=15")

# Runs a command on the VM as admin; returns @{ Code; Out; Err }.
function Invoke-LabSsh([string]$command, [int]$TimeoutSec = 600, [string]$User = $LabUser, [string]$Key = $LabKey, [string]$StdIn = $null) {
  $opts = $LabSshOptions.Clone()
  $opts[1] = $Key
  Invoke-LabExe "ssh.exe" ($opts + @("$User@$LabIp", $command)) $TimeoutSec $StdIn
}

function Copy-LabFiles([string[]]$sources, [string]$remoteDir) {
  $r = Invoke-LabExe "scp.exe" ($LabSshOptions + @("-r", "-q") + $sources + @("$LabUser@${LabIp}:$remoteDir"))
  if ($r.Code -ne 0) { throw "scp to $remoteDir failed: $($r.Err.Trim())" }
}

# Waits until admin can log in with the key; $false after the timeout.
function Wait-LabSsh([int]$TimeoutSec = 600) {
  $until = (Get-Date).AddSeconds($TimeoutSec)
  while ((Get-Date) -lt $until) {
    $r = Invoke-LabSsh "true" -TimeoutSec 20
    if ($r.Code -eq 0) { return $true }
    Start-Sleep -Seconds 3
  }
  $false
}

function Get-LabVmProcess {
  if (-not (Test-Path $LabPidFile)) { return $null }
  $id = (Get-Content $LabPidFile -ErrorAction SilentlyContinue | Select-Object -First 1)
  if (-not $id) { return $null }
  $p = Get-Process -Id ([int]$id) -ErrorAction SilentlyContinue
  if ($p -and $p.ProcessName -like "qemu-system*") { $p } else { $null }
}

function Stop-LabVm {
  $p = Get-LabVmProcess
  if ($p) { Stop-Process -Id $p.Id -Force; $p.WaitForExit(30000) | Out-Null }
  Remove-Item $LabPidFile -ErrorAction SilentlyContinue
}

# Starts QEMU on $disk in the background, headless; $extra are more QEMU arguments (the seed ISO).
function Start-LabVm([string]$disk, [string[]]$extra = @(), [int]$Cpus = 4, [int]$MemoryMB = 4096) {
  Stop-LabVm
  $fwd = ($LabPorts | ForEach-Object { "hostfwd=tcp:${LabIp}:$_-:$_" }) -join ","
  $qargs = @("-name", "BlackBox-Lab$LabSuffix", "-machine", "q35", "-accel", "tcg,thread=multi", "-cpu", "max",
    "-smp", [string]$Cpus, "-m", [string]$MemoryMB, "-rtc", "base=utc",
    "-drive", "file=$disk,if=virtio,format=qcow2,cache=writeback,discard=unmap",
    "-netdev", "user,id=n0,hostname=lab01,$fwd", "-device", "virtio-net-pci,netdev=n0",
    "-device", "virtio-rng-pci", "-display", "none", "-serial", "file:$LabSerial", "-monitor", "none") + $extra
  $p = Start-Process $LabQemu -ArgumentList ($qargs | ForEach-Object { Format-LabArg $_ }) -WindowStyle Hidden -PassThru
  Start-Sleep -Seconds 2
  if ($p.HasExited) { throw "QEMU exited at once (code $($p.ExitCode)); is another copy holding the disk or the ports?" }
  Set-Content -Path $LabPidFile -Value $p.Id
}

# A fresh server: a new task disk on top of the clean base, booted, SSH up.
function Reset-LabVm {
  Stop-LabVm
  Remove-Item $LabTaskDisk -ErrorAction SilentlyContinue
  $r = Invoke-LabExe $LabQemuImg @("create", "-q", "-f", "qcow2", "-F", "qcow2", "-b", (Split-Path $LabBase -Leaf), $LabTaskDisk)
  if ($r.Code -ne 0) { throw "qemu-img create failed: $($r.Err)" }
  Start-LabVm $LabTaskDisk
  if (-not (Wait-LabSsh)) { throw "The lab VM did not come up for SSH; see $LabSerial" }
}

# Reboots the VM from inside and waits for SSH; $false if it does not come back (locked out, say).
function Restart-LabVm([int]$TimeoutSec = 600) {
  $sudo = if ($script:LabSudoPassword) { "printf '%s\n' '$script:LabSudoPassword' | sudo -S -p ''" } else { "sudo" }
  Invoke-LabSsh "$sudo systemctl reboot" -TimeoutSec 20 | Out-Null
  Start-Sleep -Seconds 15
  Wait-LabSsh $TimeoutSec
}

# Whether a service on the VM answers from outside, through QEMU's port forwarding. QEMU accepts
# the connection on this PC before it knows whether the VM does, so a bare connect proves
# nothing: something has to come back. Services that speak first answer at once; others get a
# small request (HTTP, or PostgreSQL's SSL request on 5432).
function Test-LabPort([int]$port, [int]$TimeoutMs = 4000) {
  $c = New-Object Net.Sockets.TcpClient
  try {
    if (-not $c.ConnectAsync($LabIp, $port).Wait($TimeoutMs)) { return $false }
    $s = $c.GetStream()
    $probe = if ($port -eq 5432) { [byte[]](0, 0, 0, 8, 4, 210, 22, 47) } else { [Text.Encoding]::ASCII.GetBytes("HEAD / HTTP/1.0`r`n`r`n") }
    try { $s.Write($probe, 0, $probe.Length) } catch { return $false }
    $buf = New-Object byte[] 64
    $t = $s.ReadAsync($buf, 0, $buf.Length)
    if (-not $t.Wait($TimeoutMs)) { return $false }
    $t.Result -gt 0
  } catch { $false } finally { $c.Close() }
}

# Windows ssh.exe refuses a private key that other accounts can read.
function Protect-LabKey([string]$path) {
  & icacls.exe $path /inheritance:r /grant:r "$($env:USERNAME):F" | Out-Null
}
