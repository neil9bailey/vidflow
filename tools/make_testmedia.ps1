# Generates small test clips (frame numbers burned in) into .\testmedia using ffmpeg.
param([string]$Out = (Join-Path $PSScriptRoot '..\testmedia'))
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$font = 'C\:/Windows/Fonts/consola.ttf'
function Overlay([string]$label) {
    "drawtext=fontfile='$font':text='$label F%{frame_num} T%{pts\:hms}':fontsize=h/12:fontcolor=white:box=1:boxcolor=black@0.6:boxborderw=12:x=(w-tw)/2:y=h*0.08"
}
function FF([string[]]$a) {
    $target = $a[-1]
    if ((Test-Path $target) -and (Get-Item $target).Length -gt 0) { return }
    & ffmpeg -nostdin -hide_banner -loglevel error -y @a
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed: $a" }
}

FF @('-f','lavfi','-i','testsrc2=size=1280x720:rate=30:duration=8','-f','lavfi','-i','sine=frequency=440:duration=8',
     '-vf',(Overlay 'C01'),'-c:v','libx264','-preset','veryfast','-g','60','-pix_fmt','yuv420p','-c:a','aac','-shortest',"$Out\clip01_h264_720p30.mp4")

FF @('-f','lavfi','-i','testsrc2=size=1080x1920:rate=60:duration=5','-f','lavfi','-i','sine=frequency=660:duration=5',
     '-vf',(Overlay 'C02'),'-c:v','libx264','-preset','veryfast','-g','120','-pix_fmt','yuv420p','-c:a','aac','-shortest',"$Out\clip02_portrait_60fps.mp4")

FF @('-f','lavfi','-i','smptehdbars=size=640x360:rate=25:duration=6','-f','lavfi','-i','sine=frequency=330:duration=6',
     '-vf',(Overlay 'C03'),'-c:v','libvpx-vp9','-b:v','400k','-deadline','realtime','-cpu-used','8','-c:a','libopus','-shortest',"$Out\clip03_vp9.webm")

# HEVC, two audio tracks, embedded subtitles, chapters
$srt = @"
1
00:00:00,500 --> 00:00:03,000
Embedded subtitle one

2
00:00:04,000 --> 00:00:07,000
Embedded subtitle two

3
00:00:08,000 --> 00:00:11,500
Embedded subtitle three
"@
Set-Content -Encoding UTF8 "$Out\_embedded.srt" $srt
$meta = @"
;FFMETADATA1
title=VidFlow multi-track test

[CHAPTER]
TIMEBASE=1/1000
START=0
END=4000
title=Intro

[CHAPTER]
TIMEBASE=1/1000
START=4000
END=8000
title=Middle

[CHAPTER]
TIMEBASE=1/1000
START=8000
END=12000
title=Ending
"@
Set-Content -Encoding ASCII "$Out\_chapters.txt" $meta
FF @('-f','lavfi','-i','testsrc2=size=1280x720:rate=24:duration=12',
     '-f','lavfi','-i','sine=frequency=440:duration=12','-f','lavfi','-i','sine=frequency=880:duration=12',
     '-i',"$Out\_embedded.srt",'-i',"$Out\_chapters.txt",
     '-map','0:v','-map','1:a','-map','2:a','-map','3:s','-map_metadata','4','-map_chapters','4',
     '-vf',(Overlay 'C04'),'-c:v','libx265','-preset','ultrafast','-x265-params','log-level=error','-pix_fmt','yuv420p',
     '-c:a','aac','-c:s','srt',
     '-metadata:s:a:0','language=eng','-metadata:s:a:0','title=English 440Hz',
     '-metadata:s:a:1','language=jpn','-metadata:s:a:1','title=Japanese 880Hz',
     '-metadata:s:s:0','language=eng','-t','12',"$Out\clip04_hevc_multitrack.mkv")
Remove-Item "$Out\_embedded.srt", "$Out\_chapters.txt"

FF @('-f','lavfi','-i','testsrc=size=640x480:rate=30:duration=4','-vf',(Overlay 'C05'),
     '-c:v','libsvtav1','-preset','12','-pix_fmt','yuv420p',"$Out\clip05_av1_noaudio.mkv")

# External subtitle picked up automatically for clip01 (sub-auto=fuzzy)
Set-Content -Encoding UTF8 "$Out\clip01_h264_720p30.srt" @"
1
00:00:01,000 --> 00:00:04,000
External SRT line for clip one

2
00:00:05,000 --> 00:00:07,500
Second external line
"@

Get-ChildItem $Out | ForEach-Object { "{0,10:N0}  {1}" -f $_.Length, $_.Name }
