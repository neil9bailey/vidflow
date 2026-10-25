# Engine source: the GPL corresponding source of libmpv-2.dll

VidFlow Player embeds `libmpv-2.dll` from `mpv-dev-x86_64-20261010-git-b2c255c13e.7z`
([shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake) release
`20261010`, pinned in `tools/fetch-engine.ps1`). The DLL is built from mpv, FFmpeg and 85 other
packages (libraries, and a few header-only ones), all linked in statically, and is GPL as a whole.
So every VidFlow release has to come with the source of all of them, not just mpv and FFmpeg.

## What is published

| File | What it is |
|---|---|
| `engine-20261010-source.tar.xz` | The build recipe and every package below at its exact revision, the tarballs the recipe downloads, and the Rust crates linked into subrandr. `README.txt` and `MANIFEST.tsv` inside. |
| `engine-20261010-source-manifest.tsv` | The same manifest on its own: path, version, source location, revision or hash, licence, basis. |
| `engine-20261010-source-SHA256SUMS.txt` | Checksums of the two files above (engine pre-release only). |

The manual **Engine source** workflow (`.github/workflows/engine-source.yml`, which runs
`tools/pack-engine-source.sh`) builds these and attaches them to the `engine-20261010`
pre-release. The Release workflow copies the tarball and manifest from there into every VidFlow
release, next to the exe.

## The pin list

[`tools/engine-source.tsv`](../tools/engine-source.tsv) has one row per package (GitHub shows it as
a table): version, source, revision (full commit, or `sha256:`/`sha1:` for a tarball), the
recipe's sparse-checkout paths (it leaves out mostly test data; the tarball leaves out the same),
submodules, licence, and the basis for the revision.

The packages are mpv's dependency tree in the recipe (`DEPENDS` in its `packages/*.cmake`, with
the x86_64 options), 87 packages plus the recipe itself. The rest of what the recipe builds is not
linked into libmpv and is not included: cryptopp, expat, flac, game-music-emu, gmp, libopusenc,
libsodium, libuv, mbedtls, megasdk, mpv-packaging, mpv-release, nettle, opus-tools, opusfile,
readline, sqlite, termcap, xvidcore.

## How the revisions were established

The recipe pins only a few packages (`GIT_RESET` commits and tarball hashes). The rest follow a
branch head that is fetched at build time, so the recipe alone does not say what was built.
The `20261010` archive comes from the recipe's workflow run
[38007080592](https://github.com/shinchiro/mpv-winbuild-cmake/actions/runs/38007080592), job
"Building mpv (x86_64)", at recipe commit `b2856ee`. From that job and the earlier ones:

1. Its update step fetched the packages it tracks at 2026-10-10 00:01 UTC and reset each to its branch head.
   The log prints every branch that moved since the cached copy, with the new commit.
2. Its source cache had been carried over, run to run, from run 31041189115 (2026-08-05 19:49 UTC),
   which found no cache and cloned everything afresh.
3. So a package's revision is the last logged update of its branch since that clone (basis
   "build log"), or, when none was logged, its branch head at the clone, which was still the head
   at build time (basis "unchanged since 2026-08-05 clone"; checked against the hosts' push and
   commit history).
4. curl is left out of the update step: it was cloned once, on 2026-08-06 at 16:41 UTC (run
   31118729206), and never fetched again, so it is curl's master at that moment.
5. The recipe also checks out submodules unless it opts out. At these revisions that matters for
   libpsl (`list`, the public suffix data compiled into it) and nghttp3 (`lib/sfparse`), which
   the tarball includes at the commits their parents record. Test-only submodules (bzip2's test
   files, srt's ABI checker) are left out.

Cross-checks: mpv `b2c255c13e` and FFmpeg `ef305e32e` are in the archive's file names, and the DLL
reports libcurl `8.22.0-DEV`, which fits curl's commit.

## Not included

- The compiler toolchain the recipe builds (LLVM/clang `release/23.x`, mingw-w64, Rust nightly).
  The parts of it linked into the DLL (compiler-rt, libc++, libunwind, the mingw-w64 runtime and
  winpthreads, Rust's standard library) come with those compilers and are System Libraries in the
  sense of the GPL.
- General build tools (CMake, Meson, NASM and the like).

The recipe repository has no licence file; it is included because the GPL asks for the scripts
that control the build.

## When the engine changes

1. Re-establish the revisions for the new build the same way, from its workflow run logs.
   GitHub keeps those logs for about 90 days, so do it soon after the build.
2. Rewrite `tools/engine-source.tsv`. `tools/pack-engine-source.sh --check` refuses a list whose
   `mpv-winbuild-cmake` row does not match `$Tag` in `tools/fetch-engine.ps1`.
3. Run the Engine source workflow with "publish" ticked, then release. The Release workflow fails
   while the new engine pre-release has no source.
