<#
.SYNOPSIS
  Runs the unit tests. Tests that need game files are skipped automatically (they return early)
  when no TLOU Part II install is found, so CI runs the format/settings/keybinding tests only.
.PARAMETER SelfTest
  Also runs the desktop editor's end-to-end self-test. Requires your own game install.
#>
[CmdletBinding()]
param([ValidateSet('Release', 'Debug')][string]$Configuration = 'Release', [switch]$SelfTest)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

dotnet test (Join-Path $root 'tests/Yooos.Tests/Yooos.Tests.csproj') -c $Configuration -nologo --logger "trx;LogFileName=tests.trx" --results-directory (Join-Path $root 'out/test-results')
if ($LASTEXITCODE -ne 0) { throw "unit tests failed ($LASTEXITCODE)" }

if ($SelfTest) {
    $exe = Join-Path $root "src/App/bin/$Configuration/net10.0-windows/YooosTLOUEditor.exe"
    if (-not (Test-Path $exe)) { throw "Build first: $exe not found" }
    $out = Join-Path $root 'out/selftest/selftest.frscene'
    New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
    $p = Start-Process $exe -ArgumentList "`"$(Join-Path $root 'tests/data/selftest-hotel-street.frscene')`"", '--selftest', "`"$out`"" -PassThru -Wait
    Get-Content "$out.log"
    if ($p.ExitCode -ne 0) { throw "self-test failed ($($p.ExitCode))" }
}
Write-Host "Tests OK" -ForegroundColor Green
