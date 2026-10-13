# Logins from this PC with each person's own key; prints PASS/FAIL lines.
$keys = Join-Path $tdir "keys"
foreach ($u in "alice", "bob") {
  $r = Invoke-LabSsh "id -nG; sudo -n true && echo SUDO-OK; true" -User $u -Key (Join-Path $keys $u) -TimeoutSec 30
  if ($r.Code -eq 0) { "PASS $u can log in with the key" } else { "FAIL $u can log in with the key ($($r.Err.Trim()))" }
  if ($r.Out -match "SUDO-OK") { "PASS $u can use sudo without a password" } else { "FAIL $u can use sudo without a password" }
}
$r = Invoke-LabSsh "true" -User "carol" -Key (Join-Path $keys "carol") -TimeoutSec 30
if ($r.Code -ne 0) { "PASS carol can't log in with her key" } else { "FAIL carol can't log in with her key (she still can)" }
# What the server offers a client that has no key: only publickey may be left.
$probe = Invoke-LabExe "ssh.exe" @("-v", "-o", "BatchMode=yes", "-o", "PubkeyAuthentication=no", "-o", "StrictHostKeyChecking=no",
  "-o", "UserKnownHostsFile=$LabKnownHosts", "-o", "ConnectTimeout=5", "admin@$LabIp", "true") -TimeoutSec 30
$methods = ([regex]::Matches($probe.Err, 'Authentications that can continue: (\S+)') | Select-Object -Last 1).Groups[1].Value
if ($methods -and $methods -notmatch 'password|keyboard-interactive') { "PASS the server offers no password login ($methods)" }
else { "FAIL the server offers no password login (offers: $methods)" }
