#Requires -Version 7.0
<#
.SYNOPSIS
  Speed benchmarks (cold start, next clip, memory, CPU, back-step, seek) and the phone-footage
  release gate for VidFlow, measured through the automation pipe (docs\SPEC.md "Automation").
.DESCRIPTION
  Everything runs on a hidden desktop (obj\tools\RunHidden.exe, desktop "VidFlowBench") with
  mpv's audio output set to null, so nothing appears on screen or plays through the speakers.
  Media comes from tools\make_gatemedia.ps1, which this script runs first (it keeps existing files).

  -Suite speed   timings over -Runs runs each, median / p95 / min / max (docs\BENCHMARKS.md)
  -Suite gate    plays every testmedia\gate sample, screenshots, rotation / aspect / HDR / error
                 checks (docs\RELEASE-GATE.md)
  Results: <Out>\bench-results.json + .md, <Out>\gate-results.json + .md, screenshots <Out>\shots.
  Timings are polled over the pipe every -PollMs (plus ~2 ms per request), so they carry a few
  ms of granularity. Other work on the machine skews them: -QuietPct waits for a quieter
  moment before each section, and every section reports how busy the machine was.
.EXAMPLE
  .\tools\bench.ps1                                   # speed suite, dev build
  .\tools\bench.ps1 -Suite gate -Out obj\work\bench-gate
  .\tools\bench.ps1 -Only cold,next -Runs 10 -Exe dist\VidFlow.exe -QuietPct 40
  .\tools\bench.ps1 -Suite gate -Only hdr10,rot270
  .\tools\bench.ps1 -Only next -ExtraIni 'HwDec=no'   # diagnostic variant
#>
param(
    [string]$Exe,
    [string]$Pipe = 'VidFlow-bench',
    [ValidateSet('speed', 'gate', 'all')][string]$Suite = 'speed',
    [string[]]$Only,          # speed sections (cold, next, memory, cpu, backstep, seek) or gate case ids (hdr10, rot090, ...)
    [int]$Runs = 7,
    [int]$PollMs = 4,
    [string]$Out,
    [string]$Desktop = 'VidFlowBench',
    [string[]]$ExtraIni = @(),  # extra settings lines for diagnostic variants (must keep ao=null)
    [int]$QuietPct = 0,         # before each speed section wait until the machine is less busy than this (% of all cores); 0 = don't wait
    [int]$QuietWaitSec = 300    # ...but no longer than this
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Exe) { $Exe = Join-Path $root 'dist\dev\VidFlow.exe' }
if (-not $Out) { $Out = Join-Path $root 'obj\bench' }

# Re-run on the hidden desktop: every window this script opens stays off the user's screen.
if ($env:VIDFLOW_HIDDEN -ne '1') {
    & (Join-Path $root 'tools\ensure-runhidden.ps1')
    $set = switch ($Suite) { 'speed' { 'bench' } 'gate' { 'gate' } default { 'all' } }
    & (Join-Path $root 'tools\make_gatemedia.ps1') -Set $set
    New-Item -ItemType Directory -Force $Out | Out-Null
    $env:VIDFLOW_HIDDEN = '1'
    $fwd = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-Exe', (Resolve-Path $Exe).Path, '-Pipe', $Pipe,
        '-Suite', $Suite, '-Runs', $Runs, '-PollMs', $PollMs, '-Out', (Resolve-Path $Out).Path, '-Desktop', $Desktop)
    if ($Only) { $fwd += '-Only'; $fwd += ($Only -join ',') }
    if ($ExtraIni) { $fwd += '-ExtraIni'; $fwd += ($ExtraIni -join ';;') }
    if ($QuietPct -gt 0) { $fwd += '-QuietPct'; $fwd += $QuietPct; $fwd += '-QuietWaitSec'; $fwd += $QuietWaitSec }
    $shell = (Get-Process -Id $PID).Path
    & (Join-Path $root 'obj\tools\RunHidden.exe') --desktop $Desktop --wait --timeout 7200000 -- $shell @fwd
    $code = $LASTEXITCODE
    $env:VIDFLOW_HIDDEN = $null
    exit $code
}

if ($Only -and $Only.Count -eq 1 -and $Only[0] -match ',') { $Only = $Only[0].Split(',') }
if ($ExtraIni.Count -eq 1 -and $ExtraIni[0] -match ';;') { $ExtraIni = $ExtraIni[0] -split ';;' }
$Exe = (Resolve-Path $Exe).Path
$media = Join-Path $root 'testmedia'
$gateDir = Join-Path $media 'gate'
$benchDir = Join-Path $gateDir 'bench'
$shots = Join-Path $Out 'shots'
$logs = Join-Path $Out 'logs'
New-Item -ItemType Directory -Force $Out, $shots, $logs | Out-Null
$enc = [Text.UTF8Encoding]::new($false)
$inv = [Globalization.CultureInfo]::InvariantCulture
$cores = [Environment]::ProcessorCount

if (-not ('BenchNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class BenchNative {
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
    [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    // Screenshot analysis on packed RGB24. Returns
    // [w, h, bx0, by0, bx1, by1, redX, redY, redN, greenX, greenY, greenN, blueX, blueY, blueN,
    //  whiteLuma, backgroundLuma, cornerLuma]: the bounding box of non-black pixels (the
    // displayed video inside letterbox bars), centroids of the red/green/blue corner markers,
    // the mean luma of the centre patch, the median luma inside the box (the grey background)
    // and the mean luma near the bottom-right corner.
    public static double[] Analyze(byte[] rgb, int w, int h) {
        int bx0 = w, by0 = h, bx1 = -1, by1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                int i = (y * w + x) * 3;
                if (Math.Max(rgb[i], Math.Max(rgb[i + 1], rgb[i + 2])) > 24) {
                    if (x < bx0) bx0 = x; if (x > bx1) bx1 = x; if (y < by0) by0 = y; if (y > by1) by1 = y;
                }
            }
        var res = new double[18];
        res[0] = w; res[1] = h;
        if (bx1 < 0) return res;
        res[2] = bx0; res[3] = by0; res[4] = bx1; res[5] = by1;
        double rx = 0, ry = 0, rn = 0, gx = 0, gy = 0, gn = 0, ux = 0, uy = 0, un = 0;
        var hist = new long[256];
        long total = 0;
        for (int y = by0; y <= by1; y++)
            for (int x = bx0; x <= bx1; x++) {
                int i = (y * w + x) * 3;
                int r = rgb[i], g = rgb[i + 1], b = rgb[i + 2];
                int l = (int)Math.Min(255, 0.2126 * r + 0.7152 * g + 0.0722 * b);
                hist[l]++; total++;
                if (r > 120 && r > g + 60 && r > b + 60) { rx += x; ry += y; rn++; }
                else if (g > 120 && g > r + 60 && g > b + 60) { gx += x; gy += y; gn++; }
                else if (b > 120 && b > r + 60 && b > g + 30) { ux += x; uy += y; un++; }
            }
        double bw = bx1 - bx0 + 1, bh = by1 - by0 + 1;
        if (rn > 0) { res[6] = (rx / rn - bx0) / bw; res[7] = (ry / rn - by0) / bh; } res[8] = rn;
        if (gn > 0) { res[9] = (gx / gn - bx0) / bw; res[10] = (gy / gn - by0) / bh; } res[11] = gn;
        if (un > 0) { res[12] = (ux / un - bx0) / bw; res[13] = (uy / un - by0) / bh; } res[14] = un;
        double sum = 0; long cnt = 0;
        int cx0 = (int)(bx0 + bw * 0.46), cx1 = (int)(bx0 + bw * 0.54), cy0 = (int)(by0 + bh * 0.46), cy1 = (int)(by0 + bh * 0.54);
        for (int y = cy0; y <= cy1; y++)
            for (int x = cx0; x <= cx1; x++) {
                int i = (y * w + x) * 3;
                sum += 0.2126 * rgb[i] + 0.7152 * rgb[i + 1] + 0.0722 * rgb[i + 2]; cnt++;
            }
        res[15] = cnt > 0 ? sum / cnt : 0;
        // Bottom-right corner (the 1000-nit highlight patch on the HDR10 sample).
        sum = 0; cnt = 0;
        cx0 = (int)(bx0 + bw * 0.90); cx1 = (int)(bx0 + bw * 0.98); cy0 = (int)(by0 + bh * 0.90); cy1 = (int)(by0 + bh * 0.98);
        for (int y = cy0; y <= cy1; y++)
            for (int x = cx0; x <= cx1; x++) {
                int i = (y * w + x) * 3;
                sum += 0.2126 * rgb[i] + 0.7152 * rgb[i + 1] + 0.0722 * rgb[i + 2]; cnt++;
            }
        res[17] = cnt > 0 ? sum / cnt : 0;
        long acc = 0;
        for (int l = 0; l < 256; l++) { acc += hist[l]; if (acc * 2 >= total) { res[16] = l; break; } }
        return res;
    }
}
'@
}
[void][BenchNative]::timeBeginPeriod(1)   # 1 ms sleeps for polling (process-local on Windows 10 2004+)

# ---- pipe + process helpers ------------------------------------------------------------------

function Ctl([string]$verb, [string[]]$a = @(), [int]$connectMs = 5000, [string]$pipeName = $Pipe) {
    $c = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
    try {
        $c.Connect($connectMs)
        $w = [IO.StreamWriter]::new($c, $enc, 4096, $true)
        $w.Write("$verb`n"); foreach ($x in $a) { $w.Write("$x`n") }; $w.Write("<<END>>`n"); $w.Flush()
        $r = [IO.StreamReader]::new($c, $enc, $false, 4096, $true)
        $lines = [Collections.Generic.List[string]]::new()
        while ($null -ne ($line = $r.ReadLine()) -and $line -ne '<<END>>') { $lines.Add($line) }
        return ($lines -join "`n")
    } finally { $c.Dispose() }
}
function Dump { (Ctl 'dump') | ConvertFrom-Json }
function Prop([string]$name) { Ctl 'get' @($name) }
function Num($v) { if ($null -eq $v) { return $null }; $d = 0.0; if ([double]::TryParse("$v", [Globalization.NumberStyles]::Float, $inv, [ref]$d)) { return $d }; return $null }
function Sleep-Ms([int]$ms) { [Threading.Thread]::Sleep($ms) }
function R1($v) { if ($null -eq $v) { return $null }; [math]::Round([double]$v, 1) }

$script:started = [Collections.Generic.List[Diagnostics.Process]]::new()

# Test settings. Later lines replace earlier ones with the same key; -ExtraIni comes last
# (diagnostic variants, e.g. 'HwDec=no' or 'ExtraMpvOptions=esc:ao=null\nvo=null').
function Write-Ini([string]$path, [string[]]$extra = @()) {
    $lines = @('AutoFitWindow=false', 'WindowX=40', 'WindowY=40', 'WindowWidth=1280', 'WindowHeight=800', 'AutoLoadFolder=false',
        'SingleInstance=false', 'QuickStartShown=true', 'ResumePlayback=false', 'Repeat=One', "ScreenshotFolder=$shots", 'ExtraMpvOptions=ao=null') + $extra + $ExtraIni
    $map = [ordered]@{}
    foreach ($l in $lines) { if ($l -match '^([^=]+)=') { $map[$Matches[1]] = $l } }
    if (-not ($map['ExtraMpvOptions'] -match 'ao=null')) { throw 'ExtraMpvOptions must keep ao=null (no sound on the hidden desktop)' }
    $map.Values | Set-Content -Encoding UTF8 $path
}

function Start-App([string[]]$files, [string]$ini, [string]$log, [string]$pipeName = $Pipe) {
    $psi = [Diagnostics.ProcessStartInfo]::new($Exe)
    foreach ($a in @('--automation', '--pipe', $pipeName, '--settings', $ini, '--log', $log) + $files) { $psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false
    $p = [Diagnostics.Process]::Start($psi)
    $script:started.Add($p)
    return $p
}

$script:vidflowCpuMs = 0.0   # CPU time of VidFlow processes stopped so far (for the per-section load split)

function Stop-App([Diagnostics.Process]$p, [string]$pipeName = $Pipe) {
    if ($null -eq $p -or $p.HasExited) { return $null }
    try { $p.Refresh(); $script:vidflowCpuMs += $p.TotalProcessorTime.TotalMilliseconds } catch { }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try { $null = Ctl 'quit' @() 1000 $pipeName } catch { }
    if ($p.WaitForExit(6000)) { return $sw.Elapsed.TotalMilliseconds }
    try { $p.Kill() } catch { }
    $p.WaitForExit(3000) | Out-Null
    return $null
}

function Stop-All {
    foreach ($p in $script:started) {
        try { if (-not $p.HasExited) { $p.Kill(); $p.WaitForExit(3000) | Out-Null } } catch { }
    }
}

function Wait-Pipe([string]$pipeName = $Pipe, [int]$ms = 20000) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $ms) {
        try { $null = Ctl 'dump' @() 50 $pipeName; return $true } catch { Sleep-Ms 20 }
    }
    throw "VidFlow did not open its control pipe $pipeName"
}

# Polls until $cond returns $true; elapsed ms on $sw at that moment, or $null on timeout.
function Poll([Diagnostics.Stopwatch]$sw, [scriptblock]$cond, [int]$timeoutMs) {
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $ok = $false
        try { $ok = [bool](& $cond) } catch { }
        if ($ok) { return $sw.Elapsed.TotalMilliseconds }
        Sleep-Ms $PollMs
    }
    return $null
}

function Same-File([string]$a, [string]$b) { return $a -and $b -and [string]::Equals($a, $b, [StringComparison]::OrdinalIgnoreCase) }

# Waits until mpv has the file loaded (dump frame >= 0, or a known duration for audio) and has
# finished starting playback (seeking = no). Returns the last dump.
function Wait-Started([string]$path, [int]$ms = 15000, [switch]$Audio) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $script:last = $null
    $t = Poll $sw {
        $d = Dump; $script:last = $d
        if (-not (Same-File $d.path $path)) { return $false }
        if ($d.lastError) { return $true }
        $loaded = if ($Audio) { $null -ne $d.duration } else { $null -ne $d.frame -and $d.frame -ge 0 }
        return $loaded -and (Prop 'seeking') -eq 'no'
    } $ms
    if ($null -eq $t) { throw "timeout waiting for $([IO.Path]::GetFileName($path)); last dump: $(($script:last | ConvertTo-Json -Compress -Depth 3))" }
    return $script:last
}

# Exact seek from a paused state, waiting until it has landed. While a seek runs mpv already
# reports the target as time-pos, so "landed" = seeking is "no" again with the position in
# [t - 6 ms, t + 60 ms] (exact seeks show the first frame at/after t; VFR gaps reach ~50 ms),
# and either mpv was seen seeking or the position moved.
function Seek-Exact([double]$t, [int]$ms = 30000) {
    $ts = $t.ToString('0.######', $inv)
    $script:seekOld = [double](Dump).position
    $script:seekSeen = $false
    $null = Ctl 'mpv' @('no-osd', 'seek', $ts, 'absolute+exact')
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $inWindow = { param($pos) $pos -ge $t - 0.006 -and $pos -le $t + 0.06 }
    $r = Poll $sw {
        if ((Prop 'seeking') -eq 'yes') { $script:seekSeen = $true; return $false }
        $pos = [double](Dump).position
        return (& $inWindow $pos) -and ($script:seekSeen -or [math]::Abs($pos - $script:seekOld) -gt 0.0001)
    } $ms
    # Already standing on the target (nothing to move) and the seek was too quick to observe.
    if ($null -eq $r -and (& $inWindow ([double](Dump).position)) -and (Prop 'seeking') -eq 'no') { return }
    if ($null -eq $r) { throw "seek to $ts did not finish" }
}

function Ensure-Paused { $d = Dump; if (-not $d.paused) { $null = Ctl 'action' @('PlayPause') }; $null = Poll ([Diagnostics.Stopwatch]::StartNew()) { (Dump).paused } 3000 }
function Ensure-Playing { $d = Dump; if ($d.paused) { $null = Ctl 'action' @('PlayPause') }; $null = Poll ([Diagnostics.Stopwatch]::StartNew()) { -not (Dump).paused } 3000 }

# ---- statistics, machine info ----------------------------------------------------------------

function Stat([object[]]$xs) {
    $v = @($xs | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ } | Sort-Object)
    if ($v.Count -eq 0) { return [ordered]@{ n = 0; median = $null; p95 = $null; min = $null; max = $null; mean = $null } }
    $mid = [int][math]::Floor(($v.Count - 1) / 2)
    $median = if ($v.Count % 2) { $v[$mid] } else { ($v[$mid] + $v[$mid + 1]) / 2 }
    $p95 = $v[[int][math]::Ceiling(0.95 * $v.Count) - 1]   # nearest rank
    return [ordered]@{ n = $v.Count; median = $median; p95 = $p95; min = $v[0]; max = $v[-1]; mean = ($v | Measure-Object -Average).Average }
}

function SysTimes { $i = 0L; $k = 0L; $u = 0L; [void][BenchNative]::GetSystemTimes([ref]$i, [ref]$k, [ref]$u); return @($i, $k, $u) }
# Machine-wide CPU busy % between two SysTimes samples (all processes, VidFlow included).
function Busy($a, $b) { $idle = $b[0] - $a[0]; $all = ($b[1] - $a[1]) + ($b[2] - $a[2]); if ($all -le 0) { return $null }; return 100.0 * ($all - $idle) / $all }

# Section bookkeeping: how busy the machine was while a section ran, split into VidFlow, this
# script (polling) and everything else (other users of the machine), as % of all cores.
function Section([string]$name) {
    Write-Host "== $name" -ForegroundColor Cyan
    if ($QuietPct -gt 0) {
        # No VidFlow runs between sections, so this is the load other users put on the machine.
        $wait = [Diagnostics.Stopwatch]::StartNew()
        while ($true) {
            $a = SysTimes; Sleep-Ms 3000; $busy = Busy $a (SysTimes)
            if ($busy -lt $QuietPct) { Write-Host ("  machine {0:N0}% busy (waited {1:N0} s)" -f $busy, $wait.Elapsed.TotalSeconds); break }
            if ($wait.Elapsed.TotalSeconds -ge $QuietWaitSec) { Write-Host ("  machine still {0:N0}% busy after {1:N0} s; measuring anyway" -f $busy, $wait.Elapsed.TotalSeconds) -ForegroundColor Yellow; break }
        }
    }
    return @{ sys = (SysTimes); self = [Diagnostics.Process]::GetCurrentProcess().TotalProcessorTime.TotalMilliseconds; vidflow = $script:vidflowCpuMs; sw = [Diagnostics.Stopwatch]::StartNew() }
}
function Load([hashtable]$s) {
    $wall = $s.sw.Elapsed.TotalMilliseconds * $cores
    $machine = Busy $s.sys (SysTimes)
    $vidflow = 100 * ($script:vidflowCpuMs - $s.vidflow) / $wall
    $self = 100 * ([Diagnostics.Process]::GetCurrentProcess().TotalProcessorTime.TotalMilliseconds - $s.self) / $wall
    return [ordered]@{ machineBusyPct = [math]::Round($machine, 1); vidflowPct = [math]::Round($vidflow, 1); benchScriptPct = [math]::Round($self, 1)
        otherPct = [math]::Round([math]::Max(0, $machine - $vidflow - $self), 1) }
}

function Machine-Info {
    $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
    $gpus = @(Get-CimInstance Win32_VideoController | ForEach-Object { "$($_.Name) (driver $($_.DriverVersion))" })
    $os = Get-CimInstance Win32_OperatingSystem
    $ubr = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').UBR
    $disk = $null
    try { $d = Get-Partition -DriveLetter ($media.Substring(0, 1)) | Get-Disk; $disk = "$($d.FriendlyName) ($($d.BusType))" } catch { }
    $power = $null
    try { $power = ((powercfg /getactivescheme) -replace '^.*\((.*)\).*$', '$1') } catch { }
    $battery = $null
    try { $b = Get-CimInstance Win32_Battery | Select-Object -First 1; if ($b) { $battery = if ($b.BatteryStatus -eq 2) { 'AC power' } else { "battery status $($b.BatteryStatus) ($($b.EstimatedChargeRemaining)%)" } } } catch { }
    $exeItem = Get-Item $Exe
    return [ordered]@{
        cpu = "$($cpu.Name.Trim()) ($($cpu.NumberOfCores) cores / $($cpu.NumberOfLogicalProcessors) threads)"
        gpu = $gpus
        ramGB = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
        windows = "$($os.Caption) $($os.Version) build $($os.BuildNumber).$ubr"
        mediaDisk = $disk
        powerPlan = $power
        power = $battery
        exe = $Exe
        exeBuilt = $exeItem.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')
        exeSha256 = (Get-FileHash $Exe -Algorithm SHA256).Hash.Substring(0, 16)
        measured = (Get-Date).ToString('yyyy-MM-dd HH:mm')
    }
}

# Timestamps (HH:mm:ss.fff) of known startup lines in a VidFlow log, as ms after process start.
function Log-Milestones([string]$log, [datetime]$start) {
    $m = [ordered]@{}
    if (-not (Test-Path $log)) { return $m }
    $map = @{ 'libmpv loaded' = 'libmpvLoaded'; 'mpv ready' = 'mpvReady'; 'control pipe listening' = 'pipeListening' }
    foreach ($line in Get-Content $log) {
        foreach ($k in $map.Keys) {
            if ($line -match "^(\d\d:\d\d:\d\d\.\d{3}) INFO $k" -and -not $m.Contains($map[$k])) {
                $t = [datetime]::ParseExact($start.ToString('yyyy-MM-dd') + ' ' + $Matches[1], 'yyyy-MM-dd HH:mm:ss.fff', $inv)
                $m[$map[$k]] = ($t - $start).TotalMilliseconds
            }
        }
    }
    return $m
}

$results = [ordered]@{}
$clip1 = Join-Path $media 'clip01_h264_720p30.mp4'
$b1 = Join-Path $benchDir 'b1_h264_1080p30.mp4'
$b2 = Join-Path $benchDir 'b2_h264_1080p60.mp4'
$b3 = Join-Path $benchDir 'b3_hevc10_2160p30_gop250.mp4'
$b4 = Join-Path $benchDir 'b4_vp9_1080p30.webm'
$b5 = Join-Path $benchDir 'b5_av1_1080p30.mkv'
function Want([string]$section) { return (-not $Only) -or ($Only -contains $section) }

# ---- speed: cold start -----------------------------------------------------------------------
# Process start -> window -> pipe answering -> file loaded (dump frame >= 0) -> first frame
# rendered (mpv video-frame-info available) -> playback started (mpv seeking = no).

function Bench-Cold {
    $sys0 = Section 'cold start (H.264 1080p30)'
    $ini = Join-Path $Out 'bench.ini'; Write-Ini $ini
    $rows = [Collections.Generic.List[object]]::new()
    for ($i = 1; $i -le $Runs; $i++) {
        Sleep-Ms 1500
        $log = Join-Path $logs "cold-$i.log"; Remove-Item $log -ErrorAction SilentlyContinue
        $t0 = [datetime]::Now
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $p = Start-App @($b1) $ini $log
        $r = [ordered]@{ run = $i; window = $null; pipe = $null; loaded = $null; firstFrame = $null; started = $null }
        while ($sw.ElapsedMilliseconds -lt 30000 -and -not ($r.window -and $r.loaded -and $r.firstFrame -and $r.started)) {
            if (-not $r.window) { $p.Refresh(); if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $r.window = $sw.Elapsed.TotalMilliseconds } }
            try {
                $raw = Ctl 'dump' @() 15
                $now = $sw.Elapsed.TotalMilliseconds
                if (-not $r.pipe) { $r.pipe = $now }
                $d = $raw | ConvertFrom-Json
                if (-not $r.loaded -and $null -ne $d.frame -and $d.frame -ge 0) { $r.loaded = $now }
                if (-not $r.firstFrame -and (Prop 'video-frame-info') -ne 'error') { $r.firstFrame = $sw.Elapsed.TotalMilliseconds }
                if ($r.loaded -and -not $r.started -and (Prop 'seeking') -eq 'no') { $r.started = $sw.Elapsed.TotalMilliseconds }
            } catch { }
            Sleep-Ms $PollMs
        }
        $r.hwdec = try { (Dump).hwdecCurrent } catch { $null }
        $r.log = Log-Milestones $log $p.StartTime              # ms after the kernel created the process
        $r.processCreated = ($p.StartTime - $t0).TotalMilliseconds  # Process.Start call -> process creation
        $r.exit = Stop-App $p
        $rows.Add($r)
        Write-Host ("  run {0}: window {1} pipe {2} loaded {3} first frame {4} started {5} ms (exit {6} ms)" -f $i, (R1 $r.window), (R1 $r.pipe), (R1 $r.loaded), (R1 $r.firstFrame), (R1 $r.started), (R1 $r.exit))
    }
    $results.cold = [ordered]@{
        clip = [IO.Path]::GetFileName($b1); runs = $rows
        window = Stat ($rows | ForEach-Object { $_.window }); pipe = Stat ($rows | ForEach-Object { $_.pipe })
        loaded = Stat ($rows | ForEach-Object { $_.loaded }); firstFrame = Stat ($rows | ForEach-Object { $_.firstFrame })
        started = Stat ($rows | ForEach-Object { $_.started }); exit = Stat ($rows | ForEach-Object { $_.exit })
        logLibmpvLoaded = Stat ($rows | ForEach-Object { $_.log.libmpvLoaded }); logMpvReady = Stat ($rows | ForEach-Object { $_.log.mpvReady })
        logPipeListening = Stat ($rows | ForEach-Object { $_.log.pipeListening })
        load = Load $sys0
    }
}

# ---- speed: next clip ------------------------------------------------------------------------
# Playing clip01 (H.264 720p) -> NextFile action -> target loaded (dump frame >= 0) -> playback
# started (seeking = no). One warm-up switch per target is not counted.

function Bench-Next {
    $sys0 = Section 'next clip to first frame'
    $ini = Join-Path $Out 'bench.ini'; Write-Ini $ini
    $p = Start-App @() $ini (Join-Path $logs 'next.log')
    Wait-Pipe | Out-Null
    $targets = [ordered]@{ 'H.264 1080p30' = $b1; 'HEVC 10-bit 2160p30' = $b3; 'VP9 1080p30' = $b4; 'AV1 1080p30' = $b5 }
    $out = [ordered]@{}
    foreach ($name in $targets.Keys) {
        $target = $targets[$name]
        $rows = [Collections.Generic.List[object]]::new()
        $hw = $null
        for ($i = 0; $i -le $Runs; $i++) {
            $null = Ctl 'open' @($clip1, $target)
            $null = Wait-Started $clip1
            Ensure-Playing
            Sleep-Ms 700
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $resp = Ctl 'action' @('NextFile')
            if ($resp -ne 'ok') { throw "NextFile -> $resp" }
            $loaded = Poll $sw { $d = Dump; (Same-File $d.path $target) -and $null -ne $d.frame -and $d.frame -ge 0 } 20000
            $started = Poll $sw { (Prop 'seeking') -eq 'no' } 20000
            $hw = (Dump).hwdecCurrent
            if ($i -gt 0) { $rows.Add([ordered]@{ run = $i; loaded = $loaded; started = $started }) }
            Write-Host ("  {0} run {1}{2}: loaded {3} started {4} ms (hwdec {5})" -f $name, $i, $(if ($i -eq 0) { ' (warm-up)' } else { '' }), (R1 $loaded), (R1 $started), $hw)
        }
        $out[$name] = [ordered]@{ clip = [IO.Path]::GetFileName($target); hwdec = $hw; runs = $rows
            loaded = Stat ($rows | ForEach-Object { $_.loaded }); started = Stat ($rows | ForEach-Object { $_.started }) }
    }
    Stop-App $p | Out-Null
    $results.next = [ordered]@{ from = [IO.Path]::GetFileName($clip1); targets = $out; load = Load $sys0 }
}

# ---- speed: memory ---------------------------------------------------------------------------

function Bench-Memory {
    $sys0 = Section 'memory (working set / private bytes)'
    $ini = Join-Path $Out 'bench.ini'; Write-Ini $ini
    $rows = [Collections.Generic.List[object]]::new()
    for ($i = 1; $i -le $Runs; $i++) {
        $p = Start-App @() $ini (Join-Path $logs "memory-$i.log")
        Wait-Pipe | Out-Null
        Sleep-Ms 5000
        $p.Refresh(); $idleWs = $p.WorkingSet64; $idlePriv = $p.PrivateMemorySize64
        $null = Ctl 'open' @('--paused', $b1); $null = Wait-Started $b1; Sleep-Ms 3000
        $p.Refresh(); $pausedWs = $p.WorkingSet64; $pausedPriv = $p.PrivateMemorySize64
        $null = Ctl 'open' @($b3); $null = Wait-Started $b3; Ensure-Playing; Sleep-Ms 5000
        $p.Refresh(); $playWs = $p.WorkingSet64; $playPriv = $p.PrivateMemorySize64; $peak = $p.PeakWorkingSet64
        Stop-App $p | Out-Null
        $mb = { param($b) [math]::Round($b / 1MB, 1) }
        $row = [ordered]@{ run = $i; idleWsMB = & $mb $idleWs; idlePrivateMB = & $mb $idlePriv; paused1080WsMB = & $mb $pausedWs; paused1080PrivateMB = & $mb $pausedPriv
            playing4kWsMB = & $mb $playWs; playing4kPrivateMB = & $mb $playPriv; peakWsMB = & $mb $peak }
        $rows.Add($row)
        Write-Host ("  run {0}: idle WS {1} MB / private {2} MB; paused 1080p WS {3} / {4}; playing 4K WS {5} / {6}; peak WS {7}" -f $i,
            $row.idleWsMB, $row.idlePrivateMB, $row.paused1080WsMB, $row.paused1080PrivateMB, $row.playing4kWsMB, $row.playing4kPrivateMB, $row.peakWsMB)
    }
    $s = [ordered]@{ runs = $rows; load = Load $sys0 }
    foreach ($k in 'idleWsMB', 'idlePrivateMB', 'paused1080WsMB', 'paused1080PrivateMB', 'playing4kWsMB', 'playing4kPrivateMB', 'peakWsMB') { $s[$k] = Stat ($rows | ForEach-Object { $_[$k] }) }
    $results.memory = $s
}

# ---- speed: CPU while playing ----------------------------------------------------------------
# VidFlow process CPU and its UI thread (the process's first thread) over 5 s windows while the
# clip loops (Repeat One). % of one core; divide by the logical core count for % of the machine.

# Growth of an mpv counter over a window. mpv resets the drop counters when the file restarts
# (the clip loops under Repeat One), so after a reset only the part since the reset is counted.
function Delta($a, $b) {
    if ($null -eq $a -or $null -eq $b) { return $null }
    if ($b -lt $a) { return $b }
    return $b - $a
}

function Bench-Cpu {
    $sys0 = Section 'CPU while playing'
    $ini = Join-Path $Out 'bench.ini'; Write-Ini $ini
    $p = Start-App @() $ini (Join-Path $logs 'cpu.log')
    Wait-Pipe | Out-Null
    $p.Refresh()
    $uiId = ($p.Threads | Sort-Object { try { $_.StartTime } catch { [datetime]::MaxValue } } | Select-Object -First 1).Id
    $out = [ordered]@{}
    foreach ($e in @(@('H.264 1080p60', $b2), @('HEVC 10-bit 2160p30', $b3))) {
        $name = $e[0]; $clip = $e[1]
        $null = Ctl 'open' @($clip); $null = Wait-Started $clip; Ensure-Playing
        Sleep-Ms 3000
        $rows = [Collections.Generic.List[object]]::new()
        for ($i = 1; $i -le $Runs; $i++) {
            $p.Refresh()
            $ui = $p.Threads | Where-Object { $_.Id -eq $uiId }
            $c0 = $p.TotalProcessorTime.TotalMilliseconds; $u0 = $ui.TotalProcessorTime.TotalMilliseconds
            $vo0 = Num (Prop 'frame-drop-count'); $dec0 = Num (Prop 'decoder-frame-drop-count'); $s0 = SysTimes
            $sw = [Diagnostics.Stopwatch]::StartNew()
            Sleep-Ms 5000
            $p.Refresh()
            $ui = $p.Threads | Where-Object { $_.Id -eq $uiId }
            $wall = $sw.Elapsed.TotalMilliseconds
            $c1 = $p.TotalProcessorTime.TotalMilliseconds; $u1 = $ui.TotalProcessorTime.TotalMilliseconds
            $vo1 = Num (Prop 'frame-drop-count'); $dec1 = Num (Prop 'decoder-frame-drop-count'); $s1 = SysTimes
            $row = [ordered]@{ run = $i; processPctOfCore = 100 * ($c1 - $c0) / $wall; uiPctOfCore = 100 * ($u1 - $u0) / $wall
                voDrops = (Delta $vo0 $vo1); decoderDrops = (Delta $dec0 $dec1)
                machineBusyPct = Busy $s0 $s1 }
            $row.otherProcessesPct = $row.machineBusyPct - $row.processPctOfCore / $cores
            $rows.Add($row)
            Write-Host ("  {0} run {1}: process {2:N1}% of a core, UI thread {3:N1}%, drops vo {4} dec {5}, machine busy {6:N0}%" -f $name, $i, $row.processPctOfCore, $row.uiPctOfCore, $row.voDrops, $row.decoderDrops, $row.machineBusyPct)
        }
        # The refresh rate mpv paces against (on the hidden desktop it has no real display to sync to).
        $out[$name] = [ordered]@{ clip = [IO.Path]::GetFileName($clip); hwdec = (Dump).hwdecCurrent; runs = $rows
            displayFps = Num (Prop 'display-fps'); estimatedDisplayFps = Num (Prop 'estimated-display-fps')
            processPctOfCore = Stat ($rows | ForEach-Object { $_.processPctOfCore }); uiPctOfCore = Stat ($rows | ForEach-Object { $_.uiPctOfCore })
            voDrops = Stat ($rows | ForEach-Object { $_.voDrops }); decoderDrops = Stat ($rows | ForEach-Object { $_.decoderDrops })
            otherProcessesPct = Stat ($rows | ForEach-Object { $_.otherProcessesPct }) }
    }
    Stop-App $p | Out-Null
    $results.cpu = [ordered]@{ logicalCores = $cores; clips = $out; load = Load $sys0 }
}

# ---- speed: back-step (L2 baseline) ----------------------------------------------------------
# FrameBackStep action -> the readout shows the previous frame (dump frame = n - 1) and mpv has
# finished the seek (seeking = no). The clip has keyframes at frames 0 and 250 only. mpv runs a
# back step as a "very exact" seek, which starts decoding at the last keyframe at least 0.5 s
# before the target, so the work is (n - that keyframe) frames: positions 1.0 s and 0.4 s after
# the keyframe at 250, mid-GOP and end of GOP show the range. Every landed frame is checked
# against its timestamp.

function Bench-BackStep {
    $sys0 = Section 'back-step on long-GOP 4K HEVC 10-bit'
    $ini = Join-Path $Out 'bench.ini'; Write-Ini $ini
    $keys = @(& ffprobe -v error -select_streams v:0 -skip_frame nokey -show_entries frame=pts_time -of csv=p=0 $b3 | ForEach-Object { Num ("$_" -split ',')[0] } | Where-Object { $null -ne $_ } | Sort-Object)
    $p = Start-App @() $ini (Join-Path $logs 'backstep.log')
    Wait-Pipe | Out-Null
    $null = Ctl 'open' @('--paused', $b3); $null = Wait-Started $b3
    Ensure-Paused
    $fps = [double](Dump).fps
    $steps = 3
    $out = [ordered]@{}
    $positions = @(@('frame 280 (1.0 s after the keyframe at 250)', 280), @('frame 262 (0.4 s after the keyframe at 250)', 262),
        @('frame 130 (mid-GOP)', 130), @('frame 245 (end of the first GOP)', 245))
    foreach ($pos in $positions) {
        $name = $pos[0]; $start = [int]$pos[1]
        # First step's decode start: the last keyframe at or before (target - 0.5 s).
        $from = @($keys | Where-Object { $_ -le $start / $fps - 0.5 + 1e-6 })
        $kf = if ($from.Count) { [int][math]::Round($from[-1] * $fps) } else { 0 }
        $rows = [Collections.Generic.List[object]]::new()
        $wrong = 0; $timeouts = 0
        for ($i = 1; $i -le $Runs; $i++) {
            Seek-Exact ($start / $fps)
            $null = Poll ([Diagnostics.Stopwatch]::StartNew()) { (Dump).frame -eq $start } 5000
            for ($k = 1; $k -le $steps; $k++) {
                $expect = $start - $k
                $sw = [Diagnostics.Stopwatch]::StartNew()
                $null = Ctl 'action' @('FrameBackStep')
                $readout = Poll $sw { (Dump).frame -eq $expect } 30000
                $done = if ($null -ne $readout) { Poll $sw { (Prop 'seeking') -eq 'no' } 30000 } else { $null }
                $tp = Num (Prop 'time-pos')
                $ok = ($null -ne $readout) -and ($null -ne $tp) -and [math]::Abs($tp - $expect / $fps) -lt (0.5 / $fps)
                if ($null -eq $readout) { $timeouts++ } elseif (-not $ok) { $wrong++ }
                $rows.Add([ordered]@{ run = $i; step = $k; frame = $expect; readout = $readout; done = $done; timePos = $tp; correct = $ok })
            }
            Write-Host ("  {0} run {1}: {2} ms" -f $name, $i, (($rows | Select-Object -Last $steps | ForEach-Object { R1 $_.done }) -join ' / '))
        }
        $out[$name] = [ordered]@{ startFrame = $start; decodeFromKeyframe = $kf; framesToDecode = $start - $kf; runs = $rows
            readout = Stat ($rows | ForEach-Object { $_.readout }); done = Stat ($rows | ForEach-Object { $_.done }); wrongFrames = $wrong; timeouts = $timeouts }
    }
    $hw = (Dump).hwdecCurrent
    Stop-App $p | Out-Null
    $results.backstep = [ordered]@{ clip = [IO.Path]::GetFileName($b3); fps = $fps; keyframeFrames = (($keys | ForEach-Object { [int][math]::Round($_ * $fps) }) -join ', '); hwdec = $hw; positions = $out; load = Load $sys0 }
}

# ---- speed: seek latency ---------------------------------------------------------------------
# mpv "seek <t> absolute+exact" vs "absolute+keyframes" through the pipe, from a paused,
# settled position >= 2 s away. Done = mpv seeking = no and the position is the target
# (exact) or a new keyframe position (keyframes).

function Bench-Seek {
    $sys0 = Section 'seek latency (exact vs keyframe)'
    $ini = Join-Path $Out 'bench.ini'; Write-Ini $ini
    $p = Start-App @() $ini (Join-Path $logs 'seek.log')
    Wait-Pipe | Out-Null
    $out = [ordered]@{}
    foreach ($e in @(@('HEVC 10-bit 2160p30, GOP 250', $b3), @('H.264 1080p30, GOP 60', $b1))) {
        $name = $e[0]; $clip = $e[1]
        $null = Ctl 'open' @('--paused', $clip); $null = Wait-Started $clip
        Ensure-Paused
        $d = Dump; $fps = [double]$d.fps; $dur = [double]$d.duration
        $rows = [Collections.Generic.List[object]]::new()
        for ($i = 1; $i -le $Runs; $i++) {
            $rnd = [Random]::new(1000 + $i)
            for ($j = 0; $j -lt 5; $j++) {
                $target = [math]::Round(0.5 + $rnd.NextDouble() * ($dur - 1.5), 3)
                foreach ($mode in 'exact', 'keyframes') {
                    $pre = if ($target -gt $dur / 2) { $target - 2.3 } else { $target + 2.3 }
                    Seek-Exact $pre
                    $old = [double](Dump).position
                    $script:seenYes = $false
                    $sw = [Diagnostics.Stopwatch]::StartNew()
                    $null = Ctl 'mpv' @('no-osd', 'seek', $target.ToString('0.###', $inv), "absolute+$mode")
                    $t = Poll $sw {
                        $s = Prop 'seeking'
                        if ($s -eq 'yes') { $script:seenYes = $true; return $false }
                        $pos = [double](Dump).position
                        if ($mode -eq 'exact') { return [math]::Abs($pos - $target) -le (0.6 / $fps) }
                        return $script:seenYes -or ([math]::Abs($pos - $old) -gt 0.001 -and [math]::Abs($pos - $target) -gt 0.001)
                    } 30000
                    $landed = [double](Dump).position
                    $rows.Add([ordered]@{ run = $i; mode = $mode; target = $target; ms = $t; landed = $landed })
                }
            }
            $ex = $rows | Where-Object { $_.run -eq $i -and $_.mode -eq 'exact' } | ForEach-Object { R1 $_.ms }
            $kf = $rows | Where-Object { $_.run -eq $i -and $_.mode -eq 'keyframes' } | ForEach-Object { R1 $_.ms }
            Write-Host ("  {0} run {1}: exact {2} | keyframes {3} ms" -f $name, $i, ($ex -join ' '), ($kf -join ' '))
        }
        $out[$name] = [ordered]@{ clip = [IO.Path]::GetFileName($clip); runs = $rows
            exact = Stat ($rows | Where-Object { $_.mode -eq 'exact' } | ForEach-Object { $_.ms })
            keyframes = Stat ($rows | Where-Object { $_.mode -eq 'keyframes' } | ForEach-Object { $_.ms })
            timeouts = @($rows | Where-Object { $null -eq $_.ms }).Count }
    }
    Stop-App $p | Out-Null
    $results.seek = [ordered]@{ clips = $out; load = Load $sys0 }
}

function Fmt-Stat($s, [string]$unit = 'ms') {
    if ($null -eq $s -or $s.n -eq 0) { return '| – | – | – | – |' }
    $f = { param($v) if ($null -eq $v) { '–' } else { ([double]$v).ToString('0', $inv) } }
    if ($unit -eq '%') { $f = { param($v) if ($null -eq $v) { '–' } else { ([double]$v).ToString('0.0', $inv) } } }
    return "| $(& $f $s.median) | $(& $f $s.p95) | $(& $f $s.min) | $(& $f $s.max) |"
}

function Write-SpeedReport {
    $md = [Collections.Generic.List[string]]::new()
    $m = $results.machine
    $md.Add("Measured $($m.measured) with ``$($m.exe)`` (built $($m.exeBuilt), SHA-256 $($m.exeSha256)...).")
    $md.Add('')
    $md.Add("CPU: $($m.cpu); GPU: $($m.gpu -join '; '); RAM $($m.ramGB) GB; $($m.windows); media on $($m.mediaDisk); power plan $($m.powerPlan), $($m.power).")
    $md.Add('')
    $md.Add('| Metric | median | p95 | min | max |')
    $md.Add('|---|---:|---:|---:|---:|')
    if ($results.cold) {
        $c = $results.cold
        $md.Add("| Cold start: window created (ms) $(Fmt-Stat $c.window)")
        $md.Add("| Cold start: control pipe answers (ms) $(Fmt-Stat $c.pipe)")
        $md.Add("| Cold start: file loaded, dump frame >= 0 (ms) $(Fmt-Stat $c.loaded)")
        $md.Add("| Cold start: first frame rendered (ms) $(Fmt-Stat $c.firstFrame)")
        $md.Add("| Cold start: playback started (ms) $(Fmt-Stat $c.started)")
        $md.Add("| Log: libmpv loaded (ms after process start) $(Fmt-Stat $c.logLibmpvLoaded)")
        $md.Add("| Log: mpv initialised (ms after process start) $(Fmt-Stat $c.logMpvReady)")
        $md.Add("| Log: control pipe listening (ms after process start) $(Fmt-Stat $c.logPipeListening)")
        $md.Add("| Quit to process exit (ms) $(Fmt-Stat $c.exit)")
    }
    if ($results.next) {
        foreach ($k in $results.next.targets.Keys) {
            $t = $results.next.targets[$k]
            $md.Add("| Next clip -> $k loaded (ms, hwdec $($t.hwdec)) $(Fmt-Stat $t.loaded)")
            $md.Add("| Next clip -> $k playback started (ms) $(Fmt-Stat $t.started)")
        }
    }
    if ($results.memory) {
        $mm = $results.memory
        foreach ($k in 'idleWsMB', 'idlePrivateMB', 'paused1080WsMB', 'paused1080PrivateMB', 'playing4kWsMB', 'playing4kPrivateMB', 'peakWsMB') {
            $md.Add("| Memory ${k} $(Fmt-Stat $mm[$k] '%')")
        }
    }
    if ($results.cpu) {
        foreach ($k in $results.cpu.clips.Keys) {
            $c = $results.cpu.clips[$k]
            $md.Add("| CPU $k process (% of one core, hwdec $($c.hwdec)) $(Fmt-Stat $c.processPctOfCore '%')")
            $md.Add("| CPU $k UI thread (% of one core) $(Fmt-Stat $c.uiPctOfCore '%')")
            $md.Add("| CPU $k dropped frames per 5 s (vo) $(Fmt-Stat $c.voDrops '%')")
            $md.Add("| CPU $k other processes (% of machine) $(Fmt-Stat $c.otherProcessesPct '%')")
        }
    }
    if ($results.backstep) {
        foreach ($k in $results.backstep.positions.Keys) {
            $b = $results.backstep.positions[$k]
            $md.Add("| Back-step 4K HEVC GOP 250, $k, decodes $($b.framesToDecode) frames (ms; wrong $($b.wrongFrames), timeouts $($b.timeouts)) $(Fmt-Stat $b.done)")
        }
    }
    if ($results.seek) {
        foreach ($k in $results.seek.clips.Keys) {
            $s = $results.seek.clips[$k]
            $md.Add("| Seek exact, $k (ms) $(Fmt-Stat $s.exact)")
            $md.Add("| Seek keyframe, $k (ms) $(Fmt-Stat $s.keyframes)")
        }
    }
    $md.Add('')
    $md.Add('Machine CPU load while each section ran, % of all logical cores (other = everything except VidFlow and this script):')
    $md.Add('')
    $md.Add('| Section | machine busy | VidFlow | bench script | other processes |')
    $md.Add('|---|---:|---:|---:|---:|')
    foreach ($k in @('cold', 'next', 'memory', 'cpu', 'backstep', 'seek')) {
        if (-not $results.Contains($k)) { continue }
        $l = $results[$k].load
        $md.Add("| $k | $($l.machineBusyPct) | $($l.vidflowPct) | $($l.benchScriptPct) | $($l.otherPct) |")
    }
    if ($ExtraIni) { $md.Add(''); $md.Add("Settings variant: $($ExtraIni -join '; ')") }
    $md | Set-Content -Encoding UTF8 (Join-Path $Out 'bench-results.md')
    $md | ForEach-Object { Write-Host $_ }
}

# ---- gate ------------------------------------------------------------------------------------

function Probe-Video([string]$path) {
    $j = & ffprobe -v error -select_streams v:0 -show_entries 'stream=codec_name,width,height,sample_aspect_ratio,r_frame_rate,avg_frame_rate,pix_fmt,color_transfer:stream_side_data=rotation' -of json $path | ConvertFrom-Json
    return $j.streams | Select-Object -First 1
}

function Frame-Times([string]$path) {
    $lines = & ffprobe -v error -select_streams v:0 -show_entries packet=pts_time -of csv=p=0 $path
    return @($lines | ForEach-Object { Num $_ } | Where-Object { $null -ne $_ } | Sort-Object)
}

function Read-Rgb([string]$png) {
    $info = (& ffprobe -v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 $png).Split(',')
    $w = [int]$info[0]; $h = [int]$info[1]
    $psi = [Diagnostics.ProcessStartInfo]::new((Get-Command ffmpeg).Source)
    foreach ($a in @('-v', 'error', '-i', $png, '-f', 'rawvideo', '-pix_fmt', 'rgb24', 'pipe:1')) { $psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.CreateNoWindow = $true
    $pp = [Diagnostics.Process]::Start($psi)
    $ms = [IO.MemoryStream]::new()
    $pp.StandardOutput.BaseStream.CopyTo($ms)
    $pp.WaitForExit()
    return [BenchNative]::Analyze($ms.ToArray(), $w, $h)
}

function Quadrant([double]$x, [double]$y) { return $(if ($y -lt 0.5) { 'T' } else { 'B' }) + $(if ($x -lt 0.5) { 'L' } else { 'R' }) }

function Image-Facts([double[]]$a) {
    $bw = $a[4] - $a[2] + 1; $bh = $a[5] - $a[3] + 1
    return [ordered]@{
        box = "$([int]$bw)x$([int]$bh)"; aspect = $(if ($bh -gt 0) { [math]::Round($bw / $bh, 4) } else { $null })
        red = $(if ($a[8] -gt 50) { Quadrant $a[6] $a[7] }); green = $(if ($a[11] -gt 50) { Quadrant $a[9] $a[10] }); blue = $(if ($a[14] -gt 50) { Quadrant $a[12] $a[13] })
        whiteLuma = [math]::Round($a[15], 1); backgroundLuma = $a[16]; cornerLuma = [math]::Round($a[17], 1)
    }
}

# Where the stored top-left/top-right/bottom-left markers end up after a counter-clockwise
# display rotation (ffprobe's display-matrix angle).
function Expected-Quadrants([int]$rot) {
    switch ((($rot % 360) + 360) % 360) {
        90 { return @{ red = 'BL'; green = 'TL'; blue = 'BR' } }
        180 { return @{ red = 'BR'; green = 'BL'; blue = 'TR' } }
        270 { return @{ red = 'TR'; green = 'BR'; blue = 'TL' } }
        default { return @{ red = 'TL'; green = 'TR'; blue = 'BL' } }
    }
}

function Bench-Gate {
    Write-Host '== release gate' -ForegroundColor Cyan
    $ini = Join-Path $Out 'gate.ini'
    Write-Ini $ini @('OsdMessages=false', 'WindowWidth=960', 'WindowHeight=720')
    $log = Join-Path $Out 'gate-app.log'; Remove-Item $log -ErrorAction SilentlyContinue
    $jp = [string]::new([char[]]@(0x65E5, 0x672C, 0x8A9E)) + '_clip.mp4'
    $long = Join-Path $gateDir 'g32_longpath'
    foreach ($i in 1..4) { $long = Join-Path $long (('deep_folder_level_{0}_' -f $i).PadRight(60, 'x')) }
    $longFile = Join-Path $long 'g32_long_path_clip.mp4'
    $cases = @(
        @{ Id = 'unicode'; File = (Join-Path $gateDir $jp); Kind = 'video'; What = 'Unicode file name (日本語_clip.mp4), opened from the command line' }
        @{ Id = 'sdr'; File = 'g00_sdr_reference_1080p.mp4'; Kind = 'video'; What = 'SDR reference (BT.709), the baseline for the HDR checks' }
        @{ Id = 'hdr10'; File = 'g01_hdr10_pq_hevc10_1080p.mp4'; Kind = 'video'; Hdr = 'pq'; What = 'HDR10: PQ, BT.2020, HEVC 10-bit, mastering display + MaxCLL 1000/400' }
        @{ Id = 'hlg'; File = 'g02_hlg_hevc10_1080p.mov'; Kind = 'video'; Hdr = 'hlg'; What = 'HLG, HEVC 10-bit .mov (iPhone-style)' }
        @{ Id = 'dv84'; File = 'g03_dolbyvision84_like_hlg_1080p.mov'; Kind = 'video'; Hdr = 'hlg'; What = 'Dolby Vision profile 8.4 record over an HLG base layer (no RPU)' }
        @{ Id = 'rot090'; File = 'g04_rotate_090_hevc.mov'; Kind = 'video'; What = 'Rotation metadata 90 (HEVC .mov)' }
        @{ Id = 'rot180'; File = 'g04_rotate_180_hevc.mov'; Kind = 'video'; What = 'Rotation metadata 180 (phone held upside down)' }
        @{ Id = 'rot270'; File = 'g04_rotate_270_hevc.mov'; Kind = 'video'; What = 'Rotation metadata 270 / -90 (iPhone portrait)' }
        @{ Id = 'hlg180'; File = 'g05_hlg_rotate_180_hevc10.mov'; Kind = 'video'; Hdr = 'hlg'; What = 'HLG 10-bit + rotation 180' }
        @{ Id = 'fps120'; File = 'g06_h264_1080p120.mp4'; Kind = 'video'; Step = $true; What = '120 fps H.264' }
        @{ Id = 'fps240'; File = 'g07_hevc_1080p240.mov'; Kind = 'video'; Step = $true; What = '240 fps HEVC .mov' }
        @{ Id = 'vfr'; File = 'g08_vfr_phone_h264_1080p.mp4'; Kind = 'video'; Step = $true; What = 'VFR phone-style (jitter + 24 fps stretch, timescale 600)' }
        @{ Id = 'av1_10'; File = 'g09_av1_10bit_1080p.mkv'; Kind = 'video'; What = 'AV1 10-bit' }
        @{ Id = 'p1080x1350'; File = 'g10_h264_1080x1350_portrait45.mp4'; Kind = 'video'; What = '1080x1350 (4:5 portrait)' }
        @{ Id = 'dci4096'; File = 'g11_h264_4096x2160_dci.mp4'; Kind = 'video'; What = '4096x2160 (DCI 4K)' }
        @{ Id = 'sar43'; File = 'g12_h264_1440x1080_sar4-3.mp4'; Kind = 'video'; What = '1440x1080 with SAR 4:3 (displays 16:9)' }
        @{ Id = 'm4a'; File = 'g13_audio_only.m4a'; Kind = 'audio'; What = 'Audio only (AAC .m4a)' }
        @{ Id = 'mp3'; File = 'g14_audio_only.mp3'; Kind = 'audio'; What = 'Audio only (.mp3)' }
        @{ Id = 'zero'; File = 'g20_broken_zero_bytes.mp4'; Kind = 'error'; What = '0-byte file' }
        @{ Id = 'nomoov'; File = 'g21_broken_missing_moov.mp4'; Kind = 'error'; What = 'Truncated mp4 without its index (moov)' }
        @{ Id = 'garbage'; File = 'g23_broken_garbage.mp4'; Kind = 'error'; What = 'Random bytes named .mp4' }
        @{ Id = 'trunc'; File = 'g22_broken_truncated_half.mp4'; Kind = 'damaged'; What = 'Faststart mp4 cut in half' }
        @{ Id = 'bitrot'; File = 'g24_broken_bitrot.mkv'; Kind = 'damaged'; What = 'MKV with 64 random bytes every 32 KB' }
        @{ Id = 'special'; File = 'g31 name with spaces & $dollar (1).mp4'; Kind = 'video'; What = 'Spaces, & and $ in the file name' }
        @{ Id = 'longpath'; File = $longFile; Kind = 'video'; What = "Full path of $($longFile.Length) characters (> 260)" }
    )
    foreach ($c in $cases) { if (-not [IO.Path]::IsPathRooted($c.File)) { $c.File = Join-Path $gateDir $c.File } }
    # -Only with case ids runs just those (the SDR reference stays in when an HDR case is picked).
    if ($Only) {
        $hdr = @($cases | Where-Object { $Only -contains $_.Id -and $_.Hdr }).Count -gt 0
        $cases = @($cases | Where-Object { $Only -contains $_.Id -or ($hdr -and $_.Id -eq 'sdr') })
        if ($cases.Count -eq 0) { throw "no gate case matches -Only $($Only -join ',')" }
    }

    $checks = [Collections.Generic.List[object]]::new()
    $facts = [ordered]@{}
    $script:gcur = ''
    function GCheck([string]$name, [bool]$ok, [string]$detail = '') {
        $checks.Add([pscustomobject]@{ Case = $script:gcur; Check = $name; Pass = $ok; Detail = $detail })
        Write-Host ("  [{0}] {1}: {2} {3}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $script:gcur, $name, $detail) -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
    }
    function Capture-Window([string]$out) {
        try {
            & (Join-Path $root 'tools\capture.ps1') -ProcessId $script:gp.Id -Client -Out $out 2>&1 | Out-Null
            return (Test-Path $out)
        } catch { return $false }
    }
    function Ensure-App {
        if ($null -eq $script:gp -or $script:gp.HasExited) {
            $script:gp = Start-App @() $ini $log
            Wait-Pipe | Out-Null
        }
    }

    $script:gp = Start-App @($cases[0].File) $ini $log    # first sample comes in on the command line
    Wait-Pipe | Out-Null
    $sdrFacts = $null
    $first = $true
    foreach ($c in $cases) {
        $script:gcur = $c.Id
        $f = [ordered]@{ id = $c.Id; file = $c.File; what = $c.What; kind = $c.Kind }
        $facts[$c.Id] = $f
        Write-Host "-- $($c.Id): $($c.What)"
        try {
            if (-not (Test-Path -LiteralPath $c.File)) { GCheck 'sample exists' $false $c.File; continue }
            Ensure-App
            $pr = if ($c.Kind -eq 'video') { Probe-Video $c.File } else { $null }
            $wasFirst = $first
            $swOpen = [Diagnostics.Stopwatch]::StartNew()
            if (-not $first) { $r = Ctl 'open' @($c.File); if ($r -ne 'ok') { GCheck 'open accepted' $false $r; continue } }
            $first = $false
            switch ($c.Kind) {
                'video' {
                    $rot = 0; if ($pr.side_data_list) { $rot = [int](@($pr.side_data_list | Where-Object { $null -ne $_.rotation })[0].rotation) }
                    $f.probe = "$($pr.codec_name) $($pr.width)x$($pr.height) SAR $($pr.sample_aspect_ratio) $($pr.pix_fmt) $($pr.color_transfer) r=$($pr.r_frame_rate) avg=$($pr.avg_frame_rate) rotation=$rot"
                    $d = Wait-Started $c.File 30000
                    # open -> playback started (the command-line case includes the app's own start)
                    $f.openToStartedMs = if ($wasFirst) { $null } else { R1 $swOpen.Elapsed.TotalMilliseconds }
                    GCheck 'loads without error' (-not $d.lastError) "$($d.lastError)"
                    if ($d.lastError) { break }
                    if ($c.Id -in 'unicode', 'special', 'longpath') {
                        GCheck 'path reported exactly' (Same-File $d.path $c.File) $d.path
                        GCheck 'title shows the file name' ("$($d.title)".StartsWith([IO.Path]::GetFileName($c.File))) $d.title
                    }
                    Ensure-Playing
                    $p0 = [double](Dump).position; $drop0 = Num (Prop 'frame-drop-count')
                    Sleep-Ms 1500
                    $d = Dump; $p1 = [double]$d.position; $drop1 = Num (Prop 'frame-drop-count')
                    $adv = $p1 - $p0; if ($adv -lt 0) { $adv += [double]$d.duration }   # looped (Repeat One)
                    GCheck 'plays (position advances)' ($adv -gt 0.5) ("advanced {0:N2} s in 1.5 s, vo drops {1}" -f $adv, $(if ($null -ne $drop0 -and $null -ne $drop1) { $drop1 - $drop0 }))
                    Ensure-Paused
                    Seek-Exact 1.0
                    $vp = [ordered]@{}
                    foreach ($k in 'w', 'h', 'dw', 'dh', 'rotate', 'pixelformat', 'hw-pixelformat', 'gamma', 'primaries', 'colormatrix', 'sig-peak') { $vp[$k] = Prop "video-params/$k" }
                    $f.videoParams = $vp
                    $f.targetGamma = Prop 'video-target-params/gamma'
                    $f.targetPeak = Prop 'video-target-params/sig-peak'
                    $f.hwdec = (Dump).hwdecCurrent
                    $f.fps = (Dump).fps; $f.frameCount = (Dump).frameCount
                    if ($c.Step) {
                        $times = Frame-Times $c.File
                        $idx = -1; for ($k = 0; $k -lt $times.Count; $k++) { if ($times[$k] -le 1.0 + 1e-6) { $idx = $k } }
                        $d0 = Dump; $f0 = $d0.frame
                        $tpos0 = Num (Prop 'time-pos')
                        for ($k = 1; $k -le 3; $k++) {
                            $prev = (Dump).frame
                            $null = Ctl 'action' @('FrameStep')
                            $null = Poll ([Diagnostics.Stopwatch]::StartNew()) { $x = Dump; $x.frame -ne $prev -and (Prop 'seeking') -eq 'no' } 5000
                        }
                        $d3 = Dump; $tpos3 = Num (Prop 'time-pos')
                        $trueAt1 = $idx; $true3 = $idx + 3
                        $expectT3 = if ($true3 -lt $times.Count) { $times[$true3] } else { $null }
                        GCheck 'three frame steps land on the 3rd next frame' ($null -ne $expectT3 -and [math]::Abs($tpos3 - $expectT3) -lt 0.0015) ("time-pos {0:N4} -> {1:N4}, expected {2:N4}" -f $tpos0, $tpos3, $expectT3)
                        $f.frameAt1s = "app frame $f0, true index $trueAt1 (from packet timestamps)"
                        $f.frameAfter3 = "app frame $($d3.frame), true index $true3"
                        # Any dump flag saying the number is approximate / the rate variable (L1 adds one;
                        # the name is not fixed yet, so match loosely). A flagged estimate is honest.
                        $flags = @($d3.PSObject.Properties | Where-Object { $_.Name -match 'vfr|variable|approx' })
                        $f.vfrFlags = ($flags | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', '
                        $flagged = @($flags | Where-Object { "$($_.Value)" -eq 'True' }).Count -gt 0
                        $exact = $f0 -eq $trueAt1 -and $d3.frame -eq $true3
                        $label = if ($c.Id -eq 'vfr') { 'frame number is the true index, or flagged approximate (VFR, L1)' } else { 'app frame number = true index' }
                        $ok = if ($c.Id -eq 'vfr') { $exact -or $flagged } else { $exact }
                        GCheck $label $ok ("at 1 s: app $f0 vs true $trueAt1; after 3 steps: app $($d3.frame) vs true $true3; flags: " + $(if ($f.vfrFlags) { $f.vfrFlags } else { 'none in dump' }))
                        Seek-Exact 1.0
                    }
                    $shot = Join-Path $shots "$($c.Id).png"
                    Remove-Item $shot -ErrorAction SilentlyContinue
                    $r = Ctl 'screenshot' @($shot)
                    $null = Poll ([Diagnostics.Stopwatch]::StartNew()) { Test-Path $shot } 5000
                    GCheck 'screenshot written' ((Test-Path $shot) -and $r -eq 'ok') "$r"
                    $f.shot = "shots/$($c.Id).png"
                    $ref = Join-Path $shots "ref_$($c.Id).png"
                    Remove-Item $ref -ErrorAction SilentlyContinue
                    & ffmpeg -nostdin -v error -y -ss 1 -i $c.File -frames:v 1 -vf 'scale=trunc(iw*sar/2)*2:ih,setsar=1' $ref
                    $f.ref = "shots/ref_$($c.Id).png"
                    if (Test-Path $shot) {
                        $app = Image-Facts (Read-Rgb $shot)
                        $f.app = $app
                        $exp = Expected-Quadrants $rot
                        $dw = [int]$pr.width; $dh = [int]$pr.height
                        $sar = 1.0; if ($pr.sample_aspect_ratio -match '^(\d+):(\d+)$' -and [int]$Matches[2] -gt 0) { $sar = [double]$Matches[1] / [double]$Matches[2] }
                        $dar = $dw * $sar / $dh; if ([math]::Abs($rot % 180) -eq 90) { $dar = 1 / $dar }
                        $f.expectedAspect = [math]::Round($dar, 4)
                        if (Test-Path $ref) { $f.refImage = Image-Facts (Read-Rgb $ref) }
                        GCheck 'aspect matches the display aspect' ([math]::Abs($app.aspect - $dar) / $dar -lt 0.03) ("shown {0} ({1}), expected {2:N4}" -f $app.aspect, $app.box, $dar)
                        $orient = $app.red -eq $exp.red -and $app.green -eq $exp.green -and $app.blue -eq $exp.blue
                        $refAgrees = if ($f.refImage) { "; ffmpeg autorotated reference: red $($f.refImage.red) green $($f.refImage.green) blue $($f.refImage.blue)" } else { '' }
                        GCheck 'orientation (corner markers)' $orient ("red $($app.red) green $($app.green) blue $($app.blue), expected red $($exp.red) green $($exp.green) blue $($exp.blue) for rotation $rot$refAgrees")
                        if ($c.Id -eq 'sdr') { $sdrFacts = $app }
                        if ($c.Hdr) {
                            $gamma = $vp['gamma']
                            GCheck 'HDR transfer detected' ($gamma -eq $c.Hdr) "video-params/gamma=$gamma, output target gamma=$($f.targetGamma) peak=$($f.targetPeak)"
                            if ($sdrFacts) {
                                # Washed out (HDR signal shown as if it were SDR) = low contrast: the PQ code
                                # for 203-nit white is ~58%, so white/grey collapses towards 1.6 against
                                # SDR's 2.8. Crushed/dim = reference white far below SDR white. The
                                # un-tone-mapped ffmpeg reference frame is scored the same way to show
                                # that the contrast test catches a washed-out picture.
                                $ratio = { param($x) if ($x.backgroundLuma -gt 0) { $x.whiteLuma / $x.backgroundLuma } else { 0 } }
                                $sdrRatio = & $ratio $sdrFacts
                                $appRel = (& $ratio $app) / $sdrRatio
                                $refRel = if ($f.refImage) { (& $ratio $f.refImage) / $sdrRatio } else { $null }
                                $f.contrastVsSdr = [math]::Round($appRel, 3)
                                $f.refContrastVsSdr = if ($null -ne $refRel) { [math]::Round($refRel, 3) } else { $null }
                                GCheck 'tone-mapped: not washed out (white/grey contrast >= 80% of SDR)' ($appRel -ge 0.8) ("white {0} / grey {1} = {2:N2}x of SDR's {3:N2}; the same frame without tone mapping (ffmpeg) scores {4}" -f $app.whiteLuma, $app.backgroundLuma, $appRel, $sdrRatio, $(if ($null -ne $refRel) { $refRel.ToString('0.00', $inv) + 'x' } else { 'n/a' }))
                                GCheck 'tone-mapped: not crushed (reference white >= 60% of SDR white)' ($app.whiteLuma -ge 0.6 * $sdrFacts.whiteLuma) ("white patch luma {0} vs SDR {1}" -f $app.whiteLuma, $sdrFacts.whiteLuma)
                                if ($c.Id -eq 'hdr10') {
                                    GCheck 'tone-mapped: 1000-nit highlight brighter than reference white' ($app.cornerLuma -gt $app.whiteLuma) ("highlight patch luma {0} vs white patch {1}" -f $app.cornerLuma, $app.whiteLuma)
                                }
                            }
                        }
                    }
                }
                'audio' {
                    $d = Wait-Started $c.File 15000 -Audio
                    GCheck 'loads without error' (-not $d.lastError) "$($d.lastError)"
                    Ensure-Playing
                    $p0 = [double](Dump).position; Sleep-Ms 1500; $p1 = [double](Dump).position
                    GCheck 'plays (position advances)' (($p1 - $p0) -gt 0.5) ("advanced {0:N2} s; duration {1}" -f ($p1 - $p0), (Dump).duration)
                    GCheck 'no video track reported' ((Prop 'video-params/w') -eq 'error') "video-params/w=$(Prop 'video-params/w')"
                    $win = Join-Path $shots "win_$($c.Id).png"
                    GCheck 'window captured (audio-only surface)' (Capture-Window $win) ''
                    $f.window = "shots/win_$($c.Id).png"
                }
                'error' {
                    $sw = [Diagnostics.Stopwatch]::StartNew()
                    $t = Poll $sw { $x = Dump; $script:ed = $x; $null -ne $x.lastError } 10000
                    $d = $script:ed
                    GCheck 'error reported' ($null -ne $t) ("after {0} ms: lastError='{1}'" -f (R1 $t), $d.lastError)
                    GCheck 'error text is plain language (no exception text)' ($d.lastError -and $d.lastError -notmatch 'Exception|   at |0x[0-9a-f]{6,}') "$($d.lastError)"
                    GCheck 'still responsive (no modal dialog, no crash)' ((-not $script:gp.HasExited) -and $null -ne (Dump)) ''
                    $f.lastError = $d.lastError
                    Sleep-Ms 300
                    $win = Join-Path $shots "win_$($c.Id).png"
                    GCheck 'window captured (error surface)' (Capture-Window $win) ''
                    $f.window = "shots/win_$($c.Id).png"
                }
                'damaged' {
                    $sw = [Diagnostics.Stopwatch]::StartNew()
                    $t = Poll $sw { $x = Dump; $script:ed = $x; (Same-File $x.path $c.File) -and (($null -ne $x.frame -and $x.frame -ge 0) -or $x.lastError) } 10000
                    $d = $script:ed
                    $f.lastError = $d.lastError
                    if ($d.lastError) { GCheck 'reported as an error (acceptable for a damaged file)' $true "$($d.lastError)" }
                    else {
                        Ensure-Playing
                        Sleep-Ms 4000
                        $d = Dump
                        $f.after4s = "position $($d.position) of $($d.duration), eof $($d.eofReached), lastError '$($d.lastError)'"
                        GCheck 'plays what is decodable' ($null -ne $d.position) $f.after4s
                    }
                    GCheck 'no crash, still responsive' ((-not $script:gp.HasExited) -and $null -ne (Dump)) ''
                    $win = Join-Path $shots "win_$($c.Id).png"
                    $null = Capture-Window $win
                    $f.window = "shots/win_$($c.Id).png"
                }
            }
            if ($c.Kind -in 'error', 'damaged') {
                $null = Ctl 'open' @((Join-Path $gateDir 'g00_sdr_reference_1080p.mp4'))
                $ok = $true; try { $null = Wait-Started (Join-Path $gateDir 'g00_sdr_reference_1080p.mp4') 15000 } catch { $ok = $false }
                GCheck 'recovers: next file plays' ($ok -and -not (Dump).lastError) ''
            }
        } catch {
            # Keep the table readable: the message without the full dump, plus what the app showed.
            $msg = ($_.Exception.Message -split '; last dump:')[0]
            $shown = try { $x = Dump; "now playing '$([IO.Path]::GetFileName("$($x.path)"))', lastError '$($x.lastError)', last OSD '$($x.lastOsd)'" } catch { 'no dump' }
            GCheck 'plays without timeout or exception' $false "$msg; $shown"
        }
        if ($script:gp.HasExited) { GCheck 'process alive' $false "exit code $($script:gp.ExitCode)"; $script:gp = $null }
    }
    $script:gcur = 'app'
    if ($script:gp -and -not $script:gp.HasExited) {
        $null = Ctl 'open' @((Join-Path $gateDir 'g00_sdr_reference_1080p.mp4'))
        Sleep-Ms 500
        $win = Join-Path $shots 'win_sdr_ui.png'
        $null = Capture-Window $win
        $exit = Stop-App $script:gp
        GCheck 'quits cleanly' ($null -ne $exit) ("{0} ms" -f (R1 $exit))
    }
    $logLines = if (Test-Path $log) { @(Get-Content $log) } else { @() }
    # mpv's own messages (decoder errors on the broken samples, hwdec probing) are expected;
    # VidFlow's own ERROR lines and exceptions are not. "action not registered" comes from
    # features still being integrated in the tree under test and is reported separately.
    $errs = @($logLines | Where-Object { $_ -cmatch ' ERROR ' -and $_ -cnotmatch 'ERROR mpv\[' -and $_ -notmatch 'action not registered' })
    $mpvErrs = @($logLines | Where-Object { $_ -cmatch 'ERROR mpv\[' })
    $unreg = @($logLines | Where-Object { $_ -match 'action not registered' })
    $exc = @($logLines | Where-Object { $_ -match 'Exception' })
    GCheck 'log: no VidFlow errors or exceptions' ($errs.Count -eq 0 -and $exc.Count -eq 0) ("{0} VidFlow ERROR lines, {1} exception lines; {2} mpv error messages (expected for the broken samples), {3} 'action not registered'" -f $errs.Count, $exc.Count, $mpvErrs.Count, $unreg.Count)
    $results.gate = [ordered]@{ machine = $results.machine; cases = $facts; checks = $checks; logErrors = $errs; logExceptions = $exc }
    $results.gate | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 (Join-Path $Out 'gate-results.json')

    $md = [Collections.Generic.List[string]]::new()
    $md.Add('| Case | Sample | Check | Result | Detail |')
    $md.Add('|---|---|---|---|---|')
    foreach ($ch in $checks) {
        $file = if ($facts.Contains($ch.Case)) { [IO.Path]::GetFileName($facts[$ch.Case].file) } else { '' }
        $md.Add("| $($ch.Case) | $file | $($ch.Check) | $(if ($ch.Pass) { 'PASS' } else { '**FAIL**' }) | $(($ch.Detail -replace '\|', '/')) |")
    }
    $md | Set-Content -Encoding UTF8 (Join-Path $Out 'gate-results.md')
    $fail = @($checks | Where-Object { -not $_.Pass })
    Write-Host ("gate: {0} checks, {1} failed" -f $checks.Count, $fail.Count) -ForegroundColor $(if ($fail.Count) { 'Yellow' } else { 'Green' })
    return $fail.Count
}

# ---- main ------------------------------------------------------------------------------------

$exitCode = 0
try {
    $results.machine = Machine-Info
    Write-Host "VidFlow: $Exe ($($results.machine.exeBuilt)); $($results.machine.cpu); $($results.machine.windows)"
    if ($Suite -in 'speed', 'all') {
        if (Want 'cold') { Bench-Cold }
        if (Want 'next') { Bench-Next }
        if (Want 'memory') { Bench-Memory }
        if (Want 'cpu') { Bench-Cpu }
        if (Want 'backstep') { Bench-BackStep }
        if (Want 'seek') { Bench-Seek }
        $speed = [ordered]@{}; foreach ($k in $results.Keys) { if ($k -ne 'gate') { $speed[$k] = $results[$k] } }
        $speed | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 (Join-Path $Out 'bench-results.json')
        Write-SpeedReport
    }
    if ($Suite -in 'gate', 'all') { $exitCode = [int]@(Bench-Gate)[-1] }
} catch {
    Write-Host "bench failed: $($_.Exception.Message)`n$($_.ScriptStackTrace)" -ForegroundColor Red
    $exitCode = 1
} finally {
    Stop-All
    [void][BenchNative]::timeEndPeriod(1)
}
exit $exitCode
