# ComfyUI benchmark: BlackBox makes pictures, video, sound, music, speech and a 3D model with the
# ComfyUI on this PC, each from one plain request, and the results are checked from the run's log
# (the files the comfyui tool reported, their kind, length and sound). Uses the model in
# .dev/Data/settings.json. Runs land in .dev/comfy/<time>/, one log per task and a results.md.
#
#   ./scripts/comfy-test.ps1                      # all tasks
#   ./scripts/comfy-test.ps1 -Tasks picture,sound  # some of them
#   ./scripts/comfy-test.ps1 -StopComfy           # stop ComfyUI first, so the first task has to start it
param(
    [string[]]$Tasks = @('picture', 'video', 'sound', 'music', 'speech', 'model3d', 'combo'),
    [switch]$StopComfy,
    [int]$MinutesPerTask = 45
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$cli = Join-Path $repo 'src\BlackBox.Cli\bin\Debug\net10.0-windows\blackbox-cli.exe'
dotnet build (Join-Path $repo 'src\BlackBox.Cli\BlackBox.Cli.csproj') -v q -nologo | Out-Null
if (-not (Test-Path $cli)) { throw "The CLI didn't build: $cli" }
$out = Join-Path $repo (".dev\comfy\" + (Get-Date -Format 'yyyyMMdd-HHmm'))
New-Item -ItemType Directory -Force $out | Out-Null

$pictures = [Environment]::GetFolderPath('MyPictures')
$music = [Environment]::GetFolderPath('MyMusic')
# What each request should give: a kind of file, how long (seconds) for video and sound, sound in a video, a folder.
$all = [ordered]@{
    picture = @{ Prompt = 'Make a picture of a lighthouse on a cliff at sunset, in the style of an oil painting, and save it in my Pictures folder.'; Kind = 'image'; Folder = $pictures }
    video   = @{ Prompt = 'Make a short video, about 3 seconds, of a cat playing with a ball of yarn on a wooden floor.'; Kind = 'video'; Min = 2 }
    sound   = @{ Prompt = 'Make a 10-second sound effect of heavy rain on a tin roof with distant thunder, and save it in my Music folder.'; Kind = 'audio'; Min = 8; Max = 13; Folder = $music }
    music   = @{ Prompt = 'Make a 30-second upbeat synthwave instrumental track with no vocals.'; Kind = 'audio'; Min = 25; Max = 40 }
    speech  = @{ Prompt = 'Make a recording of a calm female voice saying: "Good morning! Your meeting with the design team starts at ten."'; Kind = 'audio'; Min = 2; Max = 15 }
    model3d = @{ Prompt = 'Make a 3D model of a wooden treasure chest that I could use in a game.'; Kind = '3d' }
    combo   = @{ Prompt = 'Make a picture of a small red dragon sitting on a mossy rock, then turn it into a 3-second video of the dragon breathing a small puff of fire, and give the video a fitting sound.'; Kind = 'video'; Min = 2; Sound = $true }
}

if ($StopComfy) {
    $line = netstat -ano -p TCP | Select-String 'LISTENING' | Where-Object { $_ -match ':8188\s' } | Select-Object -First 1
    if ($line) { Stop-Process -Id ([int](($line.ToString() -split '\s+')[-1])) -Force; Start-Sleep 3 }
}

$kinds = @{ '.png' = 'image'; '.jpg' = 'image'; '.jpeg' = 'image'; '.webp' = 'image'; '.mp4' = 'video'; '.webm' = 'video'; '.mov' = 'video';
            '.mp3' = 'audio'; '.flac' = 'audio'; '.wav' = 'audio'; '.ogg' = 'audio'; '.opus' = 'audio'; '.glb' = '3d'; '.obj' = '3d'; '.ply' = '3d'; '.stl' = '3d' }
$rows = @()
foreach ($name in $Tasks) {
    $t = $all[$name]
    if (-not $t) { Write-Warning "No task '$name'"; continue }
    Write-Host "== $name: $($t.Prompt)"
    $log = Join-Path $out "$name.log"
    $start = Get-Date
    # Waits for BlackBox only: Start-Process -Wait would also wait for a ComfyUI it starts.
    $p = Start-Process -FilePath $cli -ArgumentList @('--yes', '--save-shots', "`"$(Join-Path $out "$name-shots")`"", "`"$($t.Prompt.Replace('"', '\"'))`"") `
        -RedirectStandardOutput $log -RedirectStandardError (Join-Path $out "$name.err") -WindowStyle Hidden -PassThru
    if (-not $p.WaitForExit($MinutesPerTask * 60 * 1000)) { Stop-Process -Id $p.Id -Force }
    $secs = [int]((Get-Date) - $start).TotalSeconds
    $text = Get-Content $log -Raw -Encoding UTF8
    $files = @([regex]::Matches($text, '(?m)^\[files\] .*$') | ForEach-Object { $_.Value.Substring(8) -split ' \| ' } |
        Where-Object { (Test-Path $_) -and (Get-Item $_).LastWriteTime -ge $start.AddSeconds(-5) } | Select-Object -Unique)
    # The comfyui tool's line about each file: "C:\...\x.mp4: video 640x640, 81 frames at 16 fps (5.1 s), no sound, 1.4 MB".
    $checks = @()
    $match = $files | Where-Object { $kinds[[IO.Path]::GetExtension($_).ToLowerInvariant()] -eq $t.Kind }
    $checks += if ($match) { "made a $($t.Kind) file" } else { "FAIL: no $($t.Kind) file" }
    $best = $match | Select-Object -Last 1
    if ($best) {
        $about = ([regex]::Matches($text, [regex]::Escape($best) + ': ([^\r\n]+)') | Select-Object -Last 1).Groups[1].Value
        $seconds = if ($about -match '\(([\d.]+) s\)') { [double]$Matches[1] } elseif ($about -match 'sound ([\d.]+) s') { [double]$Matches[1] } else { $null }
        if ($t.Min) { $checks += if ($null -ne $seconds -and $seconds -ge $t.Min -and (-not $t.Max -or $seconds -le $t.Max)) { "$seconds s" } else { "FAIL: $seconds s, not $($t.Min)-$($t.Max) s" } }
        if ($t.Sound) { $checks += if ($about -match 'with sound' -and $about -notmatch 'silent throughout') { 'with sound' } else { 'FAIL: no sound in the video' } }
        if ($t.Kind -eq 'audio' -and $about -match 'silent throughout') { $checks += 'FAIL: silent' }
        if ($t.Folder) {
            $inFolder = $files | Where-Object { $_.StartsWith($t.Folder, [StringComparison]::OrdinalIgnoreCase) }
            $checks += if ($inFolder) { "saved in $($t.Folder)" } else { "FAIL: not saved in $($t.Folder)" }
        }
    }
    $done = if ($text -match '\[done\] (\w+) in (\d+)s, (\d+) model calls, (\d+) tool calls, (\d+) tool errors') { $Matches } else { $null }
    $ok = -not ($checks -match '^FAIL')
    $rows += [pscustomobject]@{
        Task = $name; Passed = $ok; Seconds = $secs
        ModelCalls = if ($done) { $done[3] } else { '?' }; ToolErrors = if ($done) { $done[5] } else { '?' }
        Checks = ($checks -join '; '); File = $best
    }
    Write-Host ("   {0} in {1}s: {2}" -f $(if ($ok) { 'passed' } else { 'FAILED' }), $secs, ($checks -join '; '))
}

$md = @('| Task | Passed | Time | Model calls | Tool errors | Checks | File |', '| --- | --- | --- | --- | --- | --- | --- |')
$md += $rows | ForEach-Object { "| $($_.Task) | $($_.Passed) | $([int]($_.Seconds / 60)):$('{0:00}' -f ($_.Seconds % 60)) | $($_.ModelCalls) | $($_.ToolErrors) | $($_.Checks) | $($_.File) |" }
Set-Content -Encoding utf8 (Join-Path $out 'results.md') $md
$md | ForEach-Object { Write-Host $_ }
Write-Host "Logs: $out"
