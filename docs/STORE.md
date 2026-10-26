# Microsoft Store edition

The Store edition is the same player packaged as an MSIX. Microsoft signs it when it is published,
so it installs without SmartScreen warnings and runs under Smart App Control, and the Store keeps
it updated. It costs nothing: the developer account is free for individuals and the Store does the
signing.

What differs from the portable exe (`AppInfo.IsPackaged`):

- the engine (`libmpv-2.dll`) ships as a file in the package instead of being unpacked at run time;
- Check for Updates is hidden: the Store delivers updates;
- the "Open With" registration is hidden: the package manifest declares the file types, so Windows
  lists VidFlow under *Open with* for video and audio files automatically;
- About says "(Microsoft Store)";
- settings, cull decisions and logs live in the package's own folder,
  `%LOCALAPPDATA%\Packages\<package family>\LocalState\VidFlow`, which Windows removes on uninstall.
  On first start it copies the portable exe's settings, decisions, mpv config and resume positions
  from `%APPDATA%\VidFlow` if there are any (the originals are only read). Keeping its own folder
  avoids MSIX's AppData redirection, which would otherwise edit the portable exe's existing files in
  place while hiding new ones.

## Building the package

Needs the Windows 10/11 SDK (for `makeappx` and `makepri`) on top of the normal build tools.

```powershell
.\tools\package-store.ps1        # build check with a placeholder identity -> obj\store\
```

The package is unsigned on purpose: Partner Center accepts unsigned packages and signs them. The
placeholder build can't be installed as it is; it checks that the package builds.
CI checks on every push that it still builds. When the Partner Center identity is saved as
repository variables (step 4 below), every tagged release also builds the real upload as the
**store-msix** workflow artifact of the Release run. It is never attached to the GitHub release.

Store versions must rise with every submission and end in `.0`, so the release version is mapped:
`1.0.0-beta.5` → `1.0.305.0`, `1.0.0` → `1.0.900.0`, `1.0.1` → `1.0.1900.0` (see the script).

The tile images in `packaging/store/Assets` come from `python tools\make_store_assets.py` (the same
drawing as `assets\VidFlow.ico`). The manifest's file types must match the player's;
`tools\run-core-tests.ps1` checks that.

## First submission (one time)

Steps marked **you** need your own account or identity.

1. **You:** create a free **Individual** developer account at
   [storedeveloper.microsoft.com](https://storedeveloper.microsoft.com) with a personal Microsoft
   account, a photo ID and a selfie. Decide first: an Individual account can never become a
   Company account, and a business-sounding publisher name may require a Company account.
2. **You:** in [Partner Center](https://partner.microsoft.com/dashboard) › Apps and games ›
   New product › **MSIX or PWA app**, reserve the name **VidFlow Player** (or another, if it is
   taken: the name in the package must match the reservation).
3. **You:** open Product management › **Product identity** and copy three values:
   `Package/Identity/Name`, `Package/Identity/Publisher` and
   `Package/Properties/PublisherDisplayName`.
4. Save them as repository variables (GitHub › Settings › Secrets and variables › Actions ›
   Variables): `STORE_IDENTITY_NAME`, `STORE_PUBLISHER`, `STORE_PUBLISHER_DISPLAY_NAME`, and
   `STORE_DISPLAY_NAME` only if you reserved a name other than *VidFlow Player*. They are not
   secret: they are printed in every Store package.
5. Tag the next release (1.0.0-beta.5 or later: earlier tags predate the Store edition). The
   Release run's **store-msix** artifact holds `VidFlow-<version>-store.msix`.
6. **You, before the first submission:** run the real packaged app once, since the tests only
   simulate it. Turn on Developer Mode (Settings › System › For developers), build the package
   locally, then register its folder (no signing needed for this):

   ```powershell
   .\tools\package-store.ps1
   Add-AppxPackage -Register obj\store\layout\AppxManifest.xml
   ```

   Start *VidFlow Player* from the Start menu and check: a clip plays; Help shows no Check for
   Updates; About says "(Microsoft Store)"; Settings › Advanced › Open settings folder opens
   `...\Packages\<family>\LocalState\VidFlow`, and changed settings survive a restart;
   right-click a video › Open with lists VidFlow Player, and choosing it opens and plays that clip;
   select three videos in Explorer and open them with VidFlow together: all three are queued in one
   window. Then remove it with `Get-AppxPackage *VidFlow* | Remove-AppxPackage`.
7. **You:** in Partner Center, start a submission:
   - **Packages:** upload the `.msix`.
   - **Pricing and availability:** Free; all markets.
   - **Properties:** category *Photo & video*; privacy policy URL below.
   - **Age ratings:** answer the questionnaire (no user-generated content, no online interaction,
     no purchases).
   - **Store listing:** paste the text below and add screenshots (at least one; 1920×1080 looks best,
     ideally playing your own footage).
   - **Submission options › Restricted capabilities:** paste the *runFullTrust* explanation below
     (Partner Center asks for it because the package declares that capability).
8. Certification usually takes up to three business days. When the listing is live, replace
   "(coming soon)" in the README with the Store link.

For later releases: tag, download the new **store-msix** artifact, and create a new submission with
it (Partner Center copies the listing forward).

## Listing text

**Product name:** VidFlow Player

**Short description:**
Get through a folder of video clips fast: keep the good ones, bin the rest.

**Description:**

> Just copied hundreds of clips off a GoPro, drone, dashcam or phone, and only a few are worth
> keeping? Open the folder in VidFlow Player, see each clip properly, decide with one key, and apply
> everything in one safe batch.
>
> • Cull mode: P to pick or X to reject, and it moves on to the next clip; 1–5 to rate.
> • Apply sends picks to a Keep folder and rejects to the Recycle Bin, after a summary you review.
> • Undo and redo every decision, move, rename and apply.
> • Exact frame stepping both ways, with honest frame numbers (marked when a phone clip has a
>   variable frame rate).
> • Instant timeline previews, 0.1×–16× speed, reverse play, zoom at the cursor, clean fullscreen.
> • A-B loops and review markers, exportable as CSV or an EDL for DaVinci Resolve.
> • Plays practically everything (H.264, HEVC, AV1, VP9, ProRes; MP4, MKV, MOV, WebM and more)
>   with hardware decoding, thanks to the mpv and FFmpeg engine.
> • Fully remappable shortcuts. Nothing is written next to your clips until you apply.
>
> Free and open source (GNU GPL v3). No ads, no account, no telemetry: VidFlow works offline.

**Features** (one per line):
- Cull a folder with single keys: pick, reject, rate, auto-advance
- Apply decisions in one reviewable batch; rejects go to the Recycle Bin
- Undo and redo for every decision, move and rename
- Frame-exact stepping forwards and backwards
- Timeline previews, speed control, reverse play and zoom
- Markers and loops, exportable to CSV and DaVinci Resolve EDL
- Plays almost any video format with hardware decoding
- Free, open source, offline, no telemetry

**Search terms:** video player, video culling, frame by frame, action camera, dashcam, clip review,
frame step

(Store policy 10.1.3: no other products' names in search terms, so no "GoPro" or "mpv" here; the
description may name them.)

**Privacy policy URL:** https://github.com/neil9bailey/vidflow/blob/main/PRIVACY.md

**Website:** https://github.com/neil9bailey/vidflow

**Support contact:** https://github.com/neil9bailey/vidflow/issues

**Additional license terms:**
Free software under the GNU General Public License v3:
https://github.com/neil9bailey/vidflow/blob/main/COPYING. The complete source, including the
playback engine's, is published at https://github.com/neil9bailey/vidflow.

**Restricted capabilities › runFullTrust:**
VidFlow Player is a Win32 desktop application (Windows Forms on .NET Framework 4.8) packaged as
MSIX, so it needs runFullTrust to run as a full-trust desktop app. It plays media files the user
opens, using its bundled mpv/FFmpeg engine (libmpv-2.dll, shipped in the package) with hardware
video decoding. When the user applies their decisions it moves the clips they chose to folders they
chose, and sends rejected clips to the Recycle Bin. It does not update itself and makes no network
requests except opening a URL the user types.
