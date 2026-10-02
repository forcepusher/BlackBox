<#
.SYNOPSIS
  Builds the portable BlackBox folder: no installer, nothing written outside the folder.

.EXAMPLE
  ./publish.ps1                 # publish/BlackBox-win-x64
  ./publish.ps1 -BundleLlama    # also copy the llama.cpp found on PATH into ./llama
  ./publish.ps1 -Runtime linux-x64
#>
param(
    [string]$Runtime = "win-x64",
    [string]$Output = "",
    [switch]$BundleLlama
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root "publish/BlackBox-$Runtime" }

# Self-contained (no .NET install needed). Deliberately not single-file: a single-file bundle
# either extracts native libraries to %TEMP% (leaving files behind) or fails to load Skia.
dotnet publish (Join-Path $root "src/BlackBox.App/BlackBox.App.csproj") `
    -c Release -r $Runtime --self-contained `
    -p:DebugType=none `
    -o $Output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

foreach ($dir in "Models", "llama", "Workspace", "Data") {
    New-Item -ItemType Directory -Force (Join-Path $Output $dir) | Out-Null
}

if ($BundleLlama) {
    $server = Get-Command llama-server -ErrorAction SilentlyContinue
    if (-not $server) { throw "llama-server is not on PATH; download a llama.cpp release and unzip it into $Output/llama instead." }
    Copy-Item (Join-Path (Split-Path $server.Source) "*") (Join-Path $Output "llama") -Recurse -Force
    Write-Host "Bundled llama.cpp from $(Split-Path $server.Source)"
}

Copy-Item (Join-Path $root "README.md") $Output -Force
Write-Host "Portable build ready: $Output"
