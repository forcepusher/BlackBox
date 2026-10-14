# Checks from this PC, as a visitor would use the page; prints PASS/FAIL lines. Docker starts the
# container a little after SSH is back from the reboot, so the page gets two minutes to appear.
$page = $null
$until = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $until) {
  $page = (& curl.exe -s -m 5 "http://${LabIp}:8080/") -join "`n"
  if ($page -match "build $token") { break }
  Start-Sleep -Seconds 3
}
if ($page -match "build $token") { "PASS the status page built from our code answers at http://${LabIp}:8080/" }
else { "FAIL the status page built from our code answers at http://${LabIp}:8080/ (got: $(if ($page) { $page.Substring(0, [Math]::Min(80, $page.Length)) } else { 'nothing' }))" }
$body = Join-Path $tdir "check-note.txt"
[IO.File]::WriteAllText($body, "host-$token")
$code = & curl.exe -s -m 10 -o NUL -w "%{http_code}" --data-binary "@$body" "http://${LabIp}:8080/checks"
if ($code -eq "201") { "PASS a check can be recorded through the app" } else { "FAIL a check can be recorded through the app (HTTP $code)" }
