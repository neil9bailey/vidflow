# VidFlow speed numbers (roadmap L10, L2 baseline)

How fast VidFlow starts, switches clips, steps back and seeks, how much memory and CPU it uses,
measured through its automation pipe by `tools\bench.ps1`. The method and the caveats matter
as much as the numbers: read "How it is measured" before quoting anything.

## Summary

* **Next clip is above the L10 threshold.** Pressing Next on 1080p H.264 shows the new clip's
  first frame after **568 ms** (median, p95 773 ms), against the roadmap's ~300 ms trigger for
  N7 (pre-load the next clip). About half of that is per-file hardware-decoder setup: with
  hardware decoding off the same switch takes **302 ms**. AV1 on a GPU without AV1 decode is
  much worse (**1483 ms**) because mpv probes three hardware decoders and fails each time
  before falling back to software (254 ms with hardware decoding off).
* **Back-stepping on long-GOP 4K is seconds, not milliseconds** (L2 baseline). One step back on
  4K HEVC 10-bit with a 250-frame GOP takes **0.53 s** one second after a keyframe, **1.7 s**
  mid-GOP and **3.0-3.5 s** at the end of a GOP, and also within 0.5 s after a keyframe,
  because mpv starts a back step's decode at the last keyframe at least 0.5 s before the frame.
  All 84 landed frames were the right ones.
* **Exact seeks on 4K long-GOP need L8.** Exact seek: **2.0 s** median (p95 4.5 s); keyframe
  seek: **0.23 s**. On 1080p H.264 with a 2 s GOP exact seeks are already quick (95 ms vs 47 ms).
* **Cold start to first frame: 2.9 s** median (best 1.8 s), of which about 1.2 s passes between
  loading libmpv and mpv reporting ready.
* **Memory:** 60 MB working set / 45 MB private bytes idle; 133 / 269 MB paused on 1080p;
  **1.2 GB private bytes while playing 4K HEVC** (mpv's 256 MiB + 256 MiB demuxer caches plus
  decoder surfaces, which count against process memory on an integrated GPU).
* **CPU while playing (hardware decoding):** 29-44 % of one core for 1080p60 and 23-25 % for
  4K30 HEVC 10-bit (lower number from the quieter repeat run), of which the UI thread alone
  uses 8-14 % and 10 % of a core.

The machine was heavily used by other work during every section (other processes used 32-76 %
of all cores, see "Background load"); these are numbers from a busy mid-range laptop, not from
an idle one. Ratios between variants measured side by side are more trustworthy than absolute
values.

## Machine and build

| | |
|---|---|
| CPU | AMD Ryzen 5 3500U (4 cores / 8 threads), laptop, Balanced power plan, on AC power |
| GPU | AMD Radeon Vega 8 (integrated), driver 31.0.21918.12; hardware decode: H.264, HEVC Main10, VP9 (d3d11va); no AV1 |
| RAM | 14.9 GB |
| OS | Windows 11 Pro 10.0.26200, build 26200.9457 |
| Media | Fanxiang S101 2 TB SSD over USB (every clip was read many times, so it sat in the OS file cache) |
| Build | private `-NoEmbed` build of the tree as it stood at 2026-10-24 19:25 (`obj\work\bench-gate\app\VidFlow.exe`, SHA-256 848D3EAD854AD2FA...), libmpv mpv v0.41.0-1115-gb2c255c13 / ffmpeg N-127267-gef305e32e, `hwdec=auto-safe`, `vo=gpu-next` |
| Measured | 2026-10-24, 20:15-20:47 (main run, then the back-step, no-hwdec and CPU repeats) |

## Results

All times in ms. 7 runs per measurement unless noted; p95 is nearest-rank, so with 7 samples
it equals the slowest run.

### Cold start (process start -> first frame), H.264 1080p30 passed on the command line

| Milestone (ms after `Process.Start`) | median | p95 | min | max |
|---|---:|---:|---:|---:|
| main window exists | 1398 | 2472 | 816 | 2472 |
| control pipe answers | 1727 | 3256 | 984 | 3256 |
| file loaded (dump `frame` >= 0) | 2072 | 4337 | 1182 | 4337 |
| first frame rendered (mpv `video-frame-info` available) | 2931 | 6013 | 1820 | 6013 |
| playback started (mpv `seeking` = no) | 2933 | 6016 | 1824 | 6016 |

From VidFlow's own log (ms after the kernel created the process, which happened 3-24 ms after
`Process.Start`): libmpv loaded 261 (165-397), mpv initialised 1500 (827-2625), control pipe
listening 1559 (855-2796). Quit command to process exit: 460 (299-974).

So a cold start is roughly: 0.26 s loading the 120 MB libmpv DLL, 1.2 s building the window and
initialising mpv, 0.5 s opening the file, 0.85 s until the first frame is decoded and drawn
(decoder and renderer set-up). This is a warm-cache "cold" start: the files were in the OS
cache, so a first launch after a reboot will be slower. The release build (single exe with the
engine embedded) extracts libmpv to `%LOCALAPPDATA%\VidFlow\engine` on its first run and loads it
from there afterwards; neither case was measured.

### Next clip to first frame

From clip01 (H.264 720p) playing, `NextFile` action -> target. "Loaded" is the definition in
the task (dump shows `frame` >= 0); it is reached ~50 ms after the key press in every case and
says nothing about the picture. "First frame" is mpv's playback-restart point (`seeking` = no),
which is when the new clip's first frame is on screen. One unmeasured warm-up switch per target.

| Target | hwdec used | loaded (median) | first frame median | p95 | min | max |
|---|---|---:|---:|---:|---:|---:|
| H.264 1080p30, 16.7 Mbps | d3d11va | 50 | **568** | 773 | 458 | 773 |
| HEVC 10-bit 2160p30, 41 Mbps | d3d11va | 55 | **691** | 1067 | 617 | 1067 |
| VP9 1080p30, 18 Mbps | d3d11va | 48 | **598** | 710 | 469 | 710 |
| AV1 1080p30, 10.7 Mbps | software (after failed probes) | 47 | **1483** | 1664 | 1107 | 1664 |

Same switch with hardware decoding off (`-ExtraIni 'HwDec=no'`, run straight after):

| Target | first frame median | p95 | min | max |
|---|---:|---:|---:|---:|
| H.264 1080p30 | **302** | 310 | 265 | 310 |
| HEVC 10-bit 2160p30 | **640** | 807 | 584 | 807 |
| VP9 1080p30 | **295** | 323 | 275 | 323 |
| AV1 1080p30 | **254** | 288 | 239 | 288 |

The second run happened while the machine was less loaded (other processes 32 % vs 57 % of all
cores), so part of the gap is load. An earlier 3-run diagnostic with both variants under the
same heavy load (64-77 %) showed the same pattern: H.264 953 vs 510 ms, VP9 1075 vs 469 ms,
AV1 2081 vs 397 ms, HEVC 4K 1350 vs 1728 ms (software 4K HEVC decoding is the one case where
hardware wins). The AV1 cost is visible in the log: per AV1 file mpv prints 14 hardware-decoder
errors and spends 1.0 s between the d3d11va and dxva2 attempts.

### Memory

Fresh process each run; working set (WS) and private bytes from the OS.

| State | WS median (min-max) MB | private bytes median (min-max) MB |
|---|---:|---:|
| idle, no file, 5 s after the pipe answered | 59.8 (59.7-59.9) | 45.3 (45.3-45.4) |
| paused on H.264 1080p30, 3 s after start | 132.8 (132.5-132.9) | 268.8 (268.4-269.0) |
| playing HEVC 10-bit 2160p30 for 5 s | 233.1 (232.8-693.8) | 1198.3 (1197.1-1198.6) |
| peak WS during the run | 233.3 (233.1-694.0) | |

Working set swings with what Windows trims; private bytes are stable. Most of the 1.2 GB is
mpv's demuxer caches (`demuxer-max-bytes` and `demuxer-max-back-bytes`, both 256 MiB in
docs\SPEC.md) and decoder/render surfaces, which an integrated GPU allocates in system memory.

### CPU while playing

The clip loops (Repeat One); after 3 s of warm-up, 7 windows of 5 s each. VidFlow's total CPU
time and that of its UI thread (the process's first thread, which runs the WinForms message
loop and handles every mpv event) come from the OS; "% of one core" = CPU ms / wall ms (divide
by 8 for % of this machine). Hardware decoding (d3d11va) in both cases; mpv paced against
60 Hz. Two runs: the main one while other work used ~52 % of the machine, a repeat
(`-Only cpu`) at ~34 %.

| Clip | run | process, % of a core: median (min-max) | UI thread, % of a core | dropped frames per 5 s |
|---|---|---:|---:|---:|
| H.264 1080p60, 25 Mbps | main | 43.9 (38.6-54.0) | 13.7 (11.5-18.1) | 42-97 |
| H.264 1080p60, 25 Mbps | repeat | **29.0** (25.6-38.3) | **8.1** (7.2-11.4) | 0 in all 7 windows |
| HEVC 10-bit 2160p30, 41 Mbps | main | 24.9 (19.6-27.7) | 10.0 (7.5-13.7) | 46-66 |
| HEVC 10-bit 2160p30, 41 Mbps | repeat | **23.1** (21.8-34.6) | **9.7** (7.8-15.0) | 12-55 (median 26) |

Dropped frames come from mpv's `frame-drop-count`; windows in which the clip looped are left
out (mpv resets the counter), which the main run's report did not yet do. The drops track the
load from other processes (none at all for 1080p60 in the quieter repeat), and the window
was on the hidden desktop, so treat them as "can drop under load", not as a rate. The UI
thread's share is worth a look: 28-42 % of VidFlow's CPU while playing (8-14 % of a core) is
spent on the UI thread rather than in mpv's decoder and render threads.

### Back-step on long-GOP 4K HEVC 10-bit (L2 baseline)

`b3_hevc10_2160p30_gop250.mp4`: 3840x2160, 10-bit, 30 fps, 41 Mbps, keyframes at frames 0
and 250 only (x265's default keyint). Paused, exact seek to the start frame, then three
`FrameBackStep` actions, each timed until the readout shows the previous frame and mpv has
finished (`seeking` = no). 7 runs x 3 steps = 21 samples per position. d3d11va decoding.

| Start frame | decode starts at | frames decoded per step | median | p95 | min | max |
|---|---:|---:|---:|---:|---:|---:|
| 280 (1.0 s after the keyframe at 250) | 250 | 30 | **528** | 753 | 283 | 789 |
| 262 (0.4 s after the keyframe at 250) | 0 | 262 | **3449** | 3863 | 2979 | 3881 |
| 130 (mid-GOP) | 0 | 130 | **1676** | 1927 | 1453 | 1968 |
| 245 (end of the first GOP) | 0 | 245 | **3017** | 3261 | 2808 | 3300 |

Every one of the 84 steps landed on the expected frame (`time-pos` = that frame's timestamp),
none timed out. The main run measured the last three positions too: 3531, 1691 and 3155 ms.

Two things follow. The cost is about 12 ms per decoded 4K 10-bit frame plus ~0.15 s (a straight
line through the mid-GOP and end-of-GOP medians predicts the other two within 7 %), so it grows
with the distance to the keyframe mpv starts from. And mpv runs `frame-back-step` as a "very
exact" seek, which backs the demuxer up at least 0.5 s before the frame (that is how mpv's seek
code treats very-exact seeks; the 262-vs-280 rows confirm it here): a frame up to 0.5 s
after a keyframe is decoded from the keyframe before that one, so stepping back across the
first half-second of a GOP costs as much as a whole GOP (262 vs 280 above). With the 1 s GOPs
typical of phones that means decoding up to 1.5 s of video per step. The L2 fast path
(backward playback into a frame queue) has to beat these numbers by 3x with zero wrong frames.

### Seek latency, exact vs keyframe

Paused; each measured seek starts from a settled position at least 2.3 s away (reached by an
unmeasured exact seek); 5 pseudo-random targets per run (fixed seeds), 7 runs = 35 samples per
cell. Sent as the mpv command `seek <t> absolute+exact` / `absolute+keyframes` through the pipe;
done when mpv reports `seeking` = no and the position is the target (exact) or a new keyframe
position (keyframes).

| Clip | exact median | p95 | min | max | keyframe median | p95 | min | max |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| HEVC 10-bit 2160p30, GOP 250 | **1998** | 4531 | 190 | 5262 | **230** | 365 | 177 | 449 |
| H.264 1080p30, GOP 60 | **95** | 176 | 41 | 179 | **47** | 58 | 33 | 58 |

Exact seeks on the long-GOP clip spread from 0.2 s (target just after the keyframe at 8.3 s)
to 5 s (end of the first GOP), the same decode-from-keyframe cost as back-stepping. This is the
case L8 (keyframe seeks while dragging, one exact seek on release) is for.

## How it is measured

* **Harness.** `tools\bench.ps1` starts `VidFlow.exe --automation --pipe <name> --settings
  <ini> --log <file>` and talks to it over the control pipe (docs\SPEC.md "Automation"): it
  sends actions (`NextFile`, `FrameBackStep`), mpv commands (`seek`) and `open`, and polls
  `dump` (VidFlow's own state, non-blocking) and `get <property>` (a synchronous mpv read) until
  a milestone is reached. Times are taken with a `Stopwatch` in the harness, started just
  before the triggering request.
* **Why mpv's `seeking` and not the position.** While a seek runs mpv already reports the
  target as `time-pos` (checked: after `seek 6.5 absolute+exact` the position read 6.5 with
  `seeking` = yes for another 30 ms), so "position reached the target" would under-count seeks.
  `seeking` turns "no" when mpv has displayed the new frame (its playback-restart point). For
  back steps the readout only changes once the new frame is shown, and the harness waits for
  both. "First frame rendered" uses `video-frame-info`, which mpv only reports once its
  renderer holds a frame; it is used for cold start only, because after a file switch the
  renderer still holds the previous file's frame.
* **Polling granularity.** One `dump` or `get` round trip costs ~2.1 ms (measured over 100
  requests); the harness sleeps 4 ms between polls (1 ms timer resolution requested for the
  harness process only). Milestones are therefore late by up to ~6 ms. The polling itself
  used 0.2-4 % of the machine (see the table below).
* **Settings.** A private INI per run: window 1280x800 at (40,40), auto-fit off, folder
  auto-load off, single instance off, quick-start card dismissed, Repeat One (clips loop for the
  CPU windows), `ExtraMpvOptions=ao=null` (no sound device; mpv starts playback without waiting
  for real audio output, so first-frame times with speakers may be slightly longer). Everything
  else is the default (hardware decoding `auto-safe`, thumbnail preview on; the preview only
  loads a file when the seek bar is hovered).
* **Hidden desktop.** All of it runs on a separate, never-shown desktop (`RunHidden.exe
  --desktop VidFlowBench`) so nothing appears on the user's screen. That desktop is not
  composited by DWM and has no display to sync to, so dropped-frame counts and anything that
  depends on presentation timing are not representative; decode-bound numbers (back-step,
  seeks, file switching) are.
* **Media.** `tools\make_gatemedia.ps1 -Set bench` encodes the clips in `testmedia\gate\bench`
  with ffmpeg 8.1.1: a test pattern with frame number and timestamp burned in, plus temporal
  noise so the bitrate (and decode work) is near camera footage: H.264 1080p30 16.7 Mbps (GOP
  60), H.264 1080p60 25 Mbps (GOP 120), HEVC Main10 2160p30 41 Mbps (GOP 250), VP9 1080p30
  18 Mbps, AV1 1080p30 10.7 Mbps, all with AAC or Opus audio.
* **Statistics.** Median and nearest-rank p95 (with 7 samples p95 = maximum), min and max.
  Raw samples are in `bench-results.json`.

### Background load

`bench.ps1 -QuietPct 40` waited before each section until the machine was under 40 % busy
(it was 12-38 % at each start), but other work kept running. Measured over each section
(% of all 8 logical cores):

| Section | machine busy | VidFlow | harness | other processes |
|---|---:|---:|---:|---:|
| cold start | 85.4 | 7.0 | 2.6 | 76 |
| next clip | 67.6 | 9.6 | 0.8 | 57 |
| next clip, HwDec=no | 52.9 | 17.5 | 3.1 | 32 |
| memory | 56.8 | 3.7 | 0.2 | 53 |
| CPU | 56.8 | 4.6 | 0.3 | 52 |
| CPU, repeat | 39.7 | 3.9 | 1.3 | 34 |
| back-step (4 positions) | 59.4 | 5.7 | 3.8 | 50 |
| seek | 61.4 | 4.5 | 1.4 | 56 |

The cold-start spread (1.8 to 6.0 s) mostly follows that load. Re-run on an otherwise idle
machine before publishing absolute numbers.

## What to do with this

* **L10 / N7:** the trigger fired (568 ms > ~300 ms on 1080p H.264). Before building the
  warm second instance, two cheaper wins are measurable with this harness: avoid the AV1
  hardware probe on GPUs that cannot decode AV1 (remember the failure per session, or set
  `hwdec-codecs` after one probe), and look at the ~270 ms per-file d3d11va set-up (H.264 302 ms
  without it). N7 would hide both.
* **L2:** the baseline to beat is 0.5-3.5 s per back step on long-GOP 4K (table above), with the
  0.5 s back-off making the first half-second of every GOP the worst case.
* **L8:** keyframe seeks while dragging are ~9x faster than exact ones on long-GOP 4K.
* **Memory:** if 1.2 GB for one 4K clip matters for the "lightweight" claim, the demuxer cache
  sizes in docs\SPEC.md are the first knob.

## Reproducing

```
pwsh -NoProfile -File build.ps1 -NoEmbed -OutDir obj\work\bench-gate\app
pwsh -NoProfile -File tools\bench.ps1 -Exe obj\work\bench-gate\app\VidFlow.exe -Out obj\work\bench-gate -Runs 7 -QuietPct 40
pwsh -NoProfile -File tools\bench.ps1 -Exe ... -Out ...\next-nohw -Only next -ExtraIni 'HwDec=no'
pwsh -NoProfile -File tools\bench.ps1 -Exe ... -Out ...\backstep2 -Only backstep
```

`bench.ps1` generates missing media first (`tools\make_gatemedia.ps1 -Set bench`, ~5 minutes
on this machine, mostly the 4K HEVC encode), relaunches itself on the hidden desktop, and
writes `bench-results.json` (every sample) and `bench-results.md` (the summary table) into
`-Out`. Sections: `-Only cold,next,memory,cpu,backstep,seek`. The whole suite takes about
20 minutes plus any `-QuietPct` waiting. Use `-ExtraIni` for diagnostic variants (it refuses
settings that would drop `ao=null`).
