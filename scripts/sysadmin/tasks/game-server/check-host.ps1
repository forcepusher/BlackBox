# Checks from this PC, the way a player's browser would connect. Runs in sysadmin-test.ps1 with
# the lab helpers loaded; prints PASS/FAIL lines.
$url = "ws://$LabIp/play"

function Connect-Ws {
  $ws = New-Object Net.WebSockets.ClientWebSocket
  $cts = New-Object Threading.CancellationTokenSource 10000
  $ws.ConnectAsync([Uri]$url, $cts.Token).Wait()
  $ws
}
function Send-Ws($ws, [string]$text) {
  $bytes = [Text.Encoding]::UTF8.GetBytes($text)
  $seg = New-Object "ArraySegment[byte]" -ArgumentList @(, $bytes)
  $ws.SendAsync($seg, [Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).Wait()
}
# The next message of the given type (for which $where holds), or $null within the time; other
# messages are skipped.
function Receive-Ws($ws, [string]$type, [scriptblock]$where = { $true }, [int]$TimeoutMs = 8000) {
  $until = (Get-Date).AddMilliseconds($TimeoutMs)
  $buf = New-Object byte[] 65536
  while ((Get-Date) -lt $until) {
    $seg = New-Object "ArraySegment[byte]" -ArgumentList @(, $buf)
    $left = [int][Math]::Max(1, ($until - (Get-Date)).TotalMilliseconds)
    $t = $ws.ReceiveAsync($seg, (New-Object Threading.CancellationTokenSource $left).Token)
    try { $t.Wait() } catch { return $null }
    $msg = [Text.Encoding]::UTF8.GetString($buf, 0, $t.Result.Count) | ConvertFrom-Json
    if ($msg.type -eq $type -and (& $where $msg)) { return $msg }
  }
  $null
}

try {
  $a = Connect-Ws
  Send-Ws $a '{"type":"join","name":"checker-a"}'
  $welcomeA = Receive-Ws $a "welcome"
  if ($welcomeA) { "PASS a player can join over $url" } else { "FAIL a player can join over $url (no welcome)" }
  $b = Connect-Ws
  Send-Ws $b '{"type":"join","name":"checker-b"}'
  $welcomeB = Receive-Ws $b "welcome"
  $seen = Receive-Ws $a "players" { param($m) @($m.players).Count -ge 2 }
  if ($welcomeB -and $seen) { "PASS the first player sees the second one join" }
  else { "FAIL the first player sees the second one join" }
  Send-Ws $b '{"type":"move","dx":1,"dy":0}'
  $idB = if ($welcomeB) { $welcomeB.id } else { "?" }
  $moved = Receive-Ws $a "moved" { param($m) $m.id -eq $idB }
  if ($moved) { "PASS moves are broadcast to the other player" }
  else { "FAIL moves are broadcast to the other player" }
  foreach ($ws in $a, $b) { try { $ws.Abort() } catch { } }
} catch {
  $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }
  "FAIL a player can join over $url ($($e.Message))"
}

# The game server's own port(s) must be closed to the outside.
$ports = @(3000)
$r = Invoke-LabSsh "sudo ss -ltnpH | grep -E 'users:\(\(""node(js)?""' | awk '{print `$4}' | sed 's/.*://' | sort -u"
$ports += @($r.Out -split '\s+' | Where-Object { $_ -match '^\d+$' } | ForEach-Object { [int]$_ })
$open = @($ports | Sort-Object -Unique | Where-Object { Test-LabPort $_ })
if ($open.Count -eq 0) { "PASS the game server's own port is closed to the outside ($((($ports | Sort-Object -Unique) -join ', ')))" }
else { "FAIL the game server's own port is closed to the outside (open: $($open -join ', '))" }
