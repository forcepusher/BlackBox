<#
  The "old server" of a two-server task (a move from one server to another): a second lab VM,
  instance -Instance, reset to a fresh disk, and the task's old\ folder (setup.sh and its files)
  run there as root with the task's vars.env. With -Stop, that VM is switched off.
  sysadmin-test.ps1 runs this in a process of its own, since lab.ps1 binds to one instance.
  Keep this file ASCII: Windows PowerShell 5.1 reads a BOM-less script as ANSI.
#>
param([int]$Instance = 1, [string]$TaskDir = "", [string]$Vars = "", [switch]$Stop)
$ErrorActionPreference = "Stop"
$env:LAB_INSTANCE = [string]$Instance
. "$PSScriptRoot\lab.ps1"
if ($Stop) {
  Stop-LabVm
  exit 0
}
Reset-LabVm
Invoke-LabSsh "rm -rf /tmp/bb-task && mkdir -p /tmp/bb-task" | Out-Null
$send = @(Get-ChildItem (Join-Path $TaskDir "old") | ForEach-Object { $_.FullName }) + $Vars
Copy-LabFiles $send "/tmp/bb-task/"
$r = Invoke-LabSsh ("cd /tmp/bb-task && sed -i 's/\r$//' setup.sh vars.env && set -a && . ./vars.env && set +a && sudo -E bash ./setup.sh; " +
  "rc=`$?; sudo rm -rf /tmp/bb-task; exit `$rc") -TimeoutSec 1800
$r.Out
$r.Err
exit $r.Code
