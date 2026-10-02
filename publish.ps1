<#
.SYNOPSIS
  Builds the portable BlackBox: no installer, nothing written outside its own folder.

.DESCRIPTION
  Default: one BlackBox.exe. The native libraries and llama.cpp are embedded and unpacked into
  Data/runtime next to the exe on first run; the app downloads the model into Models/.
  Copy just the exe anywhere and run it.

  -EmbedModels appends the model and vision projector from ./Models to the exe so it unpacks
  them on first run instead of downloading. Windows refuses to start executables larger than
  4 GB ("not a valid application for this OS platform"), so with the 7.8 GB model this only
  produces a runnable file on Linux/macOS ports, not on Windows.

  -Folder: the classic self-contained folder (exe + DLLs + llama/), handy for debugging.

.EXAMPLE
  ./publish.ps1                  # publish/BlackBox.exe (single file, llama.cpp included, ~150 MB)
  ./publish.ps1 -EmbedModels     # also append ./Models (too big to run on Windows, see above)
  ./publish.ps1 -Folder          # publish/BlackBox-win-x64/ folder layout
#>
param(
    [string]$Runtime = "win-x64",
    [switch]$Folder,
    [switch]$EmbedModels,
    [string]$LlamaDir = ""
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src/BlackBox.App/BlackBox.App.csproj"
$publishRoot = Join-Path $root "publish"

function Find-Llama {
    if ($LlamaDir) { return (Resolve-Path $LlamaDir).Path }
    $local = Join-Path $root "llama"
    if (Test-Path (Join-Path $local "llama-server.exe")) { return $local }
    $server = Get-Command llama-server -ErrorAction SilentlyContinue
    if ($server) { return Split-Path $server.Source }
    return $null
}

function Publish([string]$out, [string[]]$extra) {
    & dotnet publish $project -c Release -r $Runtime --self-contained -p:DebugType=none -o $out @extra
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}

$llama = Find-Llama
if (-not $llama) { Write-Warning "llama.cpp not found (./llama, -LlamaDir or PATH); the build will expect it to be installed separately." }

if ($Folder) {
    $out = Join-Path $publishRoot "BlackBox-$Runtime"
    Publish $out @()
    foreach ($dir in "Models", "llama", "Workspace", "Data") { New-Item -ItemType Directory -Force (Join-Path $out $dir) | Out-Null }
    if ($llama) { Copy-Item (Join-Path $llama "*") (Join-Path $out "llama") -Recurse -Force }
    Write-Host "Portable folder ready: $out"
    return
}

# 1) Single-file publish to a staging folder to collect the native libraries it leaves beside the exe.
$stage = Join-Path ([IO.Path]::GetTempPath()) "blackbox-publish-$([guid]::NewGuid().ToString('N'))"
try {
    Publish (Join-Path $stage "first") @("-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=false")
    $payloadDir = Join-Path $stage "payload"
    New-Item -ItemType Directory -Force $payloadDir | Out-Null
    Get-ChildItem (Join-Path $stage "first") -File | Where-Object { $_.Extension -in ".dll", ".so", ".dylib" } |
        Copy-Item -Destination $payloadDir
    if ($llama) {
        New-Item -ItemType Directory -Force (Join-Path $payloadDir "llama") | Out-Null
        Copy-Item (Join-Path $llama "*") (Join-Path $payloadDir "llama") -Recurse -Force
    }
    $zip = Join-Path $stage "runtime.zip"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($payloadDir, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

    # 2) Publish again with that payload embedded, and keep only the exe.
    $second = Join-Path $stage "second"
    Publish $second @("-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=false", "-p:RuntimePayload=$zip")
    New-Item -ItemType Directory -Force $publishRoot | Out-Null
    $exe = Join-Path $publishRoot "BlackBox.exe"
    Copy-Item (Join-Path $second "BlackBox.exe") $exe -Force

    # 3) Append the model files after the .NET bundle (see EmbeddedPayload.cs for the layout).
    if ($EmbedModels) {
        $models = @("Qwen3.8-27B-UD-IQ2_S.gguf", "mmproj-F16.gguf") |
            ForEach-Object { Join-Path $root "Models/$_" } | Where-Object { Test-Path $_ }
        if ($models) {
            $fs = [IO.File]::Open($exe, [IO.FileMode]::Append, [IO.FileAccess]::Write)
            try {
                $entries = @()
                foreach ($m in $models) {
                    $offset = $fs.Position
                    $in = [IO.File]::OpenRead($m)
                    try { $in.CopyTo($fs, 16MB) } finally { $in.Dispose() }
                    $entries += [ordered]@{ name = (Split-Path $m -Leaf); offset = $offset; length = (Get-Item $m).Length }
                    Write-Host "Embedded $(Split-Path $m -Leaf)"
                }
                $index = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json @($entries) -Compress))
                $fs.Write($index, 0, $index.Length)
                $len = [BitConverter]::GetBytes([long]$index.Length)
                $fs.Write($len, 0, 8)
                $magic = [Text.Encoding]::ASCII.GetBytes("BBPAYLD1")
                $fs.Write($magic, 0, 8)
            }
            finally { $fs.Dispose() }
        }
        else { Write-Warning "No model files in ./Models; building without an embedded model." }
    }
    Write-Host ("Single-file build ready: {0} ({1:N0} MB)" -f $exe, ((Get-Item $exe).Length / 1MB))
}
finally {
    Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
}
