<#
.SYNOPSIS
  Builds the portable BlackBox: no installer, nothing written outside its own folder.

.DESCRIPTION
  Default: one BlackBox.exe, trimmed to the parts of .NET and the libraries BlackBox uses and
  compressed (about 26 MB instead of 102). The native UI libraries (Skia, HarfBuzz, ANGLE) are
  embedded and unpacked into Data/runtime next to the exe on first run. Copy just the exe anywhere
  and run it.

  -Folder: the classic self-contained folder (exe + DLLs), handy for debugging.

.EXAMPLE
  ./scripts/publish.ps1           # publish/BlackBox.exe
  ./scripts/publish.ps1 -Folder   # publish/BlackBox-win-x64/
#>
param(
    [string]$Runtime = "win-x64",
    [switch]$Folder
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot
$project = Join-Path $root "src/BlackBox.App/BlackBox.App.csproj"
$publishRoot = Join-Path $root "publish"

function Publish([string]$out, [string[]]$extra) {
    & dotnet publish $project -c Release -r $Runtime --self-contained -p:DebugType=none -o $out @extra
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}

if ($Folder) {
    $out = Join-Path $publishRoot "BlackBox-$Runtime"
    Publish $out @()
    New-Item -ItemType Directory -Force (Join-Path $out "Data") | Out-Null
    Write-Host "Portable folder ready: $out"
    return
}

# Trimming needs code that works without reflection: JSON goes through source-generated contexts
# (SettingsJson, ChatJson); the build lists anything that doesn't as IL2026 warnings.
$singleFile = @("-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=false",
    "-p:PublishTrimmed=true", "-p:TrimMode=full", "-p:EnableCompressionInSingleFile=true")

# 1) Single-file publish to a staging folder to collect the native libraries it leaves beside the exe.
$stage = Join-Path ([IO.Path]::GetTempPath()) "blackbox-publish-$([guid]::NewGuid().ToString('N'))"
try {
    Publish (Join-Path $stage "first") $singleFile
    $payloadDir = Join-Path $stage "payload"
    New-Item -ItemType Directory -Force $payloadDir | Out-Null
    Get-ChildItem (Join-Path $stage "first") -File | Where-Object { $_.Extension -in ".dll", ".so", ".dylib" } |
        Copy-Item -Destination $payloadDir
    $zip = Join-Path $stage "runtime.zip"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($payloadDir, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

    # 2) Publish again with that payload embedded, and keep only the exe.
    $second = Join-Path $stage "second"
    Publish $second ($singleFile + "-p:RuntimePayload=$zip")
    New-Item -ItemType Directory -Force $publishRoot | Out-Null
    $exe = Join-Path $publishRoot "BlackBox.exe"
    Copy-Item (Join-Path $second "BlackBox.exe") $exe -Force
    Write-Host ("Single-file build ready: {0} ({1:N0} MB)" -f $exe, ((Get-Item $exe).Length / 1MB))
}
finally {
    Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
}
