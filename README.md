# Bonfire

<img src="packaging/icons/bonfire-128.png" alt="" width="96" align="right">

A native Linux desktop application (C#, .NET 10, Avalonia) for running Minecraft Java servers in
hardened Docker containers. No browser, no web panel. The interface is available in English
(default) and Polish.

## Features

- **Servers:** Vanilla, Fabric and Paper; versions from Mojang, Fabric and Paper; start, stop,
  restart, force stop; each server confined in a container that is checked against a security
  profile before every start.
- **Logs, console, players** — live log, RCON console, who is online.
- **Settings** — MOTD, game mode, difficulty, whitelist, restart policy and more.
- **Backups** — verified archives including add-on records; restore with a rollback copy.
- **Mods and plugins from Modrinth** — install plans with dependencies, updates, pinning,
  disable/enable, local `.jar` files, history with undo. Files are identified only by checksum.
- **Recovery** — every multi-step change is journaled; *Repair* finishes or rolls it back.

## Install

You need Linux on x86_64 and a **rootful Docker Engine** (25 or newer) on the same machine, with
your user allowed to use its socket. Membership in the `docker` group is equivalent to root
access — see [SECURITY.md](SECURITY.md). Bonfire never installs or configures Docker itself.

```sh
curl -fsSLO https://raw.githubusercontent.com/JamJan05/bonfire/main/install.sh
less install.sh          # read it first
sh install.sh
```

One line: `curl -fsSL https://raw.githubusercontent.com/JamJan05/bonfire/main/install.sh | sh`

| Distribution | Package |
|---|---|
| Debian 12 / 13 | `.deb` from the [releases](https://github.com/JamJan05/bonfire/releases) |
| Fedora 43 / 44 | `.rpm` |
| Arch Linux | `bonfire-bin` `.pkg.tar.zst` |
| Gentoo | this repository is an overlay: `eselect repository add bonfire git https://github.com/JamJan05/bonfire.git`, then `emerge games-util/bonfire-bin::bonfire` |
| Others | `Bonfire-<version>-x86_64.AppImage` (untested fallback) |

Docker setup per distribution, manual installation, verifying downloads and uninstalling:
[docs/INSTALL.md](docs/INSTALL.md).

## Documentation

- [docs/INSTALL.md](docs/INSTALL.md) — requirements, Docker setup, packages, building from source
- [docs/USER-GUIDE.md](docs/USER-GUIDE.md) — servers, backups, add-ons, recovery, troubleshooting
- [SECURITY.md](SECURITY.md) — threat model, how servers are confined, reporting problems
- [docs/PACKAGING.md](docs/PACKAGING.md) — release builds, packages and their tests
- [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) — architecture, design decisions, verification

## Development

Requirements: the .NET SDK pinned in `global.json` (10.0).

```sh
dotnet build -c Release
dotnet test -c Release                               # no Docker or network needed
BONFIRE_DOCKER_TESTS=1 dotnet test -c Release        # + read-only checks against the local daemon
BONFIRE_DOCKER_LIFECYCLE_TESTS=1 dotnet test -c Release   # + real servers (downloads, EULA)
./src/Bonfire.App/bin/Release/net10.0/bonfire
```

A development build needs `DOTNET_ROOT` (on Gentoo: `sudo env-update && source /etc/profile`).
UI texts for both languages live in `tools/gen-strings.py`.

## License

GPL-3.0-or-later — see [LICENSE](LICENSE). Bundled third-party components are listed with their
licenses in `THIRD-PARTY-NOTICES.txt` inside every package.
