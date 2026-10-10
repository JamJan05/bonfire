#!/usr/bin/env bash
# Builds the single release payload all packages are made from:
#   dist/bonfire-<version>-linux-x64.tar.gz   (self-contained, no .NET needed on the target)
#   dist/bonfire-<version>-source.tar.gz      (the matching source, for the GPL)
#   dist/SHA256SUMS
# Reproducible: locked restore, deterministic build, normalized archives (SOURCE_DATE_EPOCH =
# time of the last commit that changed anything but the Gentoo Manifest, unless set). Requires the .NET SDK pinned in global.json and a clean git tree.
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
cd "$ROOT"
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
RID=linux-x64
NAME="bonfire-$VERSION"
export SOURCE_DATE_EPOCH=${SOURCE_DATE_EPOCH:-$(git log -1 --format=%ct -- . ':(exclude)games-util/*/Manifest')}
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

if [[ -n "$(git status --porcelain)" && "${ALLOW_DIRTY:-0}" != 1 ]]; then
    echo "The working tree has uncommitted changes; commit them (or set ALLOW_DIRTY=1 for a test build)." >&2
    exit 1
fi

DIST="$ROOT/dist"
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
rm -rf "$DIST"
mkdir -p "$DIST" "$STAGE/$NAME"

dotnet restore src/Bonfire.App/Bonfire.App.csproj --locked-mode
dotnet publish src/Bonfire.App/Bonfire.App.csproj -c Release -r "$RID" --self-contained true --no-restore \
    -p:ContinuousIntegrationBuild=true -p:DebugType=none -p:PublishTrimmed=false -p:PublishSingleFile=false \
    -o "$STAGE/$NAME/lib"

P="$STAGE/$NAME"
# LTTng tracing support is optional and would drag in liblttng-ust as a hard dependency.
rm -f "$P/lib/libcoreclrtraceptprovider.so"
install -Dm644 packaging/linux/io.github.jamjan05.Bonfire.desktop "$P/share/applications/io.github.jamjan05.Bonfire.desktop"
install -Dm644 packaging/linux/io.github.jamjan05.Bonfire.metainfo.xml "$P/share/metainfo/io.github.jamjan05.Bonfire.metainfo.xml"
install -Dm644 packaging/icons/bonfire.svg "$P/share/icons/hicolor/scalable/apps/io.github.jamjan05.Bonfire.svg"
for size in 16 32 48 64 128 256 512; do
    install -Dm644 "packaging/icons/bonfire-$size.png" "$P/share/icons/hicolor/${size}x${size}/apps/io.github.jamjan05.Bonfire.png"
done
install -Dm644 LICENSE "$P/share/doc/bonfire/LICENSE"
install -Dm644 README.md "$P/share/doc/bonfire/README.md"
python3 tools/gen-notices.py "$P/share/doc/bonfire/THIRD-PARTY-NOTICES.txt" "$P/lib/bonfire.deps.json"

# Normalized permissions: directories 755, executables 755, everything else 644.
find "$P" -type d -exec chmod 755 {} +
find "$P" -type f -exec chmod 644 {} +
chmod 755 "$P/lib/bonfire" "$P/lib/createdump" 2>/dev/null || chmod 755 "$P/lib/bonfire"

reproducible_tar() { # <archive> <dir-in-cwd>
    tar --sort=name --mtime="@$SOURCE_DATE_EPOCH" --owner=0 --group=0 --numeric-owner \
        --pax-option=exthdr.name=%d/PaxHeaders/%f,delete=atime,delete=ctime -cf - "$2" | gzip -9n > "$1"
}
(cd "$STAGE" && reproducible_tar "$DIST/$NAME-$RID.tar.gz" "$NAME")
git archive --format=tar --prefix="$NAME-source/" HEAD | gzip -9n > "$DIST/$NAME-source.tar.gz"
(cd "$DIST" && sha256sum ./*.tar.gz > SHA256SUMS)

echo "Built $VERSION ($(du -sh "$P/lib" | cut -f1) payload):"
cat "$DIST/SHA256SUMS"
