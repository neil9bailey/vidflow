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
components). Its build recipe is the `20261010` tag of mpv-winbuild-cmake. The recipe follows
most libraries' development branches rather than pinning them, so the exact revision of each
library in this build was established from the upstream build's logs; see
[docs/ENGINE-SOURCE.md](docs/ENGINE-SOURCE.md).

## Components

The engine contains mpv, FFmpeg and 85 other packages. Every one is listed with its version,
source location, exact revision and licence in
[tools/engine-source.tsv](tools/engine-source.tsv). The main ones:

- **mpv** — GPLv2 or later / LGPLv2.1 or later (GPL in this build) — https://mpv.io
- **FFmpeg** — LGPLv2.1 or later / GPLv2 or later (GPL in this build) — https://ffmpeg.org
- **libplacebo** — LGPLv2.1 or later — https://code.videolan.org/videolan/libplacebo
- **libass**, **FreeType**, **HarfBuzz**, **FriBidi** (subtitle rendering) — ISC / FreeType / MIT / LGPL
- **dav1d**, **libvpx**, **aom**, **x264**, **x265** and other codecs — BSD / GPL, see the list

## Corresponding source

Every VidFlow Player release on the [releases page](https://github.com/neil9bailey/vidflow/releases)
publishes, next to the binaries:

1. the VidFlow Player source code at that release's tag, and
2. the complete corresponding source of the engine, `engine-20261010-source.tar.xz`: the
   mpv-winbuild-cmake recipe and every library linked into the engine at the exact revision the
   build used, with a manifest (`engine-20261010-source-manifest.tsv`) giving each one's version,
   source location and licence.

The same files, with their SHA-256 checksums, are on the
[engine-20261010](https://github.com/neil9bailey/vidflow/releases/tag/engine-20261010)
pre-release, next to the engine archive itself.

## Patent note

Some formats VidFlow Player decodes (e.g. H.264, HEVC, AAC) may be covered by patents in some
countries. See https://ffmpeg.org/legal.html. This notice is not legal advice.
