# Builds obj\tools\RunHidden.exe (hidden-desktop launcher) if it is missing or out of date.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$src = Join-Path $root 'tools\runhidden\RunHidden.cs'
$exe = Join-Path $root 'obj\tools\RunHidden.exe'
if ((Test-Path $exe) -and (Get-Item $exe).LastWriteTimeUtc -ge (Get-Item $src).LastWriteTimeUtc) { return }
New-Item -ItemType Directory -Force (Split-Path $exe) | Out-Null

# Same compiler discovery as build.ps1 (any Visual Studio edition, incl. CI runners' Enterprise).
$csc = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vswhere) {
    foreach ($p in (& $vswhere -all -products * -property installationPath 2>$null)) {
        $c = Join-Path $p 'MSBuild\Current\Bin\Roslyn\csc.exe'
        if (Test-Path $c) { $csc = $c; break }
    }
}
if (-not $csc) { throw "Roslyn csc.exe not found. Install Visual Studio Build Tools (MSBuild component)." }
$ref = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
if (-not (Test-Path "$ref\mscorlib.dll")) { $ref = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319' }
& $csc /noconfig /nologo /nostdlib+ /platform:x64 /target:exe "/out:$exe" "/reference:$ref\mscorlib.dll" "/reference:$ref\System.dll" $src
if ($LASTEXITCODE -ne 0) { throw "RunHidden build failed" }
