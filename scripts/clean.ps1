<#
.SYNOPSIS
  Removes build outputs, test results, packaging output and editor caches inside the repository.
  Never touches your preferences (%LOCALAPPDATA%\YooosTLOUEditor) or your scenes.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$targets = Get-ChildItem $root -Recurse -Directory -Force |
    Where-Object { $_.Name -in 'bin', 'obj', '.vs', 'TestResults' -and $_.FullName -notmatch '\\\.git\\' }
$targets | ForEach-Object { Remove-Item -Recurse -Force $_.FullName -ErrorAction SilentlyContinue }
$out = Join-Path $root 'out'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
Get-ChildItem $root -Recurse -File -Force -Include '*.user', '*.log', '*.tmp', '*.bad' |
    Where-Object { $_.FullName -notmatch '\\\.git\\' } | Remove-Item -Force
Write-Host "Clean: removed $($targets.Count) folder(s) and out/" -ForegroundColor Green
