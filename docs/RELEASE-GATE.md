# Release gate: phone footage, odd files, desktop integration (roadmap L9)

"Plays your phone clips correctly" is a claim we can only make if every release passes this
gate. It has two halves: an **automated** run that needs no screen (everything happens on a
hidden desktop with mpv's audio output set to null), and a **manual** checklist for the things
that only a real desktop, a real monitor or a clean machine can show.

## Running the automated half

```
pwsh -File tools\make_gatemedia.ps1 -Set gate          # synthetic samples -> testmedia\gate (kept if present)
pwsh -File tools\bench.ps1 -Suite gate -Exe <VidFlow.exe> -Out obj\work\bench-gate
pwsh -File tools\bench.ps1 -Suite gate -Only hdr10,rot270   # just some cases (the SDR baseline stays in for HDR cases)
```

Results: `<Out>\gate-results.md` (one row per check), `<Out>\gate-results.json` (every measured
value), screenshots in `<Out>\shots` (`<case>.png` is VidFlow's own render through the automation
`screenshot` verb, `ref_<case>.png` is ffmpeg's decode of the same frame, `win_<case>.png` is the
whole window captured with `tools\capture.ps1`), the app log in `<Out>\gate-app.log`.
The exit code is the number of failed checks.

### How the checks decide

Every sample frame is the same test card: mid-grey background, a red marker in the stored
top-left corner, green top-right, blue bottom-left, a white "SDR reference white" patch in the
centre, and the label, frame number and timestamp burned in. On the HDR10 sample the
bottom-right corner is a 1000-nit patch.

| Check | Pass when |
|---|---|
| loads / plays | the file loads without `lastError`, mpv finishes starting (`seeking` = no) and the position advances by more than 0.5 s in 1.5 s of playback |
| aspect | the box of non-black pixels in VidFlow's screenshot has the display aspect ffprobe implies (width x SAR / height, inverted for 90/270 rotation), within 3 % |
| orientation | the red/green/blue markers sit in the quadrants the display-matrix rotation predicts; ffmpeg's auto-rotated decode of the same frame is reported alongside as a second opinion |
| HDR detected | mpv reports the PQ or HLG transfer (`video-params/gamma`) |
| not washed out | white-patch / grey-background luma ratio is at least 80 % of the SDR reference's ratio. Showing the PQ signal as if it were SDR drops that ratio to about 60 % (the ffmpeg reference frame, which is not tone-mapped, is scored the same way to prove the check catches it) |
| not crushed | the reference-white patch keeps at least 60 % of the SDR reference's white luma |
| highlight kept | (HDR10) the 1000-nit patch renders brighter than reference white |
| frame steps | (120, 240 fps, VFR) three `FrameStep` actions from 1.0 s land exactly on the timestamp of the third next frame (from ffprobe's packet list), and the frame number VidFlow shows equals the true frame index (on the VFR sample a number the dump flags as approximate also passes: any `dump` key matching vfr / variable / approx that is true) |
| error files | `lastError` is set within 10 s in plain language (no exception text), the window stays responsive (no modal dialog), and the next good file plays |
| damaged files | no crash; VidFlow plays what is decodable (or reports an error) and recovers |
| names | the path VidFlow reports equals the file's path exactly and the title starts with the file name |
| log | no VidFlow `ERROR` lines and no exceptions (mpv's own decoder messages about the broken samples are expected and counted separately) |

## Automated results

Run 2026-10-24 20:38 against a private build of the tree as it stood at 19:25
(`obj\work\bench-gate\app\VidFlow.exe`, SHA-256 848D3EAD854AD2FA...; libmpv mpv v0.41.0-1115-gb2c255c13),
AMD Ryzen 5 3500U / Radeon Vega 8 (driver 31.0.21918.12), Windows 11 Pro build 26200.9457,
hardware decoding `auto-safe` (d3d11va), output `gpu-next` to an SDR target.
**138 of 140 checks passed.** Both failures are real defects: VFR frame numbers (L1) and paths
longer than 260 characters. This is the second full run; the first (20:10) had the same results
plus a false alarm from the log check, which matched mpv's "Error while decoding" warnings
case-insensitively and has since been fixed. A later build of the tree (20:45,
`obj\work\bench-gate\app2`) re-checked with `-Only vfr,longpath,fps120,hdr10` gave the same
results: both defects are still there.

Screenshot paths are relative to this file; they exist after a run (the `obj` tree is not committed).

| Sample (testmedia\gate) | What it covers | Result | Evidence |
|---|---|---|---|
| `日本語_clip.mp4` | Unicode name, opened from the **command line** | PASS: plays, path and title exact | [shot](../obj/work/bench-gate/shots/unicode.png) |
| `g00_sdr_reference_1080p.mp4` | SDR BT.709 baseline | PASS (white 255, grey 90) | [shot](../obj/work/bench-gate/shots/sdr.png) |
| `g01_hdr10_pq_hevc10_1080p.mp4` | HDR10: PQ, BT.2020, 10-bit, mastering display + MaxCLL 1000/400 | PASS: PQ detected, contrast 0.88x of SDR (un-tone-mapped frame: 0.60x), reference white luma 178, 1000-nit patch 254 | [shot](../obj/work/bench-gate/shots/hdr10.png), [no tone mapping](../obj/work/bench-gate/shots/ref_hdr10.png) |
| `g02_hlg_hevc10_1080p.mov` | HLG 10-bit, iPhone-style .mov | PASS: HLG detected, contrast 0.95x, white 222 | [shot](../obj/work/bench-gate/shots/hlg.png) |
| `g03_dolbyvision84_like_hlg_1080p.mov` | Dolby Vision 8.4 configuration box over an HLG base layer (no RPU) | PASS: plays the HLG base layer, contrast 0.96x, white 226 | [shot](../obj/work/bench-gate/shots/dv84.png) |
| `g04_rotate_090_hevc.mov` | display matrix 90 | PASS: portrait 0.562, markers where expected (mpv `rotate`=270, its clockwise convention) | [shot](../obj/work/bench-gate/shots/rot090.png) |
| `g04_rotate_180_hevc.mov` | display matrix 180 (phone upside down) | PASS | [shot](../obj/work/bench-gate/shots/rot180.png) |
| `g04_rotate_270_hevc.mov` | display matrix -90 (iPhone portrait) | PASS: portrait 0.562 | [shot](../obj/work/bench-gate/shots/rot270.png) |
| `g05_hlg_rotate_180_hevc10.mov` | HLG + 180 rotation | PASS | [shot](../obj/work/bench-gate/shots/hlg180.png) |
| `g06_h264_1080p120.mp4` | 120 fps | PASS: 3 steps 1.0000 -> 1.0250 s, frame 120 -> 123 = true index | [shot](../obj/work/bench-gate/shots/fps120.png) |
| `g07_hevc_1080p240.mov` | 240 fps HEVC | PASS: 3 steps 1.0000 -> 1.0125 s, frame 240 -> 243 = true index (playback fell behind real time, finding 6) | [shot](../obj/work/bench-gate/shots/fps240.png) |
| `g08_vfr_phone_h264_1080p.mp4` | VFR phone-style (jitter, 24 fps stretch, timescale 600) | **FAIL (L1)**: steps land on the right frames (1.000 -> 1.100 s) but the shown frame number is wrong: 29 where the true index is 30, then 31 where it is 33; no VFR flag in the dump | [shot](../obj/work/bench-gate/shots/vfr.png) |
| `g09_av1_10bit_1080p.mkv` | AV1 10-bit | PASS, software decode after failed hardware probes (finding 3) | [shot](../obj/work/bench-gate/shots/av1_10.png) |
| `g10_h264_1080x1350_portrait45.mp4` | 4:5 portrait | PASS: 0.799 | [shot](../obj/work/bench-gate/shots/p1080x1350.png) |
| `g11_h264_4096x2160_dci.mp4` | DCI 4K 4096x2160 | PASS: 1.899 | [shot](../obj/work/bench-gate/shots/dci4096.png) |
| `g12_h264_1440x1080_sar4-3.mp4` | non-square pixels (SAR 4:3) | PASS: shown 16:9 (mpv `dw` 1920) | [shot](../obj/work/bench-gate/shots/sar43.png) |
| `g13_audio_only.m4a`, `g14_audio_only.mp3` | audio only | PASS: plays, audio surface with music glyph and file name | [m4a](../obj/work/bench-gate/shots/win_m4a.png), [mp3](../obj/work/bench-gate/shots/win_mp3.png) |
| `g20_broken_zero_bytes.mp4` | 0-byte file | PASS: "Cannot play g20_broken_zero_bytes.mp4 / unrecognized file format" after 28 ms, next file plays | [window](../obj/work/bench-gate/shots/win_zero.png) |
| `g21_broken_missing_moov.mp4` | truncated mp4 without its index | PASS: same polite error | [window](../obj/work/bench-gate/shots/win_nomoov.png) |
| `g23_broken_garbage.mp4` | random bytes | PASS: same polite error | [window](../obj/work/bench-gate/shots/win_garbage.png) |
| `g22_broken_truncated_half.mp4` | faststart mp4 cut in half | PASS: plays the first half, loops (Repeat One), no error, no crash | [window](../obj/work/bench-gate/shots/win_trunc.png) |
| `g24_broken_bitrot.mkv` | 64 random bytes every 32 KB | PASS: plays through decode errors, no crash | [window](../obj/work/bench-gate/shots/win_bitrot.png) |
| `g31 name with spaces & $dollar (1).mp4` | shell-special characters | PASS: path and title exact | [shot](../obj/work/bench-gate/shots/special.png) |
| `g32_longpath\...\g32_long_path_clip.mp4` | full path of 315 characters | **FAIL**: rejected before playback with the OSD "Error: nothing playable in g32_long_path_clip.mp4" (finding 1) | – |
| (app) | clean quit, log | PASS: quits in 0.5 s; 0 VidFlow errors, 0 exceptions (26 mpv decoder messages from the broken samples) | [window](../obj/work/bench-gate/shots/win_sdr_ui.png) |

Every sample also passed "screenshot written" and the error/damaged ones "recovers: next file plays".

### Findings

1. **Paths longer than 260 characters are rejected** (blocker for "plays anything Explorer can
   show"). `VidFlow.exe` has no `TargetFrameworkAttribute`, so .NET Framework runs it with legacy
   path handling: `File.Exists` is false and `Path.GetFullPath`/`FileInfo` throw
   `PathTooLongException` for such paths, even though the manifest is `longPathAware` and the
   machine has `LongPathsEnabled=1`. Reproduced with a 15-line test exe built the same way
   (csc, `src\app.manifest`): fails as built; works with either
   `[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]`
   or `AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false)` +
   `AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false)` as the first lines of `Main`.
   The attribute also switches on every other .NET 4.5-4.8 behaviour change, so the two
   `SetSwitch` calls are the narrower fix; run the e2e suite either way.
2. **VFR frame numbers are estimates** (roadmap L1, expected in this build). Stepping is exact,
   but the number shown is `time / duration x count`: off by 1 at 1 s and by 2 three frames
   later on the VFR sample, and nothing in the dump marks it as approximate (still true in the
   20:45 build). Re-run `-Only vfr` once L1 lands; the check passes on either the true index or
   a number the dump flags as approximate.
3. **AV1 without hardware AV1 decode costs over a second per file.** With `hwdec=auto-safe`
   mpv tries d3d11va, then dxva2, then CUDA for every AV1 file before falling back to software:
   14 mpv error lines per file, with a 1.0 s gap between the d3d11va and dxva2 attempts in the
   log. Open -> playing in the gate runs: AV1 3.0 s against 0.9-2.0 s for every other video in
   the first run; 1.0 s against 0.3-0.6 s for most in the second (where the first three samples
   and the VFR one took 2-5 s while the machine was busy). The speed benchmark isolates it:
   1483 ms per switch with the probe, 254 ms with hardware decoding off (docs\BENCHMARKS.md,
   "next clip"). Remembering a failed
   hardware probe per codec for the session, or probing once at startup and setting
   `hwdec-codecs`, would remove it.
4. **HDR10 is tone-mapped sensibly but darker than SDR.** 203-nit reference white comes out at
   luma 178 (70 % of SDR white) and mid-grey at 71 against 90, while 1000-nit highlights still
   reach 254. HLG and the Dolby-Vision-like sample land at 87-89 %. Not washed out, not a
   blocker; worth a look at mpv's tone-mapping settings later (setting `target-peak=203` alone
   did not change it).
5. **One unexplained stall, not reproduced.** In the first trial run the HDR10 sample, opened
   right after the SDR sample had been paused, seeked and screenshotted, sat at position 0 /
   frame 0, not paused and without an error, until the 20 s wait for mpv's playback start ran
   out. Every later attempt (four gate runs and two probe scripts, fresh and with the same
   sequence) started it in 1.4-3.3 s. The machine was heavily loaded by other work at the
   time. Keep an eye on it on real hardware (manual item M5).
6. Informational: 120 and 240 fps clips drop frames during playback on the 60 Hz output (mpv
   shows what the display can show; stepping is exact). In the second run the 240 fps HEVC
   clip also fell behind real time (0.86 s of video in 1.5 s, 191 frames dropped) while other
   work loaded the machine; the 4096x2160 sample dropped 21 frames in 1.5 s in the first run
   and none in the second. Check slow-motion playback on real hardware (M5, M9).

## Manual checklist (needs a real desktop or a clean machine)

These cannot be automated on the hidden desktop: it is not composited by DWM, has no monitor,
no taskbar and no shell, and the rules for this project forbid touching the real registry,
input devices and clipboard from tests. Tick each item for every release.

| # | Item | Steps | Expected |
|---|---|---|---|
| M1 | Mixed-DPI monitors | Two monitors at different scaling (e.g. 100 % and 175 %, ideally one at 250 %). Play a clip, drag the window between them, maximize on each, toggle fullscreen on each, open the playlist, menus, Settings and the seek-bar preview on each. | Control bar, glyphs, text and the preview re-layout crisply at each DPI (no blurry bitmap scaling, nothing clipped); fullscreen covers exactly the monitor the window is on; video handle survives (no black video). |
| M2 | Single monitor at 100-250 % | Set 125, 150, 200, 250 %; restart VidFlow at each. | Same as M1; minimum window size still usable. |
| M3 | Taskbar button | Play a clip with the window not focused; watch the taskbar button; hover it. | Progress bar on the button tracks playback (paused state shown); thumbnail toolbar shows Previous / Play-Pause / Next and they work; after restarting Explorer the buttons come back. |
| M4 | Open With registration | On a test account or VM: Settings > register "Open with"; right-click an .mp4/.mov/.mkv in Explorer > Open with; Settings > Default apps > VidFlow; multi-select 3 files > Open; "Add to VidFlow playlist" verb; then unregister. | VidFlow listed with its icon; opening works for one and many files (one window, all queued); unregister removes only VidFlow's entries and leaves the user's chosen defaults alone. |
| M5 | Real phone footage on an SDR display | iPhone 12+ Dolby Vision HLG .MOV (portrait and recorded upside down), iPhone SDR HEVC, Samsung and Pixel VFR clips, GoPro/DJI 10-bit HEVC and AV1, a 240 fps slo-mo clip. | Correct orientation and aspect; HDR/DV not washed out or crushed; slo-mo plays at its recorded speed; frame numbers on VFR clips follow L1's rules; no stall on first frame (finding 5). |
| M6 | HDR display | Windows HDR on, same files as M5. | HDR content is not double tone-mapped or dull; SDR content is not over-bright; switching HDR on/off while playing does not break output. |
| M7 | SmartScreen / Smart App Control | Clean Windows 11 VM with Smart App Control on and one with it off; download the release zip through a browser (so it has Mark-of-the-Web), extract, run `VidFlow.exe`. | Record exactly what appears (SmartScreen prompt, SAC block) for the signed and unsigned builds; the release notes must say what users will see. Single-file build extracts libmpv on first run: check that is allowed too. |
| M8 | Audio | Real speakers/headphones: play, change volume, mute, switch output device while playing, unplug headphones, A/V sync on a clip with a clap or beep. | Sound follows the selected device; no glitch on pause/seek; lip sync within a frame or two. |
| M9 | High refresh rate | 120/144 Hz monitor: 24, 30, 60, 120 fps clips, windowed and fullscreen. | Smooth pacing, no periodic stutter; frame stepping unaffected. |
| M10 | Real input | Mouse wheel, Shift/Ctrl/Alt+wheel, X1/X2 buttons, touchpad scroll, media keys, drag-and-drop from Explorer (with and without Ctrl), double-click fullscreen. | Same results as the automated mouse/key tests, no double-firing of media keys. |
| M11 | Sleep / lock / RDP | Pause a clip, lock the PC, sleep and resume, connect via Remote Desktop. | Playback resumes; no black video or crash; window still on a visible monitor. |
| M12 | Clipboard and delete | Ctrl+C (frame), marker copy, Delete to Recycle Bin on a real folder (and on a USB stick without a Recycle Bin). | Frame and text land on the clipboard; delete goes to the Recycle Bin or shows the existing warning when there is none. |

## Re-running after fixes

* Long paths (finding 1): `bench.ps1 -Suite gate -Only longpath`.
* VFR numbers (finding 2): `bench.ps1 -Suite gate -Only vfr`.
* Add a sample: extend `Make-Gate` in `tools\make_gatemedia.ps1` (it keeps existing files, so
  only the new one is encoded) and add a case to `$cases` in `Bench-Gate` in `tools\bench.ps1`.
