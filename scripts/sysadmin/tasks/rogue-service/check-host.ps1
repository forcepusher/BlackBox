# The answer has to say how it was started: both the service and the cron job. "cron" alone isn't
# enough: an answer that says it checked cron and found nothing names it too (seen in a run); one
# that found the job names root's crontab or its @reboot line.
$say = (Get-Content (Join-Path $tdir "run.txt") -Raw -ErrorAction SilentlyContinue) -split '\[say\]' | Select-Object -Last 1
if ($say -match '(?i)crontab|@reboot' -and $say -match '(?i)systemd|service') { "PASS the answer names both ways it was started (systemd service and cron)" }
else { "FAIL the answer names both ways it was started (systemd service and cron)" }
