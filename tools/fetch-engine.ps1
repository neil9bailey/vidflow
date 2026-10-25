<#
.SYNOPSIS
  Puts the pinned libmpv engine into third_party\libmpv (used by build.ps1 and CI).
  Downloads the exact archive below, refuses it unless its SHA-256 matches, and extracts it.
  Does nothing when the expected libmpv-2.dll is already there.
  To move to a newer engine: change the three pinned values (and THIRD-PARTY-NOTICES.md), and
  re-establish its source pins in tools/engine-source.tsv (docs/ENGINE-SOURCE.md).
#>
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

$Tag = '20261010'
$Archive = 'mpv-dev-x86_64-20261010-git-b2c255c13e.7z'
$ArchiveSha256 = '394490ed6354c2294b87c63dfa3663b23524b5551aace9cbe35c4085682b14fa'
$DllSha256 = '34780746a0273a4fcae42dfe262a28984a426a238939474afd3a1561b450bbef'
# Upstream first; the project's own copy (release "engine-<tag>") in case upstream prunes old builds.
$Urls = @("https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/$Tag/$Archive",
          "https://github.com/neil9bailey/vidflow/releases/download/engine-$Tag/$Archive")

$dest = Join-Path $root 'third_party\libmpv'
$dll = Join-Path $dest 'libmpv-2.dll'
if ((Test-Path $dll) -and (Get-FileHash $dll -Algorithm SHA256).Hash.ToLowerInvariant() -eq $DllSha256) {
    Write-Host "Engine already present: $dll"
    return
}

$dl = Join-Path $root 'third_party\download'
New-Item -ItemType Directory -Force $dl, $dest | Out-Null
$file = Join-Path $dl $Archive
function Test-Archive { (Test-Path $file) -and (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() -eq $ArchiveSha256 }
if (-not (Test-Archive)) {
    $ProgressPreference = 'SilentlyContinue'
    $problems = @()
    foreach ($url in $Urls) {
        Write-Host "Downloading $url"
        try { Invoke-WebRequest -Uri $url -OutFile $file -UseBasicParsing }
        catch { $problems += "$url : $($_.Exception.Message)"; continue }
        if (Test-Archive) { break }
        $got = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
        $problems += "$url : SHA-256 mismatch (expected $ArchiveSha256, got $got)"
    }
    if (-not (Test-Archive)) { throw "Could not get a verified $Archive`n " + ($problems -join "`n ") }
}

# Windows' own bsdtar reads 7z on current builds (called by path: Git's GNU tar, often first on
# PATH, can't); 7-Zip is the fallback.
$tar = Join-Path $env:SystemRoot 'System32\tar.exe'
$extracted = $false
if (Test-Path $tar) {
    & $tar -xf $file -C $dest 2>$null
    $extracted = ($LASTEXITCODE -eq 0) -and (Test-Path $dll)
}
if (-not $extracted) {
    $sevenZip = (Get-Command 7z -ErrorAction SilentlyContinue).Source
    if (-not $sevenZip -and (Test-Path "$env:ProgramFiles\7-Zip\7z.exe")) { $sevenZip = "$env:ProgramFiles\7-Zip\7z.exe" }
    if (-not $sevenZip) { throw "Could not extract $Archive (need Windows tar with 7z support, or 7-Zip)" }
    & $sevenZip x -y "-o$dest" $file | Out-Null
    if ($LASTEXITCODE -gt 1) { throw "7-Zip could not extract $Archive (exit code $LASTEXITCODE)" }  # 1 = warnings only
}
if (-not (Test-Path $dll)) { throw "libmpv-2.dll missing after extracting $Archive" }
$dllGot = (Get-FileHash $dll -Algorithm SHA256).Hash.ToLowerInvariant()
if ($dllGot -ne $DllSha256) { throw "Unexpected libmpv-2.dll (SHA-256 $dllGot)" }
Write-Host "Engine ready: $dll"
exit 0   # the hash check above is the verdict, not a leftover 7-Zip warning code
