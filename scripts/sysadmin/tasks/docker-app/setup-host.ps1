# The app, on this PC: its Dockerfile, code and README, with this run's token as the build name,
# so the page proves that the container runs this code.
$app = Join-Path $tdir "statuspage"
New-Item -ItemType Directory -Force $app | Out-Null
$utf8 = New-Object Text.UTF8Encoding $false
foreach ($f in Get-ChildItem (Join-Path $PSScriptRoot "app") -File) {
  [IO.File]::WriteAllText((Join-Path $app $f.Name), ([IO.File]::ReadAllText($f.FullName) -replace "`r`n", "`n").Replace("__TOKEN__", $token), $utf8)
}
