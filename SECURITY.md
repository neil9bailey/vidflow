# Security policy

## Reporting a vulnerability

Please report security problems privately, not in a public issue: on the repository's
**Security** tab choose **Report a vulnerability**. That opens a private advisory only the
maintainers can see.

Useful details: the VidFlow Player version (Help > About), what an attacker could do, and the
steps or a file that shows it. You should get a first reply within a week. Once a fix is released
the advisory is published with credit to you, unless you'd rather stay anonymous.

## Supported versions

Fixes go into the latest release only. Pre-releases (betas) are replaced by the next one.

## Scope

VidFlow Player embeds a prebuilt mpv / FFmpeg engine (see THIRD-PARTY-NOTICES.md). Decoder bugs
belong upstream, but please tell us too so we can ship an updated engine.

Release downloads carry SHA-256 checksums in `SHA256SUMS.txt`; check them before running a file
you got from anywhere other than the project's releases page.
