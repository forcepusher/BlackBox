# Checks from this PC, as a customer's browser; prints PASS/FAIL lines.
$page = & curl.exe -s -m 10 -w "`n%{http_code}" "http://$LabIp/"
$code = ($page | Select-Object -Last 1)
if ($code -eq "200" -and ($page -join "`n") -match "build $token") { "PASS the shop answers on http://$LabIp/ with its own page" }
else { "FAIL the shop answers on http://$LabIp/ with its own page (HTTP $code)" }
$css = & curl.exe -s -m 10 -o NUL -w "%{http_code}" "http://$LabIp/style.css"
$img = & curl.exe -s -m 10 -o NUL -w "%{http_code}" "http://$LabIp/img/beans.jpg"
if ($css -eq "200" -and $img -eq "200") { "PASS its stylesheet and images load" } else { "FAIL its stylesheet and images load (HTTP $css, $img)" }
