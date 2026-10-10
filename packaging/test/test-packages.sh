#!/usr/bin/env bash
# Install → run → upgrade → remove tests of every package in clean, pinned distro containers.
# Packages must already be in dist/ for releases 1 and 2 (packaging/build-all.sh does that).
# Only hard dependencies are installed (no recommends / weak deps). The app runs as an
# unprivileged user under Xvfb, without the host Docker socket. Removal must keep user data.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
source "$ROOT/packaging/images.env"
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props")
PASS=()

run() { # <name> <image> <script>
    echo "=== $1"
    docker run --rm -i -v "$ROOT/dist:/dist:ro" -e VERSION="$VERSION" "$2" bash -euo pipefail -s <<<"$3"
    PASS+=("$1")
}

COMMON='
useradd -m tester
check_run() {
    cd /
    bonfire --version
    test -f /usr/share/applications/io.github.jamjan05.Bonfire.desktop
    test -f /usr/share/icons/hicolor/256x256/apps/io.github.jamjan05.Bonfire.png
    runuser -u tester -- env HOME=/home/tester BONFIRE_SMOKE_NETWORK=1 \
        xvfb-run -a -s "-screen 0 1280x800x24" bonfire --smoke-test
    test -d /home/tester/.local/share/bonfire
}
check_removed() {
    ! command -v bonfire
    test ! -e /usr/lib/bonfire && test ! -e /usr/lib64/bonfire
    test -d /home/tester/.local/share/bonfire  # user data is never touched
    echo "removed cleanly, user data kept"
}
'

for target in DEBIAN12 DEBIAN13; do
run "deb on $target" "${!target}" "$COMMON"'
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq --no-install-recommends xvfb xauth >/dev/null
apt-get install -y -qq --no-install-recommends /dist/bonfire_${VERSION}-1_amd64.deb >/dev/null
check_run
apt-get install -y -qq --no-install-recommends /dist/bonfire_${VERSION}-2_amd64.deb >/dev/null
test "$(dpkg-query -W -f="\${Version}" bonfire)" = "${VERSION}-2"
check_run
apt-get remove -y -qq bonfire >/dev/null
check_removed
'
done

for target in FEDORA43 FEDORA44; do
run "rpm on $target" "${!target}" "$COMMON"'
dnf install -y -q --setopt=install_weak_deps=False xorg-x11-server-Xvfb xorg-x11-xauth util-linux >/dev/null 2>&1
dnf install -y -q --setopt=install_weak_deps=False /dist/bonfire-${VERSION}-1.fc43.x86_64.rpm >/dev/null 2>&1
check_run
dnf upgrade -y -q --setopt=install_weak_deps=False /dist/bonfire-${VERSION}-2.fc43.x86_64.rpm >/dev/null 2>&1
rpm -q bonfire | grep -q "^bonfire-${VERSION}-2"
check_run
dnf remove -y -q bonfire >/dev/null 2>&1
check_removed
'
done

run "pkg on ARCH" "$ARCH" "$COMMON"'
pacman -Sy --noconfirm --needed xorg-server-xvfb xorg-xauth >/dev/null 2>&1
pacman -U --noconfirm /dist/bonfire-bin-${VERSION}-1-x86_64.pkg.tar.zst >/dev/null 2>&1
check_run
pacman -U --noconfirm /dist/bonfire-bin-${VERSION}-2-x86_64.pkg.tar.zst >/dev/null 2>&1
pacman -Q bonfire-bin | grep -q "${VERSION}-2"
check_run
pacman -R --noconfirm bonfire-bin >/dev/null 2>&1
check_removed
'

# AppImage: host provides OpenSSL, CA certificates, fontconfig and X11 — but not ICU (bundled).
APPIMAGE_RUN='
cp /dist/Bonfire-${VERSION}-x86_64.AppImage /home/tester/ && chown tester /home/tester/Bonfire-*.AppImage
cd /home/tester
runuser -u tester -- ./Bonfire-${VERSION}-x86_64.AppImage --appimage-extract-and-run --version
runuser -u tester -- env HOME=/home/tester BONFIRE_SMOKE_NETWORK=1 xvfb-run -a -s "-screen 0 1280x800x24" \
    ./Bonfire-${VERSION}-x86_64.AppImage --appimage-extract-and-run --smoke-test
'
run "AppImage on DEBIAN13" "$DEBIAN13" "useradd -m tester"'
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq --no-install-recommends xvfb xauth ca-certificates libssl3t64 libfontconfig1 libx11-6 libxext6 libxfixes3 libxi6 libxrandr2 libxcursor1 libice6 libsm6 >/dev/null
if ls /usr/lib/x86_64-linux-gnu/libicuuc.so.* 2>/dev/null; then echo "ICU present on the host"; else echo "no ICU on the host"; fi
'"$APPIMAGE_RUN"
run "AppImage on FEDORA44" "$FEDORA44" "useradd -m tester"'
dnf install -y -q --setopt=install_weak_deps=False xorg-x11-server-Xvfb xorg-x11-xauth util-linux openssl-libs ca-certificates fontconfig libX11 libXext libXfixes libXi libXrandr libXcursor libICE libSM >/dev/null 2>&1
if ls /usr/lib64/libicuuc.so.* 2>/dev/null; then echo "ICU present on the host"; else echo "no ICU on the host"; fi
'"$APPIMAGE_RUN"

echo
printf 'PASSED: %s\n' "${PASS[@]}"
