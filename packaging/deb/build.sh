#!/usr/bin/env bash
# Builds dist/bonfire_<version>-<release>_amd64.deb from the release tarball (in a pinned
# Debian 12 container: the oldest supported release). RELEASE defaults to 1.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
source "$ROOT/packaging/images.env"
TARBALL=$(ls "$ROOT"/dist/bonfire-*-linux-x64.tar.gz)
VERSION=$(basename "$TARBALL" | sed 's/bonfire-\(.*\)-linux-x64.tar.gz/\1/')
RELEASE=${RELEASE:-1}
EPOCH=${SOURCE_DATE_EPOCH:-$(git -C "$ROOT" log -1 --format=%ct -- . ':(exclude)games-util/*/Manifest')}

docker run --rm -i -v "$ROOT/dist:/dist" -v "$ROOT/packaging/deb:/recipe:ro" \
    -e VERSION="$VERSION" -e RELEASE="$RELEASE" -e SOURCE_DATE_EPOCH="$EPOCH" -e HOST_UID="$(id -u)" \
    "$DEBIAN12" bash -euo pipefail -s <<'SCRIPT'
work=$(mktemp -d); pkg=$work/pkg
tar -xzf /dist/bonfire-$VERSION-linux-x64.tar.gz -C "$work"
src=$work/bonfire-$VERSION
mkdir -p "$pkg/usr/lib" "$pkg/usr/bin" "$pkg/DEBIAN"
cp -a "$src/lib" "$pkg/usr/lib/bonfire"
ln -s ../lib/bonfire/bonfire "$pkg/usr/bin/bonfire"
cp -a "$src/share" "$pkg/usr/share"
# Debian keeps the license summary in copyright; the full texts stay next to it.
{
    echo "Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/"
    echo "Upstream-Name: Bonfire"
    echo "Source: https://github.com/JamJan05/bonfire"
    echo
    echo "Files: *"
    echo "Copyright: JamJan05"
    echo "License: GPL-3+"
    echo " On Debian systems the full text is in /usr/share/common-licenses/GPL-3."
    echo
    echo "Files: usr/lib/bonfire/*"
    echo "Copyright: see /usr/share/doc/bonfire/THIRD-PARTY-NOTICES.txt"
    echo "License: MIT and Apache-2.0 and OFL-1.1"
    echo " Bundled third-party components; their license texts are in"
    echo " /usr/share/doc/bonfire/THIRD-PARTY-NOTICES.txt."
} > "$pkg/usr/share/doc/bonfire/copyright"
size=$(du -sk --exclude=DEBIAN "$pkg" | cut -f1)
sed -e "s/@VERSION@/$VERSION/" -e "s/@RELEASE@/$RELEASE/" -e "s/@SIZE@/$size/" /recipe/control.in > "$pkg/DEBIAN/control"
(cd "$pkg" && find usr -type f -print0 | sort -z | xargs -0 md5sum > DEBIAN/md5sums)
find "$pkg" -exec touch -h -d "@$SOURCE_DATE_EPOCH" {} +
out=/dist/bonfire_${VERSION}-${RELEASE}_amd64.deb
dpkg-deb --root-owner-group -Zxz --build "$pkg" "$out"
chown "$HOST_UID" "$out"
dpkg-deb --info "$out" | sed -n '1,12p'
SCRIPT
