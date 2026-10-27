# ComfyUI benchmark: Sloperator makes pictures, video, sound, music, speech and a 3D model with the
# ComfyUI on this PC, each from one plain request, and the results are checked from the run's log
# (the files the comfyui tool reported, their kind, length and sound). Uses the model in
# Data/settings.json under the root. Runs land in .dev/comfy/<time>/, one log per task and a results.md.
#
#   ./scripts/comfy-test.ps1                      # all tasks
#   ./scripts/comfy-test.ps1 -Tasks picture,sound  # some of them
#   ./scripts/comfy-test.ps1 -StopComfy           # stop ComfyUI first, so the first task has to start it
#   ./scripts/comfy-test.ps1 -Root .dev\laptop -Tasks img2img,named,picture   # a PC without a graphics card: from no ComfyUI at all
param(
    [string[]]$Tasks = @('picture', 'img2img', 'named', 'video', 'sound', 'music', 'speech', 'model3d', 'combo'),
    [switch]$StopComfy,
    [int]$MinutesPerTask = 45,
    # A portable root of its own (its Data/ holds settings.json); Sloperator's .dev/ by default.
    [string]$Root
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
# The portable SDK in .dev\dotnet when none is installed.
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -and (Test-Path "$repo\.dev\dotnet\dotnet.exe")) {
    $env:DOTNET_ROOT = "$repo\.dev\dotnet"
    $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
}
$cli = Join-Path $repo 'src\Sloperator.Cli\bin\Debug\net10.0-windows\sloperator-cli.exe'
dotnet build (Join-Path $repo 'src\Sloperator.Cli\Sloperator.Cli.csproj') -v q -nologo | Out-Null
if (-not (Test-Path $cli)) { throw "The CLI didn't build: $cli" }
$out = Join-Path $repo (".dev\comfy\" + (Get-Date -Format 'yyyyMMdd-HHmm'))
New-Item -ItemType Directory -Force $out | Out-Null

$pictures = [Environment]::GetFolderPath('MyPictures')
$music = [Environment]::GetFolderPath('MyMusic')
# A photo every Windows 10 has: a beach with a rock arch and a runner.
$photo = Join-Path $env:WINDIR 'Web\Wallpaper\Theme1\img1.jpg'
# What each request should give: a kind of file, how long (seconds) for video and sound, sound in a video, a folder;
# Attach: a file sent with the request; Like: the result keeps that picture's layout (image to image);
# Model: the run downloaded (or found already there) a model file whose name matches.
$all = [ordered]@{
    picture = @{ Prompt = 'Make a picture of a lighthouse on a cliff at sunset, in the style of an oil painting, and save it in my Pictures folder.'; Kind = 'image'; Folder = $pictures }
    img2img = @{ Prompt = 'Turn this photo into a watercolor painting.'; Kind = 'image'; Attach = $photo; Like = $photo }
    named   = @{ Prompt = 'Make a picture of a cozy wooden cabin in a snowy forest at dusk with the DreamShaper 8 model.'; Kind = 'image'; Model = 'dreamshaper' }
    video   = @{ Prompt = 'Make a short video, about 3 seconds, of a cat playing with a ball of yarn on a wooden floor.'; Kind = 'video'; Min = 2 }
    sound   = @{ Prompt = 'Make a 10-second sound effect of heavy rain on a tin roof with distant thunder, and save it in my Music folder.'; Kind = 'audio'; Min = 8; Max = 13; Folder = $music }
    music   = @{ Prompt = 'Make a 30-second upbeat synthwave instrumental track with no vocals.'; Kind = 'audio'; Min = 25; Max = 40 }
    speech  = @{ Prompt = 'Make a recording of a calm female voice saying: "Good morning! Your meeting with the design team starts at ten."'; Kind = 'audio'; Min = 2; Max = 15 }
    model3d = @{ Prompt = 'Make a 3D model of a wooden treasure chest that I could use in a game.'; Kind = '3d' }
    combo   = @{ Prompt = 'Make a picture of a small red dragon sitting on a mossy rock, then turn it into a 3-second video of the dragon breathing a small puff of fire, and give the video a fitting sound.'; Kind = 'video'; Min = 2; Sound = $true }
}

Add-Type -AssemblyName System.Drawing
# How alike two pictures' layouts are: both shrunk to 32x32 in grey, then the correlation of their
# pixels (1 = the same light and dark in the same places, about 0 = unrelated pictures).
function Get-Likeness([string]$a, [string]$b) {
    $grey = foreach ($path in $a, $b) {
        $src = [System.Drawing.Image]::FromFile($path)
        $small = New-Object System.Drawing.Bitmap 32, 32
        $g = [System.Drawing.Graphics]::FromImage($small)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.DrawImage($src, 0, 0, 32, 32); $g.Dispose(); $src.Dispose()
        , @(for ($y = 0; $y -lt 32; $y++) { for ($x = 0; $x -lt 32; $x++) { $c = $small.GetPixel($x, $y); 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B } })
        $small.Dispose()
    }
    $ma = ($grey[0] | Measure-Object -Average).Average; $mb = ($grey[1] | Measure-Object -Average).Average
    $num = 0.0; $da = 0.0; $db = 0.0
    for ($i = 0; $i -lt 1024; $i++) { $x = $grey[0][$i] - $ma; $y = $grey[1][$i] - $mb; $num += $x * $y; $da += $x * $x; $db += $y * $y }
    if ($da -eq 0 -or $db -eq 0) { return 0 }
    return [math]::Round($num / [math]::Sqrt($da * $db), 2)
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
    Write-Host "== ${name}: $($t.Prompt)"
    $log = Join-Path $out "$name.log"
    $start = Get-Date
    # Waits for Sloperator only: Start-Process -Wait would also wait for a ComfyUI it starts.
    $cliArgs = @('--yes', '--save-shots', "`"$(Join-Path $out "$name-shots")`"")
    if ($Root) { $cliArgs += @('--root', "`"$((Resolve-Path $Root).Path)`"") }
    if ($t.Attach) { $cliArgs += @('--attach', "`"$($t.Attach)`"") }
    $p = Start-Process -FilePath $cli -ArgumentList ($cliArgs + "`"$($t.Prompt.Replace('"', '\"'))`"") `
        -RedirectStandardOutput $log -RedirectStandardError (Join-Path $out "$name.err") -WindowStyle Hidden -PassThru
    if (-not $p.WaitForExit($MinutesPerTask * 60 * 1000)) { Stop-Process -Id $p.Id -Force }
    $secs = [int]((Get-Date) - $start).TotalSeconds
    $text = Get-Content $log -Raw -Encoding UTF8
    $files = @([regex]::Matches($text, '(?m)^\[files\] .*$') | ForEach-Object { $_.Value.Substring(8).TrimEnd() -split ' \| ' } |
        Where-Object { (Test-Path -LiteralPath $_) -and (Get-Item -LiteralPath $_).LastWriteTime -ge $start.AddSeconds(-5) } | Select-Object -Unique)
    # Put in the asked folder another way, or renamed there after save_to (Copy-Item, Rename-Item in run_shell):
    # the file the user finds counts too.
    if ($t.Folder) {
        $files = @($files) + @(Get-ChildItem $t.Folder -File -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -ge $start.AddSeconds(-5) -and $kinds[$_.Extension.ToLowerInvariant()] -eq $t.Kind } | ForEach-Object FullName) | Select-Object -Unique
    }
    # The comfyui tool's line about each file: "C:\...\x.mp4: video 640x640, 81 frames at 16 fps (5.1 s), no sound, 1.4 MB".
    $checks = @()
    $match = $files | Where-Object { $kinds[[IO.Path]::GetExtension($_).ToLowerInvariant()] -eq $t.Kind }
    $checks += if ($match) { "made a $($t.Kind) file" } else { "FAIL: no $($t.Kind) file" }
    $best = $match | Select-Object -Last 1
    if ($best) {
        $about = ([regex]::Matches($text, [regex]::Escape($best) + ': ([^\r\n]+)') | Select-Object -Last 1).Groups[1].Value
        $seconds = if ($about -match '\(([\d.]+) s\)') { [double]$Matches[1] } elseif ($about -match 'sound ([\d.]+) s') { [double]$Matches[1] } else { $null }
        # A file the tool didn't describe (renamed or copied since): its length from ffprobe.
        if ($null -eq $seconds -and (Get-Command ffprobe -ErrorAction SilentlyContinue)) {
            $probed = & ffprobe -v error -show_entries format=duration -of csv=p=0 $best 2>$null
            if ($probed -as [double]) { $seconds = [math]::Round([double]$probed, 1) }
        }
        if ($t.Min) { $checks += if ($null -ne $seconds -and $seconds -ge $t.Min -and (-not $t.Max -or $seconds -le $t.Max)) { "$seconds s" } else { "FAIL: $seconds s, not $($t.Min)-$($t.Max) s" } }
        if ($t.Sound) { $checks += if ($about -match 'with sound' -and $about -notmatch 'silent throughout') { 'with sound' } else { 'FAIL: no sound in the video' } }
        if ($t.Kind -eq 'audio' -and $about -match 'silent throughout') { $checks += 'FAIL: silent' }
        if ($t.Folder) {
            $inFolder = $files | Where-Object { $_.StartsWith($t.Folder, [StringComparison]::OrdinalIgnoreCase) }
            $checks += if ($inFolder) { "saved in $($t.Folder)" } else { "FAIL: not saved in $($t.Folder)" }
        }
        if ($t.Like) {
            # A picture made from the photo keeps its layout; one made from words alone doesn't (about 0).
            $likeness = Get-Likeness $t.Like $best
            $checks += if ($likeness -ge 0.4) { "keeps the photo's layout ($likeness)" } else { "FAIL: doesn't keep the photo's layout ($likeness)" }
        }
    }
    if ($t.Model) {
        $got = [regex]::Matches($text, '(?i)(Downloaded|is there already)[^\r\n]*' + [regex]::Escape($t.Model)).Count -gt 0 -or
               [regex]::Matches($text, '(?i)ckpt_name[^\r\n]{0,10}' + [regex]::Escape($t.Model)).Count -gt 0
        $checks += if ($got) { "used a $($t.Model) model" } else { "FAIL: no $($t.Model) model downloaded or used" }
    }
    if ($text -match '\[result\][^\r\n]*Installed ComfyUI''s portable build') { $checks += 'installed ComfyUI' }
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
$md += $rows | ForEach-Object { "| $($_.Task) | $($_.Passed) | $([math]::Floor($_.Seconds / 60)):$('{0:00}' -f ($_.Seconds % 60)) | $($_.ModelCalls) | $($_.ToolErrors) | $($_.Checks) | $($_.File) |" }
Set-Content -Encoding utf8 (Join-Path $out 'results.md') $md
$md | ForEach-Object { Write-Host $_ }
Write-Host "Logs: $out"
