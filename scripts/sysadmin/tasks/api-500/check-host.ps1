# Checks from this PC, as the shop's front end would call the API; prints PASS/FAIL lines.
$list = & curl.exe -s -m 10 -w "`n%{http_code}" "http://$LabIp/api/orders"
$code = $list | Select-Object -Last 1
if ($code -eq "200" -and ($list -join "`n") -match "A-$token-1" -and ($list -join "`n") -match "A-$token-2") { "PASS the order list answers with the orders that were there" }
else { "FAIL the order list answers with the orders that were there (HTTP $code)" }
# The body goes through a file: Windows PowerShell mangles the quotes of a JSON argument to curl.exe.
$body = Join-Path $tdir "new-order.json"
[IO.File]::WriteAllText($body, '{"id": "N-' + $token + '", "item": "filter papers", "qty": 3}')
$post = & curl.exe -s -m 10 -o NUL -w "%{http_code}" -H "Content-Type: application/json" --data-binary "@$body" "http://$LabIp/api/orders"
$again = (& curl.exe -s -m 10 "http://$LabIp/api/orders") -join "`n"
if ($post -eq "201" -and $again -match "N-$token") { "PASS a new order can be stored" } else { "FAIL a new order can be stored (HTTP $post)" }
