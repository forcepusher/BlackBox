<#
.SYNOPSIS
  Measures what the folder index changes: each task of scripts\index-bench\tasks.json is run through
  blackbox-cli on a fresh copy of its project, with index_folder and with --no-index (the same build,
  without the tool, its prompt line and its note), and scored.

  Every run gets a clean copy of the project (a local git clone, reset between runs) and a portable
  root of its own, so no index is carried from one run to the next. For each run the folder
  <Work>\runs\<Label>\<task>-<with|without>-<n> keeps the log, the final answer, the diff and
  result.json; <Work>\runs\<Label>\results.csv has one line per run. Edit tasks are checked here
  (patterns, files, a build); the answers to questions are graded afterwards against each task's
  rubric, without knowing which run had the index.

.EXAMPLE
  ./scripts/index-bench.ps1 -Settings .dev\index-bench\qwen\Data\settings.json -Label qwen -Runs 2
  ./scripts/index-bench.ps1 -Settings .dev\index-bench\claude\Data\settings.json -Label claude -Parallel 3
  ./scripts/index-bench.ps1 -Settings ... -Label try -Tasks repeat -Conditions with -Runs 1
#>
param(
  [Parameter(Mandatory = $true)][string]$Settings,  # a Data\settings.json to start each run's root with (server, model, key)
  [string]$Label = (Get-Date -Format "yyyyMMdd-HHmmss"),
  [string[]]$Tasks = @(),
  [string[]]$Conditions = @("with", "without"),
  [int]$Runs = 2,
  [int]$Parallel = 1,                               # runs at once; 1 for a local server, so times compare
  [int]$TimeoutMinutes = 25,
  [string]$Cli = "",                                # a blackbox-cli.exe built before, to compare builds; else the repository's, built now
  [string]$Work = (Join-Path $env:USERPROFILE "IndexBench")  # outside the repository: the model shouldn't find the real checkout
)
# Not Stop: in Windows PowerShell a native program's stderr (git's progress) would end the script.
$ErrorActionPreference = "Continue"
$repo = Split-Path $PSScriptRoot
$dotnetDir = Join-Path $repo ".dev\dotnet"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -and (Test-Path "$dotnetDir\dotnet.exe")) {
  # The runs inherit it, so the model can build the copy too.
  $env:DOTNET_ROOT = $dotnetDir
  $env:PATH = "$dotnetDir;$env:PATH"
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"; $env:DOTNET_NOLOGO = "1"; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
if ($Cli) {
  $cli = (Resolve-Path $Cli).Path
} else {
  $cli = Join-Path $repo "src\BlackBox.Cli\bin\Debug\net10.0-windows\blackbox-cli.exe"
  dotnet build (Join-Path $repo "src\BlackBox.Cli") -v q -nologo | Out-Null
  if ($LASTEXITCODE -ne 0) {
    if (-not (Test-Path $cli)) { throw "blackbox-cli didn't build" }
    Write-Host "blackbox-cli didn't build (another benchmark's runs hold it?): using the one there"
  }
}
$Settings = (Resolve-Path $Settings).Path
# "a,b" comes as one string from powershell -File.
$Tasks = @($Tasks | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Conditions = @($Conditions | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

$spec = Get-Content (Join-Path $PSScriptRoot "index-bench\tasks.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$taskList = @($spec.tasks | Where-Object { $Tasks.Count -eq 0 -or $Tasks -contains $_.id })
if ($taskList.Count -eq 0) { throw "No task matches $($Tasks -join ', ')" }
$out = Join-Path $Work "runs\$Label"
New-Item -ItemType Directory -Force $out | Out-Null

function Quote-Arg([string]$s) {
  # As the C runtime reads a command line: backslashes before a quote doubled, the quote escaped.
  '"' + ($s -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}
# git.exe: a function named Git would be what "git" calls in this script, itself.
function Invoke-Git([string]$dir) { & git.exe -C $dir @args 2>&1 | Out-Null; if ($LASTEXITCODE -ne 0) { throw "git $args failed in $dir" } }

# ---- copies of the projects, one per run at a time
function Get-Base($projectId) {
  # The project at its commit, as a repository of that one commit without the paths in "exclude"
  # (a key or a password the models shouldn't read, nor find in the history); or else the project itself.
  $p = $spec.projects.$projectId
  $source = if ([IO.Path]::IsPathRooted($p.source)) { $p.source } else { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $p.source)) }
  if ($p.history -ne $false) { return @{ Source = $source; Commit = $p.commit } }
  $base = Join-Path $Work "base\$projectId\$($p.folder)"
  if (-not (Test-Path (Join-Path $base ".git\snapshot-done"))) {
    if (Test-Path $base) { Remove-Item -Recurse -Force $base }
    Write-Host "$(Get-Date -Format HH:mm:ss) making a copy of $source at $($p.commit) without its history"
    & git.exe clone -q --no-checkout $source $base 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Couldn't clone $source" }
    Invoke-Git $base checkout -q $p.commit
    Remove-Item -Recurse -Force (Join-Path $base ".git")
    foreach ($x in @($p.exclude)) { if ($x) { Remove-Item -Recurse -Force (Join-Path $base $x) -ErrorAction SilentlyContinue } }
    Invoke-Git $base init -q -b develop
    Invoke-Git $base add -A
    Invoke-Git $base -c user.name=bench -c user.email=bench@localhost commit -q -m "Snapshot"
    Set-Content (Join-Path $base ".git\snapshot-done") $p.commit
  }
  # A clone's develop is the snapshot's one commit.
  @{ Source = $base; Commit = "HEAD" }
}
function Get-Slot($projectId, [int]$n) {
  $p = $spec.projects.$projectId
  $b = Get-Base $projectId
  # Copies of each label's own: two benchmarks can run at once.
  $dir = Join-Path $Work "slots\$Label\$projectId-$n\$($p.folder)"
  if (-not (Test-Path (Join-Path $dir ".git"))) {
    New-Item -ItemType Directory -Force (Split-Path $dir) | Out-Null
    & git.exe clone -q --no-checkout $b.Source $dir 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Couldn't clone $($b.Source)" }
    # Nothing in the copy leads back to the real checkout.
    Invoke-Git $dir remote remove origin
  }
  Invoke-Git $dir reset -q --hard $b.Commit
  Invoke-Git $dir clean -qfdx
  # A plain branch name, as a working copy would have, not a detached head.
  Invoke-Git $dir checkout -q -B develop
  $dir
}

# ---- one run
function Start-Run($task, [string]$cond, [int]$n, [int]$slot) {
  $name = "$($task.id)-$cond-$n"
  $dir = Join-Path $out $name
  if (Test-Path (Join-Path $dir "result.json")) { return $null }  # done in an earlier call with this label
  if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
  $root = Join-Path $dir "root"
  New-Item -ItemType Directory -Force (Join-Path $root "Data") | Out-Null
  Copy-Item $Settings (Join-Path $root "Data\settings.json")
  $project = Get-Slot $task.project $slot
  $prompt = $task.prompt.Replace("{path}", $project)
  $argLine = @("--root", (Quote-Arg $root), "--yes")
  if ($cond -eq "without") { $argLine += "--no-index" }
  $argLine += (Quote-Arg $prompt)
  $p = Start-Process -FilePath $cli -ArgumentList ($argLine -join " ") -WorkingDirectory $env:USERPROFILE -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $dir "log.txt") -RedirectStandardError (Join-Path $dir "err.txt")
  Write-Host "$(Get-Date -Format HH:mm:ss) started $name (slot $slot)"
  [pscustomobject]@{ Name = $name; Task = $task; Cond = $cond; N = $n; Slot = $slot; Dir = $dir; Project = $project; Process = $p; Start = Get-Date }
}

function Read-Log([string]$path) {
  $lines = [IO.File]::ReadAllLines($path, [Text.Encoding]::UTF8)
  $r = [ordered]@{ seconds = $null; modelCalls = 0; toolCalls = 0; toolErrors = 0; stop = ""; promptTokens = 0; maxPrompt = 0; replyTokens = 0
    index = 0; indexSearch = 0; read = 0; list = 0; shell = 0; shellSearch = 0; edits = 0; other = 0; calls = @() }
  foreach ($l in $lines) {
    if ($l -match '^\[done\] (\w+) in (\d+)s, (\d+) model calls, (\d+) tool calls, (\d+) tool errors') {
      $r.stop = $Matches[1]; $r.seconds = [int]$Matches[2]; $r.modelCalls = [int]$Matches[3]; $r.toolCalls = [int]$Matches[4]; $r.toolErrors = [int]$Matches[5]
    } elseif ($l -match '^\[time\] .*\(([\d.]+)k prompt tokens, (\d+) reply tokens\)') {
      $pt = [int]([double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture) * 1000)
      $r.promptTokens += $pt; $r.replyTokens += [int]$Matches[2]
      if ($pt -gt $r.maxPrompt) { $r.maxPrompt = $pt }
    } elseif ($l -match '^\[call\] \S+ (\S+) (.*)$') {
      $tool = $Matches[1]; $argsJson = $Matches[2]
      $r.calls += "$tool $argsJson"
      switch -regex ($tool) {
        '^index_folder$' { if ($argsJson -match '"query"\s*:\s*"[^"]') { $r.indexSearch++ } else { $r.index++ } }
        '^read_file$' { $r.read++ }
        '^list_directory$' { $r.list++ }
        '^(edit_file|write_file)$' { $r.edits++ }
        '^run_shell$' {
          $r.shell++
          if ($argsJson -match '(?i)select-string|findstr|\bsls\b|\bgrep\b|\brg\b|git grep|get-childitem[^|]*-r|\bgci\b[^|]*-r|\bls\b[^|]*-r|\bdir\b[^|]*(/s|-r)') { $r.shellSearch++ }
        }
        default { $r.other++ }
      }
    }
  }
  # The answer: what the last step said (it has no tool calls).
  $lastStep = -1
  for ($i = $lines.Count - 1; $i -ge 0; $i--) { if ($lines[$i].StartsWith([string][char]0x2500 + [char]0x2500 + " step")) { $lastStep = $i; break } }
  $answer = New-Object Text.StringBuilder
  if ($lastStep -ge 0) {
    $inSay = $false
    for ($i = $lastStep + 1; $i -lt $lines.Count; $i++) {
      $l = $lines[$i]
      if ($l -match '^\[say\] \d\d:\d\d:\d\d ?(.*)$') { $inSay = $true; [void]$answer.AppendLine($Matches[1]); continue }
      if ($l -match '^\[(think|time|call|tool|result|notice|turn \d+|done)\]') { $inSay = $false; continue }
      if ($inSay) { [void]$answer.AppendLine($l) }
    }
  }
  $r.answer = $answer.ToString().Trim()
  $r
}

function Test-Build([string]$project, [string]$target, [string]$dir) {
  $log = Join-Path $dir "build.txt"
  & dotnet build (Join-Path $project $target) -v q -nologo -nodeReuse:false 2>&1 | Out-File $log -Encoding utf8
  $ok = $LASTEXITCODE -eq 0
  # Leave the copy as the model left it: the diff is taken before, and the next run resets it.
  $ok
}

function Grade-Edits($run) {
  $checks = [ordered]@{}
  $allRequired = $true
  foreach ($c in @($run.Task.checks)) {
    $ok = $false
    switch ($c.kind) {
      "match" { $f = Join-Path $run.Project $c.path; $ok = (Test-Path $f) -and ([IO.File]::ReadAllText($f) -match $c.pattern) }
      "nomatch" {
        $hits = @(Get-ChildItem $run.Project -Recurse -Filter $c.glob -File | Where-Object { $_.FullName -notmatch '\\(bin|obj|\.git)\\' } |
          Select-String -Pattern $c.pattern)
        $ok = $hits.Count -eq 0
        if (-not $ok) { $checks["$($c.name) (left)"] = $hits.Count }
      }
      "exists" { $ok = Test-Path (Join-Path $run.Project $c.path) }
      "missing" { $ok = -not (Test-Path (Join-Path $run.Project $c.path)) }
      "build" { $ok = Test-Build $run.Project $c.target $run.Dir }
    }
    $checks[$c.name] = $ok
    if ($c.required -and -not $ok) { $allRequired = $false }
  }
  $checks["passed"] = $allRequired
  $checks
}

function Finish-Run($run, [bool]$timedOut) {
  $r = Read-Log (Join-Path $run.Dir "log.txt")
  if ($timedOut) { $r.stop = "TimedOut"; $r.seconds = [int]((Get-Date) - $run.Start).TotalSeconds }
  [IO.File]::WriteAllText((Join-Path $run.Dir "answer.md"), $r.answer, (New-Object Text.UTF8Encoding $false))
  # What the run changed, files added included, before any build of ours.
  & git.exe -C $run.Project add -A 2>&1 | Out-Null
  $changed = @(& git.exe -C $run.Project diff --cached --name-only 2>$null)
  & git.exe -C $run.Project diff --cached --stat=200 2>&1 | Out-File (Join-Path $run.Dir "diffstat.txt") -Encoding utf8
  & git.exe -C $run.Project diff --cached 2>&1 | Out-File (Join-Path $run.Dir "diff.patch") -Encoding utf8
  & git.exe -C $run.Project reset -q 2>&1 | Out-Null
  $grade = $null
  if ($run.Task.checks) { $grade = Grade-Edits $run }
  $found = $null
  if ($run.Task.expect) { $found = @($run.Task.expect | Where-Object { $r.answer -match "\b$_\b" }).Count }
  $extra = @($changed | Where-Object { $run.Task.expectedFiles -notcontains $_ })
  $result = [ordered]@{
    task = $run.Task.id; condition = $run.Cond; run = $run.N; stop = $r.stop; seconds = $r.seconds; modelCalls = $r.modelCalls
    toolCalls = $r.toolCalls; toolErrors = $r.toolErrors; index = $r.index; indexSearch = $r.indexSearch; read = $r.read; list = $r.list
    shell = $r.shell; shellSearch = $r.shellSearch; edits = $r.edits; other = $r.other
    promptTokens = $r.promptTokens; maxPrompt = $r.maxPrompt; replyTokens = $r.replyTokens
    changedFiles = $changed.Count; unexpectedFiles = ($extra -join " "); expectFound = $found; expectOf = @($run.Task.expect).Count
    checks = $grade; answerChars = $r.answer.Length; calls = $r.calls
  }
  $result | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $run.Dir "result.json") -Encoding UTF8
  $line = "$(Get-Date -Format HH:mm:ss) done $($run.Name): $($r.stop) in $($r.seconds)s, $($r.toolCalls) calls (index $($r.index)+$($r.indexSearch), read $($r.read), list $($r.list), shell $($r.shell)/$($r.shellSearch) search), $([int]($r.promptTokens/1000))k prompt"
  if ($grade) { $line += ", passed=$($grade.passed)" }
  if ($null -ne $found) { $line += ", found $found of $(@($run.Task.expect).Count)" }
  Write-Host $line
}

# ---- the order: every task and condition once per round, the conditions alternating, so a
# server that warms up or gets busy weighs on both alike
$queue = @()
for ($n = 1; $n -le $Runs; $n++) {
  $i = 0
  foreach ($t in $taskList) {
    $conds = @($Conditions)
    if (($n + $i) % 2 -eq 0) { [array]::Reverse($conds) }
    foreach ($cond in $conds) { $queue += , [pscustomobject]@{ Task = $t; Cond = $cond; N = $n } }
    $i++
  }
}
$running = @()
$free = @(1..$Parallel)
while ($queue.Count -gt 0 -or $running.Count -gt 0) {
  while ($queue.Count -gt 0 -and $free.Count -gt 0) {
    $item = $queue[0]; $queue = @($queue | Select-Object -Skip 1)
    $slot = $free[0]; $free = @($free | Select-Object -Skip 1)
    $started = Start-Run $item.Task $item.Cond $item.N $slot
    if ($started) { $running += , $started } else { $free += $slot }
  }
  Start-Sleep -Seconds 2
  $still = @()
  foreach ($r in $running) {
    $timedOut = -not $r.Process.HasExited -and ((Get-Date) - $r.Start).TotalMinutes -gt $TimeoutMinutes
    if ($timedOut) { & taskkill /T /F /PID $r.Process.Id 2>&1 | Out-Null; Start-Sleep -Seconds 1 }
    if ($r.Process.HasExited -or $timedOut) {
      try { Finish-Run $r $timedOut } catch { Write-Host "$(Get-Date -Format HH:mm:ss) couldn't score $($r.Name): $_" }
      $free += $r.Slot
    } else { $still += , $r }
  }
  $running = $still
}

# ---- one line per run
$rows = Get-ChildItem $out -Directory | ForEach-Object { Join-Path $_.FullName "result.json" } | Where-Object { Test-Path $_ } | ForEach-Object {
  $j = Get-Content $_ -Raw -Encoding UTF8 | ConvertFrom-Json
  [pscustomobject]@{ task = $j.task; condition = $j.condition; run = $j.run; stop = $j.stop; seconds = $j.seconds; modelCalls = $j.modelCalls
    toolCalls = $j.toolCalls; index = $j.index; indexSearch = $j.indexSearch; read = $j.read; list = $j.list; shell = $j.shell; shellSearch = $j.shellSearch
    edits = $j.edits; promptTokens = $j.promptTokens; maxPrompt = $j.maxPrompt; replyTokens = $j.replyTokens
    passed = $(if ($j.checks) { $j.checks.passed } else { "" }); expectFound = $j.expectFound; unexpectedFiles = $j.unexpectedFiles }
}
$rows | Sort-Object task, condition, run | Export-Csv (Join-Path $out "results.csv") -NoTypeInformation -Encoding UTF8
$rows | Group-Object task, condition | ForEach-Object {
  $g = $_.Group
  "{0,-24} runs {1}  {2,6:0}s  {3,5:0.0} calls  {4,7:0}k prompt  passed {5}" -f $_.Name, $g.Count, ($g | Measure-Object seconds -Average).Average,
    ($g | Measure-Object toolCalls -Average).Average, (($g | Measure-Object promptTokens -Average).Average / 1000), (@($g | Where-Object passed -eq $true).Count)
}
Write-Host "Results: $out"
