#!/usr/bin/env bash
# Builds dist/bonfire-<version>-<release>.fc<N>.x86_64.rpm for Fedora (pinned container).
#   TARGET=FEDORA43|FEDORA44 (default FEDORA43, the oldest supported), RELEASE=1
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
source "$ROOT/packaging/images.env"
TARBALL=$(ls "$ROOT"/dist/bonfire-*-linux-x64.tar.gz)
VERSION=$(basename "$TARBALL" | sed 's/bonfire-\(.*\)-linux-x64.tar.gz/\1/')
TARGET=${TARGET:-FEDORA43}
IMAGE=${!TARGET}
EPOCH=${SOURCE_DATE_EPOCH:-$(git -C "$ROOT" log -1 --format=%ct -- . ':(exclude)games-util/*/Manifest')}

docker run --rm -i -v "$ROOT/dist:/dist" -v "$ROOT/packaging/rpm:/recipe:ro" \
    -e VERSION="$VERSION" -e RELEASE="${RELEASE:-1}" -e SOURCE_DATE_EPOCH="$EPOCH" -e HOST_UID="$(id -u)" \
    "$IMAGE" bash -euo pipefail -s <<'SCRIPT'
dnf install -y -q rpm-build >/dev/null 2>&1
mkdir -p ~/rpmbuild/SOURCES
cp /dist/bonfire-$VERSION-linux-x64.tar.gz ~/rpmbuild/SOURCES/
rpmbuild -bb --quiet /recipe/bonfire.spec \
    --define "bonfire_version $VERSION" --define "bonfire_release $RELEASE" \
    --define "use_source_date_epoch_as_buildtime 1" --define "clamp_mtime_to_source_date_epoch 1"
rpm=$(ls ~/rpmbuild/RPMS/x86_64/*.rpm)
cp "$rpm" /dist/ && chown "$HOST_UID" "/dist/$(basename "$rpm")"
rpm -qp --requires "$rpm" | sort | tr '\n' ' '; echo
SCRIPT
