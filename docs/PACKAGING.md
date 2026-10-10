# Packaging and releases (maintainers)

All packages are made from **one payload**: the self-contained build in
`dist/bonfire-<version>-linux-x64.tar.gz`. Distribution recipes only repackage it.

## Building a release

```sh
packaging/build-all.sh            # payload, .deb, .rpm, Arch package, AppImage, SHA256SUMS
TEST=1 packaging/build-all.sh     # + release 2 of each package and the install tests
```

Needs the .NET SDK from `global.json`, Docker (distribution builds run in containers) and a clean
Git tree (`ALLOW_DIRTY=1` for test builds only). Output in `dist/`.

| Step | Script | Environment |
|---|---|---|
| Payload + source archive + notices | `packaging/publish.sh` | host SDK, locked restore |
| Debian 12/13 `.deb` | `packaging/deb/build.sh` | Debian 12 container, `dpkg-deb` |
| Fedora 43/44 `.rpm` | `packaging/rpm/build.sh` (`TARGET=FEDORA43\|FEDORA44`) | Fedora container, `rpmbuild` |
| Arch `bonfire-bin` | `packaging/arch/build.sh` | Arch container, `makepkg` as a non-root user |
| AppImage | `packaging/appimage/build.sh` | Debian 12 container, pinned `appimagetool` + runtime |
| Gentoo `games-util/bonfire-bin` | the repository root is the overlay | `Manifest` written by `packaging/release.sh` |

`RELEASE=<n>` sets the Debian revision / RPM release / Arch pkgrel.

## Publishing

```sh
TEST=1 packaging/release.sh       # build + test, Manifest commit, GitHub pre-release
PRERELEASE=0 packaging/release.sh # after testing the pre-release: promote it to latest
```

The release is made from a clean, pushed `main`. Published versions are never rebuilt; bump
`<Version>` for every release. `install.sh` (repository root) installs the latest release.

## Reproducibility

- Package versions are locked (`packages.lock.json`, including the `linux-x64` runtime packs) and
  restored in locked mode; the SDK is pinned in `global.json`; builds are deterministic.
- Archives are normalized: sorted entries, `SOURCE_DATE_EPOCH` (time of the last commit that
  changed more than the Gentoo Manifest) as every mtime,
  owner 0, `gzip -n`. Two builds of the same commit produce identical SHA-256 sums.
- Containers are pinned by digest in `packaging/images.env`; AppImage tools by version and SHA-256.

## What the tests check (`packaging/test/test-packages.sh`)

In a clean container of each supported distribution, with **only hard dependencies** installed:
install → `bonfire --version` → `bonfire --smoke-test` as an unprivileged user under Xvfb (main
window shown, every sidebar page rendered, Polish resources, HTTPS to Modrinth through the system
OpenSSL and CA store) → upgrade to release 2 → run again → remove (program gone, user data kept).
The AppImage is tested on systems without ICU. The host Docker socket is never mounted.

`packaging/test/smoke-tarball.sh <image> "<packages>"` runs the payload alone, for probing the
dependencies of a new distribution release.

## Runtime dependencies

Determined empirically (ELF `NEEDED`, the libraries .NET and Avalonia load at run time, and the
tests above): glibc ≥ 2.27, libstdc++, libgcc, ICU, OpenSSL 3, CA certificates, fontconfig,
libX11, libXext, libXfixes, libXi, libXrandr, libXcursor, libICE, libSM. Optional: libGL/EGL (GPU
rendering), D-Bus and xdg-desktop-portal or GTK 3 (file chooser). Docker is only suggested — the
packages never install, start or configure it. The AppImage's bundled ICU 72 (from Debian 12)
raises its own baseline to glibc ≥ 2.34 and GLIBCXX_3.4.30 (GCC 12). The bundled Inter font is the default UI font, so
the app also runs on systems without any fonts.

## Licensing

Bonfire is GPL-3.0-or-later. `tools/gen-notices.py` writes `THIRD-PARTY-NOTICES.txt` from the lock
file and the published `deps.json`: every bundled package with its license text (shipped files, or
the standard text with the copyright declared by the package — generation fails otherwise), plus
the embedded Inter font (OFL-1.1). Each release also ships `bonfire-<version>-source.tar.gz`.

## Updating

- **.NET security release:** update the SDK (`global.json`), restore with `--force-evaluate` to
  refresh the lock files, rebuild and release. The runtime is bundled, so users only get the fix
  through a new Bonfire release.
- **New distribution release:** add its image digest to `images.env`, probe it with
  `smoke-tarball.sh`, add it to `test-packages.sh`, adjust dependency names if needed.
- **Version:** `<Version>` in `Directory.Build.props`, the AppStream `<releases>` entry, and the
  Gentoo ebuild file name (`git mv` the ebuild; its old Manifest line goes away on the next release).
