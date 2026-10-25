# Microsoft Store edition: running from an MSIX package (simulated here with VIDFLOW_SIMULATE_PACKAGED=1),
# VidFlow leaves updates to the Store and file types to its package manifest, even with an update
# repository configured. The Open With actions are never run: they would write the real registry.
# Dot-sourced by tools\e2e.ps1 (helpers: Test, Check, Ctl, Dump, Wait-Pipe, Scaled; variables $Exe,
# $Pipe, $work).

Test 'store-mode' {
    $Pipe = "$Pipe-store"   # Ctl/Dump below talk to this test's own instance
    $ini = Join-Path $work 'store.ini'
    @"
AutoFitWindow=false
WindowX=60
WindowY=40
WindowWidth=1000
WindowHeight=640
QuickStartShown=true
UpdateRepo=example/vidflow
OsdMessages=true
ExtraMpvOptions=ao=null
"@ | Set-Content -Encoding UTF8 $ini
    $env:VIDFLOW_SIMULATE_PACKAGED = '1'
    $env:VIDFLOW_UPDATE_API = 'http://127.0.0.1:9'   # a closed port: no request could leave the machine
    $p = Start-Process -FilePath $Exe -ArgumentList @('--automation', '--pipe', $Pipe, '--settings', "`"$ini`"", '--log', "`"$work\store-vidflow.log`"") -PassThru
    $env:VIDFLOW_SIMULATE_PACKAGED = $null
    $env:VIDFLOW_UPDATE_API = $null
    try {
        Check 'store instance up' (Wait-Pipe 15000)
        $d = Dump
        Check 'reports itself as the packaged (Store) edition' ($d.update.packaged -eq $true) "packaged=$($d.update.packaged)"
        Check 'Check for Updates off despite an update repository' ($d.update.enabled -eq $false -and $d.update.repo -eq 'example/vidflow') "enabled=$($d.update.enabled) repo=$($d.update.repo)"
        $json = (Ctl 'menu' @('context')) | ConvertFrom-Json | ConvertTo-Json -Depth 10 -Compress
        Check '"Check for Updates" not in the menu' ($json -notmatch 'Check for Updates') ''
        Check 'Open With registration items not in the menu' ($json -notmatch 'Open With') ''
        Check 'rest of the File menu still there' ($json -match 'Rename File' -and $json -match 'Media Info') ''
        $r = Ctl 'action' @('CheckForUpdates')
        $d = Dump
        Check 'Check for Updates action refused, no request made' ($r -match 'disabled' -and $d.update.checks -eq 0) "r=$r checks=$($d.update.checks)"
    } finally {
        try { Ctl 'quit' | Out-Null } catch { }
        if (-not $p.WaitForExit((Scaled 6000))) { try { $p.Kill() } catch { } }
    }
}
