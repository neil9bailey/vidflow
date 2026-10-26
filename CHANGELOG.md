# Changelog

Notable changes to VidFlow Player. Versions follow [Semantic Versioning](https://semver.org);
the release workflow copies the section for the tagged version into the release notes.

## [1.0.0-beta.5] - 2026-10-26

Changes since 1.0.0-beta.4; the full history is in
[CHANGELOG.md](https://github.com/neil9bailey/vidflow/blob/main/CHANGELOG.md).

### Added
- Microsoft Store edition (listing coming soon): the player can now be packaged for the Store,
  which signs it (no security warnings) and keeps it updated. That edition leaves updates to the
  Store and file types to Windows, so Check for Updates and the "Open With" registration are hidden
  there, and it keeps its settings in its own folder (copied from the portable exe's on first start).
- Releases carry a GitHub build-provenance attestation for `VidFlow.exe` and the zip
  (`gh attestation verify VidFlow.exe --repo neil9bailey/vidflow`).

### Changed
- Releases include the playback engine's complete corresponding source: every library inside it at
  the exact revision the build used, with a manifest of versions and licences.

## [1.0.0-beta.4] - 2026-10-25

First public build, from https://github.com/neil9bailey/vidflow.

### Added
- Help > Check for Updates works out of the box: one request to GitHub, only when you click it.
  Settings saved by beta 3 keep it hidden; set `UpdateRepo=neil9bailey/vidflow` in `VidFlow.ini`
  to turn it on there.
- About shows the exact version and links to the source code.

### Changed
- Check for Updates understands pre-release versions: a beta is offered the final release of the
  same version.

## [1.0.0-beta.3] - 2026-10-25

Renamed from Glance to VidFlow Player. Glance settings, shortcuts, recent files and cull decisions
are copied across on first start; the old files are left untouched.

### Added
- **Cull mode** (C): P / X pick or reject and move to the next clip, U clears; 1-5 and 0 rate;
  N jumps to the next undecided clip; F5-F9 move the clip to a bin folder.
- **Apply** (Ctrl+Enter): picks go to a Keep folder and rejects to the Recycle Bin, after a summary.
- Undo and redo (Ctrl+Z / Ctrl+Y) for every decision, move, rename and apply.
- Rename the current clip (F2); sidecar subtitles follow.
- Decisions persist between sessions without writing anything next to your clips.
- A corner badge shows the clip's decision in fullscreen while Cull mode is on.
- Exact frame numbers on constant-frame-rate video; "≈F" and a VFR badge on variable-frame-rate clips.
- Export markers as CSV or EDL (DaVinci Resolve).
- Quick-start card on first launch (Help > Quick Start Guide).
- Paths longer than 260 characters open.

### Fixed
- AV1 files switch much faster after the first one (about 0.3 s instead of 1.5 s).
- Repeated Apply no longer nests folders; a held key can no longer reject a run of clips; F5-F9
  only act in Cull mode; markers follow renamed files.

## [1.0.0-beta.2] - 2026-10-24

### Added
- On-video time, frame and speed line (O).
- Separate loop start and end (Alt+[ / Alt+]), draggable A/B markers and loop nudging.
- Review markers (B) with next / previous navigation and copy to clipboard.
- Backward playback (Shift+Space), volume normalization, stereo / mono downmix and audio device choice.
- Borderless window (Ctrl+B), saved playlists (.m3u8) and opening .m3u / .m3u8 / .pls files.
- Folders open with their subfolders; sort by name, date, size or type.
- Taskbar progress and Prev / Play / Next thumbnail buttons.
- Optional per-user "Open with" registration.
- Command-line options: `--start`, `--speed`, `--repeat`, `--paused`, `--enqueue`.

### Fixed
- Deleting from a drive without a Recycle Bin now asks first instead of deleting permanently.
- No freeze when switching clips or on the first seek-bar hover.
- Keys no longer stick in the playlist filter box; fullscreen survives minimize and restore.
- Large playlists no longer freeze; several windows no longer overwrite each other's settings.
- Several files opened together from Explorer queue in one window.

## [1.0.0-beta.1] - 2026-10-24

First beta: playback speed 0.1x-16x, frame stepping both ways, zoom at the cursor with panning,
control-free fullscreen, repeat (clip or folder), A-B loop, seek thumbnails, frame snapshots,
folder playlists, Recycle Bin delete, and fully remappable shortcuts.
