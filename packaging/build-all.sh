#!/usr/bin/env bash
# Full release build: payload, then every package format (releases 1 and, with TEST=1, 2 for the
# upgrade tests), then the package tests. Output in dist/.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
"$ROOT/packaging/publish.sh"
for release in 1 ${TEST:+2}; do
    RELEASE=$release "$ROOT/packaging/deb/build.sh"
    RELEASE=$release "$ROOT/packaging/rpm/build.sh"
    RELEASE=$release "$ROOT/packaging/arch/build.sh"
done
"$ROOT/packaging/appimage/build.sh"
if [[ -n "${TEST:-}" ]]; then
    "$ROOT/packaging/test/test-packages.sh"
fi
(cd "$ROOT/dist" && sha256sum ./*.tar.gz ./*.deb ./*.rpm ./*.pkg.tar.zst ./*.AppImage > SHA256SUMS)
cat "$ROOT/dist/SHA256SUMS"
