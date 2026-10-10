#!/usr/bin/env bash
# Runs the release tarball in a clean distro container as an unprivileged user under Xvfb:
# --version, then --smoke-test (main window, every page, Polish resources, HTTPS to Modrinth).
# The host Docker socket is never mounted: "Docker unavailable" must be handled normally.
#   packaging/test/smoke-tarball.sh <image> "<runtime packages>"
set -euo pipefail
IMAGE=$1
PACKAGES=$2
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
TARBALL=$(ls "$ROOT"/dist/bonfire-*-linux-x64.tar.gz)

docker run --rm -i -v "$TARBALL:/payload.tar.gz:ro" -e PACKAGES="$PACKAGES" "$IMAGE" bash -euxo pipefail -s <<'SCRIPT'
if command -v apt-get >/dev/null; then
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -qq
    apt-get install -y -qq --no-install-recommends xvfb xauth $PACKAGES >/dev/null
elif command -v dnf >/dev/null; then
    dnf install -y -q xorg-x11-server-Xvfb xorg-x11-xauth util-linux $PACKAGES >/dev/null
elif command -v pacman >/dev/null; then
    pacman -Sy --noconfirm --needed xorg-server-xvfb xorg-xauth $PACKAGES >/dev/null
fi
mkdir -p /opt && tar -xzf /payload.tar.gz -C /opt
useradd -m tester
/opt/bonfire-*/lib/bonfire --version
cd /  # an arbitrary working directory
runuser -u tester -- env HOME=/home/tester BONFIRE_SMOKE_NETWORK=1 \
    xvfb-run -a -s "-screen 0 1280x800x24" /opt/bonfire-*/lib/bonfire --smoke-test
SCRIPT
