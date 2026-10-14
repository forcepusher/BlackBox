# Logins from this PC; prints PASS/FAIL lines.
$k = Join-Path $tdir "keys\ops_ed25519"
$r = Invoke-LabSsh "sudo -n true && echo SUDO-OK; true" -User "deploy" -Key $k -TimeoutSec 30
if ($r.Code -eq 0) { "PASS deploy logs in with our key" } else { "FAIL deploy logs in with our key ($($r.Err.Trim()))" }
if ($r.Out -match "SUDO-OK") { "PASS deploy can use sudo without a password" } else { "FAIL deploy can use sudo without a password" }
$r = Invoke-LabSsh "true" -User "root" -Key $k -TimeoutSec 30
if ($r.Code -ne 0) { "PASS root can't log in with our key either" } else { "FAIL root can't log in with our key either (it can)" }
# What the server offers a client that has no key: only publickey may be left.
foreach ($u in "root", "deploy") {
  $probe = Invoke-LabExe "ssh.exe" @("-v", "-o", "BatchMode=yes", "-o", "PubkeyAuthentication=no", "-o", "StrictHostKeyChecking=no",
    "-o", "UserKnownHostsFile=$LabKnownHosts", "-o", "ConnectTimeout=5", "$u@$LabIp", "true") -TimeoutSec 30
  $methods = ([regex]::Matches($probe.Err, 'Authentications that can continue: (\S+)') | Select-Object -Last 1).Groups[1].Value
  if ($methods -and $methods -notmatch 'password|keyboard-interactive') { "PASS the server offers $u no password login ($methods)" }
  else { "FAIL the server offers $u no password login (offers: $methods)" }
}
