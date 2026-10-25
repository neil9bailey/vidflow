# Third-party notices

VidFlow Player is licensed under the GNU General Public License, version 3 (see [COPYING](COPYING)).
It embeds a prebuilt playback engine, `libmpv-2.dll`, which contains the following software.

## Engine build

| | |
|---|---|
| Build | `mpv-dev-x86_64-20261010-git-b2c255c13e.7z` from [shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake), release tag `20261010` |
| SHA-256 of the archive | `394490ed6354c2294b87c63dfa3663b23524b5551aace9cbe35c4085682b14fa` |
| mpv | `v0.41.0-1115-gb2c255c13` (git commit `b2c255c13e`) |
| FFmpeg | `N-127267-gef305e32e` (git commit `ef305e32e`) |

The engine archive is GPL-licensed as a whole (mpv and FFmpeg are configured with GPL
components). The build recipe, including every library and the exact versions it pins, is
the `20261010` tag of mpv-winbuild-cmake.

## Main components (non-exhaustive)

- **mpv** — GPLv2 or later / LGPLv2.1 or later (GPL in this build) — https://mpv.io
- **FFmpeg** — LGPLv2.1 or later / GPLv2 or later (GPL in this build) — https://ffmpeg.org
- **libplacebo** — LGPLv2.1 or later — https://code.videolan.org/videolan/libplacebo
- **libass**, **FreeType**, **HarfBuzz**, **FriBidi** (subtitle rendering) — ISC / FreeType / MIT / LGPL
- …and the other libraries pinned by the build recipe above (its `packages/` folder lists each
  one with its version and licence).

## Corresponding source

Every VidFlow Player release on the project page publishes, next to the binaries:

1. the VidFlow Player source code at that release's tag, and
2. the corresponding source of the engine: the mpv-winbuild-cmake recipe at tag `20261010` plus
   the source archives it fetched for mpv, FFmpeg and the libraries above.

## Patent note

Some formats VidFlow Player decodes (e.g. H.264, HEVC, AAC) may be covered by patents in some
countries. See https://ffmpeg.org/legal.html. This notice is not legal advice.
