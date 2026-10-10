#!/usr/bin/env bash
# Publishes a release of the current main branch to GitHub (JamJan05/bonfire):
#   1. builds every artifact from the clean, pushed tree (TEST=1 also runs the package tests),
#   2. writes the Gentoo Manifest for the new payload and commits it (that commit does not change
#      the payload: SOURCE_DATE_EPOCH ignores Manifest-only commits),
#   3. creates the GitHub release with all artifacts, the matching source archive and SHA256SUMS.
# PRERELEASE=1 (default) publishes a pre-release for testing; PRERELEASE=0 promotes it to latest.
# A published version is immutable: an existing tag is only promoted, never rebuilt or replaced.
set -euo pipefail
PRERELEASE=${PRERELEASE:-1}
[[ "$PRERELEASE" == 0 || "$PRERELEASE" == 1 ]] || { echo "PRERELEASE must be 0 or 1" >&2; exit 1; }
ROOT=$(cd "$(dirname "$0")/.." && pwd)
cd "$ROOT"
REPO=JamJan05/bonfire
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
TAG="v$VERSION"
DIST="$ROOT/dist"

if gh release view "$TAG" -R "$REPO" >/dev/null 2>&1; then
    if [[ "$PRERELEASE" == 0 ]]; then
        gh release edit "$TAG" -R "$REPO" --prerelease=false --latest
        echo "promoted $TAG to latest"
        exit 0
    fi
    echo "$TAG is already published; bump <Version> for a new release" >&2
    exit 1
fi
[[ "$PRERELEASE" == 1 ]] || { echo "no release $TAG to promote; new releases start as pre-releases" >&2; exit 1; }
case "$(git remote get-url origin)" in
    *github.com[:/]$REPO.git|*github.com[:/]$REPO) ;;
    *) echo "origin must be the release repository $REPO" >&2; exit 1 ;;
esac
tags=$(git ls-remote --tags "git@github.com:$REPO.git" "refs/tags/$TAG") || { echo "cannot list tags of $REPO" >&2; exit 1; }
if [[ -n "$tags" ]]; then
    echo "tag $TAG already exists without a release; refusing to reuse it" >&2
    exit 1
fi

[[ "$(git rev-parse --abbrev-ref HEAD)" == main ]] || { echo "release from main only" >&2; exit 1; }
[[ -z "$(git status --porcelain)" ]] || { echo "the working tree is not clean" >&2; exit 1; }
git fetch -q origin main
[[ "$(git rev-parse HEAD)" == "$(git rev-parse origin/main)" ]] || { echo "push main first" >&2; exit 1; }

"$ROOT/packaging/build-all.sh"

# Manifest for the payload that is about to be uploaded.
work=$(mktemp -d); trap 'rm -rf "$work"' EXIT
(cd games-util/bonfire-bin && DISTDIR="$DIST" PORTAGE_TMPDIR="$work" ebuild "bonfire-bin-$VERSION.ebuild" manifest >/dev/null)
if [[ -n "$(git status --porcelain games-util)" ]]; then
    git add games-util
    git commit -q -m "games-util/bonfire-bin: Manifest for $VERSION"
    git push -q origin main
fi

# Proof that the Manifest commit did not change the payload: rebuild and compare.
PAYLOAD="bonfire-$VERSION-linux-x64.tar.gz"
before=$(sha256sum "$DIST/$PAYLOAD" | cut -d' ' -f1)
mkdir -p "$work/keep" && cp -a "$DIST/." "$work/keep/"
"$ROOT/packaging/publish.sh" >/dev/null
after=$(sha256sum "$DIST/$PAYLOAD" | cut -d' ' -f1)
rm -rf "$DIST" && cp -a "$work/keep" "$DIST"
[[ "$before" == "$after" ]] || { echo "the payload changed after the Manifest commit ($before → $after)" >&2; exit 1; }

# The source archive is the tagged commit (code + Manifest); the payload built above is identical
# for it because the build date ignores the Manifest commit.
NAME="bonfire-$VERSION"
git archive --format=tar --prefix="$NAME-source/" HEAD | gzip -9n > "$DIST/$NAME-source.tar.gz"
ASSETS=("$NAME-linux-x64.tar.gz" "$NAME-source.tar.gz" "bonfire_${VERSION}-1_amd64.deb"
        "bonfire-${VERSION}-1.fc43.x86_64.rpm" "bonfire-bin-${VERSION}-1-x86_64.pkg.tar.zst" "Bonfire-$VERSION-x86_64.AppImage")
(cd "$DIST" && sha256sum "${ASSETS[@]}" > SHA256SUMS)

gh release create "$TAG" -R "$REPO" --target "$(git rev-parse HEAD)" --title "Bonfire $VERSION" --prerelease \
    --notes "Bonfire $VERSION. Install: see the README. Source: $NAME-source.tar.gz (and the tag). Verify downloads with SHA256SUMS." \
    "${ASSETS[@]/#/$DIST/}" "$DIST/SHA256SUMS"
echo "published $TAG as a pre-release; test it, then: PRERELEASE=0 packaging/release.sh"
