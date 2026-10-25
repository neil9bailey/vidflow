#Requires -Version 7.0
<#
.SYNOPSIS
  Generates the synthetic release-gate samples (testmedia\gate) and the benchmark clips
  (testmedia\gate\bench) with ffmpeg. Nothing here opens a window: ffmpeg runs windowless at
  below-normal priority with a capped thread count, so it does not get in the way of whoever
  is using the machine.
.DESCRIPTION
  Every video frame shows the same test card: mid-grey background, a red marker in the
  top-left corner, green top-right, blue bottom-left, a white "SDR reference white" patch in
  the centre and a burned-in label with frame number and timestamp. tools\bench.ps1 -Suite gate
  uses the markers to check rotation and aspect, and the white patch to check HDR tone mapping.

  Gate set (testmedia\gate):            Bench set (testmedia\gate\bench):
    HDR10 PQ HEVC 10-bit + mastering      H.264 1080p30, H.264 1080p60
    HLG HEVC 10-bit (iPhone-style .mov)   HEVC 10-bit 2160p30 with a long GOP (keyint 250)
    HLG with a Dolby Vision 8.4 record    VP9 1080p30, AV1 1080p30
    rotation 90/180/270 (+ HLG 180) .mov
    120 and 240 fps, VFR phone-style
    AV1 10-bit, 1080x1350, 4096x2160, anamorphic 1440x1080 (SAR 4:3)
    audio-only (.m4a, .mp3)
    broken: 0 bytes, missing moov, truncated, garbage, bit rot
    unicode name, special characters, path longer than 260 characters
  Existing non-empty outputs are kept unless -Force is given.

  Needs ffmpeg on PATH with libx264, libx265, libvpx, libsvtav1, libopus, libmp3lame and zimg
  (zscale), e.g. the gyan.dev "full" build (winget install Gyan.FFmpeg). Encoder versions
  differ between builds, so files are equivalent rather than byte-identical across machines.
.EXAMPLE
  .\tools\make_gatemedia.ps1                 # everything
  .\tools\make_gatemedia.ps1 -Set bench      # only the benchmark clips
  .\tools\make_gatemedia.ps1 -Set gate -Force
#>
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\testmedia\gate'),
    [ValidateSet('all', 'gate', 'bench')][string]$Set = 'all',
    [int]$Threads = 4,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path -LiteralPath $Out).Path
$benchDir = Join-Path $Out 'bench'
$ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source
$font = 'C\:/Windows/Fonts/consola.ttf'

# ---- helpers ---------------------------------------------------------------------------------

# Runs ffmpeg windowless at below-normal priority; throws with its error output on failure.
function Invoke-FFmpeg([string[]]$a) {
    $psi = [Diagnostics.ProcessStartInfo]::new($ffmpeg)
    foreach ($x in @('-nostdin', '-hide_banner', '-loglevel', 'error', '-y') + $a) { $psi.ArgumentList.Add($x) }
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardOutput = $true
    $p = [Diagnostics.Process]::Start($psi)
    try { $p.PriorityClass = [Diagnostics.ProcessPriorityClass]::BelowNormal } catch { }
    $err = $p.StandardError.ReadToEndAsync()
    $null = $p.StandardOutput.ReadToEndAsync()
    $p.WaitForExit()
    if ($p.ExitCode -ne 0) { throw "ffmpeg failed ($($p.ExitCode)): $($err.Result.Trim())`n  args: $($a -join ' ')" }
}

function Done([string]$path) {
    return (-not $Force) -and (Test-Path -LiteralPath $path) -and (Get-Item -LiteralPath $path).Length -gt 0
}

# Encodes into a temporary name next to the target (same extension, so ffmpeg picks the
# muxer) and renames it on success: an interrupted run never leaves a half-written sample.
function FF([string]$target, [string[]]$a) {
    if (Done $target) { Write-Host ("  kept     {0}" -f (Split-Path -Leaf $target)); return }
    $dir = Split-Path -Parent $target
    New-Item -ItemType Directory -Force $dir | Out-Null
    $tmp = Join-Path $dir ('~part_' + [IO.Path]::GetFileName($target))
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try { Invoke-FFmpeg ($a + @($tmp)) }
    catch { if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }; throw }
    Move-Item -LiteralPath $tmp -Destination $target -Force
    Write-Host ("  made     {0}  ({1:N1} MB, {2:N0} s)" -f (Split-Path -Leaf $target), ((Get-Item -LiteralPath $target).Length / 1MB), $sw.Elapsed.TotalSeconds)
}

# The test card as a lavfi source (8-bit yuv420p). Markers: red top-left, green top-right,
# blue bottom-left; white patch in the centre; label + frame number + time near the top.
function Card([int]$w, [int]$h, [double]$fps, [double]$dur, [string]$label, [string]$base = 'color=c=0x5a5a5a') {
    $fs = [int][math]::Max(14, [math]::Min($w, $h) / 18)
    $r = [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0}', $fps)
    $d = [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0}', $dur)
    $src = if ($base -like 'color=*') { "${base}:s=${w}x${h}:r=${r}:d=${d}" } else { "${base}=s=${w}x${h}:r=${r}:d=${d}" }
    return "$src,format=yuv420p," +
        "drawbox=x=0:y=0:w=iw/8:h=ih/8:color=red:t=fill," +
        "drawbox=x=iw-iw/8:y=0:w=iw/8:h=ih/8:color=0x00FF00:t=fill," +
        "drawbox=x=0:y=ih-ih/8:w=iw/8:h=ih/8:color=blue:t=fill," +
        "drawbox=x=iw*0.42:y=ih*0.42:w=iw*0.16:h=ih*0.16:color=white:t=fill," +
        "drawtext=fontfile='$font':text='$label F%{frame_num} T%{pts\:hms}':fontsize=${fs}:fontcolor=white:box=1:boxcolor=black@0.6:boxborderw=8:x=(w-tw)/2:y=h*0.15"
}

function Sine([double]$dur, [int]$freq = 440) {
    $d = [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0}', $dur)
    return @('-f', 'lavfi', '-i', "sine=frequency=${freq}:duration=${d}:sample_rate=48000")
}

# BT.709 SDR -> BT.2020 PQ or HLG, 10-bit. npl = the luminance (nits) SDR white lands on.
function ToHdr([string]$trc, [int]$npl) {
    return "zscale=tin=bt709:pin=bt709:min=bt709:rin=tv:t=${trc}:p=bt2020:m=bt2020nc:r=tv:npl=${npl},format=yuv420p10le"
}

$x265 = "log-level=error:pools=$Threads"
$x264 = @('-c:v', 'libx264', '-preset', 'veryfast', '-threads', "$Threads", '-pix_fmt', 'yuv420p')
$aac = @('-c:a', 'aac', '-b:a', '128k', '-shortest')

# The first `fraction` of a byte array (a truncated download).
function Head([byte[]]$bytes, [double]$fraction) {
    $n = [int]($bytes.Length * $fraction)
    $part = [byte[]]::new($n)
    [Array]::Copy($bytes, $part, $n)
    return , $part
}

# Copies a QuickTime/MP4 file, appending a Dolby Vision configuration box to its first hvc1
# sample entry and growing every enclosing box. Requires moov after mdat (ffmpeg's default
# without +faststart), so no chunk offsets move.
function Add-DoviConfig([string]$src, [string]$dst) {
    $b = [IO.File]::ReadAllBytes($src)
    $u32 = { param([int]$o) ([long]$b[$o] -shl 24) -bor ([long]$b[$o + 1] -shl 16) -bor ([long]$b[$o + 2] -shl 8) -bor [long]$b[$o + 3] }
    $type = { param([int]$o) [Text.Encoding]::ASCII.GetString($b, $o + 4, 4) }
    # Child boxes of [start, end) with the given type.
    $find = {
        param([int]$start, [int]$end, [string]$want)
        $list = @(); $o = $start
        while ($o + 8 -le $end) {
            $size = & $u32 $o
            if ($size -lt 8 -or $o + $size -gt $end) { throw "unexpected box size $size at $o" }
            if ((& $type $o) -eq $want) { $list += $o }
            $o += [int]$size
        }
        $list
    }
    $moov = @(& $find 0 $b.Length 'moov')
    $mdat = @(& $find 0 $b.Length 'mdat')
    if ($moov.Count -ne 1 -or $mdat.Count -lt 1 -or $moov[0] -lt $mdat[0]) { throw 'needs exactly one moov, after mdat' }
    $chain = $null
    foreach ($trak in (& $find ($moov[0] + 8) ($moov[0] + (& $u32 $moov[0])) 'trak')) {
        $path = @($moov[0], $trak); $ok = $true
        foreach ($t in 'mdia', 'minf', 'stbl', 'stsd') {
            $p = $path[-1]
            $hits = @(& $find ($p + 8) ($p + (& $u32 $p)) $t)
            if ($hits.Count -eq 0) { $ok = $false; break }
            $path += $hits[0]
        }
        if (-not $ok) { continue }
        $stsd = $path[-1]
        $hvc = @(& $find ($stsd + 16) ($stsd + (& $u32 $stsd)) 'hvc1')   # full box: version/flags + entry count
        if ($hvc.Count -gt 0) { $chain = $path + $hvc[0]; break }
    }
    if (-not $chain) { throw 'no hvc1 sample entry found' }
    # DOVIDecoderConfigurationRecord: version 1.0, profile 8, level 4 (1080p30), RPU + BL present,
    # base-layer compatibility 4 (HLG); the remaining 20 bytes are reserved zeros.
    $rec = [byte[]]::new(24)
    $rec[0] = 1; $rec[1] = 0
    $profile = 8; $level = 4; $rpu = 1; $el = 0; $bl = 1; $compat = 4
    $bits = ($profile -shl 9) -bor ($level -shl 3) -bor ($rpu -shl 2) -bor ($el -shl 1) -bor $bl
    $rec[2] = [byte](($bits -shr 8) -band 0xFF); $rec[3] = [byte]($bits -band 0xFF)
    $rec[4] = [byte]($compat -shl 4)
    $box = [byte[]]::new(32)
    $box[3] = 32
    [Text.Encoding]::ASCII.GetBytes('dvvC').CopyTo($box, 4)
    $rec.CopyTo($box, 8)
    $insertAt = $chain[-1] + [int](& $u32 $chain[-1])
    foreach ($o in $chain) {
        $size = (& $u32 $o) + 32
        $b[$o] = [byte](($size -shr 24) -band 0xFF); $b[$o + 1] = [byte](($size -shr 16) -band 0xFF)
        $b[$o + 2] = [byte](($size -shr 8) -band 0xFF); $b[$o + 3] = [byte]($size -band 0xFF)
    }
    $outBytes = [byte[]]::new($b.Length + 32)
    [Array]::Copy($b, 0, $outBytes, 0, $insertAt)
    [Array]::Copy($box, 0, $outBytes, $insertAt, 32)
    [Array]::Copy($b, $insertAt, $outBytes, $insertAt + 32, $b.Length - $insertAt)
    [IO.File]::WriteAllBytes($dst, $outBytes)
}

# Writes `count` deterministic pseudo-random bytes.
function Write-Garbage([string]$path, [int]$count, [int]$seed) {
    $buf = [byte[]]::new($count)
    [Random]::new($seed).NextBytes($buf)
    [IO.File]::WriteAllBytes($path, $buf)
}

# ---- benchmark clips -------------------------------------------------------------------------
# Noise is added so the bitrate (and so the decode work) is close to real camera footage:
# about 16 Mbps for 1080p30, 25 Mbps for 1080p60 and 40 Mbps for 2160p30 HEVC.

function Make-Bench {
    Write-Host "bench clips -> $benchDir"
    $noise = 'noise=alls=12:allf=t'
    FF "$benchDir\b1_h264_1080p30.mp4" (@('-f', 'lavfi', '-i', ((Card 1920 1080 30 10 'B1 H264 1080p30' 'testsrc2') + ",$noise")) + (Sine 10) +
        $x264 + @('-g', '60', '-b:v', '16M', '-maxrate', '20M', '-bufsize', '32M') + $aac + @('-movflags', '+faststart'))
    FF "$benchDir\b2_h264_1080p60.mp4" (@('-f', 'lavfi', '-i', ((Card 1920 1080 60 20 'B2 H264 1080p60' 'testsrc2') + ",$noise")) + (Sine 20) +
        $x264 + @('-g', '120', '-b:v', '25M', '-maxrate', '30M', '-bufsize', '50M') + $aac + @('-movflags', '+faststart'))
    # Long GOP: one keyframe every 250 frames (x265's default keyint), the worst case for stepping back.
    FF "$benchDir\b3_hevc10_2160p30_gop250.mp4" (@('-f', 'lavfi', '-i', ((Card 3840 2160 30 10 'B3 HEVC10 2160p30 GOP250' 'testsrc2') + ",$noise")) + (Sine 10) +
        @('-c:v', 'libx265', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p10le', '-tag:v', 'hvc1',
          '-x265-params', "${x265}:keyint=250:min-keyint=250:scenecut=0:bitrate=40000:vbv-maxrate=50000:vbv-bufsize=80000") + $aac + @('-movflags', '+faststart'))
    FF "$benchDir\b4_vp9_1080p30.webm" (@('-f', 'lavfi', '-i', ((Card 1920 1080 30 10 'B4 VP9 1080p30' 'testsrc2') + ",$noise")) + (Sine 10) +
        @('-c:v', 'libvpx-vp9', '-deadline', 'realtime', '-cpu-used', '8', '-row-mt', '1', '-threads', "$Threads", '-g', '60', '-b:v', '12M',
          '-c:a', 'libopus', '-b:a', '128k', '-shortest'))
    FF "$benchDir\b5_av1_1080p30.mkv" (@('-f', 'lavfi', '-i', ((Card 1920 1080 30 10 'B5 AV1 1080p30' 'testsrc2') + ",$noise")) + (Sine 10) +
        @('-c:v', 'libsvtav1', '-preset', '12', '-g', '60', '-b:v', '10M', '-pix_fmt', 'yuv420p', '-c:a', 'libopus', '-b:a', '128k', '-shortest'))
}

# ---- release-gate samples --------------------------------------------------------------------

function Make-Gate {
    Write-Host "gate samples -> $Out"
    $tmpDir = Join-Path $Out '_tmp'
    New-Item -ItemType Directory -Force $tmpDir | Out-Null

    # SDR reference: same card as the HDR samples, for comparing tone-mapped screenshots.
    FF "$Out\g00_sdr_reference_1080p.mp4" (@('-f', 'lavfi', '-i', (Card 1920 1080 30 5 'G00 SDR REF')) + (Sine 5) + $x264 + $aac)

    # HDR10: PQ, BT.2020, 10-bit, mastering display + content light level SEI. SDR white is
    # placed at 203 nits (BT.2408 reference white); the bottom-right patch is 1000 nits.
    $hdrParams = "${x265}:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:range=limited:repeat-headers=1" +
        ":master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1):max-cll=1000,400"
    FF "$Out\g01_hdr10_pq_hevc10_1080p.mp4" (@('-f', 'lavfi', '-i', (Card 1920 1080 30 5 'G01 HDR10 PQ'),
        '-f', 'lavfi', '-i', 'color=c=white:s=240x136:r=30:d=5,format=yuv420p') + (Sine 5) +
        @('-filter_complex', "[0:v]$(ToHdr 'smpte2084' 203)[b];[1:v]$(ToHdr 'smpte2084' 1000)[hi];[b][hi]overlay=x=W-w:y=H-h:format=yuv420p10[v]",
          '-map', '[v]', '-map', '2:a', '-c:v', 'libx265', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p10le', '-tag:v', 'hvc1',
          '-x265-params', $hdrParams, '-color_primaries', 'bt2020', '-color_trc', 'smpte2084', '-colorspace', 'bt2020nc', '-color_range', 'tv') + $aac)

    # HLG, iPhone-style HEVC 10-bit .mov
    $hlgParams = "${x265}:colorprim=bt2020:transfer=arib-std-b67:colormatrix=bt2020nc:range=limited:repeat-headers=1"
    $hlgArgs = @('-c:v', 'libx265', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p10le', '-tag:v', 'hvc1', '-x265-params', $hlgParams,
        '-color_primaries', 'bt2020', '-color_trc', 'arib-std-b67', '-colorspace', 'bt2020nc', '-color_range', 'tv')
    FF "$Out\g02_hlg_hevc10_1080p.mov" (@('-f', 'lavfi', '-i', ((Card 1920 1080 30 5 'G02 HLG') + ',' + (ToHdr 'arib-std-b67' 203))) + (Sine 5) + $hlgArgs + $aac)

    # Dolby-Vision-like: HLG base layer + a Dolby Vision configuration box (dvvC, profile 8.4 =
    # HLG-compatible, the iPhone signalling). ffmpeg's encoders only write Dolby Vision when the
    # source frames carry RPU metadata, which cannot be synthesised, so the box is added to the
    # HLG file directly. There are no RPU NAL units: players must fall back to the HLG base layer.
    $dvTarget = "$Out\g03_dolbyvision84_like_hlg_1080p.mov"
    $dvBase = Join-Path $tmpDir 'dv_base.mov'
    FF $dvBase (@('-f', 'lavfi', '-i', ((Card 1920 1080 30 5 'G03 DV8.4-like HLG') + ',' + (ToHdr 'arib-std-b67' 203))) + (Sine 5) + $hlgArgs + $aac)
    if (-not (Done $dvTarget)) {
        try {
            Add-DoviConfig $dvBase $dvTarget
            Write-Host "  made     $(Split-Path -Leaf $dvTarget)  (HLG + dvvC profile 8.4, no RPUs)"
        } catch { Write-Host "  skipped  $(Split-Path -Leaf $dvTarget): $($_.Exception.Message)" -ForegroundColor Yellow }
    } else { Write-Host "  kept     $(Split-Path -Leaf $dvTarget)" }

    # Rotation metadata (display matrix), iPhone-style HEVC .mov. The stored picture is landscape.
    $rotBase = Join-Path $tmpDir 'rot_base.mov'
    FF $rotBase (@('-f', 'lavfi', '-i', (Card 1920 1080 30 4 'G04 ROTATION')) + (Sine 4) +
        @('-c:v', 'libx265', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p', '-tag:v', 'hvc1', '-x265-params', $x265) + $aac)
    foreach ($deg in 90, 180, 270) {
        FF ("$Out\g04_rotate_{0:000}_hevc.mov" -f $deg) @('-display_rotation:v:0', "$deg", '-i', $rotBase, '-map', '0', '-c', 'copy')
    }
    $hlgRotBase = Join-Path $tmpDir 'hlg_rot_base.mov'
    FF $hlgRotBase (@('-f', 'lavfi', '-i', ((Card 1920 1080 30 4 'G05 HLG ROT180') + ',' + (ToHdr 'arib-std-b67' 203))) + (Sine 4) + $hlgArgs + $aac)
    FF "$Out\g05_hlg_rotate_180_hevc10.mov" @('-display_rotation:v:0', '180', '-i', $hlgRotBase, '-map', '0', '-c', 'copy')

    # High frame rates (slow-motion phone modes)
    FF "$Out\g06_h264_1080p120.mp4" (@('-f', 'lavfi', '-i', (Card 1920 1080 120 5 'G06 120FPS')) + (Sine 5) + $x264 + @('-g', '120') + $aac)
    FF "$Out\g07_hevc_1080p240.mov" (@('-f', 'lavfi', '-i', (Card 1920 1080 240 2 'G07 240FPS')) + (Sine 2) +
        @('-c:v', 'libx265', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p', '-tag:v', 'hvc1', '-x265-params', "${x265}:keyint=240") + $aac)

    # VFR, phone-style: ~30 fps with 0-16 ms jitter, a 24 fps low-light stretch from 4 s to 6 s,
    # QuickTime timescale 600. The burned-in F number is the true frame index.
    $vfrPts = "setpts='(if(lt(N,120),N/30,if(lt(N,168),4+(N-120)/24,6+(N-168)/30))+0.004*mod(N*7,5))/TB'"
    FF "$Out\g08_vfr_phone_h264_1080p.mp4" (@('-f', 'lavfi', '-i', ((Card 1920 1080 30 8 'G08 VFR') + ",$vfrPts")) + (Sine 9) +
        $x264 + @('-g', '30', '-fps_mode', 'passthrough', '-video_track_timescale', '600') + $aac)

    # AV1 10-bit
    FF "$Out\g09_av1_10bit_1080p.mkv" (@('-f', 'lavfi', '-i', ((Card 1920 1080 30 4 'G09 AV1 10BIT') + ',format=yuv420p10le')) + (Sine 4) +
        @('-c:v', 'libsvtav1', '-preset', '10', '-pix_fmt', 'yuv420p10le', '-c:a', 'libopus', '-b:a', '128k', '-shortest'))

    # Odd sizes and a non-square pixel aspect ratio
    FF "$Out\g10_h264_1080x1350_portrait45.mp4" (@('-f', 'lavfi', '-i', (Card 1080 1350 30 4 'G10 1080x1350')) + (Sine 4) + $x264 + $aac)
    FF "$Out\g11_h264_4096x2160_dci.mp4" (@('-f', 'lavfi', '-i', (Card 4096 2160 25 3 'G11 4096x2160')) + (Sine 3) + $x264 + @('-level', '5.2') + $aac)
    FF "$Out\g12_h264_1440x1080_sar4-3.mp4" (@('-f', 'lavfi', '-i', ((Card 1440 1080 30 4 'G12 SAR 4-3') + ',setsar=4/3')) + (Sine 4) + $x264 + $aac)

    # Audio only
    FF "$Out\g13_audio_only.m4a" ((Sine 6 523) + @('-c:a', 'aac', '-b:a', '128k', '-metadata', 'title=VidFlow gate audio-only sample'))
    FF "$Out\g14_audio_only.mp3" ((Sine 6 659) + @('-c:a', 'libmp3lame', '-b:a', '160k', '-metadata', 'title=VidFlow gate mp3 sample'))

    # Broken files
    $small = Join-Path $tmpDir 'small_720p.mp4'           # moov at the end (no faststart)
    FF $small (@('-f', 'lavfi', '-i', (Card 1280 720 30 6 'G SMALL')) + (Sine 6) + $x264 + $aac)
    $smallFast = Join-Path $tmpDir 'small_720p_faststart.mp4'
    FF $smallFast (@('-f', 'lavfi', '-i', (Card 1280 720 30 6 'G TRUNC')) + (Sine 6) + $x264 + $aac + @('-movflags', '+faststart'))
    $smallMkv = Join-Path $tmpDir 'small_720p.mkv'
    FF $smallMkv (@('-f', 'lavfi', '-i', (Card 1280 720 30 6 'G BITROT')) + (Sine 6) + $x264 + $aac)

    $zero = "$Out\g20_broken_zero_bytes.mp4"
    if ($Force -or -not (Test-Path -LiteralPath $zero)) { [IO.File]::WriteAllBytes($zero, [byte[]]::new(0)); Write-Host "  made     $(Split-Path -Leaf $zero)" }
    $noMoov = "$Out\g21_broken_missing_moov.mp4"
    if (-not (Done $noMoov)) {
        $bytes = [IO.File]::ReadAllBytes($small)
        [IO.File]::WriteAllBytes($noMoov, (Head $bytes 0.6))
        Write-Host "  made     $(Split-Path -Leaf $noMoov)  (first 60% of an mp4 whose index is at the end)"
    } else { Write-Host "  kept     $(Split-Path -Leaf $noMoov)" }
    $trunc = "$Out\g22_broken_truncated_half.mp4"
    if (-not (Done $trunc)) {
        $bytes = [IO.File]::ReadAllBytes($smallFast)
        [IO.File]::WriteAllBytes($trunc, (Head $bytes 0.5))
        Write-Host "  made     $(Split-Path -Leaf $trunc)  (first 50% of a faststart mp4)"
    } else { Write-Host "  kept     $(Split-Path -Leaf $trunc)" }
    $garbage = "$Out\g23_broken_garbage.mp4"
    if (-not (Done $garbage)) { Write-Garbage $garbage (300 * 1024) 1234; Write-Host "  made     $(Split-Path -Leaf $garbage)  (random bytes)" }
    else { Write-Host "  kept     $(Split-Path -Leaf $garbage)" }
    $rot = "$Out\g24_broken_bitrot.mkv"
    if (-not (Done $rot)) {
        # Keep the first 64 KB (headers) intact, then overwrite 64 bytes in every 32 KB.
        $bytes = [IO.File]::ReadAllBytes($smallMkv)
        $rnd = [Random]::new(99)
        for ($i = 65536; $i -lt $bytes.Length - 64; $i += 32768) { for ($j = 0; $j -lt 64; $j++) { $bytes[$i + $j] = [byte]$rnd.Next(256) } }
        [IO.File]::WriteAllBytes($rot, $bytes)
        Write-Host "  made     $(Split-Path -Leaf $rot)  (64 random bytes every 32 KB)"
    } else { Write-Host "  kept     $(Split-Path -Leaf $rot)" }

    # Names: unicode, shell-special characters, and a full path longer than 260 characters.
    $named = Join-Path $tmpDir 'named_720p.mp4'
    FF $named (@('-f', 'lavfi', '-i', (Card 1280 720 30 4 'G30 NAME TEST')) + (Sine 4) + $x264 + $aac + @('-movflags', '+faststart'))
    $jp = [string]::new([char[]]@(0x65E5, 0x672C, 0x8A9E)) + '_clip.mp4'   # 日本語_clip.mp4
    foreach ($name in @($jp, 'g31 name with spaces & $dollar (1).mp4')) {
        $dst = Join-Path $Out $name
        if ($Force -or -not (Test-Path -LiteralPath $dst)) { Copy-Item -LiteralPath $named -Destination $dst -Force; Write-Host "  made     $name" }
        else { Write-Host "  kept     $name" }
    }
    $long = Join-Path $Out 'g32_longpath'
    foreach ($i in 1..4) { $long = Join-Path $long (('deep_folder_level_{0}_' -f $i).PadRight(60, 'x')) }
    $longFile = Join-Path $long 'g32_long_path_clip.mp4'
    if ($Force -or -not (Test-Path -LiteralPath $longFile)) {
        [IO.Directory]::CreateDirectory($long) | Out-Null   # .NET (Core) handles paths past MAX_PATH itself
        [IO.File]::Copy($named, $longFile, $true)
        Write-Host ("  made     long path ({0} characters)" -f $longFile.Length)
    } else { Write-Host ("  kept     long path ({0} characters)" -f $longFile.Length) }
}

if ($Set -in 'all', 'bench') { Make-Bench }
if ($Set -in 'all', 'gate') { Make-Gate }
Write-Host 'done.'
