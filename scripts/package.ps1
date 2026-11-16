<#
.SYNOPSIS
  Publishes the desktop and web editors and produces the release ZIP in out/release.
.DESCRIPTION
  The ZIP contains only what is needed to run: the published binaries, the web UI files,
  README-FIRST.txt, LICENSE and THIRD-PARTY-NOTICES.md. No game data is ever included
  (the editor reads the user's own installation at run time).
.PARAMETER SelfContained
  Bundle the .NET runtime (larger ZIP, no runtime install needed).
.EXAMPLE
  .\scripts\package.ps1
  .\scripts\package.ps1 -SelfContained
#>
[CmdletBinding()]
param([switch]$SelfContained, [string]$Runtime = 'win-x64')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'Version not found in Directory.Build.props' }

$flavour = if ($SelfContained) { 'self-contained' } else { 'framework-dependent' }
$name = "YooosTLOUEditor-$version-$Runtime" + $(if ($SelfContained) { '-self-contained' } else { '' })
$stage = Join-Path $root "out/stage/$name"
$release = Join-Path $root 'out/release'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage, $release | Out-Null

$common = @('-c', 'Release', '-r', $Runtime, "--self-contained:$($SelfContained.IsPresent.ToString().ToLower())",
            '-p:DebugType=none', '-p:DebugSymbols=false', '-p:GenerateDocumentationFile=false', '-nologo')
dotnet publish (Join-Path $root 'src/App/App.csproj') @common -o $stage
if ($LASTEXITCODE -ne 0) { throw "desktop publish failed ($LASTEXITCODE)" }
dotnet publish (Join-Path $root 'src/Web/Web.csproj') @common -o (Join-Path $stage 'web')
if ($LASTEXITCODE -ne 0) { throw "web publish failed ($LASTEXITCODE)" }

# Remove anything that is not needed to run.
Get-ChildItem $stage -Recurse -Include '*.pdb', '*.xml', 'appsettings.Development.json', 'web.config' -File | Remove-Item -Force

Copy-Item (Join-Path $root 'LICENSE') $stage
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $stage
$readme = @"
Yooos TLOU Editor $version ($flavour, $Runtime)
=================================================

An independent community tool. Not affiliated with or endorsed by Naughty Dog,
Sony Interactive Entertainment or PlayStation. No game content is included:
you need your own legally obtained copy of The Last of Us Part II Remastered (PC).

Run
  Desktop editor : YooosTLOUEditor.exe
  Web editor     : web\YooosTLOUEditor.Web.exe, then open http://127.0.0.1:5178
$(if (-not $SelfContained) { @"

Requirements (framework-dependent build)
  .NET 10 Desktop Runtime (desktop editor)
  ASP.NET Core 10 Runtime (web editor)
  https://dotnet.microsoft.com/download/dotnet/10.0
"@ })

Preferences, thumbnails and layout are stored in %LOCALAPPDATA%\YooosTLOUEditor.
Scenes (.frscene) default to Documents\Factions Reborn\Scenes.
Nothing is ever written into the game folder.

Licence: see LICENSE. Third-party components: see THIRD-PARTY-NOTICES.md.
"@
Set-Content -Path (Join-Path $stage 'README-FIRST.txt') -Value $readme -Encoding UTF8

# Safety net: refuse to ship game archives or extracted game data.
$forbidden = Get-ChildItem $stage -Recurse -File -Include '*.psarc', '*.pak', '*.ndb', '*.bin', '*.frscene', '*.dds', '*.tga'
if ($forbidden) { throw "Refusing to package game-like data: $($forbidden.FullName -join ', ')" }

$zip = Join-Path $release "$name.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
Set-Content -Path "$zip.sha256" -Value "$hash  $name.zip" -Encoding ASCII
Write-Host "Release: $zip" -ForegroundColor Green
Write-Host "SHA-256: $hash"
