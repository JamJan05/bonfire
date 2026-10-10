#!/bin/sh
# Bonfire installer — https://github.com/JamJan05/bonfire
#
# Recommended (inspect before running):
#   curl -fsSLO https://raw.githubusercontent.com/JamJan05/bonfire/main/install.sh
#   less install.sh && sh install.sh
# One line:
#   curl -fsSL https://raw.githubusercontent.com/JamJan05/bonfire/main/install.sh | sh
#
# Installs the package for your distribution (Debian 12/13, Fedora 43/44, Arch Linux), adds the
# Gentoo overlay on Gentoo, or — on anything else — installs the AppImage into your home directory
# as an untested fallback. Every download is checked against the release's SHA256SUMS before use.
#
# Environment:
#   BONFIRE_VERSION=0.1.0   install this version instead of the latest release ("v0.1.0" also works)
#   BONFIRE_YES=1           do not ask for confirmation (needed when there is no terminal)
#   BONFIRE_APPIMAGE=1      install the AppImage even on a supported distribution
# Options:
#   --uninstall             remove an AppImage installation made by this script
#
# This script never installs, starts or configures Docker and never changes groups.
# The whole script is one function called on the last line, so a truncated download does nothing.

main() {
    set -eu
    REPO="JamJan05/bonfire"
    CURL="curl --proto =https --proto-redir =https --tlsv1.2 -fsSL --retry 3 --connect-timeout 15"
    APP_ROOT="${XDG_DATA_HOME:-$HOME/.local/share}/bonfire-appimage"
    BIN_LINK="$HOME/.local/bin/bonfire"
    DESKTOP="${XDG_DATA_HOME:-$HOME/.local/share}/applications/io.github.jamjan05.Bonfire.desktop"
    ICON="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps/io.github.jamjan05.Bonfire.png"

    say() { printf '%s\n' "$*"; }
    die() { printf 'bonfire-install: %s\n' "$*" >&2; exit 1; }

    [ "$(id -u)" -ne 0 ] || die "run this as your regular user, not as root (sudo is used only for the package manager)"

    if [ "${1:-}" = "--uninstall" ]; then
        uninstall_appimage
        return
    fi

    [ "$(uname -s)" = Linux ] || die "Bonfire runs on Linux only"
    [ "$(uname -m)" = x86_64 ] || die "unsupported architecture $(uname -m): only x86_64 is available"
    getconf GNU_LIBC_VERSION >/dev/null 2>&1 || die "a glibc-based distribution is required (musl is not supported)"
    command -v curl >/dev/null || die "curl is required"
    command -v sha256sum >/dev/null || die "sha256sum is required"

    resolve_version
    choose_method

    WORK=$(mktemp -d)
    trap 'rm -rf "$WORK"' EXIT INT TERM

    case "$METHOD" in
        gentoo) install_gentoo ;;
        appimage) install_appimage ;;
        *) install_package ;;
    esac
}

resolve_version() {
    if [ -n "${BONFIRE_VERSION:-}" ]; then
        VERSION=${BONFIRE_VERSION#v}
    else
        VERSION=$($CURL "https://api.github.com/repos/$REPO/releases/latest" \
            | sed -n 's/^[[:space:]]*"tag_name":[[:space:]]*"v\{0,1\}\([^"]*\)".*/\1/p' | head -n 1)
    fi
    printf '%s' "$VERSION" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$' || die "invalid or unknown version '${VERSION:-}'"
    BASE="https://github.com/$REPO/releases/download/v$VERSION"
}

choose_method() {
    ID=""; VERSION_ID=""
    if [ -r /etc/os-release ]; then
        ID=$(. /etc/os-release && printf '%s' "${ID:-}")
        VERSION_ID=$(. /etc/os-release && printf '%s' "${VERSION_ID:-}")
    fi
    METHOD=appimage
    if [ "${BONFIRE_APPIMAGE:-0}" != 1 ]; then
        case "$ID:$VERSION_ID" in
            debian:12|debian:13) METHOD=deb; FILE="bonfire_${VERSION}-1_amd64.deb" ;;
            fedora:43|fedora:44) METHOD=rpm; FILE="bonfire-${VERSION}-1.fc43.x86_64.rpm" ;;
            arch:*) METHOD=arch; FILE="bonfire-bin-${VERSION}-1-x86_64.pkg.tar.zst" ;;
            gentoo:*) METHOD=gentoo; FILE="" ;;
        esac
    fi
    [ "$METHOD" != appimage ] || FILE="Bonfire-${VERSION}-x86_64.AppImage"
}

confirm() { # <question>
    [ "${BONFIRE_YES:-0}" = 1 ] && return 0
    if ! { true </dev/tty; } 2>/dev/null; then
        die "no terminal to ask for confirmation; download the script and run it, or set BONFIRE_YES=1"
    fi
    printf '%s [y/N] ' "$1" >/dev/tty
    read -r answer </dev/tty || answer=""
    case "$answer" in y|Y|yes|YES) return 0 ;; *) die "cancelled" ;; esac
}

need_sudo() {
    command -v sudo >/dev/null || die "sudo is not available; install manually: $*"
}

fetch_verified() { # <file> → $WORK/<file>
    say "Downloading $1 …"
    $CURL --max-time 1800 -o "$WORK/$1" "$BASE/$1"
    $CURL --max-time 60 -o "$WORK/SHA256SUMS" "$BASE/SHA256SUMS"
    # Exactly one well-formed entry for exactly this file — nothing else in the list is trusted.
    # (no regex intervals: Debian 12's mawk does not support them)
    expected=$(awk -v f="$1" '($2 == f || $2 == "./" f) && length($1) == 64 && $1 ~ /^[0-9a-f]+$/ { print $1; n++ } END { if (n != 1) exit 1 }' "$WORK/SHA256SUMS") \
        || die "SHA256SUMS has no single valid entry for $1"
    actual=$(sha256sum "$WORK/$1" | cut -d' ' -f1)
    [ "$actual" = "$expected" ] || die "checksum mismatch for $1 — not installing"
    say "Checksum OK ($expected)"
}

install_package() {
    case "$METHOD" in
        deb) CMD="sudo apt-get install -y $WORK/$FILE" ;;
        rpm) CMD="sudo dnf install -y $WORK/$FILE" ;;
        arch) CMD="sudo pacman -U --noconfirm $WORK/$FILE" ;;
    esac
    say "Bonfire $VERSION for $ID $VERSION_ID: $FILE"
    say "It will be installed with: $CMD"
    confirm "Download, verify and install?"
    need_sudo "$CMD"
    fetch_verified "$FILE"
    # shellcheck disable=SC2086
    $CMD
    finish "Remove it later with your package manager (package \"$( [ "$METHOD" = arch ] && echo bonfire-bin || echo bonfire )\")."
}

install_gentoo() {
    URL="https://github.com/$REPO.git"
    say "Gentoo: Bonfire comes from the \"bonfire\" overlay ($URL) as games-util/bonfire-bin."
    if ! command -v eselect >/dev/null || ! eselect repository list >/dev/null 2>&1; then
        die "eselect-repository is needed: sudo emerge --ask app-eselect/eselect-repository dev-vcs/git"
    fi
    existing=$(eselect repository list -i 2>/dev/null | awk '$2 == "bonfire" { print }')
    if [ -n "$existing" ]; then
        printf '%s' "$existing" | grep -q "$URL" || die "a different repository named \"bonfire\" is already configured: $existing"
        say "The overlay is already added."
    else
        confirm "Add the overlay with: sudo eselect repository add bonfire git $URL ?"
        need_sudo "eselect repository add bonfire git $URL"
        sudo eselect repository add bonfire git "$URL"
    fi
    need_sudo "emaint sync -r bonfire"
    sudo emaint sync -r bonfire
    say ""
    say "The ebuild is keyworded ~amd64. If your system uses stable keywords, accept it first:"
    say "  echo 'games-util/bonfire-bin ~amd64' | sudo tee /etc/portage/package.accept_keywords/bonfire"
    ATOM="=games-util/bonfire-bin-$VERSION::bonfire"
    [ -f "$(portageq get_repo_path / bonfire 2>/dev/null)/games-util/bonfire-bin/bonfire-bin-$VERSION.ebuild" ] \
        || die "the overlay has no ebuild for version $VERSION"
    confirm "Run: sudo emerge --ask $ATOM ?"
    if [ "${BONFIRE_YES:-0}" = 1 ]; then
        sudo emerge "$ATOM"
    else
        sudo emerge --ask "$ATOM" </dev/tty
    fi
    finish "Remove it later with: sudo emerge --deselect games-util/bonfire-bin && sudo emerge --depclean"
}

install_appimage() {
    say "Your distribution (${ID:-unknown} ${VERSION_ID:-}) has no tested Bonfire package."
    say "Fallback: the AppImage, installed for your user only (UNTESTED on this distribution):"
    say "  $APP_ROOT/$VERSION/$FILE, launcher $BIN_LINK, menu entry and icon."
    say "It needs glibc >= 2.34, libstdc++ from GCC >= 12, OpenSSL 3, CA certificates, fontconfig"
    say "and the X11 libraries (libX11, libXext, libXfixes, libXi, libXrandr, libXcursor, libICE, libSM)."
    if { [ -e "$BIN_LINK" ] || [ -L "$BIN_LINK" ]; } && ! ours "$BIN_LINK"; then
        die "$BIN_LINK exists and was not created by this installer; not overwriting it"
    fi
    confirm "Download, verify and install the AppImage?"
    fetch_verified "$FILE"
    mkdir -p "$APP_ROOT/$VERSION" "$(dirname "$BIN_LINK")" "$(dirname "$DESKTOP")" "$(dirname "$ICON")"
    install -m 755 "$WORK/$FILE" "$APP_ROOT/$VERSION/$FILE.tmp"
    mv -f "$APP_ROOT/$VERSION/$FILE.tmp" "$APP_ROOT/$VERSION/$FILE"
    (cd "$WORK" && "$APP_ROOT/$VERSION/$FILE" --appimage-extract io.github.jamjan05.Bonfire.png >/dev/null 2>&1) \
        && install -m 644 "$WORK/squashfs-root/io.github.jamjan05.Bonfire.png" "$ICON" || true
    ln -sfn "$APP_ROOT/$VERSION/$FILE" "$BIN_LINK.tmp" && mv -fT "$BIN_LINK.tmp" "$BIN_LINK"
    cat > "$DESKTOP" <<EOF
[Desktop Entry]
Type=Application
Name=Bonfire
Comment=Run Minecraft servers safely in Docker
Exec=$BIN_LINK
Icon=io.github.jamjan05.Bonfire
Terminal=false
Categories=Game;
EOF
    # Older versions are removed only after the new launcher is in place.
    for old in "$APP_ROOT"/*; do
        [ "$old" = "$APP_ROOT/$VERSION" ] || rm -rf "$old"
    done
    finish "Remove it later with: sh install.sh --uninstall  (your servers and backups are kept)"
}

ours() { # <path>: a symlink into our own AppImage directory (literal prefix, no regex)
    [ -L "$1" ] || return 1
    case "$(readlink "$1")" in "$APP_ROOT"/*) return 0 ;; *) return 1 ;; esac
}

uninstall_appimage() {
    removed=0
    if ours "$BIN_LINK"; then rm -f "$BIN_LINK"; removed=1; fi
    [ -d "$APP_ROOT" ] && rm -rf "$APP_ROOT" && removed=1
    [ -f "$DESKTOP" ] && grep -q "^Exec=$BIN_LINK" "$DESKTOP" && rm -f "$DESKTOP"
    rm -f "$ICON"
    if [ "$removed" = 1 ]; then
        say "Removed the Bonfire AppImage. Servers, backups and settings in $HOME/.local/share/bonfire,"
        say "$HOME/.config/bonfire and $HOME/.local/state/bonfire were kept."
    else
        say "No AppImage installation by this script was found (packages are removed with the package manager)."
    fi
}

finish() {
    say ""
    say "Bonfire $VERSION is installed. Start it from the application menu or with: bonfire"
    say "It needs a rootful Docker daemon and access to its socket (the \"docker\" group is"
    say "equivalent to root access). Setup: https://github.com/$REPO#install"
    say "$1"
}

main "$@"
