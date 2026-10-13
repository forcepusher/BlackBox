# Reference solution, the part on this PC: copy the site to the server as it is.
Invoke-LabSsh "rm -rf /tmp/site-v2" | Out-Null
Copy-LabFiles @((Join-Path $tdir "site-v2")) "/tmp/"
