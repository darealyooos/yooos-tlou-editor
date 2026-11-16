<#
.SYNOPSIS
  Restores and builds every project (desktop editor, web editor, Core, tests).
.EXAMPLE
  .\scripts\build.ps1                 # Release
  .\scripts\build.ps1 -Configuration Debug
#>
[CmdletBinding()]
param([ValidateSet('Release', 'Debug')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'YooosTLOUEditor.slnx'

Write-Host "dotnet $(dotnet --version)"
dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw "restore failed ($LASTEXITCODE)" }
dotnet build $solution -c $Configuration --no-restore -warnaserror -nologo
if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }
Write-Host "Build OK ($Configuration)" -ForegroundColor Green
