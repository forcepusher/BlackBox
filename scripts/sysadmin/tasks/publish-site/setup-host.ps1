# The new site, on this PC: a page, a stylesheet in a subfolder, and two images (binary files,
# which only arrive intact if they are copied as bytes).
$site = Join-Path $tdir "site-v2"
New-Item -ItemType Directory -Force (Join-Path $site "css"), (Join-Path $site "img") | Out-Null
$utf8 = New-Object Text.UTF8Encoding $false
[IO.File]::WriteAllText((Join-Path $site "index.html"), "<!doctype html>`n<html><head><meta charset=`"utf-8`"><title>Lab Bakery</title><link rel=`"stylesheet`" href=`"css/site.css`"></head>`n<body><h1>Lab Bakery</h1><img src=`"img/logo.png`" alt=`"logo`"><img src=`"img/bread.jpg`" alt=`"bread`"><p>version 2, $token</p></body></html>`n", $utf8)
[IO.File]::WriteAllText((Join-Path $site "css\site.css"), "body { font-family: serif; background: #fdf6e3; }`n", $utf8)
$rng = New-Object Random 7
foreach ($name in "logo.png", "bread.jpg") {
  $bytes = New-Object byte[] 150000
  $rng.NextBytes($bytes)
  $bytes[0] = 0x89; $bytes[1] = 0x50; $bytes[2] = 0x00; $bytes[3] = 0x0A # a NUL and a newline early on, as in real images
  [IO.File]::WriteAllBytes((Join-Path $site "img\$name"), $bytes)
}
