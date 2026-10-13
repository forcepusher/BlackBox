# The answer has to say how it was started: both the service and the cron job.
$say = (Get-Content (Join-Path $tdir "run.txt") -Raw -ErrorAction SilentlyContinue) -split '\[say\]' | Select-Object -Last 1
if ($say -match '(?i)cron' -and $say -match '(?i)systemd|service') { "PASS the answer names both ways it was started (systemd service and cron)" }
else { "FAIL the answer names both ways it was started (systemd service and cron)" }
