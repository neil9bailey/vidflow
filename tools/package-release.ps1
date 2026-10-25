<#
.SYNOPSIS
  Packs dist\VidFlow.exe the way a GitHub release ships it (used by the build and release
  workflows; safe to run locally). Writes into -OutDir:
    VidFlow.exe, VidFlow-<version>-win-x64.zip (the exe plus README, COPYING, notices, changelog),
    SHA256SUMS.txt
  and release-notes.md next to -OutDir (the CHANGELOG.md section for the version).
  Refuses when the exe is stamped with another version, or when CHANGELOG.md has no section for
  it. With -Release the section must also be dated (not "Unreleased").
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OutDir,
    [switch]$Release
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutDir) { $OutDir = Join-Path $root 'obj\release-assets' }
$Version = $Version.Trim().TrimStart('v')

$exe = Join-Path $root 'dist\VidFlow.exe'
if (-not (Test-Path $exe)) { throw "dist\VidFlow.exe not found: run build.ps1 first" }
$stamped = (Get-Item $exe).VersionInfo.ProductVersion
if ($stamped -ne $Version) { throw "dist\VidFlow.exe is stamped '$stamped', not '$Version' (build with -Version $Version)" }

# Release notes: the "## [<version>] - <date>" section, build metadata (+ci...) ignored.
$plain = ($Version -split '\+')[0]
$lines = Get-Content (Join-Path $root 'CHANGELOG.md')
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match ('^## \[' + [regex]::Escape($plain) + '\]')) { $start = $i; break }
}
if ($start -lt 0) { throw "CHANGELOG.md has no '## [$plain]' section" }
if ($Release -and $lines[$start] -match 'Unreleased') { throw "CHANGELOG.md section for $plain is still marked Unreleased: date it before tagging" }
$end = $lines.Count
for ($i = $start + 1; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^## ') { $end = $i; break } }
$notes = if ($end -gt $start + 1) { ($lines[($start + 1)..($end - 1)] -join "`n").Trim() } else { '' }
if ($Release -and -not $notes) { throw "CHANGELOG.md section for $plain is empty" }

if (Test-Path $OutDir) { [IO.Directory]::Delete($OutDir, $true) }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$notesFile = Join-Path (Split-Path $OutDir -Parent) 'release-notes.md'
Set-Content -Encoding UTF8 $notesFile $notes

Copy-Item $exe (Join-Path $OutDir 'VidFlow.exe')
$stage = Join-Path ([IO.Path]::GetTempPath()) ("vidflow-pack-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $stage | Out-Null
try {
    foreach ($f in 'README.md', 'COPYING', 'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md') { Copy-Item (Join-Path $root $f) $stage }
    Copy-Item $exe $stage
    $zip = Join-Path $OutDir "VidFlow-$Version-win-x64.zip"
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
}
finally { [IO.Directory]::Delete($stage, $true) }

$sums = Get-ChildItem $OutDir -File | Sort-Object Name | ForEach-Object {
    (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name
}
[IO.File]::WriteAllText((Join-Path $OutDir 'SHA256SUMS.txt'), (($sums -join "`n") + "`n"))
Get-ChildItem $OutDir -File | ForEach-Object { "{0,-40} {1,12:N0}" -f $_.Name, $_.Length }
Write-Host "Packed $Version -> $OutDir (notes: $notesFile)"
