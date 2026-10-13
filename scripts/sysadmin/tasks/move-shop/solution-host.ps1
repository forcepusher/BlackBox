# Reference solution, the part on this PC: the database dump and the files go from the old server
# to the new one through this PC.
$oldIp = "127.0.0.$(3 + $LabInstance)"
$opts = @("-i", $LabKey, "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=no", "-o", "UserKnownHostsFile=$LabKnownHosts", "-o", "LogLevel=ERROR")
$pack = "sudo -u postgres pg_dump -Fc shop > /tmp/shop.dump && sudo tar -czf /tmp/shop-files.tgz -C / var/www/shop opt/shop-api " +
  "etc/shop-api.env etc/systemd/system/shop-api.service etc/nginx/sites-available/shop && sudo chmod 644 /tmp/shop.dump /tmp/shop-files.tgz"
$r = Invoke-LabExe "ssh.exe" ($opts + @("$LabUser@$oldIp", $pack))
if ($r.Code -ne 0) { throw "packing on the old server failed: $($r.Err)" }
$here = Join-Path $tdir "move"
New-Item -ItemType Directory -Force $here | Out-Null
$r = Invoke-LabExe "scp.exe" ($opts + @("$LabUser@${oldIp}:/tmp/shop.dump", "$LabUser@${oldIp}:/tmp/shop-files.tgz", $here))
if ($r.Code -ne 0) { throw "scp from the old server failed: $($r.Err)" }
Invoke-LabSsh "rm -rf /tmp/move" | Out-Null
Copy-LabFiles @($here) "/tmp/"
