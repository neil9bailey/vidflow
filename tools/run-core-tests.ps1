<#
.SYNOPSIS
  Builds and runs tools\tests\CoreTests.cs (key map, shortcut parsing, settings round trip,
  launch options, time formatting). No window, no media, no network: safe for CI.
  Exit code 0 = all passed.
#>
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'build.ps1') -Harness core-tests -Files (Join-Path $root 'tools\tests\CoreTests.cs')
if ($LASTEXITCODE -ne 0) { throw "core tests did not build" }
$exe = Join-Path $root 'obj\harness\core-tests\core-tests.exe'
$out = Join-Path $root 'obj\harness\core-tests\results.txt'
if (Test-Path $out) { Remove-Item $out }
$p = Start-Process -FilePath $exe -ArgumentList "`"$out`"" -PassThru -Wait -NoNewWindow
if (-not (Test-Path $out)) { throw "core tests wrote no results (exit $($p.ExitCode))" }
Get-Content $out
if ((Get-Content $out | Select-Object -Last 1) -ne 'ALL OK') { Write-Host "CORE TESTS FAILED" -ForegroundColor Red; exit 1 }
Write-Host "CORE TESTS OK" -ForegroundColor Green
