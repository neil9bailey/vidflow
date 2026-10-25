#!/usr/bin/env bash
# Packs the complete corresponding source of the embedded engine (libmpv-2.dll) into
# engine-<tag>-source.tar.xz, with a manifest and SHA256SUMS next to it:
#   - every package in tools/engine-source.tsv at the exact revision the upstream build used
#     (honouring the recipe's sparse checkouts and the submodules it pulls in),
#   - the tarballs the recipe downloads, checked against its hashes,
#   - the Rust crates linked into subrandr, checked against its Cargo.lock.
# Linux only (needs git, curl, cargo, jq, xz); run by .github/workflows/engine-source.yml.
# See docs/ENGINE-SOURCE.md for how the pins were established.
#
#   tools/pack-engine-source.sh --check      validate the pin list (no network)
#   tools/pack-engine-source.sh <out-dir>    fetch, pack and write the files to <out-dir>
set -euo pipefail
export GIT_TERMINAL_PROMPT=0 LC_ALL=C

root=$(cd "$(dirname "$0")/.." && pwd)
pins="$root/tools/engine-source.tsv"
tag=$(sed -n "s/^[$]Tag = '\([^']*\)'.*/\1/p" "$root/tools/fetch-engine.ps1")
archive=$(sed -n "s/^[$]Archive = '\([^']*\)'.*/\1/p" "$root/tools/fetch-engine.ps1")
name="engine-$tag-source"
crate_target=x86_64-pc-windows-gnullvm   # what the recipe's clang toolchain builds subrandr for

die() { echo "error: $*" >&2; exit 1; }

rows() { tail -n +2 "$pins" | tr -d '\r'; }

check() {
    [ -n "$tag" ] && [ -n "$archive" ] || die "cannot read \$Tag/\$Archive from tools/fetch-engine.ps1"
    [ "$(head -n 1 "$pins" | tr -d '\r')" = "$(printf 'package\tversion\tsource\trevision\tpaths\tsubmodules\tlicence\tbasis')" ] \
        || die "unexpected header in $pins"
    local n=0 pkg ver src rev paths subs lic basis extra
    while IFS=$'\t' read -r -u 3 pkg ver src rev paths subs lic basis extra; do
        n=$((n + 1))
        [ -n "$basis" ] && [ -z "$extra" ] || die "$pkg: need exactly 8 tab-separated fields"
        case "$src" in https://*) ;; *) die "$pkg: source must be an https URL" ;; esac
        case "$rev" in
            sha256:*) [[ ${rev#sha256:} =~ ^[0-9a-f]{64}$ ]] || die "$pkg: bad sha256" ;;
            sha1:*) [[ ${rev#sha1:} =~ ^[0-9a-f]{40}$ ]] || die "$pkg: bad sha1" ;;
            *) [[ $rev =~ ^[0-9a-f]{40}$ ]] || die "$pkg: revision must be a full commit hash" ;;
        esac
    done 3< <(rows)
    [ "$(rows | cut -f1 | sort | uniq -d)" = "" ] || die "duplicate packages in $pins"
    # The pins belong to one engine build: refuse to pack them under another engine's name.
    [ "$(rows | awk -F'\t' '$1 == "mpv-winbuild-cmake" { print $2 }')" = "$tag" ] \
        || die "the pin list is for a different engine than tools/fetch-engine.ps1 ($tag)"
    echo "$n packages pinned for engine $tag"
}

fetch_git() {  # url, commit, directory, sparse-checkout patterns or "-"
    local url=$1 rev=$2 dir=$3 paths=$4
    git init -q "$dir"
    git -C "$dir" remote add origin "$url"
    git -C "$dir" config remote.origin.promisor true             # partial clone: blobs on demand
    git -C "$dir" config remote.origin.partialclonefilter blob:none
    if [ "$paths" != - ]; then
        git -C "$dir" config core.sparseCheckout true
        git -C "$dir" config core.sparseCheckoutCone false
        set -f; printf '%s\n' $paths > "$dir/.git/info/sparse-checkout"; set +f
    fi
    # One commit, blobs only where checked out. Hosts that refuse a fetch by hash get a full one.
    if ! git -C "$dir" fetch -q --depth=1 --filter=blob:none origin "$rev"; then
        echo "  shallow fetch refused, fetching all branches"
        git -C "$dir" fetch -q --filter=blob:none origin '+refs/heads/*:refs/remotes/origin/*'
    fi
    git -C "$dir" -c advice.detachedHead=false checkout -q "$rev"
    [ "$(git -C "$dir" rev-parse HEAD)" = "$rev" ] || die "$dir is not at $rev"
}

fetch_url() {  # url, algorithm:hash, file
    local url=$1 sum=$2 file=$3
    curl -fsSL --retry 5 --retry-all-errors -o "$file" "$url"
    echo "${sum#*:}  $file" | "${sum%%:*}sum" -c --quiet - || die "$url does not match $sum"
}

pack() {
    local out=$1 pkg ver src rev paths subs lic basis sub
    check
    mkdir -p "$out"
    out=$(cd "$out" && pwd)
    work=$(mktemp -d)
    trap 'rm -rf "$work"' EXIT
    local stage="$work/$name" manifest="$work/$name/MANIFEST.tsv"
    mkdir -p "$stage/packages" "$stage/tarballs" "$stage/crates"
    printf 'path\tpackage\tversion\tsource\trevision\tlicence\tbasis\n' > "$manifest"

    while IFS=$'\t' read -r -u 3 pkg ver src rev paths subs lic basis; do
        echo "== $pkg"
        case "$rev" in
        sha256:* | sha1:*)
            fetch_url "$src" "$rev" "$stage/tarballs/${src##*/}"
            printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "tarballs/${src##*/}" "$pkg" "$ver" "$src" "$rev" "$lic" "$basis" >> "$manifest"
            ;;
        *)
            fetch_git "$src" "$rev" "$stage/packages/$pkg" "$paths"
            printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "packages/$pkg" "$pkg" "$ver" "$src" "$rev" "$lic" "$basis" >> "$manifest"
            # Submodules the recipe checks out, at the commit the package records for them.
            [ "$subs" = - ] || for sub in ${subs//,/ }; do
                local dir="$stage/packages/$pkg" key subrev suburl
                subrev=$(git -C "$dir" ls-tree "$rev" -- "$sub" | awk '$2 == "commit" { print $3 }')
                key=$(git -C "$dir" config --blob "$rev:.gitmodules" --get-regexp '^submodule\..*\.path$' \
                    | awk -v p="$sub" '$2 == p { print $1 }')
                suburl=$(git -C "$dir" config --blob "$rev:.gitmodules" --get "${key%.path}.url")
                [ -n "$subrev" ] && [[ $suburl == https://* ]] || die "$pkg: cannot resolve submodule $sub"
                echo "  submodule $sub"
                rmdir "$dir/$sub" 2>/dev/null || true
                fetch_git "$suburl" "$subrev" "$dir/$sub" -
                printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "packages/$pkg/$sub" "$pkg/$sub" "submodule" "$suburl" "$subrev" "$lic" "submodule of $pkg" >> "$manifest"
            done
            ;;
        esac
    done 3< <(rows)

    # subrandr is Rust: the crates linked into it (normal and build dependencies, for the
    # target the recipe builds) come from crates.io at the versions its Cargo.lock pins.
    echo "== subrandr crates"
    local sr="$stage/packages/subrandr" cname cver csum clic line
    cargo metadata --locked --format-version 1 --manifest-path "$sr/Cargo.toml" --filter-platform "$crate_target" \
        | jq -r '.packages[] | select(.source != null) | [.name, .version, (.license // "see crate")] | @tsv' > "$work/crate-licences.tsv"
    cargo tree --locked --manifest-path "$sr/Cargo.toml" -p subrandr --target "$crate_target" -e normal,build \
        --prefix none --format '{p}' | sed -e 's/ (\*)$//' -e 's/ (proc-macro)$//' | sort -u > "$work/crates.txt"
    while read -r -u 3 line; do
        case "$line" in
            *" (/"*) continue ;;                      # path crates inside subrandr itself
            *" ("*) die "unexpected crate source: $line" ;;
        esac
        cname=${line%% *}; cver=${line#* v}
        csum=$(awk -v n="\"$cname\"" -v v="\"$cver\"" \
            '$1 == "name" { nm = $3 } $1 == "version" { vs = $3 } $1 == "checksum" && nm == n && vs == v { gsub(/"/, "", $3); print $3 }' "$sr/Cargo.lock")
        [ -n "$csum" ] || die "no Cargo.lock checksum for $cname $cver"
        clic=$(awk -F'\t' -v n="$cname" -v v="$cver" '$1 == n && $2 == v { print $3 }' "$work/crate-licences.tsv")
        fetch_url "https://static.crates.io/crates/$cname/$cname-${cver//+/%2B}.crate" "sha256:$csum" "$stage/crates/$cname-$cver.crate"
        printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "crates/$cname-$cver.crate" "$cname" "$cver" \
            "https://crates.io/crates/$cname/$cver" "sha256:$csum" "${clic:-see crate}" "subrandr Cargo.lock" >> "$manifest"
    done 3< "$work/crates.txt"

    find "$stage/packages" -name .git -prune -exec rm -rf {} +
    cat > "$stage/README.txt" <<EOF
Complete corresponding source of libmpv-2.dll, the playback engine embedded in VidFlow Player
($archive, shinchiro/mpv-winbuild-cmake release $tag).

packages/mpv-winbuild-cmake  the build recipe (CMake files and patches); its README explains the build
packages/<name>              every library linked into the engine, at the exact revision the
                             upstream $tag build used; files the recipe itself leaves out with a
                             sparse checkout (mostly test data) are left out here too
tarballs/                    sources the recipe downloads as tarballs, checked against its hashes
crates/                      Rust crates linked into subrandr, checked against its Cargo.lock
MANIFEST.tsv                 every item with its version, source location, revision or hash and licence

The recipe follows most libraries' branch heads instead of pinning them, so the revisions were
established from the upstream build's own logs: see docs/ENGINE-SOURCE.md and
tools/engine-source.tsv in the VidFlow Player source, https://github.com/neil9bailey/vidflow.
Not included: the compiler toolchain (LLVM/clang, mingw-w64, Rust); the runtime parts of it that
are linked into the engine are System Libraries in the sense of the GPL.
EOF

    # Reproducible archive: fixed order, owners and timestamps.
    tar --sort=name --mtime="${tag:0:4}-${tag:4:2}-${tag:6:2} 00:00:00Z" --owner=0 --group=0 --numeric-owner \
        --format=gnu -C "$work" -cf - "$name" | xz -T0 -6 > "$out/$name.tar.xz"
    cp "$manifest" "$out/$name-manifest.tsv"
    (cd "$out" && sha256sum -- "$name.tar.xz" "$name-manifest.tsv" > "$name-SHA256SUMS.txt")
    echo "$(($(wc -l < "$manifest") - 1)) items, $(du -m "$out/$name.tar.xz" | cut -f1) MB:"
    ls -l "$out"
}

case "${1:-}" in
    --check) check ;;
    "" | -*) die "usage: $0 --check | $0 <out-dir>" ;;
    *) pack "$1" ;;
esac
