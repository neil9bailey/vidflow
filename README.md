# VidFlow Player

**Get through a folder of video clips fast — keep the good ones, bin the rest.**

Just copied 300 clips off a GoPro, drone, dashcam or phone, and only 40 are worth keeping?
Open the folder in VidFlow Player, see each clip properly (frame-exact stepping both ways,
instant timeline previews), decide with one key, and apply everything in one safe batch.

Free and open source (GPLv3) · offline · one file, no installer · Windows 10/11 x64 ·
never touches your originals until you say so.

## The cull loop

| Key | |
|---|---|
| `C` | Cull mode on/off |
| `P` / `X` / `U` | Pick (keep) / Reject / clear — then it moves to the next clip |
| `1`–`5`, `0` | Star rating / clear (numpad too) |
| `N` | Next undecided clip |
| `F5`–`F9` | Move the clip to one of five bin folders right away |
| `Ctrl+Enter` | **Apply**: picks go to a *Keep* folder, rejects to the Recycle Bin — shown first as a reviewable summary |
| `Ctrl+Z` / `Ctrl+Y` | Undo / redo any decision, move, rename or apply |
| `F2` | Rename the clip you're watching |

Decisions are remembered between sessions in VidFlow's own index — nothing is written next to
your files. Drives without a Recycle Bin (USB sticks, SD cards, network shares) always ask
before anything is deleted permanently.

## Seeing each clip properly

- **Frame-exact stepping** forwards and backwards: `.` / `,` (or `Ctrl+→` / `Ctrl+←`, Shift+wheel).
  Hold to keep stepping. Frame numbers are exact on constant-frame-rate video and marked `≈F`
  with a **VFR** badge on variable-frame-rate phone footage, where they can only be estimates.
- **Instant previews** when you hover the timeline; `0`–`9` jump to 0–90 %; `Home` / `End` first / last frame.
- **Speed** 0.1×–16× (`[` `]`, `{` `}`, `Backspace` = 1×) with natural pitch; **reverse play** (`Shift+Space`).
- **Zoom at the cursor** (`Ctrl+wheel`, drag to pan, `Ctrl+1` = 1:1 pixels).
- **Clean fullscreen** (`F`, double-click): nothing but the video. Press `O` for a persistent time/frame line.
- **Loops & markers**: `L` A-B loop (or `Alt+[` / `Alt+]`, drag the markers), `B` review markers,
  export markers to **CSV** or an **EDL for DaVinci Resolve**.

## And it's a capable player

Plays practically everything (H.264, HEVC, AV1, VP9, ProRes…; MP4, MKV, MOV, WebM…) via
[mpv](https://mpv.io)'s engine with hardware decoding; subtitles and audio tracks; chapters;
playlists (`.m3u`/`.pls`, subfolders, sort, shuffle); screenshots (`S`, `Ctrl+C`); aspect,
rotate, flip, deinterlace, picture adjustments; audio normalize, downmix and device choice;
borderless window, always on top, taskbar controls; fully remappable shortcuts (`F1` lists them all).

## Privacy

No ads, no accounts, no telemetry, no background network access. "Check for updates" only runs
when you click it. Details: [PRIVACY.md](PRIVACY.md).

## Install

Download `VidFlow.exe` from the [releases page](https://github.com/neil9bailey/vidflow/releases)
(check it against `SHA256SUMS.txt` if you like) and
run it. The exe is not code-signed yet, so Windows SmartScreen may ask you to confirm the first
time. The first start unpacks the playback
engine once (a few seconds) to `%LOCALAPPDATA%\VidFlow\engine`. Settings live in
`%APPDATA%\VidFlow\VidFlow.ini`; put an empty `VidFlow.ini` next to the exe for portable mode.

## Building

Windows 10/11 x64 with Visual Studio Build Tools (MSBuild component, for the C# compiler);
Python 3 + Pillow for the icon; ffmpeg only to regenerate test media.

```powershell
.\tools\fetch-engine.ps1     # one time: the pinned engine build, SHA-256 checked (THIRD-PARTY-NOTICES.md)
.\build.ps1                  # -> dist\VidFlow.exe (single file, engine embedded)
.\build.ps1 -NoEmbed         # -> dist\dev\VidFlow.exe + libmpv-2.dll (fast dev builds)
.\tools\run-core-tests.ps1   # unit-level checks, no window (what CI runs)
.\tools\make_testmedia.ps1   # test clips, needs ffmpeg on PATH
.\tools\e2e.ps1              # 450+ end-to-end checks, run on a hidden desktop (silent, off-screen)
```

How to contribute: [CONTRIBUTING.md](CONTRIBUTING.md). Design and module contracts:
[docs/SPEC.md](docs/SPEC.md). Benchmarks: [docs/BENCHMARKS.md](docs/BENCHMARKS.md).

## Licence

GNU GPL v3 — see [COPYING](COPYING). VidFlow Player embeds libmpv and FFmpeg; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for versions and where the corresponding source is published.
