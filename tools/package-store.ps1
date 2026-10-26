<#
.SYNOPSIS
  Builds the Microsoft Store edition: an unsigned MSIX (the Store signs it on submission) holding
  VidFlow.exe with libmpv-2.dll beside it, the licence files and the tile images.
  Output: obj\store\VidFlow-<version>-store.msix.

  The identity must match Partner Center (Product management > Product identity) for an upload to
  be accepted; the defaults only suit a local test build.

  Store versions are four numbers ending in .0 that must rise with every submission, so the
  release version is mapped onto the third number:
    1.0.0-alpha.N -> 1.0.N.0     1.0.0-beta.N -> 1.0.(300+N).0     1.0.0-rc.N -> 1.0.(600+N).0
    1.0.0         -> 1.0.900.0   1.0.1-beta.2 -> 1.0.1302.0        (patch numbers up to 64)
.EXAMPLE
  .\tools\package-store.ps1                                   # local test package
  .\tools\package-store.ps1 -Version 1.0.0-beta.4 -IdentityName 12345NeilBailey.VidFlowPlayer `
      -Publisher 'CN=ABCD1234-...' -PublisherDisplayName 'Neil Bailey'
  .\tools\package-store.ps1 -Version 1.2.3-rc.1 -MapVersionOnly   # prints 1.2.3601.0
#>
param(
    [string]$Version,
    [string]$IdentityName = 'VidFlowPlayer.LocalTest',
    [string]$Publisher = 'CN=VidFlow Local Test',
    [string]$PublisherDisplayName = 'VidFlow Player (local test)',
    [string]$DisplayName = 'VidFlow Player',
    [string]$OutDir,
    [switch]$MapVersionOnly
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Version) { $Version = (Get-Content (Join-Path $root 'VERSION') -TotalCount 1).Trim() }
$Version = $Version.Trim().TrimStart('v')

function ConvertTo-StoreVersion([string]$v) {
    $plain = ($v -split '\+')[0]
    if ($plain -notmatch '^(\d+)\.(\d+)\.(\d+)(?:-(alpha|beta|rc)\.(\d+))?$') {
        throw "Version '$v' can't be mapped to a Store version (expected 1.2.3 or 1.2.3-alpha|beta|rc.N)"
    }
    $major = [int]$Matches[1]; $minor = [int]$Matches[2]; $patch = [int]$Matches[3]
    $step = if (-not $Matches[4]) { 900 } else {
        $n = [int]$Matches[5]
        if ($n -lt 1 -or $n -gt 299) { throw "Pre-release number $n is outside 1-299" }
        @{ alpha = 0; beta = 300; rc = 600 }[$Matches[4]] + $n
    }
    if ($patch -gt 64) { throw "Patch $patch is too large for the Store version scheme (max 64)" }
    if ($major -gt 65535 -or $minor -gt 65535) { throw "Version '$v' is out of range" }
    return "$major.$minor.$($patch * 1000 + $step).0"
}

$storeVersion = ConvertTo-StoreVersion $Version
if ($MapVersionOnly) { return $storeVersion }

function Find-SdkTool([string]$name) {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $tool = Get-ChildItem $kits -Recurse -Filter $name -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq 'x64' -and $_.Directory.Parent.Name -match '^\d+(\.\d+){3}$' } |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
    if (-not $tool) { throw "$name not found: install the Windows 10/11 SDK" }
    return $tool.FullName
}
$makeappx = Find-SdkTool 'makeappx.exe'
$makepri = Find-SdkTool 'makepri.exe'

if (-not $OutDir) { $OutDir = Join-Path $root 'obj\store' }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$build = Join-Path $OutDir 'build'
$layout = Join-Path $OutDir 'layout'
foreach ($d in $build, $layout) { if (Test-Path $d) { [IO.Directory]::Delete($d, $true) } }

# The engine ships as a file next to the exe: no unpacking at run time inside a package.
& (Join-Path $root 'build.ps1') -NoEmbed -Version $Version -OutDir $build
if ($LASTEXITCODE -ne 0) { throw "build failed" }

New-Item -ItemType Directory -Force $layout | Out-Null
Copy-Item (Join-Path $build 'VidFlow.exe'), (Join-Path $build 'libmpv-2.dll') $layout
Copy-Item (Join-Path $root 'COPYING'), (Join-Path $root 'THIRD-PARTY-NOTICES.md') $layout
Copy-Item (Join-Path $root 'packaging\store\Assets') (Join-Path $layout 'Assets') -Recurse

$esc = { param($s) [Security.SecurityElement]::Escape($s) }
$manifest = [IO.File]::ReadAllText((Join-Path $root 'packaging\store\AppxManifest.xml'))
$manifest = $manifest.Replace('$IdentityName$', (& $esc $IdentityName)).Replace('$Publisher$', (& $esc $Publisher)).
    Replace('$PublisherDisplayName$', (& $esc $PublisherDisplayName)).Replace('$DisplayName$', (& $esc $DisplayName)).
    Replace('$Version$', $storeVersion)
if ($manifest -match '\$\w+\$') { throw "Unfilled manifest value: $($Matches[0])" }
[IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, (New-Object Text.UTF8Encoding($false)))

# resources.pri lets Windows pick the scaled and unplated tile images.
$priConfig = Join-Path $OutDir 'priconfig.xml'
& $makepri createconfig /cf $priConfig /dq en-US /pv 10.0.0 /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri createconfig failed" }
# One index for everything: split per-scale indexes only work in bundles with resource packages.
$cfg = [xml](Get-Content $priConfig -Raw)
foreach ($n in @($cfg.SelectNodes('//packaging'))) { [void]$n.ParentNode.RemoveChild($n) }
$cfg.Save($priConfig)
& $makepri new /pr $layout /cf $priConfig /mn (Join-Path $layout 'AppxManifest.xml') /of (Join-Path $layout 'resources.pri') /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri new failed" }

$plain = ($Version -split '\+')[0]
$msix = Join-Path $OutDir "VidFlow-$plain-store.msix"
$out = & $makeappx pack /d $layout /p $msix /o 2>&1
if ($LASTEXITCODE -ne 0) { $out | Write-Host; throw "makeappx pack failed" }

$hash = (Get-FileHash $msix -Algorithm SHA256).Hash.ToLowerInvariant()
"{0}  {1:N1} MB  Store version {2}  identity {3}" -f (Split-Path $msix -Leaf), ((Get-Item $msix).Length / 1MB), $storeVersion, $IdentityName
"sha256 $hash"
