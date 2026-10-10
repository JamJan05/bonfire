#!/usr/bin/env bash
# Release workflow for JamJan05/bonfire. main is protected (changes only through pull requests with
# green CI), so a release takes three steps:
#
#   TEST=1 packaging/release.sh prepare   build + test every artifact from the clean, current main,
#                                         write the Gentoo Manifest for that payload and open a pull
#                                         request with it (branch release/vX.Y.Z-<commit>)
#   packaging/release.sh finalize         after that pull request is merged: check that main now
#                                         carries exactly that Manifest, rebuild the payload to prove
#                                         it is unchanged, then create the GitHub pre-release from main
#   packaging/release.sh promote          after testing the pre-release: mark it as the latest release
#
# The Manifest commit does not change the payload: SOURCE_DATE_EPOCH ignores Manifest-only commits
# and no Git commit is embedded in the binaries. Published versions and release tags are immutable.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
cd "$ROOT"
REPO=JamJan05/bonfire
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
TAG="v$VERSION"
DIST="$ROOT/dist"
PAYLOAD="bonfire-$VERSION-linux-x64.tar.gz"
EBUILD_DIR=games-util/bonfire-bin

die() { echo "release: $*" >&2; exit 1; }

release_exists() { gh release view "$TAG" -R "$REPO" >/dev/null 2>&1; }

require_clean_main() {
    case "$(git remote get-url origin)" in
        *github.com[:/]$REPO.git|*github.com[:/]$REPO) ;;
        *) die "origin must be the release repository $REPO" ;;
    esac
    [[ "$(git rev-parse --abbrev-ref HEAD)" == main ]] || die "run this on main"
    [[ -z "$(git status --porcelain)" ]] || die "the working tree is not clean"
    git fetch -q origin main
    [[ "$(git rev-parse HEAD)" == "$(git rev-parse origin/main)" ]] || die "main is not identical to origin/main (pull first)"
}

require_new_tag() {
    release_exists && die "$TAG is already published; bump <Version> for a new release"
    local tags
    tags=$(git ls-remote --tags "git@github.com:$REPO.git" "refs/tags/$TAG") || die "cannot list tags of $REPO"
    [[ -z "$tags" ]] || die "tag $TAG already exists without a release; refusing to reuse it"
}

# The DIST line of the committed Manifest must describe exactly this file.
manifest_matches() { # <file>
    python3 - "$1" "$EBUILD_DIR/Manifest" <<'PY'
import hashlib, os, sys
path, manifest = sys.argv[1], sys.argv[2]
name = os.path.basename(path)
data = open(path, "rb").read()
want = f"DIST {name} {len(data)} BLAKE2B {hashlib.blake2b(data).hexdigest()} SHA512 {hashlib.sha512(data).hexdigest()}"
lines = [l.strip() for l in open(manifest) if l.startswith(f"DIST {name} ")]
sys.exit(0 if lines == [want] else 1)
PY
}

prepare() {
    require_clean_main
    require_new_tag
    [[ -f "$EBUILD_DIR/bonfire-bin-$VERSION.ebuild" ]] || die "$EBUILD_DIR/bonfire-bin-$VERSION.ebuild is missing (git mv the old ebuild)"
    # One preparation branch per built commit, so preparing again after main moved always works.
    local branch
    branch="release/$TAG-$(git rev-parse --short HEAD)"
    local remote
    remote=$(git ls-remote --heads origin "$branch") || die "cannot list branches of origin"
    if git show-ref --quiet "refs/heads/$branch" || [[ -n "$remote" ]]; then
        die "$branch already exists (delete it, or merge its pull request and run finalize)"
    fi
    "$ROOT/packaging/build-all.sh"

    local work
    work=$(mktemp -d)
    (cd "$EBUILD_DIR" && DISTDIR="$DIST" PORTAGE_TMPDIR="$work" ebuild "bonfire-bin-$VERSION.ebuild" manifest >/dev/null)
    rm -rf "$work"
    manifest_matches "$DIST/$PAYLOAD" || die "the generated Manifest does not describe $PAYLOAD"

    if [[ -z "$(git status --porcelain "$EBUILD_DIR")" ]]; then
        echo "The committed Manifest already matches $PAYLOAD; run: packaging/release.sh finalize"
        return
    fi
    local built
    built=$(git rev-parse --short HEAD)
    git switch -q -c "$branch"
    git add "$EBUILD_DIR"
    git commit -q -m "games-util/bonfire-bin: Manifest for $VERSION"
    git push -q -u origin "$branch"
    gh pr create -R "$REPO" --base main --head "$branch" --title "Release $VERSION: Gentoo Manifest" \
        --body "Manifest for \`$PAYLOAD\` (built from $built). After CI passes and this is merged, run \`packaging/release.sh finalize\`."
    git switch -q main
    echo "Merge the pull request after CI passes, then: git pull && packaging/release.sh finalize"
}

finalize() {
    require_clean_main
    require_new_tag
    [[ -f "$DIST/$PAYLOAD" ]] || die "dist/ has no $PAYLOAD; run prepare first"
    manifest_matches "$DIST/$PAYLOAD" || die "main does not carry the Manifest for dist/$PAYLOAD (is the pull request merged and pulled?)"

    # Proof that the merged commit builds the very same payload: rebuild and compare.
    local before after keep
    before=$(sha256sum "$DIST/$PAYLOAD" | cut -d' ' -f1)
    keep=$(mktemp -d)
    cp -a "$DIST/." "$keep/"
    "$ROOT/packaging/publish.sh" >/dev/null
    after=$(sha256sum "$DIST/$PAYLOAD" | cut -d' ' -f1)
    rm -rf "$DIST" && cp -a "$keep" "$DIST" && rm -rf "$keep"
    [[ "$before" == "$after" ]] || die "main builds a different payload ($before → $after); prepare again"

    local name="bonfire-$VERSION"
    git archive --format=tar --prefix="$name-source/" HEAD | gzip -9n > "$DIST/$name-source.tar.gz"
    local assets=("$PAYLOAD" "$name-source.tar.gz" "bonfire_${VERSION}-1_amd64.deb"
                  "bonfire-${VERSION}-1.fc43.x86_64.rpm" "bonfire-bin-${VERSION}-1-x86_64.pkg.tar.zst" "Bonfire-$VERSION-x86_64.AppImage")
    (cd "$DIST" && sha256sum "${assets[@]}" > SHA256SUMS)
    gh release create "$TAG" -R "$REPO" --target "$(git rev-parse HEAD)" --title "Bonfire $VERSION" --prerelease \
        --notes "Bonfire $VERSION. Install: see the README. Source: $name-source.tar.gz (and the tag). Verify downloads with SHA256SUMS." \
        "${assets[@]/#/$DIST/}" "$DIST/SHA256SUMS"
    echo "Published $TAG as a pre-release. Test it, then: packaging/release.sh promote"
}

promote() {
    release_exists || die "there is no release $TAG to promote"
    gh release edit "$TAG" -R "$REPO" --prerelease=false --latest
    echo "Promoted $TAG to the latest release."
}

case "${1:-}" in
    prepare) prepare ;;
    finalize) finalize ;;
    promote) promote ;;
    *) die "usage: packaging/release.sh prepare|finalize|promote" ;;
esac
