# Frame accuracy (roadmap L1, L2, L8). Needs the clips from tools\make_vfrmedia.ps1 (testmedia\frames).
# Every clip carries its frame index as a 12-bit barcode (see make_vfrmedia.ps1), read back from an
# mpv "video" screenshot, so each check compares VidFlow's frame number with the frame really shown.

Add-Type -AssemblyName System.Drawing
$frMedia = Join-Path $media 'frames'
$frShots = Join-Path $work 'frames'
$script:frShotNo = 0
New-Item -ItemType Directory -Force $frShots | Out-Null

# While mpv seeks, time-pos (and so the frame number) already reports the target; wait until the
# frame is really decoded and shown.
function Wait-Settled([int]$ms = 20000) {
    $sw = [Diagnostics.Stopwatch]::StartNew(); $calm = 0
    while ($sw.ElapsedMilliseconds -lt $ms -and $calm -lt 2) {
        if ((Ctl 'get' @('seeking')) -eq 'no') { $calm++ } else { $calm = 0 }
        Start-Sleep -Milliseconds 30
    }
}

# The frame index burned into the shown frame (-1 when the barcode cannot be read).
function Read-FrameBarcode {
    Wait-Settled
    $script:frShotNo++
    $png = Join-Path $frShots ("f{0:D4}.png" -f $script:frShotNo)
    $r = Ctl 'mpv' @('screenshot-to-file', $png, 'video')
    if ($r -ne 'ok' -or -not (Test-Path $png)) { return -1 }
    $bmp = [Drawing.Bitmap]::FromFile($png)
    try {
        $w = $bmp.Width; $h = $bmp.Height; $b = [int]($w / 20); $n = 0
        for ($k = 0; $k -lt 12; $k++) {
            $cx = [int]($b + 1.5 * $b * $k + $b / 2); $cy = [int]($h - 1.8 * $b + $b / 2); $sum = 0
            foreach ($dx in -2, 0, 2) { foreach ($dy in -2, 0, 2) { $c = $bmp.GetPixel($cx + $dx, $cy + $dy); $sum += ($c.R + $c.G + $c.B) / 3 } }
            $avg = $sum / 9
            if ($avg -gt 170) { $n += [int][math]::Pow(2, $k) } elseif ($avg -gt 85) { return -1 }
        }
        return $n
    } finally { $bmp.Dispose(); Remove-Item $png -ErrorAction SilentlyContinue }
}

# "vfr" / "frameApproximate" from the dump, or from the user-data VidFlow mirrors them to.
function Get-FrameFlags {
    $d = Dump
    if ($null -ne $d.PSObject.Properties['frameApproximate']) {
        return [pscustomobject]@{ Vfr = $d.vfr; Approx = $d.frameApproximate; Source = 'dump'; Timing = $d.frameTiming }
    }
    $v = (Ctl 'get' @('user-data/vidflow/vfr')).Trim('"'); $a = (Ctl 'get' @('user-data/vidflow/frame-approximate')).Trim('"')
    return [pscustomobject]@{ Vfr = $(if ($v -eq 'yes') { $true } elseif ($v -eq 'no') { $false } else { $null }); Approx = ($a -eq 'yes'); Source = 'user-data'; Timing = '' }
}

function Open-FrameClip([string]$name) {
    $path = Join-Path $frMedia $name
    Ctl 'open' @($path) | Out-Null
    $d = WaitFor { param($d) $d.path -like "*$name" -and $d.frameCount -gt 0 -and $null -ne $d.frame } 8000 $name
    if (-not $d.paused) { Act 'PlayPause' }
    # The container probe answers within a few ms of the file start; give it a moment on a busy disk.
    $sw = [Diagnostics.Stopwatch]::StartNew()
    do { $f = Get-FrameFlags; if ($null -ne $f.Vfr) { break }; Start-Sleep -Milliseconds 50 } while ($sw.ElapsedMilliseconds -lt 3000)
    return $f
}

# Raw mpv exact seek (bypasses VidFlow's own seek code), settled.
function Seek-Frame([double]$t, [long]$expect) {
    Ctl 'mpv' @('seek', $t.ToString('R', [Globalization.CultureInfo]::InvariantCulture), 'absolute+exact') | Out-Null
    Start-Sleep -Milliseconds 50
    Wait-Settled
    if ($expect -lt 0) { return Dump }
    try { return WaitFor { param($d) $d.frame -eq $expect } 4000 "frame $expect" } catch { return Dump }
}

# After an action: wait for the wanted frame number, let the decoder settle, then compare the final
# number with the burned-in index. Mismatches go to $script:frHonesty (number != frame on screen) or
# $script:frLanding (VidFlow landed on another frame than intended).
function Expect-Frame([long]$want, [string]$what, [int]$ms = 6000) {
    try { WaitFor { param($d) $d.frame -eq $want } $ms "frame $want" | Out-Null } catch { }
    $bc = Read-FrameBarcode
    $d = Dump
    if ($d.frame -ne $bc) { $script:frHonesty.Add("$what -> number $($d.frame), on screen $bc") }
    elseif ($bc -ne $want) { $script:frLanding.Add("$what -> frame $bc, wanted $want") }
    return $d
}

$frCfr = @(
    @('fr_cfr_23.976fps.mp4', 24000, 1001), @('fr_cfr_24fps.mkv', 24, 1), @('fr_cfr_25fps.mp4', 25, 1),
    @('fr_cfr_29.97fps.mp4', 30000, 1001), @('fr_cfr_29.97fps.mkv', 30000, 1001), @('fr_cfr_30fps.mp4', 30, 1),
    @('fr_cfr_50fps.mkv', 50, 1), @('fr_cfr_59.94fps.mp4', 60000, 1001), @('fr_cfr_60fps.mp4', 60, 1),
    @('fr_cfr_120fps.mp4', 120, 1), @('fr_cfr_240fps.mp4', 240, 1), @('fr_cfr_240fps.mkv', 240, 1)
)

Test 'frames-cfr' {
    if (-not (Test-Path (Join-Path $frMedia 'fr_cfr_30fps.mp4'))) { Check 'test clips present (run tools\make_vfrmedia.ps1)' $false $frMedia; return }
    foreach ($c in $frCfr) {
        $name, $num, $den = $c
        $fps = $num / $den
        $f = Open-FrameClip $name
        $d = Dump
        Check "$name : CFR, exact frame numbers ($($f.Source))" ($f.Vfr -eq $false -and $f.Approx -eq $false) "vfr=$($f.Vfr) approx=$($f.Approx) $($f.Timing)"
        Check "$name : fps $([math]::Round($fps, 5))" (Near $d.fps $fps 1e-6) "$($d.fps)"
        $count = [long]$d.frameCount; $dur = [double]$d.duration
        $script:frHonesty = New-Object System.Collections.Generic.List[string]
        $script:frLanding = New-Object System.Collections.Generic.List[string]
        $raw = New-Object System.Collections.Generic.List[string]
        # VidFlow's own seeks: number keys (SeekPercent), markers (SeekAbsolute), Home / End.
        foreach ($p in 10, 50, 90) {
            $n = [long][math]::Min($count - 1, [math]::Floor($dur * $p / 100 * $fps + 0.25))
            Act "SeekPercent:$p"; Expect-Frame $n "SeekPercent:$p" | Out-Null
            Key @('.') | Out-Null; Expect-Frame ($n + 1) "  then step +1" | Out-Null
            Key @(',', ',') | Out-Null; Expect-Frame ($n - 1) "  then step -2" | Out-Null
            if ($p -eq 50) {
                $m = $n - 1
                Ctl 'action' @('ClearMarkers') | Out-Null
                Act 'AddMarker'
                Act 'GoToStart'; Expect-Frame 0 'GoToStart' | Out-Null
                Act 'GoToMarker:0'; Expect-Frame $m "GoToMarker (marker at frame $m)" | Out-Null
                Ctl 'action' @('ClearMarkers') | Out-Null
            }
        }
        Act 'GoToEnd'; Expect-Frame ($count - 1) 'GoToEnd' | Out-Null
        # Raw mpv exact seeks to N/fps: the number must match the frame shown however it got there.
        foreach ($n in 1, 7, ($count - 3)) {
            $d = Seek-Frame ($n * $den / $num) -1
            $bc = Read-FrameBarcode; $d = Dump
            if ($d.frame -ne $bc) { $script:frHonesty.Add("mpv seek $n/fps -> number $($d.frame), on screen $bc") }
            elseif ($bc -ne $n) { $raw.Add("$n->$bc") }
        }
        Check "$name : frame number = burned-in frame after every seek and step" ($script:frHonesty.Count -eq 0) ("count=$count " + ($script:frHonesty -join '; '))
        Check "$name : VidFlow's seeks and steps land on the intended frame" ($script:frLanding.Count -eq 0) ($script:frLanding -join '; ')
        if ($fps -le 100) { Check "$name : raw mpv exact seek to N/fps shows frame N" ($raw.Count -eq 0) ($raw -join ', ') }
        else { Check "$name : (info) raw mpv exact seek to N/fps (5 ms hr-seek window)" $true ($(if ($raw.Count) { 'lands early: ' + ($raw -join ', ') } else { 'exact' })) }
    }
}

Test 'frames-vfr' {
    if (-not (Test-Path (Join-Path $frMedia 'fr_vfr_jitter.mp4'))) { Check 'test clips present (run tools\make_vfrmedia.ps1)' $false $frMedia; return }
    foreach ($name in 'fr_vfr_jitter.mp4', 'fr_vfr_jitter.mkv', 'fr_vfr_steps.mp4', 'fr_vfr_steps.mkv') {
        $f = Open-FrameClip $name
        Check "$name : VFR detected, frame numbers marked approximate ($($f.Source))" ($f.Vfr -eq $true -and $f.Approx -eq $true) "vfr=$($f.Vfr) approx=$($f.Approx) $($f.Timing)"
        # Stepping still moves one real frame at a time; the number is only an estimate. Report how far off.
        $off = New-Object System.Collections.Generic.List[string]
        Seek-Frame 4.5 -1 | Out-Null
        Start-Sleep -Milliseconds 300
        $d = Dump; $b0 = Read-FrameBarcode
        Key @('.') | Out-Null; Start-Sleep -Milliseconds 400
        $b1 = Read-FrameBarcode
        Key @(',') | Out-Null; Start-Sleep -Milliseconds 600
        $b2 = Read-FrameBarcode
        Check "$name : step +1 / -1 moves exactly one real frame" ($b0 -ge 0 -and $b1 -eq $b0 + 1 -and $b2 -eq $b0) "shown $b0 -> $b1 -> $b2"
        Check "$name : (info) estimate at 4.5 s" $true "≈F $($d.frame) vs real $b0"
    }
    $avi = 'fr_unknown_30fps.avi'
    if (Test-Path (Join-Path $frMedia $avi)) {
        Ctl 'open' @((Join-Path $frMedia $avi)) | Out-Null
        WaitFor { param($d) $d.path -like "*$avi" -and $d.frameCount -gt 0 } 8000 $avi | Out-Null
        Start-Sleep -Milliseconds 400
        $f = Get-FrameFlags
        Check "$avi : unreadable container -> frame numbers marked approximate, no VFR claim" ($null -eq $f.Vfr -and $f.Approx -eq $true) "vfr=$($f.Vfr) approx=$($f.Approx) $($f.Timing)"
    }
    Ctl 'open' @($clip1) | Out-Null
    $d = WaitFor { param($d) $d.path -like '*clip01*' -and $d.frameCount -gt 0 } 6000 'clip01'
    Start-Sleep -Milliseconds 200
    $f = Get-FrameFlags
    Check 'next file (clip01, CFR) clears the VFR state' ($f.Vfr -eq $false -and $f.Approx -eq $false) "vfr=$($f.Vfr) approx=$($f.Approx)"
}

# L8: seek-bar drag sends "absolute+keyframes" seeks (OnSeekBarSeek), release an exact one. The drag
# itself cannot be driven over automation; this checks the two seek kinds it uses in VidFlow's mpv
# configuration on the long-GOP 4K clip (one keyframe, at 0 s): "keyframes" beats hr-seek=yes and is
# fast, and the exact seek lands on the targeted frame. Timings include ~30 ms of pipe polling.
Test 'frames-scrub' {
    $name = 'fr_4k_hevc_gop250.mp4'
    if (-not (Test-Path (Join-Path $frMedia $name))) { Check 'test clip present (run tools\make_vfrmedia.ps1)' $false $frMedia; return }
    Open-FrameClip $name | Out-Null
    $key = @(); $exact = @(); $kpos = @(); $bad = @()
    foreach ($t in 3.0, 6.0, 8.5) {
        Seek-Frame 0 0 | Out-Null
        $sw = [Diagnostics.Stopwatch]::StartNew()
        Ctl 'mpv' @('seek', "$t", 'absolute+keyframes') | Out-Null
        Start-Sleep -Milliseconds 20; Wait-Settled
        $key += $sw.ElapsedMilliseconds
        $kpos += (Dump).position
        $target = $t + 0.013 # mid-frame target: mpv shows the first frame starting at or after (target - 5 ms)
        $want = [long][math]::Ceiling(($target - 0.005) * 25 - 1e-9)
        $sw.Restart()
        Ctl 'mpv' @('seek', "$target", 'absolute+exact') | Out-Null
        Start-Sleep -Milliseconds 20; Wait-Settled
        $exact += $sw.ElapsedMilliseconds
        $bc = Read-FrameBarcode; $d = Dump
        if ($d.frame -ne $want -or $bc -ne $want) { $bad += "exact $target -> frame $($d.frame) shown $bc (want $want)" }
    }
    Check 'keyframe seek ignores hr-seek (lands on the keyframe at 0 s)' (@($kpos | Where-Object { [double]$_ -gt 0.05 }).Count -eq 0) "positions: $($kpos -join ', ')"
    Check 'release (exact) lands on the targeted frame' ($bad.Count -eq 0) ($bad -join '; ')
    Check '(info) 4K long-GOP: keyframe seek ms / exact seek ms' $true (($key -join ', ') + ' / ' + ($exact -join ', '))
}

# L2: back-step correctness on the long-GOP 4K clip (each step re-decodes the GOP up to the target).
Test 'frames-backstep' {
    $name = 'fr_4k_hevc_gop250.mp4'
    if (-not (Test-Path (Join-Path $frMedia $name))) { Check 'test clip present (run tools\make_vfrmedia.ps1)' $false $frMedia; return }
    Open-FrameClip $name | Out-Null
    Seek-Frame (200 / 25) 200 | Out-Null
    $bad = @(); $ms = @()
    for ($i = 1; $i -le 3; $i++) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        Key @(',') | Out-Null
        try { WaitFor { param($d) $d.frame -eq (200 - $i) } 15000 "frame $(200 - $i)" | Out-Null } catch { }
        Wait-Settled
        $ms += $sw.ElapsedMilliseconds
        $bc = Read-FrameBarcode; $d = Dump
        if ($d.frame -ne 200 - $i -or $bc -ne 200 - $i) { $bad += "step $i -> frame $($d.frame) shown $bc" }
    }
    Check 'back-step x3 from frame 200 shows frames 199, 198, 197' ($bad.Count -eq 0) ($bad -join '; ')
    Check '(info) back-step ms' $true ($ms -join ', ')
    # Rapid presses are netted (queue): three quick "," from 197 end on 194.
    Key @(',', ',', ',') | Out-Null
    try { WaitFor { param($d) $d.frame -eq 194 } 30000 'frame 194' | Out-Null } catch { }
    Wait-Settled
    $bc = Read-FrameBarcode; $d = Dump
    Check 'three rapid back-steps from 197 -> 194' ($d.frame -eq 194 -and $bc -eq 194) "frame $($d.frame) shown $bc"
    # A press while a slow (> 2 s) back-step is still decoding must queue, not restart the step.
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Key @(',') | Out-Null
    Start-Sleep -Milliseconds 2300
    Key @(',') | Out-Null
    try { WaitFor { param($d) $d.frame -eq 192 } 30000 'frame 192' | Out-Null } catch { }
    Wait-Settled
    $bc = Read-FrameBarcode; $d = Dump
    Check 'second press 2.3 s into a slow back-step still counts (194 -> 192)' ($d.frame -eq 192 -and $bc -eq 192) "frame $($d.frame) shown $bc after $($sw.ElapsedMilliseconds) ms"
}
