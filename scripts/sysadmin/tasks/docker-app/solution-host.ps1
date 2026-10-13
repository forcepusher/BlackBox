# Reference solution, the part on this PC: copy the app to the server.
Invoke-LabSsh "rm -rf /tmp/statuspage" | Out-Null
Copy-LabFiles @((Join-Path $tdir "statuspage")) "/tmp/"
