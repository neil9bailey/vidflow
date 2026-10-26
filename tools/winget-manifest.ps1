<#
.SYNOPSIS
  Writes the three winget manifests (version, installer, default locale) for a PUBLISHED release,
  ready to validate and submit to microsoft/winget-pkgs (docs/WINGET.md). The installer is the
  portable VidFlow.exe from the GitHub release; its SHA-256 is read from the release (no download).
  Output: obj\winget\manifests\n\NeilBailey\VidFlowPlayer\<version>\
.EXAMPLE
  .\tools\winget-manifest.ps1 -Version 1.0.0
  .\tools\winget-manifest.ps1 -Version 1.0.0 -ExeSha256 <hex>   # without asking GitHub (testing)
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$ExeSha256,
    [string]$ReleaseDate,
    [string]$OutDir,
    [switch]$AllowPrerelease
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Version = $Version.Trim().TrimStart('v')
$repo = 'neil9bailey/vidflow'
$id = 'NeilBailey.VidFlowPlayer'
$schema = '1.12.0'

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') { throw "Version '$Version' is not like 1.2.0" }
# winget has no pre-release channel, so a beta would be offered to every user as an upgrade: stable only.
if ($Version -match '-' -and -not $AllowPrerelease) { throw "winget gets stable releases only ($Version is a pre-release; -AllowPrerelease to override)" }

if (-not $ExeSha256) {
    $json = gh api "repos/$repo/releases/tags/v$Version" 2>$null   # drafts are not found by tag
    if ($LASTEXITCODE -ne 0 -or -not $json) { throw "v$Version is not a published release on ${repo}: publish it first" }
    $rel = $json | ConvertFrom-Json
    $asset = $rel.assets | Where-Object name -eq 'VidFlow.exe'
    if (-not $asset -or $asset.digest -notmatch '^sha256:([0-9a-f]{64})$') { throw "v$Version has no VidFlow.exe with a SHA-256 digest" }
    $ExeSha256 = $Matches[1]
    if (-not $ReleaseDate) { $ReleaseDate = ([datetime]$rel.published_at).ToString('yyyy-MM-dd') }
}
if ($ExeSha256 -notmatch '^[0-9a-fA-F]{64}$') { throw "ExeSha256 must be 64 hex digits" }
if (-not $ReleaseDate) { $ReleaseDate = (Get-Date).ToString('yyyy-MM-dd') }

if (-not $OutDir) { $OutDir = Join-Path $root 'obj\winget' }
$dir = Join-Path $OutDir "manifests\n\NeilBailey\VidFlowPlayer\$Version"
New-Item -ItemType Directory -Force $dir | Out-Null
$enc = New-Object Text.UTF8Encoding($false)
function Write-Manifest([string]$name, [string]$kind, [string]$body) {
    $text = "# yaml-language-server: `$schema=https://aka.ms/winget-manifest.$kind.$schema.schema.json`n`n" + $body.Trim() + "`n"
    [IO.File]::WriteAllText((Join-Path $dir $name), $text.Replace("`r`n", "`n"), $enc)
}

Write-Manifest "$id.yaml" 'version' @"
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
"@

Write-Manifest "$id.installer.yaml" 'installer' @"
PackageIdentifier: $id
PackageVersion: $Version
InstallerType: portable
Commands:
- vidflow
MinimumOSVersion: 10.0.17763.0
ReleaseDate: $ReleaseDate
Installers:
- Architecture: x64
  InstallerUrl: https://github.com/$repo/releases/download/v$Version/VidFlow.exe
  InstallerSha256: $($ExeSha256.ToUpperInvariant())
ManifestType: installer
ManifestVersion: $schema
"@

Write-Manifest "$id.locale.en-US.yaml" 'defaultLocale' @"
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: Neil Bailey
PublisherUrl: https://github.com/neil9bailey
PublisherSupportUrl: https://github.com/$repo/issues
PrivacyUrl: https://github.com/$repo/blob/main/PRIVACY.md
PackageName: VidFlow Player
PackageUrl: https://github.com/$repo
License: GPL-3.0
LicenseUrl: https://github.com/$repo/blob/main/COPYING
ShortDescription: Get through a folder of video clips fast - keep the good ones, bin the rest.
Description: |-
  A free, offline video player for culling folders of clips. Step frames exactly in both directions,
  mark and loop the good parts, then keep, reject or bin each clip with one key. Decisions are applied
  in one reviewable batch (rejects go to the Recycle Bin), and your originals are never touched until
  you say so. One portable exe, no telemetry.
Moniker: vidflow
Tags:
- video-player
- video
- culling
- frame-by-frame
- mpv
- gopro
- dashcam
ReleaseNotesUrl: https://github.com/$repo/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: $schema
"@

Get-ChildItem $dir | ForEach-Object { $_.FullName }
