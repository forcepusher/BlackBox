<#
.SYNOPSIS
  Builds the portable Sloperator: no installer, nothing written outside its own folder.

.DESCRIPTION
  Default: one Sloperator.exe, trimmed to the parts of .NET and the libraries Sloperator uses and
  compressed (about 28 MB). The native libraries (Skia, HarfBuzz, ANGLE, PDFium) are embedded,
  packed with Brotli by pack-runtime.cs, and unpacked into Data/runtime next to the exe on first
  run. Copy just the exe anywhere and run it.

  -Folder: the classic self-contained folder (exe + DLLs), handy for debugging.

.EXAMPLE
  ./scripts/publish.ps1           # publish/Sloperator.exe
  ./scripts/publish.ps1 -Folder   # publish/Sloperator-win-x64/
#>
param(
    [string]$Runtime = "win-x64",
    [switch]$Folder
)

$ErrorActionPreference = "Stop"

# Right-click > Run with PowerShell closes its window the moment the script stops. Started that way
# (from Explorer, for this script), an error waits for Enter so it can be read.
$self = Get-CimInstance Win32_Process -Filter "ProcessId=$PID" -ErrorAction SilentlyContinue
$fromExplorer = $self -and $self.CommandLine -match [regex]::Escape($PSCommandPath) -and
    (Get-Process -Id $self.ParentProcessId -ErrorAction SilentlyContinue).ProcessName -eq "explorer"
trap {
    if ($fromExplorer) {
        Write-Host ($_ | Out-String) -ForegroundColor Red
        Read-Host "Press Enter to close" | Out-Null
    }
    break
}

$root = Split-Path $PSScriptRoot
$project = Join-Path $root "src/Sloperator.App/Sloperator.App.csproj"
$publishRoot = Join-Path $root "publish"

# The portable SDK in .dev\dotnet when none is installed.
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -and (Test-Path "$root\.dev\dotnet\dotnet.exe")) {
    $env:DOTNET_ROOT = "$root\.dev\dotnet"
    $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
}

function Publish([string]$out, [string[]]$extra) {
    & dotnet publish $project -c Release -r $Runtime --self-contained -p:DebugType=none -o $out @extra
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}

if ($Folder) {
    $out = Join-Path $publishRoot "Sloperator-$Runtime"
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
$stage = Join-Path ([IO.Path]::GetTempPath()) "sloperator-publish-$([guid]::NewGuid().ToString('N'))"
try {
    Publish (Join-Path $stage "first") $singleFile
    $payloadDir = Join-Path $stage "payload"
    New-Item -ItemType Directory -Force $payloadDir | Out-Null
    Get-ChildItem (Join-Path $stage "first") -File | Where-Object { $_.Extension -in ".dll", ".so", ".dylib" } |
        Copy-Item -Destination $payloadDir
    # Packed with Brotli by a script of its own: Windows PowerShell's .NET has no Brotli.
    $packed = Join-Path $stage "runtime.br"
    & dotnet run (Join-Path $PSScriptRoot "pack-runtime.cs") -- $payloadDir $packed
    if ($LASTEXITCODE -ne 0) { throw "packing the native libraries failed" }

    # 2) Publish again with that payload embedded, and keep only the exe.
    $second = Join-Path $stage "second"
    Publish $second ($singleFile + "-p:RuntimePayload=$packed")
    New-Item -ItemType Directory -Force $publishRoot | Out-Null
    $exe = Join-Path $publishRoot "Sloperator.exe"
    Copy-Item (Join-Path $second "Sloperator.exe") $exe -Force
    Write-Host ("Single-file build ready: {0} ({1:N0} MB)" -f $exe, ((Get-Item $exe).Length / 1MB))
}
finally {
    Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
}
