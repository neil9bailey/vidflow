# Cross-module integration (v1): dump keys and bar render state, frame labels on markers, markers
# following renames and deletes, paths over 260 characters, the per-session hardware decoding memo,
# the quick start card through the automation mouse verb, and Check for Updates hidden without a
# repository. Uses the helpers of tools\e2e.ps1.

$intDir = Join-Path $work 'integration'
New-Item -ItemType Directory -Force $intDir | Out-Null

# Lines of a log file from line $from on (the log is shared with the running instance).
function Read-LogFrom([string]$path, [int]$from) {
    $fs = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $r = New-Object IO.StreamReader($fs)
        $all = New-Object System.Collections.Generic.List[string]
        while ($null -ne ($l = $r.ReadLine())) { $all.Add($l) }
    } finally { $fs.Dispose() }
    if ($from -ge $all.Count) { return @() }
    return @($all.GetRange($from, $all.Count - $from))
}
function Log-LineCount([string]$path) { return @(Read-LogFrom $path 0).Count }

Test 'int-dump-keys' {
    Ctl 'open' @($clip1) | Out-Null
    $d = WaitFor { param($d) $d.path -like '*clip01*' -and $null -ne $d.frameApproximate -and $null -ne $d.vfr } 8000 'clip01 with a frame verdict'
    foreach ($k in 'vfr', 'frameApproximate', 'frameTiming', 'hwdecMemo', 'markedFiles', 'quickStart', 'markerExport', 'update', 'cull') {
        Check "dump has '$k'" ($null -ne $d.PSObject.Properties[$k]) ''
    }
    foreach ($k in 'frameText', 'vfrBadgeVisible', 'cullChipVisible') {
        Check "controlBar has '$k'" ($null -ne $d.controlBar.PSObject.Properties[$k]) ''
    }
    Check 'CFR clip: vfr false, exact frames' ($d.vfr -eq $false -and $d.frameApproximate -eq $false) "vfr=$($d.vfr) approx=$($d.frameApproximate) $($d.frameTiming)"
    if (-not (Dump).paused) { Act 'PlayPause' }
    Act 'SeekPercent:50'
    $d = WaitFor { param($d) $d.frame -eq 120 -and $d.controlBar.frameText -match '120' } 4000 'frame 120 on the bar'
    Check 'bar frame text shows the exact frame' ($d.controlBar.frameText -match '^[^≈]*\b120\b' -and $d.controlBar.vfrBadgeVisible -eq $false) "text='$($d.controlBar.frameText)' badge=$($d.controlBar.vfrBadgeVisible)"
    Check 'cull chip hidden outside cull mode' ($d.controlBar.cullChipVisible -eq $false) "$($d.controlBar.cullChipVisible)"

    $vfr = Join-Path $media 'frames\fr_vfr_steps.mp4'
    if (Test-Path $vfr) {
        # The badge is among the first parts the bar drops when it is short of room: give it room.
        $cs = (Dump).clientSize
        Ctl 'resize' @('1400', '700') | Out-Null
        Ctl 'open' @('--paused', $vfr) | Out-Null
        $d = WaitFor { param($d) $d.path -like '*fr_vfr_steps*' -and $d.vfr -eq $true -and $d.controlBar.vfrBadgeVisible } 8000 'VFR verdict on the bar'
        # The readout appears with the first frame number, which can trail the verdict.
        $d = Settled { param($d) $d.frameApproximate -eq $true -and $d.controlBar.frameText -like '≈*' }
        Check 'VFR clip: vfr true, approximate, badge shown, "≈" in the readout' ($d.frameApproximate -eq $true -and $d.controlBar.frameText -like '≈*') "text='$($d.controlBar.frameText)' timing=$($d.frameTiming)"
        Ctl 'resize' @("$($cs[0])", "$($cs[1])") | Out-Null
        # The OSD status line follows the verdict without polling (FeatureFrameTimingChanged).
        $msg = Poll { Ctl 'get' @('osd-status-msg') } { param($m) $m -match '≈F \$\{estimated-frame-number\}' }
        Check 'VFR clip: osd-status-msg marks "≈F"' ($msg -match '≈F \$\{estimated-frame-number\}') $msg
    } else { Write-Host '  (VFR part skipped: testmedia\frames missing; run tools\make_vfrmedia.ps1)' -ForegroundColor Yellow }

    $avi = Join-Path $media 'frames\fr_unknown_30fps.avi'
    if (Test-Path $avi) {
        Ctl 'open' @('--paused', $avi) | Out-Null
        $d = WaitFor { param($d) $d.path -like '*fr_unknown*' -and $d.frameApproximate -eq $true } 8000 'AVI verdict'
        $msg = Poll { Ctl 'get' @('osd-status-msg') } { param($m) $m -match '≈F' } 1000 20
        Check 'unreadable container: osd-status-msg marks "≈F" (frame numbers are estimates)' ($msg -match '≈F \$\{estimated-frame-number\}') $msg
    }
    Ctl 'open' @($clip1) | Out-Null
    WaitFor { param($d) $d.path -like '*clip01*' -and $d.frameApproximate -eq $false } 8000 'clip01 again' | Out-Null
    $msg = Poll { Ctl 'get' @('osd-status-msg') } { param($m) $m -match '   F \$\{estimated-frame-number\}' -and $m -notmatch '≈' }
    Check 'back on a CFR clip: plain "F"' ($msg -match '   F \$\{estimated-frame-number\}' -and $msg -notmatch '≈') $msg
}

Test 'int-marker-frame-label' {
    # Matroska stores millisecond timestamps: at 240 fps frame 61 starts at 0.254166 s but is stored
    # as 0.254, which floor(t * fps) would call frame 60. Marker labels use the player's own rule.
    $mkv = Join-Path $media 'frames\fr_cfr_240fps.mkv'
    if (-not (Test-Path $mkv)) { Write-Host '  (skipped: testmedia\frames missing)' -ForegroundColor Yellow; return }
    Ctl 'open' @('--paused', $mkv) | Out-Null
    WaitFor { param($d) $d.path -like '*fr_cfr_240fps.mkv' -and $d.frameApproximate -eq $false -and $d.paused } 8000 '240 fps mkv (exact)' | Out-Null
    Ctl 'action' @('ClearMarkers') | Out-Null
    # mpv's exact seek shows the first frame starting at or after (target - 5 ms): aim past the
    # middle of frame 60 (the app's FrameSeekTime rule), which lands on frame 61 (stored 0.254).
    $t = 61 / 240 + 0.005 - 0.5 / 240
    Ctl 'mpv' @('no-osd', 'seek', $t.ToString('0.######', [Globalization.CultureInfo]::InvariantCulture), 'absolute+exact') | Out-Null
    $d = WaitFor { param($d) $d.frame -eq 61 -and (Ctl 'get' @('seeking')) -eq 'no' } 5000 'frame 61'
    Check 'mkv timestamp is millisecond-rounded' ((Near $d.position 0.254 0.0005) -and [math]::Floor($d.position * 240 + 1e-6) -eq 60) "pos=$($d.position)"
    Act 'AddMarker'
    $d = Dump
    Check 'marker OSD names the frame shown (F 61)' ("$($d.lastOsd)" -match '\(F 61\)') "osd='$($d.lastOsd)' frame=$($d.frame) pos=$($d.position)"
    $m = (Ctl 'menu' @('context')) | ConvertFrom-Json
    $json = $m | ConvertTo-Json -Depth 10 -Compress
    Check 'marker menu label says F 61' ($json -match '1:\s+0:00\.254\s+\(F 61\)') ''
    Act 'ClearMarkers'
}

Test 'int-markers-follow-files' {
    $a = Join-Path $intDir 'int-mark-a.webm'
    $b = Join-Path $intDir 'int-mark-b.mkv'
    Copy-Item (Join-Path $media 'clip03_vp9.webm') $a -Force
    Copy-Item (Join-Path $media 'clip05_av1_noaudio.mkv') $b -Force
    Ctl 'open' @('--paused', $a) | Out-Null
    WaitFor { param($d) $d.path -like '*int-mark-a.webm' -and $d.duration -gt 0 -and $d.paused } 8000 'copy a' | Out-Null
    Ctl 'mpv' @('no-osd', 'seek', '1', 'absolute+exact') | Out-Null
    WaitFor { param($d) Near $d.position 1 0.03 } 3000 'pos 1' | Out-Null
    Act 'AddMarker'
    $renamed = Join-Path $intDir 'int-mark-renamed.webm'
    $r = Ctl 'action' @('RenameFile:int-mark-renamed.webm')
    $d = WaitFor { param($d) $d.path -like '*int-mark-renamed.webm' } 8000 'renamed and reloaded'
    Check 'rename ok, file moved' ($r -eq 'ok' -and (Test-Path $renamed) -and -not (Test-Path $a)) "r=$r"
    # MainForm.Cull.cs reports the move through FeatureFileMoved (CullFinishJobs).
    $d = Settled { param($d) @($d.markers).Count -eq 1 }
    Check 'marker follows the rename' ((@($d.markers).Count -eq 1) -and (Near @($d.markers)[0] 1 0.03) -and (@($d.markedFiles) -contains $renamed) -and -not (@($d.markedFiles) -contains $a)) "markers=$($d.markers) files=$($d.markedFiles)"
    if (-not (@($d.markedFiles) -contains $renamed)) {
        # Not followed (see above): mark the renamed clip itself so the delete half still runs.
        Ctl 'mpv' @('no-osd', 'seek', '1', 'absolute+exact') | Out-Null
        WaitFor { param($d) Near $d.position 1 0.03 } 3000 'pos 1' | Out-Null
        if (@((Dump).markers).Count -eq 0) { Act 'AddMarker' }
    }

    # Deleted (recycled) with markers: the markers go with it. Only this copy under obj\ is recycled.
    Check 'renamed clip has a marker before the delete' (@((Dump).markedFiles) -contains $renamed) ''
    Ctl 'append' @($b) | Out-Null
    Act 'DeleteFileNoConfirm'
    Poll { Test-Path $renamed } { param($there) -not $there } 6000 | Out-Null
    $d = WaitFor { param($d) -not (@($d.markedFiles) -contains $renamed) } 3000 'markers dropped'
    Check 'deleted file: gone, its markers dropped' (-not (Test-Path $renamed) -and -not (@($d.markedFiles) -contains $renamed)) "files=$($d.markedFiles)"
    Ctl 'action' @('ClearMarkers') | Out-Null
}

Test 'int-long-path' {
    $lp = Get-ChildItem (Join-Path $media 'gate\g32_longpath') -Recurse -Filter *.mp4 -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $lp) { Write-Host '  (skipped: run tools\make_gatemedia.ps1 -Set gate for testmedia\gate\g32_longpath)' -ForegroundColor Yellow; return }
    $path = $lp.FullName
    Check 'gate clip path is over 260 characters' ($path.Length -gt 260) "$($path.Length) chars"
    Ctl 'open' @($path) | Out-Null
    $d = WaitFor { param($d) ($d.path -eq $path -and $d.duration -gt 0 -and $d.frame -ge 0) -or $d.lastError -or "$($d.lastOsd)" -like 'Error*' } 10000 'long path clip'
    Check 'long-path clip opens and plays' ($d.path -eq $path -and $d.duration -gt 0 -and -not $d.lastError) "path=$("$($d.path)".Length) chars err=$($d.lastError) osd=$($d.lastOsd)"
    Check 'listed in the playlist' ($d.playlistCount -ge 1 -and @($d.playlist) -contains $path) "count=$($d.playlistCount)"
}

Test 'int-hwdec-memo' {
    # The memo lasts a session, and in the main one an earlier test may already have played AV1 long
    # enough for the verdict (how long it stays on such a clip depends on the machine's speed): this
    # test needs a session whose first AV1 file is its own, set up like the main one (same settings,
    # a hardware-decoded clip first).
    $Pipe = "$Pipe-hw"   # Ctl/Dump/WaitFor below talk to this test's own instance
    $log = Join-Path $intDir 'hw-vidflow.log'
    $ini = Join-Path $intDir 'hw.ini'
    @('AutoFitWindow=false', 'WindowX=80', 'WindowY=60', 'WindowWidth=1000', 'WindowHeight=640', 'Repeat=Off', 'ThumbnailPreview=true',
      'ConfirmDelete=true', 'SingleInstance=true', 'OsdMessages=true', 'QuickStartShown=true', 'UpdateRepo=', 'ExtraMpvOptions=ao=null') | Set-Content -Encoding UTF8 $ini
    $av1a = Join-Path $intDir 'int-av1-a.mkv'; $av1b = Join-Path $intDir 'int-av1-b.mkv'
    Copy-Item (Join-Path $media 'clip05_av1_noaudio.mkv') $av1a -Force
    Copy-Item (Join-Path $media 'clip05_av1_noaudio.mkv') $av1b -Force
    $p = Start-Process -FilePath $Exe -ArgumentList @('--automation', '--pipe', $Pipe, '--settings', "`"$ini`"", '--log', "`"$log`"") -PassThru
    try {
        if (-not (Wait-Pipe 15000)) { throw "hwdec VidFlow did not open its control pipe" }
        Ctl 'open' @($clip1) | Out-Null
        WaitFor { param($d) $d.path -like '*clip01*' -and $d.frame -ge 0 } 8000 'clip01 playing' | Out-Null
        $d0 = Dump
        if ("$($d0.hwdec)" -eq 'no') { Write-Host '  (skipped: hardware decoding is off)' -ForegroundColor Yellow; return }
        $codecs0 = Ctl 'get' @('hwdec-codecs')
        $skipped0 = @($d0.hwdecMemo.skipped)
        $n0 = Log-LineCount $log
        Ctl 'open' @($av1a) | Out-Null
        # Not waiting for hwdecCurrent here: after the hardware attempts fail, the copy VidFlow keeps of
        # mpv's hwdec-current for this file may stay null (or "") - the verdict below reads mpv itself.
        $d = WaitFor { param($d) $d.path -like '*int-av1-a.mkv' -and $d.frame -ge 0 } 15000 'first AV1 file playing'
        # The verdict is read at the first playback restart, asynchronously.
        $d = WaitFor { param($d) (@($d.hwdecMemo.skipped) -contains 'av1') -or (@($d.hwdecMemo.hardware) -contains 'av1') -or ($skipped0 -contains 'av1') } 6000 'hwdec verdict for av1'
        $hwFirst = "$($d.hwdecCurrent)"
        $codecs1 = Ctl 'get' @('hwdec-codecs')
        if (@($d.hwdecMemo.hardware) -contains 'av1') {
            Check 'AV1 decodes in hardware here: never skipped' (-not (@($d.hwdecMemo.skipped) -contains 'av1') -and $codecs1 -match 'av1') "hwdec=$hwFirst codecs=$codecs1"
            return
        }
        Check 'AV1 fell back to software: skipped for the session' ((@($d.hwdecMemo.skipped) -contains 'av1') -and $codecs1 -notmatch '(^|,)av1(,|$)' -and $codecs0 -match 'av1') "hwdec=$hwFirst before=$codecs0 after=$codecs1"
        $errs1 = @(Read-LogFrom $log $n0 | Where-Object { $_ -match 'ERROR' -and $_ -match 'mpv\[' }).Count
        $n1 = Log-LineCount $log
        $sw = [Diagnostics.Stopwatch]::StartNew()
        Ctl 'open' @($av1b) | Out-Null
        $d = WaitFor { param($d) $d.path -like '*int-av1-b.mkv' -and $d.frame -ge 0 -and (Ctl 'get' @('seeking')) -eq 'no' } 15000 'second AV1 file playing'
        $ms = $sw.ElapsedMilliseconds
        $d = Settled { param($d) "$($d.hwdecCurrent)" -eq 'no' }   # mpv reports it a moment after the start
        Start-Sleep -Milliseconds (Scaled 300)   # time for a late probe error to reach the log
        $errs2 = @(Read-LogFrom $log $n1 | Where-Object { $_ -match 'ERROR' -and $_ -match 'mpv\[' }).Count
        Check 'second AV1 file: no hardware probe errors' ($errs2 -eq 0 -and "$($d.hwdecCurrent)" -eq 'no') "mpv errors first=$errs1 second=$errs2 hwdec=$($d.hwdecCurrent) started in $ms ms"
        Check 'other codecs still allowed in hardware' ($codecs1 -match 'h264' -and $codecs1 -match 'hevc') $codecs1
    } finally {
        try { Ctl 'quit' | Out-Null } catch { }
        if (-not $p.WaitForExit((Scaled 6000))) { try { $p.Kill() } catch { } }
    }
}

Test 'int-quickstart-menu' {
    $Pipe = "$Pipe-int"   # Ctl/Dump/Act/WaitFor below talk to this test's own instance
    $ini = Join-Path $intDir 'int.ini'
    @"
AutoFitWindow=false
WindowX=60
WindowY=40
WindowWidth=1100
WindowHeight=860
QuickStartShown=false
UpdateRepo=
OsdMessages=true
ExtraMpvOptions=ao=null
"@ | Set-Content -Encoding UTF8 $ini
    $p = Start-Process -FilePath $Exe -ArgumentList @('--automation', '--pipe', $Pipe, '--settings', "`"$ini`"", '--log', "`"$intDir\int-vidflow.log`"") -PassThru
    try {
        $up = Wait-Pipe 15000
        Check 'integration instance up' $up
        $m = (Ctl 'menu' @('context')) | ConvertFrom-Json
        $json = $m | ConvertTo-Json -Depth 10 -Compress
        Check 'no UpdateRepo: "Check for Updates" not in the menu (hidden, not greyed)' ($json -notmatch 'Check for Updates') ''
        Check 'other Help items still there' ($json -match 'About') ''

        $d = WaitFor { param($d) $d.quickStart.shown } 6000 'quick start card painted'
        $g = $d.quickStart.gotIt
        $gx = [int]($g[0] + $g[2] / 2); $gy = [int]($g[1] + $g[3] / 2)
        $p0 = $d.paused
        $r = Ctl 'mouse' @('click', "$gx", "$gy")
        $d = WaitFor { param($d) -not $d.quickStart.visible } 3000 'card dismissed'
        Check 'automation click on "Got it" presses the card button' ($r -eq 'ok GotIt' -and $d.quickStart.settingShown -and $d.paused -eq $p0) "r=$r settingShown=$($d.quickStart.settingShown)"
        Check '"Got it" saved QuickStartShown=true' ((Get-Content $ini -Raw) -match '(?im)^QuickStartShown=true') ''
        $r = Ctl 'mouse' @('click', '5', '5')
        Check 'a click off the card is an ordinary video click' ($r -eq 'ok') "r=$r"
    } finally {
        try { Ctl 'quit' | Out-Null } catch { }
        if (-not $p.WaitForExit((Scaled 6000))) { try { $p.Kill() } catch { } }
    }
    # With a repository the item is listed (the check itself is click-only and is not run here).
    $ini2 = Join-Path $intDir 'int2.ini'
    (Get-Content $ini) -replace '^UpdateRepo=.*$', 'UpdateRepo=example/vidflow' -replace '^QuickStartShown=.*$', 'QuickStartShown=true' | Set-Content -Encoding UTF8 $ini2
    $env:VIDFLOW_UPDATE_API = 'http://127.0.0.1:9'
    $p = Start-Process -FilePath $Exe -ArgumentList @('--automation', '--pipe', $Pipe, '--settings', "`"$ini2`"", '--log', "`"$intDir\int2-vidflow.log`"") -PassThru
    $env:VIDFLOW_UPDATE_API = $null
    try {
        Wait-Pipe 15000 | Out-Null
        $json =((Ctl 'menu' @('context')) | ConvertFrom-Json) | ConvertTo-Json -Depth 10 -Compress
        Check 'with UpdateRepo: "Check for Updates" listed and enabled' ($json -match '"text":"Check for Updates[^"]*","shortcut":"[^"]*","checked":false,"enabled":true') ''
        Check 'no update request made' ((Dump).update.checks -eq 0) ''
    } finally {
        try { Ctl 'quit' | Out-Null } catch { }
        if (-not $p.WaitForExit((Scaled 6000))) { try { $p.Kill() } catch { } }
    }
}
