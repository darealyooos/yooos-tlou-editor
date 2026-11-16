<#
.SYNOPSIS
  Builds the public asset database site into out/site (ready to upload) and out/release/yooos-asset-site.zip.
.DESCRIPTION
  Exports the catalogue from YOUR game install with the desktop editor (--export-catalog) into out/catalog
  (kept between runs, so image rendering resumes), then assembles the static site in out/site.
  Default: names and technical metadata only.
.PARAMETER Thumbnails
  Also publish one preview image per mesh.
.PARAMETER Turntables
  Also publish a 360° view per mesh (24 frames you can turn with the mouse); implies -Thumbnails.
  First run ~30-40 min, ~400-600 MB. Images are derived from the game's models and textures: publishing them
  is your decision and responsibility.
.EXAMPLE
  .\scripts\build-site.ps1
  .\scripts\build-site.ps1 -Turntables
#>
[CmdletBinding()]
param([switch]$Thumbnails, [switch]$Turntables, [switch]$NoZip)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src/App/bin/Release/net10.0-windows/YooosTLOUEditor.exe'
if (-not (Test-Path $exe)) { & (Join-Path $PSScriptRoot 'build.ps1') }

$catalog = Join-Path $root 'out/catalog'
New-Item -ItemType Directory -Force $catalog | Out-Null
$exportArgs = @('--export-catalog', $catalog)
if ($Turntables) { $exportArgs += '--turntables' } elseif ($Thumbnails) { $exportArgs += '--thumbnails' }
$p = Start-Process $exe -ArgumentList $exportArgs -PassThru -Wait
Get-Content (Join-Path $catalog 'export.log') -Tail 4
if ($p.ExitCode -ne 0) { throw "catalogue export failed ($($p.ExitCode)); see out/catalog/export.log" }

$site = Join-Path $root 'out/site'
# Empty the folder rather than deleting it, so it works while it is open in Explorer.
if (Test-Path $site) { Get-ChildItem $site -Force | Remove-Item -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $site 'data') | Out-Null
Copy-Item (Join-Path $root 'site/*') $site -Recurse -Exclude 'README.md'

# Image paths in assets.json are relative to the catalogue folder; the site serves them from data/.
$json = Get-Content (Join-Path $catalog 'assets.json') -Raw
$json = $json.Replace('"thumbnail":"thumbs/', '"thumbnail":"data/thumbs/').Replace('"turntable":"turntables/', '"turntable":"data/turntables/')
[IO.File]::WriteAllText((Join-Path $site 'data/assets.json'), $json, (New-Object Text.UTF8Encoding($false)))
foreach ($d in @(@{ On = ($Thumbnails -or $Turntables); Name = 'thumbs' }, @{ On = $Turntables; Name = 'turntables' })) {
    if ($d.On -and (Test-Path (Join-Path $catalog $d.Name))) { Copy-Item (Join-Path $catalog $d.Name) (Join-Path $site 'data') -Recurse }
}
# Without the matching switch, drop image references so the site never points at files it does not ship.
if (-not ($Thumbnails -or $Turntables) -or -not $Turntables) {
    $doc = Get-Content (Join-Path $site 'data/assets.json') -Raw | ConvertFrom-Json
    foreach ($a in $doc.assets) {
        if (-not $Turntables) { $a.PSObject.Properties.Remove('turntable'); $a.PSObject.Properties.Remove('turntableFrames') }
        if (-not ($Thumbnails -or $Turntables)) { $a.PSObject.Properties.Remove('thumbnail') }
    }
    [IO.File]::WriteAllText((Join-Path $site 'data/assets.json'), ($doc | ConvertTo-Json -Depth 6 -Compress), (New-Object Text.UTF8Encoding($false)))
}

$files = Get-ChildItem $site -Recurse -File
$mb = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "Site: $site ($($files.Count) files, $mb MB)" -ForegroundColor Green
if ($files.Count -gt 20000) { Write-Warning 'More than 20 000 files: Cloudflare Pages will refuse it (use Netlify or object storage).' }
if (-not $NoZip) {
    $zip = Join-Path $root 'out/release/yooos-asset-site.zip'
    New-Item -ItemType Directory -Force (Split-Path $zip) | Out-Null
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path (Join-Path $site '*') -DestinationPath $zip -CompressionLevel Fastest
    Write-Host "ZIP : $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
}
if (-not ($Thumbnails -or $Turntables)) { Write-Host 'Metadata only (no images).' }
