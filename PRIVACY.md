# Privacy

VidFlow Player works offline. It has no telemetry, no analytics, no crash reporting service and
no account.

## When it uses the network

Only when you ask it to:

- **Help > Check for Updates.** One request to the GitHub API for the project's latest release.
  GitHub sees your IP address and a `VidFlow/<version>` user agent, and nothing else is sent.
  VidFlow never checks in the background.
- **File > Open URL or Path** with a web address. The video is fetched from the server you typed.

The playback engine's own online features (such as youtube-dl / yt-dlp lookups) are turned off.

## What stays on your PC

- Settings: `%APPDATA%\VidFlow\VidFlow.ini`, or next to `VidFlow.exe` in portable mode.
- Cull decisions and ratings: `cull-decisions.tsv` in the same folder. Nothing is written
  next to your clips, except files you move, rename or export yourself.
- Resume positions: the engine's `watch_later` folder in the same place.
- Problems: `%LOCALAPPDATA%\VidFlow\crash.log`. It stays on your PC unless you choose to attach it
  to a bug report, so check it for file names you'd rather not share.
- The playback engine is unpacked once to `%LOCALAPPDATA%\VidFlow\engine`.

Deleting those folders removes everything VidFlow Player has stored.
