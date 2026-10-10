# Installing Bonfire

Bonfire is distributed as a **self-contained** build: the .NET runtime is included, so you do not
need to install .NET. All packages below are made from the same release archive
(`bonfire-<version>-linux-x64.tar.gz`). They are project-provided packages, not official packages
of the distributions.

## Requirements

- Linux on x86_64 with glibc 2.27 or newer (the AppImage needs glibc 2.34 and libstdc++ from
  GCC 12 or newer (`GLIBCXX_3.4.30`), because of its bundled ICU).
- An X11 session or XWayland.
- A **rootful** Docker Engine (25 or newer, API 1.44–1.56) on the same machine, reachable through
  its Unix socket (`/var/run/docker.sock`, or `DOCKER_HOST=unix:///…`). Rootless Docker, remote
  engines (`tcp://`, `ssh://`) and Docker contexts are not supported.
- Your user must be allowed to use the Docker socket, usually through the `docker` group.

> **Warning:** membership in the `docker` group is equivalent to root access: anyone in it can
> start a container that mounts the host's root filesystem. Add only accounts you trust as
> administrators. Bonfire itself refuses to run as root.

Tested on Debian 12 and 13, Fedora 43 and 44, Arch Linux and Gentoo.

## 1. Set up Docker

| Distribution | Commands |
|---|---|
| Gentoo (OpenRC) | `sudo emerge -av app-containers/docker app-containers/docker-cli`<br>`sudo rc-update add docker default && sudo rc-service docker start` |
| Debian 13 | `sudo apt install docker.io`<br>`sudo systemctl enable --now docker` |
| Debian 12 | Docker's repository — see below |
| Fedora | `sudo dnf install moby-engine`<br>`sudo systemctl enable --now docker` |
| Arch Linux | `sudo pacman -S docker`<br>`sudo systemctl enable --now docker.service` |

Debian 12's own `docker.io` is Docker 20.10 (API 1.41), too old for Bonfire — use Docker's
official repository there:

```sh
sudo apt install ca-certificates curl
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/debian/gpg -o /etc/apt/keyrings/docker.asc
echo "deb [arch=amd64 signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/debian bookworm stable" \
    | sudo tee /etc/apt/sources.list.d/docker.list
sudo apt update && sudo apt install docker-ce docker-ce-cli containerd.io
```

Then add your user to the group and log out and back in:

```sh
sudo usermod -aG docker "$USER"
```

The packages never install, start or configure Docker, and never change groups.

## 2. Install Bonfire

### With the installer script (all distributions)

```sh
curl -fsSLO https://raw.githubusercontent.com/JamJan05/bonfire/main/install.sh
less install.sh          # read it first
sh install.sh
```

or in one line: `curl -fsSL https://raw.githubusercontent.com/JamJan05/bonfire/main/install.sh | sh`.

The script checks the architecture and distribution, downloads the matching package from the
latest release, verifies it against the release's `SHA256SUMS`, shows what it will do and asks
before installing (with `sudo` only for the package manager). On Gentoo it adds the overlay; on
other distributions it offers the AppImage as an untested fallback, installed for your user only
(`sh install.sh --uninstall` removes it). `BONFIRE_VERSION=0.1.0` pins a version. The checksums
come from the same release as the files: they detect damaged downloads, not a compromised release.

### Manually

Download the files from the [releases page](https://github.com/JamJan05/bonfire/releases) and
check them:

```sh
sha256sum -c SHA256SUMS --ignore-missing
```

| Distribution | Command |
|---|---|
| Debian 12 / 13 | `sudo apt install ./bonfire_0.1.0-1_amd64.deb` |
| Fedora 43 / 44 | `sudo dnf install ./bonfire-0.1.0-1.fc43.x86_64.rpm` |
| Arch Linux | `sudo pacman -U bonfire-bin-0.1.0-1-x86_64.pkg.tar.zst` |

On Arch you can also build the package yourself with `packaging/arch/PKGBUILD` and `makepkg -si` as a regular user.

### Gentoo (overlay)

```sh
sudo emerge --ask app-eselect/eselect-repository dev-vcs/git   # if not installed yet
sudo eselect repository add bonfire git https://github.com/JamJan05/bonfire.git
sudo emaint sync -r bonfire
echo 'games-util/bonfire-bin ~amd64' | sudo tee /etc/portage/package.accept_keywords/bonfire
sudo emerge --ask games-util/bonfire-bin::bonfire
```

The ebuild downloads the release archive and verifies it against the overlay's `Manifest`.

### AppImage (any distribution)

```sh
chmod +x Bonfire-0.1.0-x86_64.AppImage
./Bonfire-0.1.0-x86_64.AppImage
```

The AppImage includes ICU (built on Debian 12, so it needs glibc ≥ 2.34 and libstdc++ from
GCC ≥ 12 (`GLIBCXX_3.4.30`) — every supported distribution has them). The host must provide
OpenSSL 3, CA certificates, fontconfig and the X11 libraries (libX11, libXext, libXfixes, libXi, libXrandr, libXcursor, libICE, libSM), so that
security updates of those come from your distribution. Without FUSE, add
`--appimage-extract-and-run`.

## 3. Start

Run **Bonfire** from the application menu, or `bonfire` in a terminal. `bonfire --version`
prints the version.

Optional, for a better experience: GPU drivers with libGL/EGL (otherwise rendering is done in
software), and `xdg-desktop-portal` or GTK 3 for the file chooser used by "Add local file…".

## Uninstalling

Remove the package with your package manager (or delete the AppImage). Your servers, backups,
settings and logs stay in your home directory:

| What | Location |
|---|---|
| Settings | `~/.config/bonfire/` |
| Servers, worlds, backups | `~/.local/share/bonfire/` |
| Logs | `~/.local/state/bonfire/` |

Stop and remove the servers in Bonfire first if you also want their Docker containers gone; delete
those directories only if you no longer need the worlds.

## Building from source

The source is this repository (each release also carries `bonfire-<version>-source.tar.gz`).
Requirements: the .NET SDK pinned in `global.json` (10.0).

```sh
dotnet build -c Release && dotnet test -c Release
./src/Bonfire.App/bin/Release/net10.0/bonfire
```

A development build needs `DOTNET_ROOT` (on Gentoo `env-update` sets it). The release packages are
built with `packaging/build-all.sh`, described in [PACKAGING.md](PACKAGING.md).
