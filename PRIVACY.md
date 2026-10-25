# Privacy

VidFlow Player works offline. It has no telemetry, no analytics, no crash reporting service and
no account.

## When it uses the network

Only when you ask it to:

- **Help > Check for Updates.** One request to the GitHub API for the project's latest release.
  GitHub sees your IP address and a `VidFlow/<version>` user agent, and nothing else is sent.
  VidFlow never checks in the background. The Microsoft Store edition has no such check: the Store
  handles its updates.
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

The Microsoft Store edition keeps its settings, decisions, resume positions and logs in its own
folder, `%LOCALAPPDATA%\Packages\<package>\LocalState\VidFlow` (Settings › Advanced › Open settings
folder), and Windows deletes that folder when you uninstall it. It has no unpacked engine folder.
On its first start it copies the portable exe's settings from `%APPDATA%\VidFlow` if there are any;
it only reads those, so they stay as they are.
