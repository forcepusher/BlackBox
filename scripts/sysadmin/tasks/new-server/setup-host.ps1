# Our key pair, on this PC: the private key stays here (for the checks), the public one also goes
# to the VM for the reference solution.
$keys = Join-Path $tdir "keys"
$upload = Join-Path $tdir "upload"
New-Item -ItemType Directory -Force $keys, $upload | Out-Null
$k = Join-Path $keys "ops_ed25519"
if (-not (Test-Path $k)) { Invoke-LabExe "ssh-keygen.exe" @("-q", "-t", "ed25519", "-N", "", "-C", "ops@office", "-f", $k) | Out-Null }
Protect-LabKey $k
Copy-Item "$k.pub" (Join-Path $upload "ops.pub")
