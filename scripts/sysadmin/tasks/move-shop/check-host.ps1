# Checks from this PC, as a visitor's browser would; prints PASS/FAIL lines. The old server is off.
# shop-api starts a little after SSH is back from the reboot, so the API gets a minute and a half.
$page = (& curl.exe -s -m 10 "http://$LabIp/") -join "`n"
if ($page -match "Lab Shop $token") { "PASS the shop's page answers at http://$LabIp/" } else { "FAIL the shop's page answers at http://$LabIp/" }
$n = 0
$until = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $until) {
  $api = (& curl.exe -s -m 10 "http://$LabIp/api/products") -join ""
  try { $n = ($api | ConvertFrom-Json).Count } catch { $n = 0 }
  if ($n -eq 250) { break }
  Start-Sleep -Seconds 3
}
if ($n -eq 250) { "PASS http://$LabIp/api/products lists the 250 products" } else { "FAIL http://$LabIp/api/products lists the 250 products (got $n)" }
