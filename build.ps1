<#
.SYNOPSIS
  Builds VidFlow.exe with the C# compiler that ships with Visual Studio Build Tools,
  targeting .NET Framework 4.8 (built into Windows 10/11, so nothing to install).

.EXAMPLE
  .\build.ps1                 # Release, single-file exe with the engine embedded -> dist\VidFlow.exe
  .\build.ps1 -NoEmbed        # fast dev build: exe + libmpv-2.dll side by side -> dist\dev\
  .\build.ps1 -Check -Files src\Ui\SeekBar.cs   # compile-check Core + Theme + the given files only
  .\build.ps1 -Check -WithContracts -Files src\Ui\ControlBar.cs
      # ...plus the contract stubs (contracts\*.cs) of every module not passed in -Files,
      # so a module can be checked without depending on other modules' in-progress code.
  .\build.ps1 -Harness seekbar -WithContracts -Files src\Ui\SeekBar.cs obj\harness\seekbar\Main.cs
      # throwaway test exe (one of the files must contain Main) -> obj\harness\seekbar\seekbar.exe
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [switch]$NoEmbed,
    [switch]$DebugBuild,
    [switch]$Check,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Files,
    [switch]$WithContracts,
    [switch]$Overlay,        # with -Check: compile the frozen tree in obj\snapshot\src, with -Files (live) swapped in
    [string]$Harness,
    [string]$OutDir,
    [string]$Version          # e.g. 1.2.0 or 1.2.0-beta.4; default: the VERSION file (release CI passes the tag)
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root
# Test tooling: obj\tools\RunHidden.exe runs GUI tests on a hidden desktop (see docs\SPEC.md).
& (Join-Path $root 'tools\ensure-runhidden.ps1')

function Find-Csc {
    $candidates = @()
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $paths = & $vswhere -all -products * -property installationPath 2>$null
        foreach ($p in $paths) { $candidates += (Join-Path $p 'MSBuild\Current\Bin\Roslyn\csc.exe') }
    }
    $candidates += 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    throw "Roslyn csc.exe not found. Install 'Visual Studio Build Tools' (MSBuild component)."
}

$csc = Find-Csc
$refDir = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
if (-not (Test-Path "$refDir\mscorlib.dll")) {
    # Fall back to the runtime directory (works, just without reference-assembly checking).
    $refDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
}
$refs = 'mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'Microsoft.VisualBasic.dll'

$obj = Join-Path $root 'obj'
New-Item -ItemType Directory -Force $obj | Out-Null

$common = @('/nologo', '/nostdlib+', '/platform:x64', '/langversion:latest', '/utf8output',
            '/warn:4', '/nowarn:1591', '/filealign:512')
foreach ($r in $refs) { $common += "/reference:$refDir\$r" }
if ($DebugBuild) { $common += '/debug:portable', '/optimize-', '/define:DEBUG;TRACE' } else { $common += '/debug-', '/optimize+', '/deterministic' }

function Invoke-Csc([string[]]$cscArgs) {
    $rsp = Join-Path $obj ("csc-" + [guid]::NewGuid().ToString('N') + ".rsp")
    # One argument per line; quote everything so paths with spaces survive.
    ($cscArgs | ForEach-Object { if ($_ -match '^/[a-z0-9]+[:+-]' -and $_ -notmatch ' ') { $_ } else { '"' + $_ + '"' } }) -join "`r`n" |
        Set-Content -Encoding UTF8 $rsp
    $out = & $csc /noconfig "@$rsp" 2>&1
    $code = $LASTEXITCODE
    Remove-Item $rsp -ErrorAction SilentlyContinue
    $out | ForEach-Object { "$_" } | Where-Object { $_ -ne '' } | ForEach-Object {
        if ($_ -match ': error ') { Write-Host $_ -ForegroundColor Red }
        elseif ($_ -match ': warning ') { Write-Host $_ -ForegroundColor Yellow }
        else { Write-Host $_ }
    }
    return $code
}

$allSources = Get-ChildItem -Path (Join-Path $root 'src') -Recurse -Filter *.cs | ForEach-Object { $_.FullName }

if ($Check -and $Overlay) {
    # Whole-app compile: a snapshot of src (copied to obj\snapshot\src before parallel work starts)
    # with each live file in -Files replacing its snapshot copy (matched by path relative to src),
    # plus any new live files. Other half-written changes can't break this check.
    $snap = Join-Path $root 'obj\snapshot\src'
    if (-not (Test-Path $snap)) { throw "No snapshot at $snap" }
    $srcRoot = Join-Path $root 'src'
    $live = @{}
    foreach ($f in $Files) {
        $full = (Resolve-Path $f).Path
        $rel = if ($full.StartsWith($srcRoot, 'OrdinalIgnoreCase')) { $full.Substring($srcRoot.Length).TrimStart('\') } else { $full }
        $live[$rel.ToLowerInvariant()] = $full
    }
    $set = @()
    foreach ($s in Get-ChildItem $snap -Recurse -Filter *.cs) {
        $rel = $s.FullName.Substring($snap.Length).TrimStart('\').ToLowerInvariant()
        if ($live.ContainsKey($rel)) { $set += $live[$rel]; $live.Remove($rel) } else { $set += $s.FullName }
    }
    $set += $live.Values
    $dll = Join-Path $obj ("check-" + [guid]::NewGuid().ToString('N') + ".dll")
    $code = Invoke-Csc ($common + @('/target:library', "/out:$dll") + $set)
    Remove-Item $dll -ErrorAction SilentlyContinue
    if ($code -ne 0) { Write-Host "CHECK FAILED" -ForegroundColor Red; exit 1 }
    Write-Host "CHECK OK (overlay, $($set.Count) files)" -ForegroundColor Green
    exit 0
}

if ($Check -or $Harness) {
    # Library compile of the shared foundation plus the requested files (no Program/MainForm needed).
    $core = Get-ChildItem (Join-Path $root 'src\Core') -Filter *.cs | ForEach-Object { $_.FullName }
    $core += (Join-Path $root 'src\Ui\Theme.cs')
    $extra = @()
    foreach ($f in $Files) { $extra += (Resolve-Path $f).Path }
    if ($WithContracts) {
        foreach ($c in Get-ChildItem (Join-Path $root 'contracts') -Filter *.cs) {
            $name = $c.Name
            $replaced = $false
            foreach ($f in $extra) {
                $leaf = Split-Path $f -Leaf
                if ($leaf -eq $name) { $replaced = $true }
                if ($name -eq 'Dialogs.cs' -and $f -match '\\Dialogs\\') { $replaced = $true }
                if ($name -eq 'MainForm.cs' -and $leaf -like 'MainForm*.cs') { $replaced = $true }
            }
            if (-not $replaced) { $extra += $c.FullName }
        }
    }
    $set = @($core + $extra | Select-Object -Unique)
    if ($Harness) {
        $hdir = Join-Path $obj "harness\$Harness"
        New-Item -ItemType Directory -Force $hdir | Out-Null
        $hexe = Join-Path $hdir "$Harness.exe"
        $code = Invoke-Csc ($common + @('/target:winexe', "/out:$hexe", "/win32manifest:$(Join-Path $root 'src\app.manifest')") + $set)
        if ($code -ne 0) { Write-Host "HARNESS BUILD FAILED" -ForegroundColor Red; exit 1 }
        $hdll = Join-Path $hdir 'libmpv-2.dll'
        if (-not (Test-Path $hdll)) { Copy-Item (Join-Path $root 'third_party\libmpv\libmpv-2.dll') $hdll }
        Write-Host "HARNESS OK -> $hexe" -ForegroundColor Green
        exit 0
    }
    $dll = Join-Path $obj ("check-" + [guid]::NewGuid().ToString('N') + ".dll")
    $code = Invoke-Csc ($common + @('/target:library', "/out:$dll") + $set)
    Remove-Item $dll -ErrorAction SilentlyContinue
    if ($code -ne 0) { Write-Host "CHECK FAILED" -ForegroundColor Red; exit 1 }
    Write-Host "CHECK OK ($($set.Count) files)" -ForegroundColor Green
    exit 0
}

# App icon (generated once).
$icon = Join-Path $root 'assets\VidFlow.ico'
if (-not (Test-Path $icon)) {
    Write-Host "Generating icon..."
    & python -I (Join-Path $root 'tools\make_icon.py') $icon
    if ($LASTEXITCODE -ne 0) { throw "icon generation failed" }
}

$dllSrc = Join-Path $root 'third_party\libmpv\libmpv-2.dll'
if (-not (Test-Path $dllSrc)) { throw "Missing $dllSrc - see README (Building) for how to fetch libmpv." }

if (-not $OutDir) { $OutDir = if ($NoEmbed) { Join-Path $root 'dist\dev' } else { Join-Path $root 'dist' } }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$exe = Join-Path $OutDir 'VidFlow.exe'

$args2 = $common + @('/target:winexe', "/out:$exe", "/win32manifest:$(Join-Path $root 'src\app.manifest')", "/win32icon:$icon")

# Version stamp: "1.2.0-beta.4" -> AssemblyVersion/FileVersion 1.2.0.0, InformationalVersion as given.
if (-not $Version) { $Version = (Get-Content (Join-Path $root 'VERSION') -TotalCount 1).Trim() }
$Version = $Version.Trim().TrimStart('v')
if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)(?:[-+][0-9A-Za-z.\-+]+)?$') { throw "Version '$Version' is not like 1.2.0 or 1.2.0-beta.4" }
$numeric = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
$versionFile = Join-Path $obj 'VersionInfo.cs'
@"
// Generated by build.ps1 from the VERSION file or -Version. Do not edit.
[assembly: System.Reflection.AssemblyVersion("$numeric")]
[assembly: System.Reflection.AssemblyFileVersion("$numeric")]
[assembly: System.Reflection.AssemblyInformationalVersion("$Version")]
"@ | Set-Content -Encoding UTF8 $versionFile
$allSources += $versionFile

if (-not $NoEmbed) {
    # Embed libmpv gzip-compressed; cache the compressed blob by content hash.
    $hash = (Get-FileHash $dllSrc -Algorithm SHA256).Hash.ToLowerInvariant()
    $size = (Get-Item $dllSrc).Length
    $gz = Join-Path $obj ("libmpv-" + $hash.Substring(0, 16) + ".dll.gz")
    if (-not (Test-Path $gz)) {
        Write-Host "Compressing libmpv-2.dll (one time)..."
        $in = [IO.File]::OpenRead($dllSrc)
        $outStream = [IO.File]::Create($gz + ".tmp")
        $z = New-Object IO.Compression.GZipStream($outStream, [IO.Compression.CompressionLevel]::Optimal)
        $in.CopyTo($z, 1MB); $z.Dispose(); $outStream.Dispose(); $in.Dispose()
        Move-Item -Force ($gz + ".tmp") $gz
    }
    $idFile = Join-Path $obj 'libmpv.id'
    [IO.File]::WriteAllText($idFile, "$hash $size")
    $args2 += "/resource:$gz,libmpv-2.dll.gz"
    $args2 += "/resource:$idFile,libmpv.id"
}

$sw = [Diagnostics.Stopwatch]::StartNew()
$code = Invoke-Csc ($args2 + $allSources)
if ($code -ne 0) { Write-Host "BUILD FAILED" -ForegroundColor Red; exit 1 }

if ($NoEmbed) {
    $dst = Join-Path $OutDir 'libmpv-2.dll'
    if (-not (Test-Path $dst) -or (Get-Item $dst).Length -ne (Get-Item $dllSrc).Length) { Copy-Item -Force $dllSrc $dst }
} else {
    # A stale side-by-side DLL would take precedence over the embedded engine.
    Remove-Item (Join-Path $OutDir 'libmpv-2.dll') -ErrorAction SilentlyContinue
}

$mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ("BUILD OK -> {0} ({1} MB, {2:N1} s)" -f $exe, $mb, $sw.Elapsed.TotalSeconds) -ForegroundColor Green
