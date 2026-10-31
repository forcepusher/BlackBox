param([string]$Only = "*", [int]$Round = 1)
# Mockup bench on the user's Qwen: each ask once as SVG and once as HTML (round 1), then each
# conversation continued with the picture of its round-1 file to fix it (round 2). Files land in
# out\<ask>-<fmt>-r<round>.<fmt> with a .json of seconds and tokens. The server and key are read from
# release-qa's settings at run time and never written anywhere.
$here = Split-Path $MyInvocation.MyCommand.Path
$s = (Get-Content C:\Repos\Sloperator\.dev\release-qa\qwen\Data\settings.json -Raw | ConvertFrom-Json).server
$hdr = @{ Authorization = "Bearer $($s.apiKey)" }
$asks = Get-Content "$here\asks.json" -Raw | ConvertFrom-Json
$system = "You are a UI designer. Reply with only the file's contents, no explanation."
$fix = "This is how your file renders. Fix any layout problems you see (text running out of its box, overlaps, misalignment, missing parts) and reply with the whole corrected file."
function Ending($fmt, $w, $h) {
  if ($fmt -eq "svg") { "Make it as a single self-contained SVG file of exactly ${w}x${h} (width, height and viewBox set). No external images, fonts or scripts. Font: Segoe UI, sans-serif." }
  else { "Make it as a single self-contained HTML file whose page is exactly ${w}x${h} (no scrolling). Inline CSS only, no external images, fonts or scripts. Font: Segoe UI, sans-serif." }
}
function Clean([string]$text, [string]$fmt) {
  $t = [regex]::Replace($text, '(?s)<think>.*?</think>', '')
  $m = [regex]::Match($t, '(?s)```[a-zA-Z]*[ \t]*\r?\n(.*?)```')
  if ($m.Success) { $t = $m.Groups[1].Value }
  $start = if ($fmt -eq "svg") { '(?is)(<\?xml|<svg)' } else { '(?is)(<!doctype|<html)' }
  $end = if ($fmt -eq "svg") { '</svg>' } else { '</html>' }
  $a = [regex]::Match($t, $start); if ($a.Success) { $t = $t.Substring($a.Index) }
  $z = $t.LastIndexOf($end, [StringComparison]::OrdinalIgnoreCase); if ($z -ge 0) { $t = $t.Substring(0, $z + $end.Length) }
  $t.Trim() + "`n"
}
foreach ($a in $asks) {
  foreach ($fmt in "svg", "html") {
    $id = "$($a.id)-$fmt-r$Round"
    if ($id -notlike $Only -or (Test-Path "$here\out\$id.$fmt")) { continue }
    $msgs = [Collections.ArrayList]@(@{ role = "system"; content = $system }, @{ role = "user"; content = "$($a.ask) $(Ending $fmt $a.w $a.h)" })
    if ($Round -eq 2) {
      $prev = "$($a.id)-$fmt-r1"
      $raw = [IO.File]::ReadAllText("$here\out\$prev.raw.txt")
      $png = [Convert]::ToBase64String([IO.File]::ReadAllBytes("$here\out\$prev.png"))
      [void]$msgs.Add(@{ role = "assistant"; content = $raw })
      [void]$msgs.Add(@{ role = "user"; content = @(@{ type = "image_url"; image_url = @{ url = "data:image/png;base64,$png" } }, @{ type = "text"; text = $fix }) })
    }
    $body = @{ model = $s.model; messages = $msgs; temperature = 1.0; top_p = 0.95; top_k = 20; min_p = 0; presence_penalty = 0; repetition_penalty = 1.0; max_tokens = 32000 } | ConvertTo-Json -Depth 8
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
      $r = Invoke-RestMethod -Uri "$($s.baseUrl)/chat/completions" -Method Post -ContentType "application/json; charset=utf-8" -Headers $hdr -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 600
    } catch { "$id  FAILED after $([int]$sw.Elapsed.TotalSeconds) s: $($_.Exception.Message -replace [regex]::Escape($s.baseUrl),'<server>')"; continue }
    $sec = [int]$sw.Elapsed.TotalSeconds
    $content = $r.choices[0].message.content
    [IO.File]::WriteAllText("$here\out\$id.raw.txt", $content)
    [IO.File]::WriteAllText("$here\out\$id.$fmt", (Clean $content $fmt))
    @{ seconds = $sec; prompt_tokens = $r.usage.prompt_tokens; completion_tokens = $r.usage.completion_tokens; finish = $r.choices[0].finish_reason } | ConvertTo-Json | Set-Content "$here\out\$id.json"
    "$id  $sec s  in=$($r.usage.prompt_tokens) out=$($r.usage.completion_tokens) finish=$($r.choices[0].finish_reason)"
  }
}
