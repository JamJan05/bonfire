#!/usr/bin/env bash
# Builds dist/Bonfire-<version>-x86_64.AppImage from the release tarball in a pinned Debian 12
# container (the oldest supported glibc). appimagetool and the AppImage runtime are pinned by
# version and verified by SHA-256 before use.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
source "$ROOT/packaging/images.env"
TARBALL=$(ls "$ROOT"/dist/bonfire-*-linux-x64.tar.gz)
VERSION=$(basename "$TARBALL" | sed 's/bonfire-\(.*\)-linux-x64.tar.gz/\1/')
EPOCH=${SOURCE_DATE_EPOCH:-$(git -C "$ROOT" log -1 --format=%ct -- . ':(exclude)games-util/*/Manifest')}
CACHE="$ROOT/dist/.tools"
mkdir -p "$CACHE"

fetch() { # <url> <sha256> <file>
    if [[ ! -f "$CACHE/$3" ]] || ! echo "$2  $CACHE/$3" | sha256sum -c --quiet; then
        curl -fsSL -o "$CACHE/$3" "$1"
    fi
    echo "$2  $CACHE/$3" | sha256sum -c --quiet || { echo "checksum mismatch: $3" >&2; exit 1; }
}
fetch "$APPIMAGETOOL_URL" "$APPIMAGETOOL_SHA256" appimagetool.AppImage
fetch "$APPIMAGE_RUNTIME_URL" "$APPIMAGE_RUNTIME_SHA256" runtime-x86_64

docker run --rm -i -v "$ROOT/dist:/dist" -v "$ROOT/packaging/appimage:/recipe:ro" \
    -e VERSION="$VERSION" -e SOURCE_DATE_EPOCH="$EPOCH" -e HOST_UID="$(id -u)" \
    "$DEBIAN12" bash -euo pipefail -s <<'SCRIPT'
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq && apt-get install -y -qq --no-install-recommends libicu72 file >/dev/null
work=$(mktemp -d); app=$work/Bonfire.AppDir
tar -xzf /dist/bonfire-$VERSION-linux-x64.tar.gz -C "$work"
src=$work/bonfire-$VERSION
mkdir -p "$app/usr/lib/icu"
cp -a "$src/lib" "$app/usr/lib/bonfire"
cp -a "$src/share" "$app/usr/share"
for lib in libicuuc libicui18n libicudata; do cp -L /usr/lib/x86_64-linux-gnu/$lib.so.72 "$app/usr/lib/icu/"; done
install -m755 /recipe/AppRun "$app/AppRun"
cp "$src/share/applications/io.github.jamjan05.Bonfire.desktop" "$app/"
cp "$src/share/icons/hicolor/256x256/apps/io.github.jamjan05.Bonfire.png" "$app/io.github.jamjan05.Bonfire.png"
ln -s io.github.jamjan05.Bonfire.png "$app/.DirIcon"
find "$app" -exec touch -h -d "@$SOURCE_DATE_EPOCH" {} +
out=/dist/Bonfire-$VERSION-x86_64.AppImage
cp /dist/.tools/appimagetool.AppImage "$work/appimagetool" && chmod +x "$work/appimagetool"
ARCH=x86_64 "$work/appimagetool" --appimage-extract-and-run --no-appstream --runtime-file /dist/.tools/runtime-x86_64 "$app" "$out" >/dev/null 2>&1 \
    || ARCH=x86_64 "$work/appimagetool" --appimage-extract-and-run --no-appstream --runtime-file /dist/.tools/runtime-x86_64 "$app" "$out"
chown "$HOST_UID" "$out"
SCRIPT
(cd "$ROOT/dist" && sha256sum "Bonfire-$VERSION-x86_64.AppImage")
