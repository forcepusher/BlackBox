# Checks from this PC, as a visitor's browser that accepted the self-signed certificate, with
# studio.lab.test pointing at the server.
$name = "studio.lab.test"
$resolve = @("--resolve", "${name}:443:$LabIp", "--resolve", "${name}:80:$LabIp")
$page = & curl.exe -sk -m 10 @resolve -w "`n%{http_code}" "https://$name/"
$code = ($page | Select-Object -Last 1)
if ($code -eq "200" -and ($page -join "`n") -match "release $token") { "PASS the site answers on https://$name/" }
else { "FAIL the site answers on https://$name/ (HTTP $code)" }
$css = & curl.exe -sk -m 10 @resolve -o NUL -w "%{http_code}" "https://$name/css/main.css"
if ($css -eq "200") { "PASS its stylesheet loads" } else { "FAIL its stylesheet loads (HTTP $css)" }
$r = & curl.exe -s -m 10 @resolve -o NUL -w "%{http_code} %{redirect_url}" "http://$name/"
if ($r -match '^30[1278] https://') { "PASS http:// redirects to https:// ($r)" } else { "FAIL http:// redirects to https:// ($r)" }
