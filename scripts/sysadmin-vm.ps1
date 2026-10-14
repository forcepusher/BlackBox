<#
.SYNOPSIS
  Builds the sysadmin lab that sysadmin-test.ps1 hands to BlackBox: a Debian 13 server in
  QEMU, admin@127.0.0.2 with a key. Portable: QEMU and everything else lives in
  .dev\sysadmin-lab\; nothing is installed, no admin rights or Windows features are needed, and
  deleting the folder removes it all. QEMU runs in software emulation, several times slower
  than a hypervisor, which matters little next to the model's own time per step.

  Downloads (once, checked against the publishers' checksums): QEMU for Windows from
  qemu.weilnetz.de (the build qemu.org links to; its server is slow per connection, so it is
  fetched in parallel parts), 7-Zip's standalone 7zr to unpack it if no 7-Zip is around, and
  Debian's official cloud image (genericcloud, the one for virtual machines; a fixed release, so
  a rebuild gets the same server). The first boot is set up by cloud-init from a seed ISO built
  with Windows' own IMAPI2; the apt timers that would grab the dpkg lock at random moments in a
  run are turned off, cloud-init is disabled, and the result is the clean disk base.qcow2.

.EXAMPLE
  ./scripts/sysadmin-vm.ps1            # build it (once)
  ./scripts/sysadmin-vm.ps1 -Rebuild   # throw the clean disk away and build it again
#>
param([switch]$Rebuild, [int]$DiskGB = 24)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
. "$PSScriptRoot\sysadmin\lab.ps1"

$qemuBuild = "20260811"
$qemuUrl = "https://qemu.weilnetz.de/w64/2026/qemu-w64-setup-$qemuBuild.exe"
$imageRelease = "https://cloud.debian.org/images/cloud/trixie/20261001-2618"
$imageUrl = "$imageRelease/$(Split-Path $LabImage -Leaf)"
$downloads = Join-Path $LabDir "downloads"
$seedIso = Join-Path $LabDir "seed\seed.iso"

function Step([string]$text) { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $text) }

# curl.exe in $parts parallel byte ranges (weilnetz.de serves each connection slowly).
function Get-Download([string]$url, [string]$file, [int]$parts = 24) {
  $head = & curl.exe -sIL $url
  $size = [long](($head | Select-String '^content-length:\s*(\d+)' | Select-Object -Last 1).Matches[0].Groups[1].Value)
  $dir = "$file.parts"
  New-Item -ItemType Directory -Force $dir | Out-Null
  $chunk = [long][Math]::Ceiling($size / $parts)
  for ($try = 0; $try -lt 5; $try++) {
    $procs = @()
    for ($i = 0; $i -lt $parts; $i++) {
      $s = $i * $chunk; $e = [Math]::Min($size, $s + $chunk) - 1
      $part = Join-Path $dir $i
      $have = if (Test-Path $part) { (Get-Item $part).Length } else { 0 }
      if ($have -ge $e - $s + 1) { continue }
      $procs += Start-Process curl.exe -ArgumentList @("-s", "-r", "$($s + $have)-$e", "-o", "`"$part.new`"", $url) -WindowStyle Hidden -PassThru
    }
    $procs | ForEach-Object { $_.WaitForExit() }
    for ($i = 0; $i -lt $parts; $i++) {
      $part = Join-Path $dir $i
      if (Test-Path "$part.new") {
        $bytes = [IO.File]::ReadAllBytes("$part.new")
        $f = [IO.File]::Open($part, 'Append'); $f.Write($bytes, 0, $bytes.Length); $f.Close()
        Remove-Item "$part.new"
      }
    }
    if ($procs.Count -eq 0) { break }
  }
  $out = [IO.File]::Create($file)
  for ($i = 0; $i -lt $parts; $i++) { $b = [IO.File]::ReadAllBytes((Join-Path $dir $i)); $out.Write($b, 0, $b.Length) }
  $out.Close()
  if ((Get-Item $file).Length -ne $size) { throw "Download of $url is incomplete" }
  Remove-Item -Recurse -Force $dir
}

# A 7-Zip that unpacks NSIS installers: one already on this PC, or 7-Zip's own, fetched and
# unpacked with its standalone 7zr.exe.
function Get-7Zip {
  foreach ($p in "C:\Program Files\7-Zip\7z.exe", "C:\Program Files (x86)\7-Zip\7z.exe", "C:\ProgramData\chocolatey\tools\7z.exe", (Join-Path $downloads "7zip\7z.exe")) {
    if (Test-Path $p) { return $p }
  }
  $dir = Join-Path $downloads "7zip"
  New-Item -ItemType Directory -Force $dir | Out-Null
  $page = (& curl.exe -sL "https://www.7-zip.org/download.html") -join "`n"
  $setup = ([regex]::Match($page, 'a/(7z\d+-x64\.exe)')).Groups[1].Value
  & curl.exe -sLo (Join-Path $dir "7zr.exe") "https://www.7-zip.org/a/7zr.exe"
  & curl.exe -sLo (Join-Path $dir $setup) "https://www.7-zip.org/a/$setup"
  & (Join-Path $dir "7zr.exe") x (Join-Path $dir $setup) "-o$dir" -y | Out-Null
  Join-Path $dir "7z.exe"
}

# 1. QEMU, unpacked from its installer without running it.
if (-not (Test-Path $LabQemu)) {
  $setup = Join-Path $downloads "qemu\qemu-w64-setup-$qemuBuild.exe"
  New-Item -ItemType Directory -Force (Split-Path $setup) | Out-Null
  & curl.exe -sLo "$setup.sha512" ($qemuUrl -replace '\.exe$', '.sha512')
  $want = ((Get-Content "$setup.sha512" -Raw) -split '\s+')[0].ToUpper()
  if (-not (Test-Path $setup) -or (Get-FileHash $setup -Algorithm SHA512).Hash -ne $want) {
    Step "Downloading $qemuUrl"
    Get-Download $qemuUrl $setup
    if ((Get-FileHash $setup -Algorithm SHA512).Hash -ne $want) { throw "QEMU setup does not match its SHA512" }
  }
  Step "Unpacking QEMU"
  & (Get-7Zip) x $setup "-o$(Split-Path $LabQemu)" -y | Out-Null
  if (-not (Test-Path $LabQemu)) { throw "Unpacking QEMU failed" }
}
Step ((& $LabQemu --version | Select-Object -First 1) + " ok")

# 2. The official cloud image, checked against Debian's SHA512SUMS. (A built lab is left alone
#    before this: its clean disk is a layer on top of the image file.)
if ((Test-Path $LabBase) -and -not $Rebuild) { Step "The lab is already built ($LabBase); use -Rebuild to make it again"; return }
New-Item -ItemType Directory -Force (Split-Path $LabImage), (Split-Path $LabKey), (Split-Path $seedIso), (Split-Path $LabBase) | Out-Null
$sums = Join-Path (Split-Path $LabImage) "SHA512SUMS"
& curl.exe -fsSLo $sums "$imageRelease/SHA512SUMS"
$name = Split-Path $LabImage -Leaf
$want = ((Get-Content $sums | Where-Object { $_ -match " \*?$([regex]::Escape($name))$" }) -split '\s+')[0].ToUpper()
if (-not $want) { throw "$name is not listed in $imageRelease/SHA512SUMS" }
if (-not (Test-Path $LabImage) -or (Get-FileHash $LabImage -Algorithm SHA512).Hash -ne $want) {
  Step "Downloading $imageUrl"
  & curl.exe -fL --retry 3 -C - -o $LabImage $imageUrl
  if ((Get-FileHash $LabImage -Algorithm SHA512).Hash -ne $want) { throw "$name does not match SHA512SUMS" }
}
Step "Image ok: $LabImage"

if (Test-Path $LabBase) {
  Stop-LabVm
  Set-ItemProperty $LabBase -Name IsReadOnly -Value $false
  Remove-Item $LabBase, $LabTaskDisk -ErrorAction SilentlyContinue
}

# 3. The admin key BlackBox gets, and the harness logs in with.
if (-not (Test-Path $LabKey)) {
  $r = Invoke-LabExe "ssh-keygen.exe" @("-q", "-t", "ed25519", "-N", "", "-C", "blackbox-lab-admin", "-f", $LabKey)
  if ($r.Code -ne 0) { throw "ssh-keygen failed: $($r.Err)" }
}
Protect-LabKey $LabKey
$pub = (Get-Content "$LabKey.pub" -Raw).Trim()
Remove-Item $LabKnownHosts -ErrorAction SilentlyContinue

# 4. cloud-init seed: the admin user with the key and passwordless sudo, the apt lists.
$seedDir = Join-Path $LabDir "seed\files"
New-Item -ItemType Directory -Force $seedDir | Out-Null
$userData = @"
#cloud-config
hostname: lab01
manage_etc_hosts: true
users:
  - name: $LabUser
    gecos: Lab Admin
    groups: [adm, sudo]
    shell: /bin/bash
    sudo: "ALL=(ALL) NOPASSWD:ALL"
    lock_passwd: true
    ssh_authorized_keys:
      - $pub
ssh_pwauth: false
timezone: Etc/UTC
package_update: true
package_upgrade: false
runcmd:
  - [systemctl, disable, apt-daily.timer, apt-daily-upgrade.timer]
"@
$utf8 = New-Object Text.UTF8Encoding $false
[IO.File]::WriteAllText((Join-Path $seedDir "user-data"), $userData.Replace("`r`n", "`n"), $utf8)
[IO.File]::WriteAllText((Join-Path $seedDir "meta-data"), "instance-id: blackbox-lab-1`nlocal-hostname: lab01`n", $utf8)
if (-not ("LabIsoWriter" -as [type])) {
  Add-Type -TypeDefinition @"
using System; using System.IO; using System.Runtime.InteropServices;
public static class LabIsoWriter {
  public static void Save(object image, string path) {
    var s = (System.Runtime.InteropServices.ComTypes.IStream)image;
    System.Runtime.InteropServices.ComTypes.STATSTG st; s.Stat(out st, 1); long left = st.cbSize;
    var buf = new byte[65536]; IntPtr pRead = Marshal.AllocHGlobal(4);
    try { using (var f = File.Create(path)) { while (left > 0) { s.Read(buf, buf.Length, pRead); int n = Marshal.ReadInt32(pRead); if (n <= 0) break; f.Write(buf, 0, n); left -= n; } } }
    finally { Marshal.FreeHGlobal(pRead); }
  }
}
"@
}
$fsi = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
$fsi.FileSystemsToCreate = 3 # ISO 9660 + Joliet, so Linux sees the lower-case names
$fsi.VolumeName = "cidata"
$fsi.Root.AddTree($seedDir, $false)
[LabIsoWriter]::Save($fsi.CreateResultImage().ImageStream, $seedIso)
Step "Seed ISO ok: $seedIso"

# 5. The clean disk on top of the untouched image, first boot with the seed.
# The backing file is named relative to the disk, so the lab folder can move.
$r = Invoke-LabExe $LabQemuImg @("create", "-q", "-f", "qcow2", "-F", "qcow2", "-b", "../image/$name", $LabBase, "${DiskGB}G")
if ($r.Code -ne 0) { throw "qemu-img create failed: $($r.Err)" }
Step "First boot (cloud-init); software emulation takes a while"
# The seed goes in as a virtio disk: genericcloud's kernel has virtio for sure, a CD drive maybe not.
Start-LabVm $LabBase @("-drive", "file=$seedIso,if=virtio,format=raw,readonly=on")
if (-not (Wait-LabSsh 1800)) { throw "No SSH on $LabIp after the first boot; see $LabSerial" }
$r = Invoke-LabSsh "sudo cloud-init status --wait --long" -TimeoutSec 3600
Write-Host $r.Out.Trim()
if ($r.Out -notmatch "status: done") { throw "cloud-init did not finish cleanly" }

# 6. Freeze it: cloud-init off (without its seed it would treat the next boot as a new
#    instance), logs emptied, powered off, read-only.
$prep = "sudo touch /etc/cloud/cloud-init.disabled && sudo apt-get clean && " +
  "sudo rm -f /var/log/cloud-init*.log && sudo journalctl --rotate && sudo journalctl --vacuum-time=1s && " +
  "grep PRETTY_NAME /etc/os-release; cat /etc/debian_version; uname -r; ip -4 -br addr; df -h / | tail -1; systemctl is-system-running"
$r = Invoke-LabSsh $prep -TimeoutSec 1800
Write-Host $r.Out.Trim()
Invoke-LabSsh "sudo systemctl poweroff" -TimeoutSec 20 | Out-Null
$p = Get-LabVmProcess
if ($p -and -not $p.WaitForExit(600000)) { throw "The VM did not power off" }
Remove-Item $LabPidFile -ErrorAction SilentlyContinue
Set-ItemProperty $LabBase -Name IsReadOnly -Value $true
Step "The lab is ready: ssh -i $LabKey $LabUser@$LabIp (after Reset-LabVm)"
