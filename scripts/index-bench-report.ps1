<#
.SYNOPSIS
  Tables of index-bench runs for a report, in Markdown: per task the average time, tool calls,
  prompt tokens and result of the runs without the index and of each labelled set with it, and the
  grades from index-bench-blind.ps1 when there are some.

.EXAMPLE
  ./scripts/index-bench-report.ps1 -Model "Claude Opus 5.5" -Without claude -With claude,claude-v2 -Judge round1
#>
param(
  [string]$Model = "",
  [Parameter(Mandatory = $true)][string]$Without,  # the label whose "without" runs are the baseline
  [Parameter(Mandatory = $true)][string[]]$With,    # labels whose "with" runs are compared to it
  [string]$Judge = "",
  [string[]]$Tasks = @(),                          # only these tasks
  [string]$Work = (Join-Path $env:USERPROFILE "IndexBench")
)
$ErrorActionPreference = "Stop"
$With = @($With | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Tasks = @($Tasks | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$spec = Get-Content (Join-Path $PSScriptRoot "index-bench\tasks.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$scores = @()
if ($Judge -and (Test-Path (Join-Path $Work "judge\$Judge\scores.csv"))) { $scores = Import-Csv (Join-Path $Work "judge\$Judge\scores.csv") }

function Runs([string]$label, [string]$cond, [string]$task) {
  Get-ChildItem (Join-Path $Work "runs\$label") -Directory -Filter "$task-$cond-*" -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match "^$([regex]::Escape($task))-$cond-\d+$" -and (Test-Path (Join-Path $_.FullName "result.json")) } |
    ForEach-Object { Get-Content (Join-Path $_.FullName "result.json") -Raw -Encoding UTF8 | ConvertFrom-Json }
}
function Avg($xs) { $a = @($xs | Where-Object { $null -ne $_ }); if ($a.Count -eq 0) { return $null }; ($a | Measure-Object -Average).Average }
function Cell($all, [string]$label, [string]$cond, $task) {
  # Averages of the runs that ended as the model chose: one an error ended (a refusal) says nothing of the index.
  $runs = @($all | Where-Object { $_.stop -eq "Completed" })
  $failed = @($all | Where-Object { $_.stop -ne "Completed" })
  $n = $runs.Count
  if ($n -eq 0) { return $null }
  $result = if ($task.checks) { "$(@($runs | Where-Object { $_.checks.passed }).Count)/$n passed" }
    else {
      $s = @($scores | Where-Object { $_.label -eq $label -and $_.task -eq $task.id -and $_.condition -eq $cond })
      if ($s.Count) { "{0:0.0}/{1}" -f (Avg ($s | ForEach-Object { [double]$_.score })), $s[0].of } else { "not graded" }
    }
  [pscustomobject]@{ N = $n; Seconds = Avg ($runs.seconds); Calls = Avg ($runs.toolCalls); Tokens = (Avg ($runs.promptTokens)) / 1000
    Index = Avg ($runs | ForEach-Object { $_.index + $_.indexSearch }); Search = Avg ($runs.shellSearch); Result = $result
    Stops = $(if ($failed.Count) { "and $($failed.Count) left out: " + (($failed | ForEach-Object stop) -join ", ") } else { "" }) }
}

$out = New-Object System.Text.StringBuilder
if ($Model) { [void]$out.AppendLine("### $Model").AppendLine() }
$header = "| Task | Runs | Time (s) | Tool calls | Index calls | Shell searches | Prompt tokens (k) | Result |"
[void]$out.AppendLine($header).AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |")
$ratios = @{}
foreach ($task in @($spec.tasks | Where-Object { $Tasks.Count -eq 0 -or $Tasks -contains $_.id })) {
  $base = Cell (Runs $Without "without" $task.id) $Without "without" $task
  if (-not $base) { continue }
  $rows = @(@{ Name = "without"; Cell = $base })
  foreach ($label in $With) {
    $c = Cell (Runs $label "with" $task.id) $label "with" $task
    if ($c) { $rows += @{ Name = $(if ($With.Count -gt 1) { "with ($label)" } else { "with" }); Cell = $c } }
  }
  foreach ($r in $rows) {
    $c = $r.Cell
    $stops = if ($c.Stops) { " ($($c.Stops))" } else { "" }
    [void]$out.AppendLine(("| {0} {1} | {2} | {3:0} | {4:0.0} | {5:0.0} | {6:0.0} | {7:0} | {8}{9} |" -f $task.id, $r.Name, $c.N, $c.Seconds, $c.Calls, $c.Index, $c.Search, $c.Tokens, $c.Result, $stops))
    if ($r.Name -ne "without") {
      if (-not $ratios[$r.Name]) { $ratios[$r.Name] = @() }
      $ratios[$r.Name] += [pscustomobject]@{ T = $c.Seconds / $base.Seconds; C = $c.Calls / [Math]::Max(1, $base.Calls); K = $c.Tokens / $base.Tokens }
    }
  }
}
[void]$out.AppendLine()
foreach ($name in $ratios.Keys) {
  # The geometric mean of the per-task ratios: every task weighs the same, however long it is.
  $g = { param($xs) [Math]::Exp((($xs | ForEach-Object { [Math]::Log($_) }) | Measure-Object -Average).Average) }
  $r = $ratios[$name]
  [void]$out.AppendLine(("Over {0} tasks, {1} against without (geometric mean of the ratios): time x{2:0.00}, tool calls x{3:0.00}, prompt tokens x{4:0.00}." -f
    $r.Count, $name, (& $g $r.T), (& $g $r.C), (& $g $r.K)))
}
$out.ToString()
