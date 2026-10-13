# Every file of the new site must come back from the server byte for byte.
$site = Join-Path $tdir "site-v2"
$page = & curl.exe -s -m 10 "http://$LabIp/"
if (($page -join "`n") -match "version 2, $token") { "PASS http://$LabIp/ shows the new site" } else { "FAIL http://$LabIp/ shows the new site" }
foreach ($rel in "css/site.css", "img/logo.png", "img/bread.jpg") {
  $got = Join-Path $tdir ("got-" + ($rel -replace '/', '-'))
  $code = & curl.exe -s -m 10 -o $got -w "%{http_code}" "http://$LabIp/$rel"
  $want = (Get-FileHash (Join-Path $site $rel) -Algorithm SHA256).Hash
  $have = if (Test-Path $got) { (Get-FileHash $got -Algorithm SHA256).Hash } else { "" }
  if ($code -eq "200" -and $have -eq $want) { "PASS $rel arrives intact" } else { "FAIL $rel arrives intact (HTTP $code$(if ($code -eq '200') { ', different bytes' }))" }
}
