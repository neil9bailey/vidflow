# VidFlow — design spec (for implementers)

VidFlow is a lightweight Windows video player for **quick reviewing of short clips**, with the
everyday feature set of VLC / mpv / MPC-HC. It ships as one offline `VidFlow.exe`.

## Stack (fixed, do not change)

* C# (Roslyn, C# 13 syntax allowed) compiled by `build.ps1` against **.NET Framework 4.8**
  WinForms (`mscorlib, System, System.Core, System.Drawing, System.Windows.Forms,
  Microsoft.VisualBasic`). No NuGet, no other assemblies, no WPF.
  - .NET Framework limits: no `System.Text.Json`, no `Span` APIs, no records/`init` (need
    IsExternalInit), no `Index/Range`, no default interface methods. Tuples, pattern matching,
    switch expressions, local functions, string interpolation, `?.`, `??=` are fine.
* Playback engine: **libmpv** (`libmpv-2.dll`, mpv 0.41-dev, client API 2.5) via the
  P/Invoke wrapper in `src/Core`. Video renders into a WinForms panel through mpv's `wid`.
* Windows 10/11 x64 only. Per-monitor-v2 DPI aware (manifest).

## Build & check

```
.\build.ps1 -NoEmbed                       # dev build -> dist\dev\VidFlow.exe (+ libmpv-2.dll beside it)
.\build.ps1                                # release single-file exe -> dist\VidFlow.exe
.\build.ps1 -Check -WithContracts -Files <your files>   # compile YOUR files against contract stubs
.\build.ps1 -Check -Overlay -Files <your files>         # whole-app compile: the frozen tree in obj\snapshot\src
                                                        # with your live files swapped in (others' half-written
                                                        # files cannot break it)
.\build.ps1 -NoEmbed -OutDir obj\work\<key>\app         # private build to run tests against
```

`contracts\*.cs` hold the **frozen public API** of every UI module. Your real file must keep
every public member of its contract with the same signature and semantics (add more if useful,
never remove/rename). Always compile-check with `-WithContracts` so other modules' in-progress
code cannot break your check. Do not edit `contracts\`, `src\Core\`, `src\Ui\Theme.cs`,
`src\Program.cs` or `build.ps1` — if you believe one needs a change, say so in your final report.

Test media: `testmedia\` (frame number + timestamp burned into every frame, e.g. "C01 F61 T00:00:02.033"):
`clip01_h264_720p30.mp4` (8 s, 30 fps, + external `clip01_h264_720p30.srt`),
`clip02_portrait_60fps.mp4` (1080x1920, 5 s), `clip03_vp9.webm` (640x360 25 fps),
`clip04_hevc_multitrack.mkv` (12 s 24 fps, 2 audio tracks eng/jpn, embedded eng subs, 3 chapters),
`clip05_av1_noaudio.mkv` (4 s, no audio).

Generated media (kept out of `testmedia\` itself so opening clip01 still queues exactly 5 files):
* `testmedia\frames\` — `tools\make_vfrmedia.ps1`: frame-accuracy clips `fr_cfr_<rate>.mp4/.mkv`
  (23.976–240 fps), `fr_vfr_jitter`, `fr_vfr_steps` (variable rate), `fr_unknown_30fps.avi` (a
  container the frame probe does not read), `fr_4k_hevc_gop250.mp4`. Every frame carries its index
  as a 12-bit barcode (see the script) so tests compare the frame number with the frame shown.
* `testmedia\gate\` — `tools\make_gatemedia.ps1` (needs ffmpeg with libx264/libx265/libvpx/
  libsvtav1/libopus/libmp3lame/zimg): the release-gate samples `g00`–`g31` (HDR10/HLG/DV-like,
  rotation, high frame rates, VFR, odd sizes, audio only, broken files, odd names) plus
  `g32_longpath\…\g32_long_path_clip.mp4`, a path over 260 characters. `testmedia\gate\bench\`
  holds the benchmark clips `b1`–`b5` (H.264 1080p30/60, HEVC 10-bit 2160p GOP 250, VP9, AV1).
  Ignored by `.gitignore`; regenerate with the script.

## Testing without disturbing the user (mandatory)

The developer's desktop is in use. **Never open a window on it.** Every process that creates
windows (VidFlow.exe, harness exes, capture/screenshot scripts) runs on the hidden desktop via
`obj\tools\RunHidden.exe` (built by `build.ps1` / `tools\ensure-runhidden.ps1`):

```
obj\tools\RunHidden.exe --wait --timeout 600000 -- pwsh -NoProfile -File my-test.ps1   # whole script hidden; output comes back
obj\tools\RunHidden.exe -- obj\harness\x\x.exe args                                    # start one GUI process hidden; prints its pid
```

Children inherit the hidden desktop. `tools\e2e.ps1` and `tools\dialogshots.ps1` relaunch
themselves there automatically. Window enumeration and `tools\capture.ps1` only see windows on
the caller's desktop, so launch-and-capture must happen inside one hidden script; for video
content prefer the automation `screenshot` verb (mpv renders it). Test settings always include
`ExtraMpvOptions=ao=null` (no sound) and harness mpv instances use `ao=null`. Do not touch the
clipboard, the real mouse/keyboard (no SendKeys/SetCursorPos/SendInput), or system settings.

`tools\e2e.ps1 [-Exe <exe>] [-Pipe <name>] [-Only a,b] [-TimeScale <x>]` runs the core tests,
then every `tools\e2e.d\NN-name.ps1` suite in name order (dot-sourced; they add `Test` blocks using
the script's helpers `Ctl, Dump, Act, Key, WaitFor, Settled, Poll, GetWhen, Scaled, Near, Check,
Test` and `$media, $work, $clip1, $clip4, $settingsPath, $Exe, $Pipe`): `10-frames`, `20-cull`,
`25-cull-ui`, `30-extras`, `40-integration`. Its work folder is `obj\e2e-<pipe>` (wiped at start)
and the single-instance tests use the pipes `<pipe>-SI` / `<pipe>-B`, so runs with different
`-Pipe` values never share files or pipes. Its settings include `QuickStartShown=true` (suites that
test the quick start card start their own instance, as does the per-session hwdec memo test).
Files a test deletes are copies under `obj\`.

Waits follow the machine's load. Each is its written time × the time scale: by default twice the
slowdown other processes (WSL, Docker, builds) cause at startup, measured from system CPU time
minus the suite's and VidFlow's own. A wait whose condition still fails then carries on while the
load since it began stays high (up to 4× longer). A wait ends as soon as its condition holds, so
this costs a passing run nothing. State an action changes asynchronously (anything mpv reports
back) is read with `Settled` / `Poll` / `GetWhen`, which wait for the expected value and otherwise
return the last one for the check to report; a fixed pause is only for checks that something does
*not* happen (`Scaled` stretches it). The few checks of an upper time limit stretch only the part
that is VidFlow's own work, by the load measured over that interval ("exits within 6 s" = the 5 s
engine shutdown limit + 1 s).
`tools\bench.ps1` (speed suite, release gate; `docs\BENCHMARKS.md`, `docs\RELEASE-GATE.md`) runs on
its own hidden desktop the same way.

## Code layout

| File | Owner | Purpose |
|---|---|---|
| `src/Core/LibMpv.cs`, `MpvPlayer.cs` | foundation | libmpv P/Invoke + UI-thread event pump (read these first) |
| `src/Core/NativeLoader.cs` | foundation | finds/extracts/loads libmpv-2.dll |
| `src/Core/Settings.cs` | foundation | INI-persisted preferences (enums `RepeatMode`, `WheelAction`, `ClickAction`) |
| `src/Core/Actions.cs` | foundation | `ActionIds`, `ActionCatalog` (titles + default keys), `KeyNames`, `ActionRegistry` |
| `src/Core/Presets.cs`, `Fmt.cs`, `Log.cs`, `Json.cs`, `ControlChannel.cs` | foundation | presets, formatting, logging, JSON writer, named-pipe channel |
| `src/Ui/Theme.cs` | foundation | palette, fonts, Segoe MDL2/Fluent glyphs, DPI helpers |
| `src/Program.cs` | foundation | entry point, `LaunchOptions`, single instance, engine load |
| `src/Ui/SeekBar.cs` | seekbar | timeline control |
| `src/Ui/ThumbnailPreview.cs` | seekbar | hover preview popup (2nd mpv instance) |
| `src/Ui/ControlBar.cs` | controlbar | bottom control strip |
| `src/Media/Playlist.cs` | playlist | playlist model |
| `src/Ui/PlaylistPanel.cs` | playlist | playlist side panel |
| `src/Ui/Menus.cs` | menus | dark renderer + all menus |
| `src/Ui/Dialogs/*.cs` | dialogs | every dialog (one class per file) |
| `src/Ui/MainForm*.cs`, `src/Ui/VideoHost.cs` | mainform | window, mpv wiring, input, actions, automation |

Namespaces: `VidFlow.Core`, `VidFlow.Media`, `VidFlow.Ui`, `VidFlow.Ui.Dialogs`.

## Visual language

Dark, quiet, video-first. Colours only from `Theme` (`Theme.Panel` bar background, `Theme.Text`,
`Theme.TextDim`, `Theme.Accent` for progress/active toggles, `Theme.Hover`/`Pressed` for button
states). Icons are glyphs from `Theme.IconFontFamily` (`Glyphs.*`). Text: Segoe UI. All sizes are
logical 96-dpi pixels scaled with `Theme.Scale(control, px)` / `DeviceDpi`; handle
`DpiChangedAfterParent`/`OnDpiChanged*` so a monitor change re-lays-out. Paint with
double buffering (`SetStyle(OptimizedDoubleBuffer | AllPaintingInWmPaint | UserPaint |
ResizeRedraw, true)`), anti-aliased GDI+, `TextRenderingHint.ClearTypeGridFit`. Custom controls
must not take keyboard focus (`SetStyle(ControlStyles.Selectable, false)`, `TabStop = false`).
Hover/pressed feedback on every clickable thing; tooltips via one shared `ToolTip`.

## Behaviour reference (MainForm owns the wiring; leaf modules must make it possible)

### Window & layout
* `MainForm` (min client 360x200): `VideoHost` (Dock Fill) → inside it the **video surface**
  panel (Dock Fill, black). `PlaylistPanel` docked Right (resizable via a thin splitter, width
  persisted), `ControlBar` docked Bottom (height = `PreferredBarHeight`).
* **Video surface**: a plain `Panel` whose handle is passed to mpv as `wid`. It is **disabled**
  (`Enabled = false`) so every mouse message over the video goes to `VideoHost` instead (verified:
  `WindowFromPoint` over mpv's child window returns the host). Its handle must never be
  recreated after mpv starts (verify after fullscreen toggles; prefer Win32 style changes over
  operations that recreate handles). While no video is shown it paints an idle hint
  ("Drop videos here · Ctrl+O to open", plus up to 5 recent files listed dimly) or, for
  audio-only files, the title with a music glyph; an error text if the file failed.
* Title: `"<file name> [i/n] — VidFlow"` (`[i/n]` only when the playlist has > 1 item), `"VidFlow"` when idle.
* **Auto-fit** (setting, default on): when a video whose display size differs from the previous
  file's starts, resize the window so the video area is the video's size (1 video px = 1 device
  px), capped at `AutoFitMaxPercent` of the monitor work area, keeping aspect, keeping the window
  centre, clamped on-screen. Not while maximized/fullscreen.
* Persist on exit: bounds (normal bounds if maximized), maximized, volume, mute, repeat, shuffle,
  playlist visible/width, controls visible, always-on-top, recent files.

### Fullscreen ("no controls in view")
* Toggle: F, F11, Alt+Enter, double-click video, control-bar button. Esc leaves.
* Borderless, covers the whole monitor the window is on (taskbar hidden), **nothing but video**:
  control bar, playlist and seek bar hidden. Cursor hides after 1.5 s of no movement.
* Optional `FullscreenControlsOnHover` (default **off**): moving the mouse to the bottom 12% of
  the screen shows the control bar in an owned, borderless overlay form at the bottom; it hides
  1 s after the mouse leaves it.
* On-screen feedback in fullscreen comes from mpv's OSD (`show-text`), e.g. "Speed 0.50×",
  "Frame 61 / 240", seeks show an OSD bar (`osd-bar` prefix).

### Mouse (video area)
* Left click: play/pause (setting `LeftClick`). Double-click: fullscreen; the double-click must
  not leave the pause state changed (undo the first click's toggle).
* Left drag: when zoomed in (zoom ≠ 0 or pan ≠ 0) pans the video; otherwise, windowed and not
  maximized, moves the window (`DragMovesWindow`). Middle drag pans; middle click resets zoom.
* Wheel: setting `Wheel` (Volume default, Seek, FrameStep, Zoom). Ctrl+wheel zooms **at the
  cursor**, Shift+wheel steps frames (up = next frame), Alt+wheel speed ±0.05.
  Wheel up = louder / forward / next frame / zoom in.
* Right click: context menu. X1/X2 buttons: previous/next file.
* Cursor auto-hides over the video after 1.5 s idle while playing (always in fullscreen).
* Drag & drop files/folders/URLs onto the window: open (replace playlist); hold Ctrl to append.
  Subtitle files dropped while a video plays are added as subtitle tracks.

### Keyboard
All shortcuts come from `ActionCatalog` defaults + `Settings.KeyOverrides` via
`ActionRegistry.BuildKeyMap`. `MainForm.ProcessCmdKey` looks up `keyData` and executes the action
(key auto-repeat simply repeats the action, e.g. holding "." keeps stepping). Exception: while a
text box has focus (playlist filter) keys go to the text box (Esc returns focus to the video).
Media keys also arrive as `WM_APPCOMMAND`; handle both without double-firing.

### Playback semantics (mpv mapping)
* mpv options set before init (main player): `wid`, `hwdec=<setting>`, `keep-open=yes`,
  `keep-open-pause=yes`, `idle=yes`, `force-window=no`, `input-default-bindings=no`,
  `input-vo-keyboard=no`, `input-cursor=no`, `input-media-keys=no`, `cursor-autohide=no`,
  `osc=no`, `ytdl=no`, `terminal=no`, `config=yes`, `config-dir=<settings dir>\mpv`,
  `osd-level=1`, `osd-duration=1200`, `osd-font-size=34`, `osd-border-size=2`, `osd-bar-h=2.5`,
  `hr-seek=yes`, `hr-seek-framedrop=no`, `cache=yes`, `demuxer-max-bytes=64MiB`,
  `demuxer-max-back-bytes=64MiB` (holds a whole short clip; v1 measured 256 MiB only adding memory on long files), `volume=<setting>`, `volume-max=150`, `mute=<setting>`,
  `audio-pitch-correction=<setting>`, `loop-file=inf|no` (repeat One), `sub-auto=fuzzy`,
  `sub-visibility=<setting>`, `sub-scale=<setting>`, `alang`/`slang` when set,
  `screenshot-format`, `screenshot-jpeg-quality=95`, `save-position-on-quit=<ResumePlayback>`,
  `watch-later-directory=<settings dir>\watch_later`, then each `ExtraMpvOptions` line.
* **Hardware decoding memo**: when a file starts in software decoding although hardware decoding
  was asked for (`hwdec-current` = `no` at its first playback restart, codec listed in
  `hwdec-codecs`), that codec is removed from `hwdec-codecs` for the rest of the session, so later
  files of it skip mpv's per-file probe of every hardware API (AV1 on a GPU without AV1 decode:
  ~1.2 s and 14+ logged errors per file). A codec seen decoding in hardware once is never removed.
  `hwdec-codecs=all` (via ExtraMpvOptions) turns the memo off. Dump key `hwdecMemo`.
* **Long paths**: `Program.Main` sets `Switch.System.IO.UseLegacyPathHandling=false` and
  `Switch.System.IO.BlockLongPaths=false` first (the exe has no TargetFrameworkAttribute, so .NET
  would otherwise use legacy path handling); with the manifest's `longPathAware` paths over 260
  characters open (gate clip `g32_longpath`).
* **Frame numbers**: exact on a constant-frame-rate file verified by the frame probe (frame n
  starts at n / fps; millisecond-rounded Matroska timestamps are absorbed), otherwise estimates,
  shown as "≈F" in the bar, the persistent OSD status line, marker OSD/menu labels and marker
  exports (CSV `Approximate=yes`). The verdict reaches the OSD and markers through
  `FeatureFrameTimingChanged` (no polling).
* **Frame step**: `frame-step` / `frame-back-step` (both pause). Verified exact: seek 2.0 → frame
  60; 3× step → 63; 2× back → 61 (`estimated-frame-number`).
* **Seeks**: keyboard seeks `seek <±s> relative+exact`; number keys `seek <p> absolute-percent+exact`;
  seek-bar drag `seek <t> absolute+keyframes` (async), release/click `absolute+exact`.
  Go to end = `seek <duration − 1/fps> absolute+exact` + pause (never trip EOF/loop).
  Stop = pause + seek 0 (file stays loaded).
* **Speed**: presets `Presets.Speeds`; SpeedUp/Down = next/previous preset strictly
  above/below current; Double/Halve clamp to [0.01, 100]; fine ±0.05; reset 1. Pitch correction
  toggle → `audio-pitch-correction`. `KeepSpeedBetweenFiles=false` → speed 1 on each new file.
* **Repeat**: Off (pause on last frame), One (`loop-file=inf`), All (on `eof-reached` → next
  file, wrapping). Off + `AutoAdvance` → next file without wrap. Shuffle affects next/previous.
* **A-B loop**: L cycles: set A at current time → set B (swap if B < A) → clear. Uses
  `ab-loop-a`/`ab-loop-b`. Markers shown on the seek bar.
* **Zoom/pan** (mpv `video-zoom` = log2 scale relative to fit, `video-pan-x/y` = fraction of the
  scaled video size): step 25% (`Presets.ZoomStepLog2`), clamp `MinZoomLog2..MaxZoomLog2`.
  Zoom at cursor keeps the video point under the cursor fixed:
  with fit size W×H (computed from the display aspect incl. aspect override and rotation, fitted
  into the video-host client size), cursor offset p from the host centre, old scale s=2^z,
  new s'=2^z': `pan' = p/(s'·W) − p/(s·W) + pan` (per axis). Drag pan: `pan += Δpx/(s·W)`.
  `SetZoom:<pct>` = pct of video pixels (100 = 1:1). Zoom/pan reset on each new file.
  `ZoomText` = "Fit" at zoom 0/pan 0, else the displayed size as a % of video pixels.
* **Window size** `WindowSize:<pct|fit>`: video area = pct of video pixels (fit = largest that fits
  the work area); leaves fullscreen/maximize first.
* **Rotate** `video-rotate` (0/90/180/270). **Flip** H/V via `vf` (`@vidflow-hflip:hflip`,
  `@vidflow-vflip:vflip`); software filters need copy-back decoding, so while a flip is active use
  `hwdec=auto-copy-safe` (restore the setting when no flip is active). **Aspect**
  `video-aspect-override` (`Presets.Aspects`). **Fill** `panscan` 0/1. Picture adjust:
  brightness/contrast/saturation/gamma/hue ±5. Deinterlace `deinterlace`. HW decoding toggle
  between `no` and the setting.
* **Audio**: volume ±5 (0..150), mute, tracks (`aid`), delay ±0.1 s. **Subtitles**: tracks
  (`sid`), visibility, delay ±0.1, scale ±0.1, load file (`sub-add <path> select`).
* **Screenshots**: `S` saves the video frame (no subs) as
  `<ScreenshotFolder>\<name>_<hh-mm-ss.fff>.<fmt>` via `screenshot-to-file <path> video`
  (unique suffix if it exists), Shift+S `window` mode (as displayed). Ctrl+C copies the frame
  to the clipboard via `screenshot-raw video` (bgr0 → `Format32bppRgb`). OSD confirms.
* **Delete** (review workflow): confirm (unless disabled / Shift+Delete) → remember the path,
  remove it from the playlist, load the next item (or `stop` if none) → when mpv reports the old
  file ended, move it to the Recycle Bin (`Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
  UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin)`), retrying briefly if still locked.
* **Statistics overlay**: `script-binding stats/display-stats-toggle`. Info OSD (Tab):
  `show-text` with `${filename}`, time/duration, frame n/total, speed.
* OSD messages respect `Settings.OsdMessages`. Errors (failed to open) always show.

### Automation (`--automation`, for tests)
`ControlChannel` verbs handled by MainForm (UI thread). `open`/`append`/`activate` always;
the rest only with `--automation`:

| verb | args | response |
|---|---|---|
| `open` | paths… | `ok` (replaces playlist, activates window; no paths = just activate) |
| `append` | paths… | `ok` |
| `activate` | | `ok` |
| `action` | ids… | `ok` or `error: <id> unknown/disabled/failed` (runs each via the registry) |
| `key` | key names… (`Ctrl+Right`) | `ok <actionId>` per key, or `unbound <key>` (same path as a real key press) |
| `mouse` | `click x y` / `dblclick x y` / `wheel x y delta [ctrl\|shift\|alt]` / `drag x1 y1 x2 y2 [middle]` / `rclick x y` (client coords of VideoHost) | `ok` (calls the same handlers real input uses); a `click` on a quick start card button presses it like VideoHost does and answers `ok OpenFolder` / `ok GotIt` |
| `dump` | | JSON state (below) |
| `get` | mpv property | value string or `error` |
| `mpv` | command args… | `ok` / `error <code>` |
| `menu` | `context`\|`speed`\|`repeat`\|`zoom` | JSON tree of the built menu: `[{text, shortcut, checked, enabled, children}]` |
| `screenshot` | path | `ok` (mpv `screenshot-to-file <path> window`) |
| `resize` | w h | `ok` (client size of the form) |
| `quit` | | `ok`, then closes |

`dump` JSON keys (null when unknown): `path, playlistIndex, playlistCount, playlist (array),
paused, position, duration, frame, frameCount, fps, speed, volume, muted, repeat, shuffle,
abA, abB, zoom (video-zoom), panX, panY, zoomText, rotate, aspect, panscan, hwdec,
hwdecCurrent, fullscreen, onTop, controlsVisible, playlistVisible, windowBounds [x,y,w,h],
clientSize [w,h], videoHostSize [w,h], surfaceHandle, surfaceHandleAtStart, title, eofReached,
idle, subVisible, sid, aid, audioDelay, subDelay, cursorHidden, lastOsd, lastError,
controlBar {paused, position, duration, speed, repeat, abLoop, zoomText, playlistText}`.

Keys added in v1 (same null rule):

| key | meaning |
|---|---|
| `controlBar.frameText`, `controlBar.vfrBadgeVisible`, `controlBar.cullChipVisible` | what the bar renders right now: the frame readout text (`"F 61 / 240"`, `"≈F 61 / 180"`), whether the VFR badge and the cull chip are laid out (the badge is among the first parts dropped when the bar is short of room) |
| `vfr` | the frame probe's verdict: true = variable frame rate; null = unknown, still probing, or no media |
| `frameApproximate` | frame numbers on screen are estimates ("≈F"): variable rate, or a container the probe cannot read (false while the probe runs) |
| `frameTiming` | the probe's summary, e.g. `"mkv CFR 240 fps, 960 frames, 200-250 fps"` |
| `hwdecMemo` | `{skipped, hardware}`: codecs taken out of `hwdec-codecs` for the session after starting in software although hardware decoding was asked for, and codecs seen decoding in hardware |
| `osdStatus`, `borderless`, `captionVisible`, `keepAspect`, `reverse`, `audioNormalize`, `audioChannels`, `audioDevice`, `taskbarProgress`, `taskbarState`, `playlistSort`, `playlistDescending` | feature state (MainForm.Features / Taskbar) |
| `markers`, `markedFiles` | marker times of the current file; every file with markers this session (markers follow files VidFlow renames or moves and are dropped with files it deletes) |
| `cull` | `{mode, filter, autoAdvance, flag, rating, picked, rejected, undecided, rated, total, progressText, indicator, lastPlan {picks, rejects, alreadyKept, alreadyRejected, permanentDeletes}, marks [[flag, rating] per playlist item], undo, redo, undoLabel, busy, lastResult, storePath, storeDirty, panelVisible, panelSummary, panelFilter, bar {mode, flag, rating, progress}}` (MainForm.Cull.cs `CullDump` is authoritative; decisions index: `<settings dir>\cull-decisions.tsv`) |
| `quickStart` | `{visible, shown, card [x,y,w,h], open [..], gotIt [..], rows, hot, settingShown}` (video-surface coordinates) |
| `hostHandle` | VideoHost window handle (tests post mouse messages to it) |
| `markerExport` | `{files, markers, lastPath, lastKind}` |
| `update` | `{repo, enabled, busy, checks, dialogOpen, current, status, latest, tag, pageUrl, error, pageNotOpened}` |

Automation-only action forms (run through `action`): `CullCommit:apply` runs the cull Apply step
without its dialog (rejects on drives without a Recycle Bin are skipped, never deleted);
`RenameFile:<new name>` renames the current file without asking and fails with the reason for a bad
or taken name.

VidFlow also publishes `user-data/vidflow/vfr` (`yes`/`no`/`unknown`) and
`user-data/vidflow/frame-approximate` (`yes`/`no`) to mpv, readable with `get` and by mpv scripts or
OSD format strings (`${?user-data/vidflow/frame-approximate==yes:≈}`).

Test-only environment variable: `VIDFLOW_UPDATE_API` replaces `https://api.github.com` for Check for
Updates (tests point it at a loopback server; the check only runs when clicked).
