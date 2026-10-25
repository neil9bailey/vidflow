<#
.SYNOPSIS
  End-to-end tests: drives a real VidFlow.exe over the automation pipe and checks its state.
  Everything runs on a hidden desktop (tools\RunHidden) with audio sent to mpv's null output,
  so nothing appears on screen or plays through the speakers. The user's clipboard is only
  touched with -Clipboard.
.EXAMPLE
  .\tools\e2e.ps1                       # dev build (dist\dev\VidFlow.exe)
  .\tools\e2e.ps1 -Exe dist\VidFlow.exe  # single-file release build
  .\tools\e2e.ps1 -Only zoom,fullscreen
  .\tools\e2e.ps1 -TimeScale 4           # every wait 4x its written time (default: from the load)
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\dist\dev\VidFlow.exe'),
    [string]$Pipe = 'VidFlowE2E',
    [string[]]$Only,
    [switch]$KeepOpen,
    [switch]$Clipboard,
    [double]$TimeScale = 0   # multiplies every wait; 0 = twice the slowdown measured at startup
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# Re-run this script on the hidden desktop: every window it opens stays off the user's screen.
if ($env:VIDFLOW_HIDDEN -ne '1') {
    & (Join-Path $root 'tools\ensure-runhidden.ps1')
    $env:VIDFLOW_HIDDEN = '1'
    $fwd = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-Exe', (Resolve-Path $Exe).Path, '-Pipe', $Pipe)
    if ($Only) { $fwd += '-Only'; $fwd += ($Only -join ',') }
    if ($KeepOpen) { $fwd += '-KeepOpen' }
    if ($Clipboard) { $fwd += '-Clipboard' }
    if ($TimeScale -gt 0) { $fwd += '-TimeScale'; $fwd += $TimeScale.ToString([Globalization.CultureInfo]::InvariantCulture) }
    $shell = (Get-Process -Id $PID).Path
    & (Join-Path $root 'obj\tools\RunHidden.exe') --wait --timeout ([int](900000 * [math]::Max(4, $TimeScale))) -- $shell @fwd
    $code = $LASTEXITCODE
    $env:VIDFLOW_HIDDEN = $null
    exit $code
}
if ($Only -and $Only.Count -eq 1 -and $Only[0] -match ',') { $Only = $Only[0].Split(',') }
$Exe = (Resolve-Path $Exe).Path
$media = Join-Path $root 'testmedia'
# One work folder per pipe: runs with different -Pipe values (in parallel) never share files.
$work = Join-Path $root ('obj\e2e-' + ($Pipe -replace '[^A-Za-z0-9._-]', '_'))
if (Test-Path $work) { Get-ChildItem $work -Recurse -Force | Sort-Object FullName -Descending | ForEach-Object { if ($_.PSIsContainer) { [IO.Directory]::Delete($_.FullName) } else { [IO.File]::Delete($_.FullName) } } }
New-Item -ItemType Directory -Force $work, "$work\shots" | Out-Null
$settingsPath = Join-Path $work 'settings.ini'
@"
AutoFitWindow=false
WindowX=80
WindowY=60
WindowWidth=1000
WindowHeight=640
Repeat=Off
ThumbnailPreview=true
ScreenshotFolder=$work\shots
ConfirmDelete=true
SingleInstance=true
OsdMessages=true
QuickStartShown=true
UpdateRepo=
ExtraMpvOptions=ao=null
"@ | Set-Content -Encoding UTF8 $settingsPath

$results = New-Object System.Collections.Generic.List[object]
$enc = New-Object System.Text.UTF8Encoding($false)

if (-not ('E2ELoad' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

// How busy the rest of the machine keeps the CPU: system-wide CPU time (GetSystemTimes, which
// includes the WSL / Docker virtual machines) minus that of this process and of every VidFlow.
public static class E2ELoad {
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    static readonly Process Me = Process.GetCurrentProcess();
    static readonly Dictionary<int, long> Seen = new Dictionary<int, long>(); // VidFlow pid -> CPU time
    static Process[] vidflow = new Process[0];
    static int listedAt;
    static bool listed;

    // CPU time (100 ns units) of this process and of every VidFlow seen so far (one that has exited
    // keeps its last reading). The process list is refreshed at most every 2 s: it is the slow part.
    static long Ours() {
        if (!listed || unchecked(Environment.TickCount - listedAt) > 2000) {
            foreach (var p in vidflow) p.Dispose();
            vidflow = Process.GetProcessesByName("VidFlow");
            listedAt = Environment.TickCount;
            listed = true;
        }
        foreach (var p in vidflow) { try { Seen[p.Id] = p.TotalProcessorTime.Ticks; } catch { } }
        long t = Me.TotalProcessorTime.Ticks;
        foreach (var v in Seen.Values) t += v;
        return t;
    }

    // A reading to measure from: busy and total CPU time of all processors, and ours.
    public static long[] Mark() {
        long idle, kernel, user;
        GetSystemTimes(out idle, out kernel, out user);
        return new[] { kernel - idle + user, kernel + user, Ours() };
    }

    // How much slower than idle the other processes made things since the mark: 1 / the share of
    // the CPU they left free, from 1 (idle) to 4.
    public static double Factor(long[] mark) {
        var now = Mark();
        double total = now[1] - mark[1];
        if (total <= 0) return 1;
        double others = Math.Max(0, (now[0] - mark[0]) - (now[2] - mark[2]));
        return Math.Max(1, Math.Min(4, 1 / Math.Max(0.25, 1 - others / total)));
    }
}
'@
}

# Every wait is its written time x $TimeScale, by default twice the slowdown measured now (load often
# comes in bursts later). A wait whose condition still fails at that point carries on while the load
# since it began says the machine is busy, up to 4x longer. Waits end as soon as their condition
# holds, so the extra patience costs a passing run nothing. Upper limits that are themselves checked
# ("exits within 6 s") stretch only by the load measured over that interval.
if ($TimeScale -le 0) {
    $m = [E2ELoad]::Mark(); Start-Sleep -Milliseconds 1000
    $TimeScale = [math]::Round(2 * [E2ELoad]::Factor($m), 1)
    Write-Host "time scale $TimeScale (twice the load at startup)" -ForegroundColor Cyan
} else { Write-Host "time scale $TimeScale" -ForegroundColor Cyan }
function Scaled([double]$ms) { return [int][math]::Ceiling($ms * $TimeScale) }
# True once $sw has passed $limit ms, and $limit x the load measured since $load (E2ELoad.Mark).
function Overdue($sw, [int]$limit, $load) {
    $t = $sw.ElapsedMilliseconds
    return $t -ge $limit -and $t -ge $limit * [E2ELoad]::Factor($load)
}

function Ctl([string]$verb, [string[]]$a = @()) {
    $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $Pipe, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $c.Connect(5000)
        $w = New-Object System.IO.StreamWriter($c, $enc, 4096, $true)
        $w.Write("$verb`n"); foreach ($x in $a) { $w.Write("$x`n") }; $w.Write("<<END>>`n"); $w.Flush()
        $r = New-Object System.IO.StreamReader($c, $enc, $false, 4096, $true)
        $lines = New-Object System.Collections.Generic.List[string]
        while ($null -ne ($line = $r.ReadLine()) -and $line -ne '<<END>>') { $lines.Add($line) }
        return ($lines -join "`n")
    } finally { $c.Dispose() }
}
function Dump { (Ctl 'dump') | ConvertFrom-Json }
function Act([string[]]$ids) { $r = Ctl 'action' $ids; if ($r -ne 'ok') { throw "action $ids -> $r" }; Start-Sleep -Milliseconds 120 }
function Key([string[]]$keys) { $r = Ctl 'key' $keys; Start-Sleep -Milliseconds 120; return $r }
# The scriptblocks passed to WaitFor / Poll / Settled see the caller's variables, but a variable of
# these functions with the same name would hide one: theirs have names no test uses ($wait*, $poll*).
function WaitFor([scriptblock]$cond, [int]$ms = 6000, [string]$what = 'condition') {
    $waitLimit = Scaled $ms; $waitLoad = [E2ELoad]::Mark()
    $sw = [Diagnostics.Stopwatch]::StartNew(); $d = $null
    while (-not (Overdue $sw $waitLimit $waitLoad)) {
        try { $d = Dump; if (& $cond $d) { return $d } } catch { }
        Start-Sleep -Milliseconds 100
    }
    throw "timeout waiting for $what ($([math]::Round($sw.Elapsed.TotalSeconds, 1)) s); last dump: $(($d | ConvertTo-Json -Compress -Depth 4))"
}
# Reads & $pollGet until & $pollOk holds for the value or the (scaled) time runs out; returns the last value.
function Poll([scriptblock]$pollGet, [scriptblock]$pollOk, [int]$pollMs = 3000, [int]$pollEvery = 100) {
    $pollLimit = Scaled $pollMs; $pollLoad = [E2ELoad]::Mark()
    $pollSw = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $pollValue = & $pollGet
        if ((& $pollOk $pollValue) -or (Overdue $pollSw $pollLimit $pollLoad)) { return $pollValue }
        Start-Sleep -Milliseconds $pollEvery
    }
}
# For state an action changes asynchronously (mpv reports it back later): the dump once the condition
# holds, else the last one, so the Check that follows still reports what was there.
function Settled([scriptblock]$waitCond, [int]$waitMs = 3000) { return Poll { Dump } $waitCond $waitMs 50 }
# The same for one mpv property: its value once it reads $waitWant, else the last reading.
function GetWhen([string]$waitProp, [string]$waitWant, [int]$waitMs = 3000) {
    return Poll { Ctl 'get' @($waitProp) } { param($v) $v -eq $waitWant } $waitMs
}
function Near($a, $b, $tol) { return ($null -ne $a) -and ([math]::Abs([double]$a - [double]$b) -le $tol) }
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    $results.Add([pscustomobject]@{ Test = $script:current; Check = $name; Pass = $ok; Detail = $detail })
    $col = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("  [{0}] {1} {2}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $detail) -ForegroundColor $col
}
function Test([string]$name, [scriptblock]$body) {
    if ($Only -and -not ($Only -contains $name)) { return }
    $script:current = $name
    Write-Host "== $name" -ForegroundColor Cyan
    try { & $body } catch { Check 'no exception' $false $_.Exception.Message }
}
function Start-VidFlow([string[]]$extra) {
    $argList = @('--automation', '--pipe', $Pipe, '--settings', "`"$settingsPath`"", '--log', "`"$work\vidflow.log`"") + $extra
    $p = Start-Process -FilePath $Exe -ArgumentList $argList -PassThru
    if (Wait-Pipe 15000) { return $p }
    throw "VidFlow did not open its control pipe"
}
# True once the VidFlow on $Pipe answers (the caller's $Pipe: tests with their own instance set it).
function Wait-Pipe([int]$ms) {
    return Poll { try { (Ctl 'activate') -eq 'ok' } catch { $false } } { param($up) $up } $ms 150
}

$clip1 = Join-Path $media 'clip01_h264_720p30.mp4'
$clip4 = Join-Path $media 'clip04_hevc_multitrack.mkv'
$proc = Start-VidFlow @("`"$clip1`"")

Test 'open' {
    $d = WaitFor { param($d) $d.path -like '*clip01*' -and (Near $d.duration 8 0.1) } 8000 'clip01 loaded'
    Check 'path' ($d.path -like '*clip01_h264_720p30.mp4') $d.path
    Check 'folder auto-loaded (5 media files)' ($d.playlistCount -eq 5) "count=$($d.playlistCount)"
    Check 'index 0' ($d.playlistIndex -eq 0) "$($d.playlistIndex)"
    Check 'title has name + [1/5]' ($d.title -like 'clip01_h264_720p30.mp4*1/5*') $d.title
    Check 'fps 30' (Near $d.fps 30 0.01) "$($d.fps)"
    Check 'surface handle stable' ($d.surfaceHandle -eq $d.surfaceHandleAtStart) "$($d.surfaceHandle)"
}

Test 'pause' {
    $d0 = Dump
    if (-not $d0.paused) { Act 'PlayPause' }
    $d = WaitFor { param($d) $d.paused } 3000 'paused'
    Check 'paused' $d.paused
    Check 'control bar shows paused' ($d.controlBar.paused -eq $true)
}

Test 'seek' {
    Act 'SeekPercent:50'
    $d = WaitFor { param($d) Near $d.position 4.0 0.05 } 4000 'seek 50%'
    Check 'position 4.0' (Near $d.position 4.0 0.05) "$($d.position)"
    Check 'frame 120' ($d.frame -eq 120) "$($d.frame)"
    Act 'SeekForwardShort'
    $d = WaitFor { param($d) Near $d.position 5.0 0.05 } 4000 'seek +1'
    Check 'shift+right = +1s' (Near $d.position 5.0 0.05) "$($d.position)"
    Act 'SeekBackward'
    $d = WaitFor { param($d) Near $d.position 0.0 0.05 } 4000 'seek -5'
    Check 'left = -5s' (Near $d.position 0.0 0.05) "$($d.position)"
    Act 'GoToEnd'
    $d = WaitFor { param($d) $d.frame -eq 239 } 4000 'last frame'
    Check 'go to end = last frame (239), paused' ($d.frame -eq 239 -and $d.paused) "frame=$($d.frame) pos=$($d.position)"
    Act 'Stop'
    $d = WaitFor { param($d) Near $d.position 0 0.05 } 4000 'stop'
    Check 'stop = paused at 0' ($d.paused -and (Near $d.position 0 0.05)) "$($d.position)"
}

Test 'framestep' {
    Act 'SeekPercent:50'
    WaitFor { param($d) $d.frame -eq 120 } 4000 'frame 120' | Out-Null
    $r = Key @('.', '.', '.')
    $d = WaitFor { param($d) $d.frame -eq 123 } 4000 'frame 123'
    Check '"." x3 -> frame 123' ($d.frame -eq 123) "frame=$($d.frame) keys=[$r]"
    $r = Key @('Ctrl+Left', 'Ctrl+Left')
    $d = WaitFor { param($d) $d.frame -eq 121 } 4000 'frame 121'
    Check 'Ctrl+Left x2 -> frame 121' ($d.frame -eq 121) "frame=$($d.frame) keys=[$r]"
    Check 'still paused' $d.paused
    $r = Key @(',', ',', ',', ',', ',')
    $d = WaitFor { param($d) $d.frame -eq 116 } 5000 'frame 116'
    Check '5 rapid back steps -> frame 116' ($d.frame -eq 116) "frame=$($d.frame)"
    Key @('.', ',', '.') | Out-Null
    $d = WaitFor { param($d) $d.frame -eq 117 } 5000 'frame 117'
    Check 'mixed . , . -> net +1 (117)' ($d.frame -eq 117) "frame=$($d.frame)"
}

Test 'speed' {
    Act 'SpeedReset'
    Act 'SpeedUp'; $d = Settled { param($d) Near $d.speed 1.25 0.001 }; Check 'SpeedUp 1 -> 1.25' (Near $d.speed 1.25 0.001) "$($d.speed)"
    Act 'SpeedDown'; Act 'SpeedDown'; $d = Settled { param($d) Near $d.speed 0.75 0.001 }; Check 'SpeedDown x2 -> 0.75' (Near $d.speed 0.75 0.001) "$($d.speed)"
    Act 'SetSpeed:0.5'; $d = Settled { param($d) Near $d.speed 0.5 0.001 }; Check 'SetSpeed 0.5' (Near $d.speed 0.5 0.001)
    Act 'SpeedDouble'; $d = Settled { param($d) Near $d.speed 1 0.001 }; Check 'double -> 1' (Near $d.speed 1 0.001)
    Act 'SpeedFineUp'; $d = Settled { param($d) Near $d.speed 1.05 0.001 }; Check 'fine +0.05' (Near $d.speed 1.05 0.001) "$($d.speed)"
    Key @('Backspace') | Out-Null; $d = Settled { param($d) (Near $d.speed 1 0.001) -and (Near $d.controlBar.speed 1 0.001) }
    Check 'Backspace resets' (Near $d.speed 1 0.001)
    Check 'control bar speed' (Near $d.controlBar.speed 1 0.001)
    $m = (Ctl 'menu' @('speed')) | ConvertFrom-Json
    $one = $m | Where-Object { $_.text -match '^1(\.0+)?×$' } | Select-Object -First 1
    Check 'speed menu has checked 1×' ($null -ne $one -and $one.checked) "$(($one | ConvertTo-Json -Compress))"
}

Test 'repeat' {
    Act 'SetRepeat:One'; Check 'loop-file inf' ((Ctl 'get' @('loop-file')) -eq 'inf') (Ctl 'get' @('loop-file'))
    Act 'CycleRepeat'; $d = Dump; Check 'cycle One -> All' ($d.repeat -eq 'All') "$($d.repeat)"
    Check 'loop-file off for All' ((Ctl 'get' @('loop-file')) -eq 'no')
    Act 'CycleRepeat'; $d = Dump; Check 'cycle All -> Off' ($d.repeat -eq 'Off') "$($d.repeat)"
}

Test 'abloop' {
    Ctl 'mpv' @('seek', '1', 'absolute+exact') | Out-Null
    WaitFor { param($d) Near $d.position 1 0.05 } 3000 'pos 1' | Out-Null
    Act 'AbLoop'; $d = Settled { param($d) (Near $d.abA 1.0 0.05) -and $d.controlBar.abLoop -eq 'ASet' }
    Check 'A set at 1.0' (Near $d.abA 1.0 0.05) "$($d.abA)"
    Check 'bar shows ASet' ($d.controlBar.abLoop -eq 'ASet') "$($d.controlBar.abLoop)"
    Ctl 'mpv' @('seek', '2', 'absolute+exact') | Out-Null
    WaitFor { param($d) Near $d.position 2 0.05 } 3000 'pos 2' | Out-Null
    Act 'AbLoop'; $d = Settled { param($d) (Near $d.abB 2.0 0.05) -and $d.controlBar.abLoop -eq 'Active' }
    Check 'B set at 2.0' (Near $d.abB 2.0 0.05) "$($d.abB)"
    Check 'bar shows Active' ($d.controlBar.abLoop -eq 'Active')
    Act 'AbLoop'; $d = Settled { param($d) $null -eq $d.abA -and $null -eq $d.abB }; Check 'cleared' ($null -eq $d.abA -and $null -eq $d.abB) "a=$($d.abA) b=$($d.abB)"
}

Test 'zoom' {
    Act 'ZoomReset'
    Act 'ZoomIn'; $d = Settled { param($d) Near $d.zoom 0.3219 0.001 }; Check 'zoom in 25%' (Near $d.zoom 0.3219 0.001) "$($d.zoom)"
    Act 'ZoomOut'; $d = Settled { param($d) Near $d.zoom 0 0.001 }; Check 'zoom out back to 0' (Near $d.zoom 0 0.001) "$($d.zoom)"
    $s = (Dump).videoHostSize
    Ctl 'mouse' @('wheel', [string][int]($s[0] * 0.8), [string][int]($s[1] * 0.25), '120', 'ctrl') | Out-Null
    $d = Settled { param($d) $d.zoom -gt 0.1 -and $d.panX -lt 0 -and $d.panY -gt 0 }
    Check 'ctrl+wheel zooms' ($d.zoom -gt 0.1) "$($d.zoom)"
    Check 'zoom at cursor pans toward cursor' ($d.panX -lt 0 -and $d.panY -gt 0) "panX=$($d.panX) panY=$($d.panY)"
    $before = $d.panX
    Ctl 'mouse' @('drag', '300', '300', '200', '300') | Out-Null
    $d = Settled { param($d) -not (Near $d.panX $before 0.0001) }
    Check 'drag pans when zoomed' (-not (Near $d.panX $before 0.0001)) "before=$before after=$($d.panX)"
    Act 'ZoomReset'
    $d = Settled { param($d) (Near $d.zoom 0 0.0001) -and (Near $d.panX 0 0.0001) -and (Near $d.panY 0 0.0001) -and $d.zoomText -eq 'Fit' }
    Check 'reset' ((Near $d.zoom 0 0.0001) -and (Near $d.panX 0 0.0001) -and (Near $d.panY 0 0.0001))
    Check 'zoomText Fit' ($d.zoomText -eq 'Fit') $d.zoomText
    Act 'SetZoom:100'; $d = Settled { param($d) $d.zoomText -eq '100%' }
    Check 'SetZoom:100 -> "100%"' ($d.zoomText -eq '100%') "$($d.zoomText) zoom=$($d.zoom)"
    Act 'ZoomReset'
}

Test 'fullscreen' {
    $d0 = Dump
    Act 'ToggleFullscreen'
    $d = WaitFor { param($d) $d.fullscreen } 3000 'fullscreen'
    Add-Type -AssemblyName System.Windows.Forms
    Check 'fullscreen' $d.fullscreen
    Check 'no controls visible' (-not $d.controlsVisible -and -not $d.playlistVisible) "controls=$($d.controlsVisible) playlist=$($d.playlistVisible)"
    Check 'video fills window' ($d.videoHostSize[0] -eq $d.clientSize[0] -and $d.videoHostSize[1] -eq $d.clientSize[1]) "host=$($d.videoHostSize) client=$($d.clientSize)"
    Check 'surface handle unchanged' ($d.surfaceHandle -eq $d.surfaceHandleAtStart)
    $shot = Join-Path $work 'fullscreen.png'
    Ctl 'screenshot' @($shot) | Out-Null
    Act 'ExitFullscreen'
    $d = WaitFor { param($d) -not $d.fullscreen } 3000 'windowed'
    Check 'bounds restored' (($d.windowBounds -join ',') -eq ($d0.windowBounds -join ',')) "$($d0.windowBounds) -> $($d.windowBounds)"
    Check 'controls back' $d.controlsVisible
    Check 'surface handle unchanged after exit' ($d.surfaceHandle -eq $d.surfaceHandleAtStart)
}

Test 'mouse' {
    $d = Dump; $s = $d.videoHostSize; $p0 = $d.paused
    $cx = [string][int]($s[0] / 2); $cy = [string][int]($s[1] / 2)
    Ctl 'mouse' @('click', $cx, $cy) | Out-Null
    $d = WaitFor { param($d) $d.paused -ne $p0 } 3000 'click toggles pause'
    Check 'click toggles pause' ($d.paused -ne $p0)
    $p1 = $d.paused
    Ctl 'mouse' @('dblclick', $cx, $cy) | Out-Null
    $d = WaitFor { param($d) $d.fullscreen } 3000 'dblclick fullscreen'
    Check 'double-click -> fullscreen' $d.fullscreen
    Check 'double-click keeps pause state' ($d.paused -eq $p1) "before=$p1 after=$($d.paused)"
    Act 'ExitFullscreen'
    if (-not (Dump).paused) { Act 'PlayPause' }
    $v0 = (Dump).volume
    Ctl 'mouse' @('wheel', '100', '100', '-120') | Out-Null
    $d = Settled { param($d) $d.volume -lt $v0 }
    Check 'wheel down lowers volume' ($d.volume -lt $v0) "$v0 -> $($d.volume)"
    Act 'SeekPercent:50'; WaitFor { param($d) $d.frame -eq 120 } 3000 'f120' | Out-Null
    Ctl 'mouse' @('wheel', '100', '100', '120', 'shift') | Out-Null
    $d = WaitFor { param($d) $d.frame -eq 121 } 3000 'shift wheel step'
    Check 'shift+wheel up = next frame' ($d.frame -eq 121) "$($d.frame)"
}

Test 'volume' {
    Act 'VolumeUp'; $v = (Dump).volume
    Act 'VolumeDown'; $d = Settled { param($d) Near $d.volume ($v - 5) 0.01 }; Check 'volume -5' (Near $d.volume ($v - 5) 0.01) "$v -> $($d.volume)"
    Act 'ToggleMute'; Check 'muted' ((Settled { param($d) $d.muted -eq $true }).muted -eq $true)
    Act 'ToggleMute'; Check 'unmuted' ((Settled { param($d) $d.muted -eq $false }).muted -eq $false)
}

Test 'playlist' {
    Act 'NextFile'; $d = WaitFor { param($d) $d.path -like '*clip02*' } 5000 'clip02'
    Check 'next -> clip02, index 1' ($d.playlistIndex -eq 1) "$($d.path)"
    Act 'LastFile'; $d = WaitFor { param($d) $d.path -like '*clip05*' } 5000 'clip05'
    Check 'last -> clip05' ($d.playlistIndex -eq 4)
    $r = Ctl 'action' @('NextFile'); Start-Sleep -Milliseconds 400; $d = Dump
    Check 'next at end without wrap (repeat off) stays (action disabled)' ($d.playlistIndex -eq 4 -and $r -match 'disabled') "$($d.playlistIndex) $r"
    Act 'FirstFile'; $d = WaitFor { param($d) $d.path -like '*clip01*' } 5000 'clip01'
    Check 'first -> clip01' ($d.playlistIndex -eq 0)
    Act 'TogglePlaylist'; $d = Dump; Check 'playlist panel shown' $d.playlistVisible
    Act 'TogglePlaylist'; $d = Dump; Check 'playlist panel hidden' (-not $d.playlistVisible)
}

Test 'tracks' {
    Act 'PlayIndex:3'
    $d = WaitFor { param($d) $d.path -like '*clip04*' -and $d.duration -gt 11 } 6000 'clip04'
    Check 'clip04 loaded' $true $d.path
    # aid / sid reach the dump only through mpv's property notifications.
    Act 'AudioTrack:2'; $d = Settled { param($d) "$($d.aid)" -eq '2' }; Check 'audio track 2' ("$($d.aid)" -eq '2') "aid=$($d.aid)"
    Act 'SubTrack:1'; $d = Settled { param($d) "$($d.sid)" -eq '1' }; Check 'subtitle track 1' ("$($d.sid)" -eq '1') "sid=$($d.sid)"
    $m = (Ctl 'menu' @('context')) | ConvertFrom-Json
    $json = $m | ConvertTo-Json -Depth 10 -Compress
    Check 'context menu lists chapters' ($json -match 'Middle') ''
    Check 'context menu lists jpn audio' ($json -match 'Japanese') ''
    Check 'menu shows Space for Play / Pause' ($json -match '"Play / Pause","shortcut":"Space') ''
    # Paused, so the position read after the jump is where it landed, not where playback has got to.
    $wasPaused = (Dump).paused; if (-not $wasPaused) { Act 'PlayPause' }
    Ctl 'mpv' @('seek', '1', 'absolute+exact') | Out-Null; WaitFor { param($d) Near $d.position 1 0.05 } 3000 'pos 1' | Out-Null
    Act 'NextChapter'; $d = WaitFor { param($d) $d.position -ge 3.99 } 3000 'chapter 2'
    Check 'next chapter -> 4 s' (Near $d.position 4 0.1) "$($d.position)"
    if (-not $wasPaused) { Act 'PlayPause' }
}

Test 'video' {
    Act 'SetRotate:90'; $d = Settled { param($d) "$($d.rotate)" -eq '90' }; Check 'rotate 90' ("$($d.rotate)" -eq '90')
    Act 'SetRotate:0'
    Act 'SetAspect:4:3'; $d = Settled { param($d) "$($d.aspect)" -match '4:3|1\.333' }; Check 'aspect 4:3' ("$($d.aspect)" -match '4:3|1\.333') "$($d.aspect)"
    Act 'SetAspect:no'
    Act 'FlipH'; $vf = Ctl 'get' @('vf'); Check 'hflip filter on' ($vf -match 'hflip') $vf
    Check 'copy-back hwdec while flipped' ((Dump).hwdec -match 'copy|^no$') "$((Dump).hwdec)"
    Act 'FlipH'; $vf = Ctl 'get' @('vf'); Check 'hflip filter off' ($vf -notmatch 'hflip') $vf
    Act 'ToggleFill'; Check 'panscan 1' (Near (Settled { param($d) Near $d.panscan 1 0.01 }).panscan 1 0.01)
    Act 'ToggleFill'; Check 'panscan 0' (Near (Settled { param($d) Near $d.panscan 0 0.01 }).panscan 0 0.01)
}

Test 'screenshot' {
    Act 'Screenshot'
    $f = Poll { Get-ChildItem "$work\shots" -Filter *.png -ErrorAction SilentlyContinue | Select-Object -First 1 } { param($f) $null -ne $f } 2000
    Check 'screenshot file written' ($null -ne $f) "$($f.Name)"
    if ($Clipboard) {
        Act 'CopyFrame'
        $img = & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -STA -Command "Add-Type -AssemblyName System.Windows.Forms; `$i=[Windows.Forms.Clipboard]::GetImage(); if(`$i){'{0}x{1}' -f `$i.Width,`$i.Height}else{'none'}"
        Check 'frame on clipboard' ($img -match '^\d+x\d+$') "$img"
    }
}

Test 'windowsize' {
    Act 'PlayIndex:0'; WaitFor { param($d) $d.path -like '*clip01*' } 5000 'clip01' | Out-Null
    Act 'WindowSize:50'
    $d = WaitFor { param($d) [math]::Abs($d.videoHostSize[0] - 640) -le 2 -and [math]::Abs($d.videoHostSize[1] - 360) -le 2 } 5000 'window 50%'
    Check 'WindowSize:50 -> 640x360 video area' ([math]::Abs($d.videoHostSize[0] - 640) -le 2 -and [math]::Abs($d.videoHostSize[1] - 360) -le 2) "$($d.videoHostSize)"
}

Test 'keys' {
    $r = Key @('Ctrl+Alt+F12'); Check 'unbound key reported' ($r -match 'unbound') $r
    $m0 = (Dump).muted
    $r = Key @('M'); Check 'M maps to ToggleMute' ($r -match 'ToggleMute' -and (Settled { param($d) $d.muted -ne $m0 }).muted -ne $m0) $r
    Key @('M') | Out-Null
    $r = Key @('Shift+.'); Check '">" (Shift+.) maps to NextFile' ($r -match 'NextFile') $r
    Act 'PrevFile'
}

Test 'delete' {
    $dir = Join-Path $work 'deltest'; New-Item -ItemType Directory -Force $dir | Out-Null
    Copy-Item (Join-Path $media 'clip03_vp9.webm') (Join-Path $dir 'vidflow-e2e-delete-test-a.webm')
    Copy-Item (Join-Path $media 'clip05_av1_noaudio.mkv') (Join-Path $dir 'vidflow-e2e-delete-test-b.mkv')
    Ctl 'open' @((Join-Path $dir 'vidflow-e2e-delete-test-a.webm')) | Out-Null
    WaitFor { param($d) $d.path -like '*delete-test-a*' } 5000 'delete test a' | Out-Null
    Act 'DeleteFileNoConfirm'
    $d = WaitFor { param($d) $d.path -like '*delete-test-b*' } 5000 'advanced to b'
    Check 'advanced to next file' ($d.path -like '*delete-test-b*') $d.path
    Check 'playlist shrank to 1' ($d.playlistCount -eq 1) "$($d.playlistCount)"
    $gone = Poll { -not (Test-Path (Join-Path $dir 'vidflow-e2e-delete-test-a.webm')) } { param($gone) $gone } 4000
    Check 'file moved to Recycle Bin' $gone
}

Test 'osdstatus' {
    Ctl 'open' @($clip1) | Out-Null
    WaitFor { param($d) $d.path -like '*clip01*' -and $d.frameCount -gt 0 } 5000 'clip01' | Out-Null
    Act 'ToggleOsdStatus'; $d = Dump
    Check 'O -> FullscreenOnly' ("$($d.osdStatus)" -eq 'FullscreenOnly') "$($d.osdStatus)"
    $lvl = GetWhen 'osd-level' '1'; Check 'windowed: osd-level 1' ($lvl -eq '1') $lvl
    Act 'ToggleFullscreen'; Settled { param($d) $d.fullscreen } | Out-Null
    $lvl = GetWhen 'osd-level' '3'; Check 'fullscreen: osd-level 3' ($lvl -eq '3') $lvl
    Act 'ExitFullscreen'; Settled { param($d) -not $d.fullscreen } | Out-Null
    Check 'back to 1 after fullscreen' ((GetWhen 'osd-level' '1') -eq '1')
    Act 'ToggleOsdStatus'; Check 'O -> Always: osd-level 3' ((GetWhen 'osd-level' '3') -eq '3' -and "$((Dump).osdStatus)" -eq 'Always')
    Act 'ToggleOsdStatus'; Check 'O -> Off: osd-level 1' ((GetWhen 'osd-level' '1') -eq '1' -and "$((Dump).osdStatus)" -eq 'Off')
}

Test 'abset' {
    if (-not (Dump).paused) { Act 'PlayPause' }
    Ctl 'mpv' @('seek', '1', 'absolute+exact') | Out-Null; WaitFor { param($d) Near $d.position 1 0.02 } 3000 'pos 1' | Out-Null
    Act 'AbSetA'; $d = Settled { param($d) Near $d.abA 1.0 0.02 }; Check 'AbSetA at 1.0' (Near $d.abA 1.0 0.02) "$($d.abA)"
    Ctl 'mpv' @('seek', '2', 'absolute+exact') | Out-Null; WaitFor { param($d) Near $d.position 2 0.02 } 3000 'pos 2' | Out-Null
    Act 'AbSetB'; $d = Settled { param($d) Near $d.abB 2.0 0.02 }; Check 'AbSetB at 2.0' (Near $d.abB 2.0 0.02) "$($d.abB)"
    Act 'AbNudge:B-1'; $d = WaitFor { param($d) Near $d.abB (2.0 - 1 / 30) 0.005 } 2000 'nudge B'
    Check 'nudge B -1 frame' (Near $d.abB (2.0 - 1 / 30) 0.005) "$($d.abB)"
    Act 'AbNudge:A+1'; $d = WaitFor { param($d) Near $d.abA (1.0 + 1 / 30) 0.005 } 2000 'nudge A'
    Check 'nudge A +1 frame' (Near $d.abA (1.0 + 1 / 30) 0.005) "$($d.abA)"
    Act 'AbLoopClear'; $d = Settled { param($d) $null -eq $d.abA -and $null -eq $d.abB }; Check 'cleared' ($null -eq $d.abA -and $null -eq $d.abB)
}

Test 'markers' {
    Ctl 'action' @('ClearMarkers') | Out-Null   # disabled (harmless) when there are none
    foreach ($t in 1, 3) { Ctl 'mpv' @('seek', "$t", 'absolute+exact') | Out-Null; WaitFor { param($d) Near $d.position $t 0.02 } 3000 "pos $t" | Out-Null; Act 'AddMarker' }
    $d = Dump; Check 'two markers' (@($d.markers).Count -eq 2) "$($d.markers)"
    Ctl 'mpv' @('seek', '0', 'absolute+exact') | Out-Null; WaitFor { param($d) Near $d.position 0 0.02 } 3000 'pos 0' | Out-Null
    Act 'NextMarker'; $d = WaitFor { param($d) Near $d.position 1 0.03 } 3000 'marker 1'; Check 'next marker -> 1.0' $true
    Act 'NextMarker'; $d = WaitFor { param($d) Near $d.position 3 0.03 } 3000 'marker 3'; Check 'next marker -> 3.0' $true
    Act 'PrevMarker'; $d = WaitFor { param($d) Near $d.position 1 0.03 } 3000 'back to 1'; Check 'previous marker -> 1.0' $true
    if ($Clipboard) {
        Act 'CopyMarkers'
        $clip = & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -STA -Command "Add-Type -AssemblyName System.Windows.Forms; [Windows.Forms.Clipboard]::GetText()"
        Check 'markers copied' ("$clip" -match '0:01\.000' -and "$clip" -match '0:03\.000') "$clip"
    }
    Act 'ClearMarkers'; Check 'markers cleared' (@((Dump).markers).Count -eq 0)
}

Test 'audiofx' {
    Act 'ToggleAudioNormalize'; $af = Ctl 'get' @('af'); Check 'normalize on (dynaudnorm)' ($af -match 'dynaudnorm') $af
    Act 'ToggleAudioNormalize'; $af = Ctl 'get' @('af'); Check 'normalize off' ($af -notmatch 'dynaudnorm') $af
    Act 'SetAudioChannels:mono'; Check 'mono downmix' ((Ctl 'get' @('audio-channels')) -match 'mono') (Ctl 'get' @('audio-channels'))
    Act 'SetAudioChannels:auto'; Check 'channels back to auto' ((Ctl 'get' @('audio-channels')) -match 'auto') (Ctl 'get' @('audio-channels'))
}

Test 'reverse' {
    Ctl 'mpv' @('seek', '6', 'absolute+exact') | Out-Null; WaitFor { param($d) Near $d.position 6 0.05 } 3000 'pos 6' | Out-Null
    Act 'ToggleReverse'
    # Backward playback spins up asynchronously (software decode, demuxer back-buffer): allow a few seconds.
    $dir = GetWhen 'play-direction' 'backward' 4000
    Check 'play-direction backward' ($dir -eq 'backward') $dir
    $d = WaitFor { param($d) $d.position -lt 5.5 -and -not $d.paused } 6000 'moving backwards'
    Check 'position moved backwards' ($d.position -lt 5.5) "pos=$($d.position) paused=$($d.paused)"
    Act 'ToggleReverse'
    $dir = GetWhen 'play-direction' 'forward' 4000
    Check 'forward again' ($dir -eq 'forward') $dir
    if (-not (Dump).paused) { Act 'PlayPause' }
}

Test 'borderless' {
    $d0 = Dump
    Act 'ToggleBorderless'; $d = Dump
    Check 'borderless on' ($d.borderless -eq $true)
    Check 'title bar removed, video area kept' ($d.captionVisible -eq $false -and ($d.clientSize -join ',') -eq ($d0.clientSize -join ',')) "caption=$($d.captionVisible) $($d0.clientSize) -> $($d.clientSize)"
    Check 'surface handle unchanged' ($d.surfaceHandle -eq $d.surfaceHandleAtStart)
    Act 'ToggleFullscreen'; Settled { param($d) $d.fullscreen } | Out-Null
    Act 'ExitFullscreen'; $d = Settled { param($d) -not $d.fullscreen }
    Check 'still borderless after fullscreen' ($d.borderless -eq $true -and -not $d.fullscreen)
    Act 'ToggleBorderless'; $d = Settled { param($d) $d.borderless -eq $false -and ($d.clientSize -join ',') -eq ($d0.clientSize -join ',') }
    Check 'borderless off, size restored' ($d.borderless -eq $false -and ($d.clientSize -join ',') -eq ($d0.clientSize -join ',')) "$($d.clientSize)"
    Act 'ToggleKeepAspect'; Check 'keep aspect on' ((Dump).keepAspect -eq $true)
    Act 'ToggleKeepAspect'
}

Test 'playlistfiles' {
    $pl = Join-Path $work 'list.m3u8'
    # A path relative to the playlist's folder, however deep the work folder sits under the root.
    $up = (@($work.Substring($root.Length).Trim('\').Split('\') | ForEach-Object { '..' }) -join '\')
    "#EXTM3U`n#EXTINF:5,third`n$up\testmedia\clip03_vp9.webm`n$clip1`n" | Set-Content -Encoding UTF8 $pl
    Ctl 'open' @($pl) | Out-Null
    $d = WaitFor { param($d) $d.path -like '*clip03*' } 5000 'playlist file'
    Check 'm3u8 expanded to 2 items' ($d.playlistCount -eq 2) "$($d.playlistCount) $($d.playlist)"
    $nested = Join-Path $work 'nested'
    New-Item -ItemType Directory -Force "$nested\day1\cam", "$nested\day2" | Out-Null
    Copy-Item (Join-Path $media 'clip05_av1_noaudio.mkv') "$nested\day1\cam\a.mkv"
    Copy-Item (Join-Path $media 'clip03_vp9.webm') "$nested\day2\b.webm"
    Ctl 'open' @($nested) | Out-Null
    $d = WaitFor { param($d) $d.playlistCount -eq 2 -and $d.path -like '*nested*' } 5000 'recursive folder'
    Check 'folder opened recursively' ($d.playlistCount -eq 2) "$($d.playlist)"
    Ctl 'open' @($clip1) | Out-Null
    WaitFor { param($d) $d.path -like '*clip01*' -and $d.playlistCount -eq 5 } 5000 'clip01 folder' | Out-Null
    Act 'SortPlaylist:Size'; $d = Dump
    $sizes = @($d.playlist | ForEach-Object { (Get-Item $_).Length })
    Check 'sort by size ascending' (($sizes -join ',') -eq (($sizes | Sort-Object) -join ',')) ($sizes -join ',')
    Check 'current item kept' ($d.path -like '*clip01*')
    Act 'TogglePlaylistSortOrder'; $d = Dump
    $sizes = @($d.playlist | ForEach-Object { (Get-Item $_).Length })
    Check 'descending' (($sizes -join ',') -eq (($sizes | Sort-Object -Descending) -join ',')) ($sizes -join ',')
    Act 'TogglePlaylistSortOrder'; Act 'SortPlaylist:Name'
}

Test 'launchopts' {
    Ctl 'open' @('--start=2', '--speed=0.5', '--paused', (Join-Path $media 'clip03_vp9.webm')) | Out-Null
    $d = WaitFor { param($d) $d.path -like '*clip03*' -and (Near $d.position 2 0.06) } 6000 'start 2'
    Check '--start=2' (Near $d.position 2 0.06) "$($d.position)"
    Check '--speed=0.5' (Near $d.speed 0.5 0.001) "$($d.speed)"
    Check '--paused' ($d.paused -eq $true)
    Act 'SpeedReset'
}

# Feature suites: each tools\e2e.d\NN-name.ps1 adds Test blocks using the helpers above
# (Ctl, Dump, Act, Key, WaitFor, Near, Check, Test, $media, $work, $clip1, $clip4, $settingsPath).
$suiteDir = Join-Path $PSScriptRoot 'e2e.d'
if (Test-Path $suiteDir) {
    foreach ($suite in Get-ChildItem $suiteDir -Filter *.ps1 | Sort-Object Name) { . $suite.FullName }
}

Test 'quit' {
    Ctl 'open' @($clip1) | Out-Null
    WaitFor { param($d) $d.path -like '*clip01*' } 5000 'clip01' | Out-Null
    Act 'SetRepeat:One'
    # 6 s = the 5 s VidFlow gives a stuck engine before abandoning it (MpvPlayer) + 1 s of work, and
    # only that second stretches with the load measured meanwhile.
    $load = [E2ELoad]::Mark(); $sw = [Diagnostics.Stopwatch]::StartNew()
    Ctl 'quit' | Out-Null
    $exited = $proc.WaitForExit(15000)
    $secs = $sw.Elapsed.TotalSeconds; $limit = 5 + [E2ELoad]::Factor($load)
    Check 'exits within 6 s (the last 1 s stretched by load)' ($exited -and $secs -le $limit) $(if ($exited) { "after {0:N1} s, limit {1:N1} s" -f $secs, $limit } else { 'still running after 15 s' })
    $ini = Get-Content $settingsPath -Raw
    Check 'settings saved (Repeat=One)' ($ini -match 'Repeat=One')
    Check 'recent files saved' ($ini -match 'recent\.0=.*clip01')
}

Test 'single-instance' {
    $si = Join-Path $work 'si.ini'; Copy-Item $settingsPath $si
    $a = Start-Process -FilePath $Exe -ArgumentList @('--pipe', "$Pipe-SI", '--settings', "`"$si`"", "`"$clip1`"") -PassThru
    # The second launch must find the first one serving its pipe (it has opened clip01 by then), and
    # start more than 1.5 s after it: launches closer together are one Explorer multi-select
    # (MainForm OpenBurstTicks), whose files are queued instead of opened. A fixed gap, not a wait.
    & { $Pipe = "$Pipe-SI"; Wait-Pipe 15000 } | Out-Null
    $gap = 3000 - ([DateTime]::Now - $a.StartTime).TotalMilliseconds
    if ($gap -gt 0) { Start-Sleep -Milliseconds ([int]$gap) }
    $b = Start-Process -FilePath $Exe -ArgumentList @('--pipe', "$Pipe-SI", '--settings', "`"$si`"", "`"$clip4`"") -PassThru
    $bExited = $b.WaitForExit((Scaled 5000))
    $title = Poll { $a.Refresh(); $a.MainWindowTitle } { param($t) $t -like 'clip04*' } 2000
    Check 'second launch exits (handed off)' $bExited
    Check 'first window now plays clip04' ($title -like 'clip04*') $title
    $a.CloseMainWindow() | Out-Null
    Check 'first instance closes' ($a.WaitForExit((Scaled 6000)))
    foreach ($p in $a, $b) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }

    # Explorer multi-select: one process per file, all started together -> one window, all queued.
    $burst = Join-Path $work 'burst'; New-Item -ItemType Directory -Force $burst | Out-Null
    $files = @('x1.mkv', 'x2.webm', 'x3.mkv')
    Copy-Item (Join-Path $media 'clip05_av1_noaudio.mkv') "$burst\x1.mkv"
    Copy-Item (Join-Path $media 'clip03_vp9.webm') "$burst\x2.webm"
    Copy-Item (Join-Path $media 'clip05_av1_noaudio.mkv') "$burst\x3.mkv"
    $bi = Join-Path $work 'burst.ini'; (Get-Content $settingsPath) + 'AutoLoadFolder=false' | Set-Content -Encoding UTF8 $bi
    $procs = foreach ($f in $files) { Start-Process -FilePath $Exe -ArgumentList @('--pipe', "$Pipe-B", '--settings', "`"$bi`"", "`"$burst\$f`"") -PassThru }
    # The two later launches hand their files over and exit once the first window serves the pipe.
    $alive = Poll { @($procs | Where-Object { -not $_.HasExited }) } { param($left) $left.Count -le 1 } 6000 200
    $alive = @($alive)
    Check 'multi-select: one window remains' ($alive.Count -eq 1) "alive=$($alive.Count)"
    if ($alive.Count -ge 1) {
        $title = Poll { $alive[0].Refresh(); $alive[0].MainWindowTitle } { param($t) $t -match '\[\d/3\]' } 2000
        Check 'multi-select: all three queued' ($title -match '\[\d/3\]') $title
        foreach ($p in $alive) { $p.CloseMainWindow() | Out-Null; $p.WaitForExit((Scaled 6000)) | Out-Null }
    }
    foreach ($p in $procs) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
}

if (-not $proc.HasExited -and -not $KeepOpen) {
    try { Ctl 'quit' | Out-Null } catch { }
    if (-not $proc.WaitForExit((Scaled 5000))) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}
$crash = Join-Path $env:LOCALAPPDATA 'VidFlow\crash.log'
$fail = @($results | Where-Object { -not $_.Pass })
Write-Host ""
Write-Host ("{0} checks, {1} passed, {2} failed" -f $results.Count, ($results.Count - $fail.Count), $fail.Count) -ForegroundColor $(if ($fail.Count) { 'Red' } else { 'Green' })
if ($fail.Count) { $fail | Format-Table -AutoSize | Out-String -Width 220 | Write-Host }
if (Test-Path $crash) { Write-Host "crash.log exists: $crash" -ForegroundColor Yellow }
$results | ConvertTo-Json -Depth 3 | Set-Content -Encoding UTF8 (Join-Path $work 'results.json')
exit $fail.Count
