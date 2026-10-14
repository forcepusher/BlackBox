# The answer has to say what was going on: a log deleted while metricsd still had it open.
$say = (Get-Content (Join-Path $tdir "run.txt") -Raw -ErrorAction SilentlyContinue) -split '\[say\]' | Select-Object -Last 1
if ($say -match '(?i)delet' -and $say -match '(?i)open|handle|lsof|descriptor|still (held|in use)') { "PASS the answer explains it: a deleted log that metricsd still held open" }
else { "FAIL the answer explains it: a deleted log that metricsd still held open" }
