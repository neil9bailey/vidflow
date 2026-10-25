<#
.SYNOPSIS
  Generates the frame-accuracy test clips (roadmap L1/L2/L8) into testmedia\frames using ffmpeg.
.DESCRIPTION
  Every frame carries its index twice: as text ("F123", for people) and as a 12-bit barcode of
  white/black blocks along the bottom (for scripts: bit k of the frame index is block k, left to
  right; read it from an mpv "screenshot-to-file <path> video" image with Read-FrameBarcode in
  tools\e2e.d\10-frames.ps1). Frame n of a CFR clip starts at n / fps.

  CFR clips cover 23.976, 24, 25, 29.97, 30, 50, 59.94, 60, 120 and 240 fps in MP4 (1/timescale
  timestamps) and Matroska (millisecond timestamps). The VFR clips are made with setpts:
  fr_vfr_jitter.* wobbles between ~27.7 and ~31.3 fps like phone footage, fr_vfr_steps.* runs
  3 s at 30 fps, 2 s at 15 fps, then 1 s at 60 fps. fr_4k_hevc_gop250.mp4 is the back-step
  benchmark clip: 10 s of 3840x2160 HEVC at 25 fps whose only keyframe is the first frame.
  fr_unknown_30fps.avi is the 30 fps clip in a container the frame probe does not read.

  The clips live in a subfolder so opening testmedia\clip01 still queues exactly the five
  core test clips. Existing files are kept; -Force regenerates them.
#>
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\testmedia\frames'),
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$font = 'C\:/Windows/Fonts/consola.ttf'

# Text + barcode overlay for a W x H frame. Block size b = W/20; block k sits at x = b + 1.5*b*k,
# y = H - 1.8*b (size b x b) on a black strip, and is white when bit k of the frame index is set.
function Overlay([string]$label, [int]$w, [int]$h) {
    $b = [int]($w / 20)
    $parts = @("drawtext=fontfile='$font':text='$label F%{frame_num} T%{pts\:hms}':fontsize=h/10:fontcolor=white:box=1:boxcolor=black@0.7:boxborderw=8:x=(w-tw)/2:y=h*0.06")
    $parts += "drawbox=x=$([int]($b / 2)):y=$([int]($h - 2.1 * $b)):w=$([int](18.5 * $b)):h=$([int](1.6 * $b)):color=black:t=fill"
    for ($k = 0; $k -lt 12; $k++) {
        $x = [int]($b + 1.5 * $b * $k)
        $parts += "drawbox=x=${x}:y=$([int]($h - 1.8 * $b)):w=${b}:h=${b}:color=white:t=fill:enable='bitand(n,$([int][math]::Pow(2, $k)))'"
    }
    return ($parts -join ',')
}

function FF([string[]]$a) {
    $target = $a[-1]
    if (-not $Force -and (Test-Path $target) -and (Get-Item $target).Length -gt 0) { return }
    & ffmpeg -nostdin -hide_banner -loglevel error -y @a
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed: $a" }
}

# name, rate expression, duration (s), container, audio
$cfr = @(
    @('23.976', '24000/1001', 4, 'mp4', $true),
    @('24', '24', 4, 'mkv', $false),
    @('25', '25', 4, 'mp4', $false),
    @('29.97', '30000/1001', 6, 'mp4', $true),
    @('29.97', '30000/1001', 4, 'mkv', $true),
    @('30', '30', 4, 'mp4', $false),
    @('50', '50', 4, 'mkv', $false),
    @('59.94', '60000/1001', 4, 'mp4', $false),
    @('60', '60', 4, 'mp4', $true),
    @('120', '120', 4, 'mp4', $false),
    @('240', '240', 4, 'mp4', $false),
    @('240', '240', 4, 'mkv', $false)
)
foreach ($c in $cfr) {
    $name, $rate, $dur, $ext, $audio = $c
    $num, $den = ($rate + '/1').Split('/')[0, 1]
    $fps = [double]$num / [double]$den
    $a = @('-f', 'lavfi', '-i', "testsrc2=size=640x360:rate=${rate}:duration=$dur")
    if ($audio) { $a += @('-f', 'lavfi', '-i', "sine=frequency=440:duration=$dur") }
    $a += @('-vf', (Overlay "CFR $name" 640 360), '-c:v', 'libx264', '-preset', 'veryfast', '-g', [string][int][math]::Ceiling(2 * $fps), '-pix_fmt', 'yuv420p')
    if ($audio) { $a += @('-c:a', 'aac', '-shortest') }
    FF ($a + "$Out\fr_cfr_${name}fps.$ext")
}

# A container VidFlow's frame probe does not read (AVI): frame numbers must be marked as estimates.
FF @('-i', "$Out\fr_cfr_30fps.mp4", '-c', 'copy', "$Out\fr_unknown_30fps.avi")

# VFR: re-timed after the overlay (whose n is the source frame index), in a fine time base.
$vfr = @(
    @('jitter', "if(eq(N,0),0,PREV_OUTPTS+1/(29.5+1.75*sin(N*0.37))/TB)", 180),
    @('steps', "if(lt(N,90),N/30,if(lt(N,120),3+(N-90)/15,5+(N-120)/60))/TB", 180)
)
foreach ($v in $vfr) {
    $name, $expr, $frames = $v
    foreach ($ext in 'mp4', 'mkv') {
        $a = @('-f', 'lavfi', '-i', "testsrc2=size=640x360:rate=30", '-f', 'lavfi', '-i', 'sine=frequency=330:duration=8',
               '-vf', ((Overlay "VFR $name" 640 360) + ",settb=1/90000,setpts='$expr'"), '-frames:v', [string]$frames,
               '-fps_mode', 'passthrough', '-enc_time_base:v', '1/90000', '-c:v', 'libx264', '-preset', 'veryfast', '-g', '60', '-pix_fmt', 'yuv420p',
               '-c:a', 'aac', '-shortest')
        if ($ext -eq 'mp4') { $a += @('-video_track_timescale', '90000') }
        FF ($a + "$Out\fr_vfr_$name.$ext")
    }
}

# Back-step benchmark: long-GOP 4K HEVC, one keyframe in 10 s.
FF @('-f', 'lavfi', '-i', 'testsrc2=size=3840x2160:rate=25:duration=10', '-vf', (Overlay 'HEVC4K' 3840 2160),
     '-c:v', 'libx265', '-preset', 'ultrafast', '-crf', '26', '-x265-params', 'keyint=250:min-keyint=250:scenecut=0:log-level=error',
     '-tag:v', 'hvc1', '-pix_fmt', 'yuv420p', "$Out\fr_4k_hevc_gop250.mp4")

Get-ChildItem $Out -Filter 'fr_*' | ForEach-Object { "{0,12:N0}  {1}" -f $_.Length, $_.Name }
