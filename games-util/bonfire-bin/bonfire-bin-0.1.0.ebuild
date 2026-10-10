# Copyright 2026 JamJan05
# Distributed under the terms of the GNU General Public License v3 or later

EAPI=8

inherit desktop xdg

DESCRIPTION="Run Minecraft servers safely in Docker"
HOMEPAGE="https://github.com/JamJan05/bonfire"
SRC_URI="https://github.com/JamJan05/bonfire/releases/download/v${PV}/bonfire-${PV}-linux-x64.tar.gz"
S="${WORKDIR}/bonfire-${PV}"

LICENSE="GPL-3+ MIT Apache-2.0 OFL-1.1"
SLOT="0"
KEYWORDS="~amd64"
RESTRICT="strip"

# Loaded at run time (dlopen) by .NET and Avalonia.
RDEPEND="
	app-misc/ca-certificates
	dev-libs/icu
	>=dev-libs/openssl-3:0
	media-libs/fontconfig
	x11-libs/libICE
	x11-libs/libSM
	x11-libs/libX11
	x11-libs/libXcursor
	x11-libs/libXext
	x11-libs/libXfixes
	x11-libs/libXi
	x11-libs/libXrandr
"

QA_PREBUILT="opt/bonfire/*"

src_install() {
	insinto /opt/bonfire
	doins -r lib/.
	fperms 0755 /opt/bonfire/bonfire /opt/bonfire/createdump
	dosym -r /opt/bonfire/bonfire /usr/bin/bonfire

	domenu share/applications/io.github.jamjan05.Bonfire.desktop
	insinto /usr/share/metainfo
	doins share/metainfo/io.github.jamjan05.Bonfire.metainfo.xml
	local size
	for size in 16 32 48 64 128 256 512; do
		doicon -s ${size} share/icons/hicolor/${size}x${size}/apps/io.github.jamjan05.Bonfire.png
	done
	doicon -s scalable share/icons/hicolor/scalable/apps/io.github.jamjan05.Bonfire.svg
	dodoc share/doc/bonfire/README.md share/doc/bonfire/THIRD-PARTY-NOTICES.txt
}

pkg_postinst() {
	xdg_pkg_postinst
	elog "Bonfire needs a rootful Docker daemon (app-containers/docker) on this machine."
	elog "Your user must be allowed to use its socket; membership in the \"docker\" group"
	elog "is equivalent to root access. Rootless Docker is not supported."
}
