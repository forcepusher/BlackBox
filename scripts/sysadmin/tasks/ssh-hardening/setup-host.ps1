# Key pairs for alice, bob and carol: the private keys stay on this PC (for the checks), the
# public ones go to the VM.
$keys = Join-Path $tdir "keys"
$upload = Join-Path $tdir "upload"
New-Item -ItemType Directory -Force $keys, $upload | Out-Null
foreach ($u in "alice", "bob", "carol") {
  $k = Join-Path $keys $u
  if (-not (Test-Path $k)) { Invoke-LabExe "ssh-keygen.exe" @("-q", "-t", "ed25519", "-N", "", "-C", "$u@laptop", "-f", $k) | Out-Null }
  Protect-LabKey $k
  Copy-Item "$k.pub" (Join-Path $upload "$u.pub")
}
