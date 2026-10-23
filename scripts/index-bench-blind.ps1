<#
.SYNOPSIS
  Gathers the answers of index-bench runs for grading without knowing which run had the index:
  each task's answers go to <Work>\judge\<name>\<task>\ under shuffled names (A01.md, A02.md…), with
  the task's question and rubric in rubric.md. key.csv, beside them, says which run each one is;
  the grader shouldn't open it. After grading, -Scores reads <task>\scores.json (written by the
  grader: [{"answer":"A01","points":[1,0.5,…],"wrong":0,"note":"…"}]) and adds them to the runs'
  results.

.EXAMPLE
  ./scripts/index-bench-blind.ps1 -Labels qwen,claude -Name round1
  ./scripts/index-bench-blind.ps1 -Name round1 -Scores
#>
param(
  [string[]]$Labels = @(),
  [string[]]$Tasks = @(),
  [string]$Name = "round1",
  [switch]$Scores,
  [string]$Work = (Join-Path $env:USERPROFILE "IndexBench")
)
$ErrorActionPreference = "Stop"
$Labels = @($Labels | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Tasks = @($Tasks | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$spec = Get-Content (Join-Path $PSScriptRoot "index-bench\tasks.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$judge = Join-Path $Work "judge\$Name"
$keyFile = Join-Path $judge "key.csv"

if ($Scores) {
  $key = Import-Csv $keyFile
  $rows = foreach ($k in $key) {
    $scoreFile = Join-Path $judge "$($k.task)\scores.json"
    if (-not (Test-Path $scoreFile)) { continue }
    # Into a variable first: Windows PowerShell passes a parsed JSON array down a pipe as one object.
    $list = Get-Content $scoreFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $s = $list | Where-Object { $_.answer -eq $k.answer }
    if (-not $s) { continue }
    $points = @($s.points | ForEach-Object { [double]$_ })
    [pscustomobject]@{ label = $k.label; task = $k.task; condition = $k.condition; run = $k.run; answer = $k.answer
      score = ($points | Measure-Object -Sum).Sum; of = $points.Count; wrong = $s.wrong; note = $s.note }
  }
  $rows | Sort-Object label, task, condition, run | Export-Csv (Join-Path $judge "scores.csv") -NoTypeInformation -Encoding UTF8
  $rows | Group-Object label, task, condition | ForEach-Object {
    "{0,-34} {1} answers, {2:0.0} of {3} points, {4:0.0} wrong claims" -f $_.Name, $_.Count, ($_.Group | Measure-Object score -Average).Average,
      $_.Group[0].of, ($_.Group | Measure-Object wrong -Average).Average
  }
  return
}

if (Test-Path $judge) { throw "$judge is there already: pick another -Name" }
$key = @()
$random = New-Object Random
foreach ($task in @($spec.tasks | Where-Object { $_.rubric -and ($Tasks.Count -eq 0 -or $Tasks -contains $_.id) })) {
  $runs = foreach ($label in $Labels) {
    Get-ChildItem (Join-Path $Work "runs\$label") -Directory -Filter "$($task.id)-*" | Where-Object { Test-Path (Join-Path $_.FullName "answer.md") } |
      ForEach-Object { [pscustomobject]@{ Label = $label; Dir = $_.FullName; Name = $_.Name } }
  }
  if (-not $runs) { continue }
  $dir = Join-Path $judge $task.id
  New-Item -ItemType Directory -Force $dir | Out-Null
  $i = 0
  foreach ($r in @($runs | Sort-Object { $random.Next() })) {
    $answer = "A{0:00}" -f (++$i)
    Copy-Item (Join-Path $r.Dir "answer.md") (Join-Path $dir "$answer.md")
    $parts = $r.Name -split '-'
    $key += [pscustomobject]@{ answer = $answer; task = $task.id; label = $r.Label; condition = $parts[-2]; run = $parts[-1]; dir = $r.Dir }
  }
  $rubric = "# $($task.id)`n`nThe question (the project's path differs between answers):`n`n> $($task.prompt.Replace('{path}', '<project>'))`n`nRubric, one point each:`n`n"
  $n = 0
  foreach ($item in $task.rubric) { $rubric += "$(++$n). $item`n" }
  [IO.File]::WriteAllText((Join-Path $dir "rubric.md"), $rubric, (New-Object Text.UTF8Encoding $false))
}
$key | Export-Csv $keyFile -NoTypeInformation -Encoding UTF8
Write-Host "$($key.Count) answers in $judge"
