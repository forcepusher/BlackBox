<#
  The game's players, for the sqlite-to-postgres ticket. sysadmin-test.ps1 starts this on this PC
  once setup is done and stops it when the run is over (it creates $Dir\traffic.stop; this also
  stops when the runner is gone). Like the game clients, it calls http://<Ip>/ a few times a
  second, mostly writes (new scores, cloud saves, new players, friends) and some reads, and
  retries a request that fails (no answer, 5xx) with a growing pause, giving up after 12 tries.
  Every attempt goes to $Dir\traffic.jsonl: its time, result, and what it wrote; check.sh looks
  for every write the API acknowledged in PostgreSQL and for the longest stretch without an answer.
  Players 1 to 20 are left alone (the checks read them as they were seeded).
  Keep this file ASCII: Windows PowerShell 5.1 reads a BOM-less script as ANSI.
#>
param([string]$Ip, [string]$Dir, [string]$Token, [int]$ParentPid = 0)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http
$handler = New-Object Net.Http.HttpClientHandler
$handler.UseProxy = $false
$http = New-Object Net.Http.HttpClient($handler)
$http.Timeout = [TimeSpan]::FromSeconds(10)
$base = "http://$Ip"
$utf8 = New-Object Text.UTF8Encoding $false
$stream = [IO.File]::Open((Join-Path $Dir "traffic.jsonl"), 'Append', 'Write', 'ReadWrite')
$log = New-Object IO.StreamWriter($stream, $utf8)
$log.AutoFlush = $true
$stopFile = Join-Path $Dir "traffic.stop"
$parent = if ($ParentPid) { Get-Process -Id $ParentPid -ErrorAction SilentlyContinue } else { $null }
$rng = New-Object Random
$sha = [Security.Cryptography.SHA256]::Create()

function U([int[]]$codes) { -join ($codes | ForEach-Object { [char]::ConvertFromUtf32($_) }) }
function Hex([byte[]]$bytes) { -join ($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString("x2") }) }
function NowMs { [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }
function Write-Entry($entry) { $log.WriteLine(($entry | ConvertTo-Json -Compress -Depth 4)) }
function Stopping { (Test-Path $stopFile) -or ($parent -and $parent.HasExited) }
function Pick($list) { $list[$rng.Next($list.Count)] }

$emoji = @((U 0x1F409), (U 0x1F525), (U 0x1F3AE), (U 0x1F47E), (U 0x2694, 0xFE0F), (U 0x1F469, 0x200D, 0x1F4BB), (U 0x1F1EF, 0x1F1F5))
$names = @("Ada", "Linus", ("Zo" + (U 0xEB)), ("Jos" + (U 0xE9)), "O'Brien", 'The "Boss"', "back\slash", "Smith, John",
  (U 0x674E, 0x5C0F, 0x9F99), (U 0x041C, 0x0430, 0x0440, 0x0438, 0x044F), (U 0x0645, 0x062D, 0x0645, 0x062F), "NULL", "xX_sniper_Xx")
$items = @("sword", "potion", ("Schwert " + (U 0x1F5E1, 0xFE0F)), 'key "gold"', "map\old", (U 0x9B54, 0x6CD5, 0x306E, 0x6756))
$countries = @("US", "DE", "FR", "JP", "KR", "BR", "PL", "UA", "SE", $null)

# The players online, whom the clients write for: some of the seeded ones (21 to 600; some of the
# ids were deleted, as in seed.py) and the ones that sign up while this runs. Reads go to anyone.
$players = New-Object Collections.Generic.List[int]
foreach ($i in 21..600) { if (($i * 7) % 13 -ne 5) { $players.Add($i) } }
$everyone = @(21..3000 | Where-Object { ($_ * 7) % 13 -ne 5 })
$saved = @{}

function Send([string]$method, [string]$path, [string]$body) {
  $req = New-Object Net.Http.HttpRequestMessage((New-Object Net.Http.HttpMethod $method), "$base$path")
  if ($body) { $req.Content = New-Object Net.Http.StringContent($body, $utf8, "application/json") }
  $sw = [Diagnostics.Stopwatch]::StartNew()
  try {
    $resp = $http.SendAsync($req).GetAwaiter().GetResult()
    $text = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    @{ Status = [int]$resp.StatusCode; Body = $text; Ms = $sw.ElapsedMilliseconds; Error = "" }
  } catch {
    @{ Status = 0; Body = ""; Ms = $sw.ElapsedMilliseconds; Error = $_.Exception.GetBaseException().Message }
  } finally {
    $req.Dispose()
  }
}

function New-Save([int]$pid_, [int]$rev) {
  $inv = @(1..$rng.Next(1, 8) | ForEach-Object { [ordered]@{ item = (Pick $items); qty = $rng.Next(1, 99) } })
  $blob = New-Object byte[] ($rng.Next(100, 6000))
  $rng.NextBytes($blob)
  [ordered]@{
    version = 3; rev = $rev; client = "traffic-$Token"; player = $pid_; level = "$($rng.Next(1, 9))-$($rng.Next(1, 5))"
    coins = $rng.Next(0, 1000000); inventory = $inv; notes = ("checkpoint " + (Pick $emoji) + ' "C:\Games\save.dat"')
    map = [Convert]::ToBase64String($blob)
  } | ConvertTo-Json -Compress -Depth 4
}

$n = 0
$rev = 0
Write-Entry ([ordered]@{ ev = "start"; ms = (NowMs); t = (Get-Date).ToUniversalTime().ToString("o"); ip = $Ip })
while (-not (Stopping)) {
  $began = NowMs
  try {
    $n++
    $roll = $rng.Next(100)
    $w = $null
    $body = ""
    $method = "GET"
    if ($roll -lt 35) {
      $op = "score"; $method = "POST"; $path = "/api/scores"
      $level = if ($rng.Next(10) -eq 0) { $null } else { "$($rng.Next(1, 9))-$($rng.Next(1, 5))" }
      $w = [ordered]@{ kind = "score"; player_id = (Pick $players); score = $rng.Next(0, 250000); level = $level }
      $body = [ordered]@{ player_id = $w.player_id; score = $w.score; level = $level } | ConvertTo-Json -Compress
    } elseif ($roll -lt 60) {
      $op = "save"; $method = "PUT"
      $pid_ = Pick $players
      $rev++
      $path = "/api/saves/$pid_"
      $body = New-Save $pid_ $rev
      $w = [ordered]@{ kind = "save"; player_id = $pid_; sha256 = (Hex $utf8.GetBytes($body)); bytes = $utf8.GetByteCount($body) }
    } elseif ($roll -lt 70) {
      $op = "player"; $method = "POST"; $path = "/api/players"
      $name = (Pick $names) + " " + (Pick $emoji) + " $n-$Token"
      $avatar = $null
      if ($rng.Next(3) -eq 0) {
        $a = New-Object byte[] ($rng.Next(64, 2048))
        $rng.NextBytes($a)
        $a[0] = 0x89; $a[1] = 0; $a[2] = 10
        $avatar = [Convert]::ToBase64String($a)
      }
      $country = Pick $countries
      $w = [ordered]@{ kind = "player"; name = $name; country = $country; avatar_sha256 = $(if ($avatar) { Hex $a } else { $null }) }
      $body = [ordered]@{ name = $name; country = $country; avatar = $avatar } | ConvertTo-Json -Compress
    } elseif ($roll -lt 75) {
      $op = "friend"; $method = "POST"; $path = "/api/friends"
      $a1 = Pick $players; $a2 = Pick $players
      if ($a1 -eq $a2) { $a2 = $players[0] }
      $w = [ordered]@{ kind = "friend"; player_id = $a1; friend_id = $a2 }
      $body = [ordered]@{ player_id = $a1; friend_id = $a2 } | ConvertTo-Json -Compress
    } elseif ($roll -lt 85) {
      $op = "leaderboard"; $path = "/api/leaderboard"
    } elseif ($roll -lt 95 -or $saved.Count -eq 0) {
      $op = "get-player"; $path = "/api/players/$(Pick $(if ($rng.Next(2)) { $players } else { $everyone }))"
    } else {
      $op = "get-save"
      $pid_ = Pick @($saved.Keys)
      $path = "/api/saves/$pid_"
    }

    for ($try = 1; $try -le 12; $try++) {
      $r = Send $method $path $body
      $ok = $r.Status -ge 200 -and $r.Status -lt 300
      $entry = [ordered]@{ ev = "req"; ms = (NowMs); n = $n; try = $try; op = $op; method = $method; path = $path; status = $r.Status; ok = $ok; took = $r.Ms }
      if ($r.Error) { $entry.error = $r.Error }
      elseif (-not $ok) { $entry.error = $r.Body.Substring(0, [Math]::Min(160, $r.Body.Length)) }
      if ($w) {
        $entry.w = $w
        if ($ok -and ($op -eq "player" -or $op -eq "score")) {
          $id = [int64](($r.Body | ConvertFrom-Json).id)
          $w.id = $id
          if ($op -eq "player") { $players.Add([int]$id) }
        }
        if ($ok -and $op -eq "save") { $saved[$w.player_id] = $w.sha256 }
      }
      if ($ok -and $op -eq "get-save") { $entry.same = ((Hex $utf8.GetBytes($r.Body)) -eq $saved[$pid_]) }
      Write-Entry $entry
      # A client error (4xx other than timeouts and rate limits) is not retried.
      if ($ok -or ($r.Status -ge 400 -and $r.Status -lt 500 -and $r.Status -ne 408 -and $r.Status -ne 429)) { break }
      $pause = @(500, 1000, 2000, 3000, 5000)[[Math]::Min($try - 1, 4)]
      $until = (NowMs) + $pause
      while ((NowMs) -lt $until -and -not (Stopping)) { Start-Sleep -Milliseconds 100 }
      if (Stopping) { break }
    }
  } catch {
    Write-Entry ([ordered]@{ ev = "error"; ms = (NowMs); n = $n; error = $_.Exception.Message })
  }
  # A few requests a second.
  $left = 280 - ((NowMs) - $began)
  if ($left -gt 0) { Start-Sleep -Milliseconds $left }
}
Write-Entry ([ordered]@{ ev = "stop"; ms = (NowMs); t = (Get-Date).ToUniversalTime().ToString("o"); requests = $n })
$log.Dispose()
