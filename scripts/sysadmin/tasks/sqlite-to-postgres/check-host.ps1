# Checks from this PC, as a game client reaches the API through nginx; prints PASS/FAIL lines.
# New rows go in through every table (the ids come from the database's counters), and what was
# stored here and the players' traffic log go to check.sh (in $tdir\upload) to look for in PostgreSQL.
# PostgreSQL and the API start a little after SSH is back from the reboot: two minutes for that.
$upload = Join-Path $tdir "upload"
New-Item -ItemType Directory -Force $upload | Out-Null
$trafficLog = Join-Path $tdir "traffic.jsonl"
if (Test-Path $trafficLog) { Copy-Item $trafficLog (Join-Path $upload "traffic.jsonl") -Force }
else { "FAIL the players' traffic ran during the work (no $trafficLog)" }

$until = (Get-Date).AddSeconds(120)
do {
  $code = & curl.exe -s -m 5 -o NUL -w "%{http_code}" "http://$LabIp/health"
  if ($code -eq "200") { break }
  Start-Sleep -Seconds 2
} while ((Get-Date) -lt $until)
if ($code -eq "200") { "PASS the API answers at http://$LabIp/health after the reboot" }
else { "FAIL the API answers at http://$LabIp/health after the reboot (HTTP $code)" }

# Bodies go through files: Windows PowerShell mangles the quotes of a JSON argument to curl.exe.
$utf8 = New-Object Text.UTF8Encoding $false
$stored = New-Object Collections.Generic.List[string]
function Call([string]$method, [string]$path, [string]$json) {
  $bodyFile = Join-Path $tdir "check-body.json"
  $answer = Join-Path $tdir "check-answer.json"
  [IO.File]::WriteAllText($bodyFile, $json, $utf8)
  Remove-Item $answer -ErrorAction SilentlyContinue
  $code = & curl.exe -s -m 15 -o $answer -w "%{http_code}" -X $method -H "Content-Type: application/json" --data-binary "@$bodyFile" "http://$LabIp$path"
  $text = if (Test-Path $answer) { [IO.File]::ReadAllText($answer, $utf8) } else { "" }
  $obj = $null
  try { $obj = $text | ConvertFrom-Json } catch { }
  @{ Code = $code; Text = $text; Json = $obj }
}
function Short([string]$text) { $text.Substring(0, [Math]::Min(120, $text.Length)) -replace '\s+', ' ' }

$name = "host check " + [char]::ConvertFromUtf32(0x1F3C1) + " $token"
$p = Call "POST" "/api/players" (@{ name = $name; country = "SE" } | ConvertTo-Json -Compress)
$newPlayer = if ($p.Code -eq "201" -and $p.Json.id) { [int64]$p.Json.id } else { 0 }
if ($newPlayer) {
  "PASS a new player can sign up through http://$LabIp/api/players (id $newPlayer)"
  $stored.Add((@{ kind = "player"; id = $newPlayer; name = $name } | ConvertTo-Json -Compress))
} else {
  "FAIL a new player can sign up through http://$LabIp/api/players (HTTP $($p.Code): $(Short $p.Text))"
}
$who = if ($newPlayer) { $newPlayer } else { 21 }
$ok = 0
$ids = @()
foreach ($score in 4242, 8484) {
  $s = Call "POST" "/api/scores" (@{ player_id = $who; score = $score; level = "check" } | ConvertTo-Json -Compress)
  if ($s.Code -eq "201" -and $s.Json.id) {
    $ok++
    $ids += [int64]$s.Json.id
    $stored.Add((@{ kind = "score"; id = [int64]$s.Json.id; player_id = $who; score = $score; level = "check" } | ConvertTo-Json -Compress))
  } else { $last = "HTTP $($s.Code): $(Short $s.Text)" }
}
if ($ok -eq 2) { "PASS new scores can be posted (ids $($ids -join ', '))" } else { "FAIL new scores can be posted ($last)" }

$save = '{"version":3,"from":"check-host","token":"' + $token + '","note":"' + [char]::ConvertFromUtf32(0x1F4BE) + ' \"quoted\" C:\\saves"}'
$sv = Call "PUT" "/api/saves/$who" $save
$back = & curl.exe -s -m 15 -o (Join-Path $tdir "check-save.json") -w "%{http_code}" "http://$LabIp/api/saves/$who"
$same = (Test-Path (Join-Path $tdir "check-save.json")) -and ([IO.File]::ReadAllText((Join-Path $tdir "check-save.json"), $utf8) -ceq $save)
if ($sv.Code -eq "200" -and $back -eq "200" -and $same) {
  "PASS a cloud save can be stored and comes back as it was sent"
  $sha = -join ([Security.Cryptography.SHA256]::Create().ComputeHash($utf8.GetBytes($save)) | ForEach-Object { $_.ToString("x2") })
  $stored.Add((@{ kind = "save"; player_id = $who; sha256 = $sha } | ConvertTo-Json -Compress))
} else { "FAIL a cloud save can be stored and comes back as it was sent (PUT HTTP $($sv.Code), GET HTTP $back, same: $same)" }

$f = Call "POST" "/api/friends" (@{ player_id = $who; friend_id = 1 } | ConvertTo-Json -Compress)
if ($f.Code -eq "201") {
  "PASS a friend can be added"
  $stored.Add((@{ kind = "friend"; player_id = $who; friend_id = 1 } | ConvertTo-Json -Compress))
} else { "FAIL a friend can be added (HTTP $($f.Code): $(Short $f.Text))" }

[IO.File]::WriteAllLines((Join-Path $upload "host-writes.jsonl"), $stored, $utf8)
