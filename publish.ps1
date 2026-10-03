<#
.SYNOPSIS
  Builds the portable BlackBox: no installer, nothing written outside its own folder.

.DESCRIPTION
  Default: one BlackBox.exe. The native UI libraries (Skia, HarfBuzz, ANGLE) are embedded and
  unpacked into Data/runtime next to the exe on first run. Copy just the exe anywhere and run it.

  -Folder: the classic self-contained folder (exe + DLLs), handy for debugging.

.EXAMPLE
  ./publish.ps1           # publish/BlackBox.exe
  ./publish.ps1 -Folder   # publish/BlackBox-win-x64/
#>
param(
    [string]$Runtime = "win-x64",
    [switch]$Folder
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src/BlackBox.App/BlackBox.App.csproj"
$publishRoot = Join-Path $root "publish"

function Publish([string]$out, [string[]]$extra) {
    & dotnet publish $project -c Release -r $Runtime --self-contained -p:DebugType=none -o $out @extra
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}

if ($Folder) {
    $out = Join-Path $publishRoot "BlackBox-$Runtime"
    Publish $out @()
    foreach ($dir in "Workspace", "Data") { New-Item -ItemType Directory -Force (Join-Path $out $dir) | Out-Null }
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
    $zip = Join-Path $stage "runtime.zip"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($payloadDir, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

    # 2) Publish again with that payload embedded, and keep only the exe.
    $second = Join-Path $stage "second"
    Publish $second @("-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=false", "-p:RuntimePayload=$zip")
    New-Item -ItemType Directory -Force $publishRoot | Out-Null
    $exe = Join-Path $publishRoot "BlackBox.exe"
    Copy-Item (Join-Path $second "BlackBox.exe") $exe -Force
    Write-Host ("Single-file build ready: {0} ({1:N0} MB)" -f $exe, ((Get-Item $exe).Length / 1MB))
}
finally {
    Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
}
