#!/usr/bin/env bash
# Builds dist/bonfire-bin-<version>-<pkgrel>-x86_64.pkg.tar.zst with makepkg (as a non-root user)
# in a pinned Arch container. RELEASE (pkgrel) defaults to 1.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
source "$ROOT/packaging/images.env"
TARBALL=$(ls "$ROOT"/dist/bonfire-*-linux-x64.tar.gz)
VERSION=$(basename "$TARBALL" | sed 's/bonfire-\(.*\)-linux-x64.tar.gz/\1/')
SHA=$(sha256sum "$TARBALL" | cut -d' ' -f1)
EPOCH=${SOURCE_DATE_EPOCH:-$(git -C "$ROOT" log -1 --format=%ct -- . ':(exclude)games-util/*/Manifest')}

docker run --rm -i -v "$ROOT/dist:/dist" -v "$ROOT/packaging/arch:/recipe:ro" \
    -e VERSION="$VERSION" -e RELEASE="${RELEASE:-1}" -e SHA="$SHA" -e SOURCE_DATE_EPOCH="$EPOCH" -e HOST_UID="$(id -u)" \
    "$ARCH" bash -euo pipefail -s <<'SCRIPT'
pacman -Sy --noconfirm --needed base-devel >/dev/null 2>&1
useradd -m builder
work=/home/builder/pkg; mkdir -p "$work"
sed -e "s/^pkgver=.*/pkgver=$VERSION/" -e "s/^pkgrel=.*/pkgrel=$RELEASE/" -e "s/@SHA256@/$SHA/" /recipe/PKGBUILD > "$work/PKGBUILD"
cp /dist/bonfire-$VERSION-linux-x64.tar.gz "$work/"
chown -R builder "$work"
runuser -u builder -- bash -c "cd $work && PKGEXT=.pkg.tar.zst makepkg --nodeps --noconfirm -f >/dev/null"
pkg=$(ls $work/*.pkg.tar.zst)
cp "$pkg" /dist/ && chown "$HOST_UID" "/dist/$(basename "$pkg")"
pacman -Qip "$pkg" | sed -n '1,3p;/Depends On/p'
SCRIPT
