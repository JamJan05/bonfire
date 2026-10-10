# Security

## Threat model in one paragraph

Bonfire runs Minecraft servers — large, network-facing Java programs that also load third-party
mods and plugins — on your desktop. It assumes the **server and its add-ons may be malicious or
compromised** and tries to keep them inside their container and their data folder. It trusts the
local Docker daemon and your own user account. It does not protect against someone who already
controls your account or belongs to the `docker` group.

## Docker access is root access

Bonfire needs a rootful Docker daemon. Whoever can use `/var/run/docker.sock` (for example members
of the `docker` group) can start a privileged container and take over the machine. Add only
accounts you trust as administrators. Bonfire refuses to run as root itself, connects only to a
local Unix socket and refuses rootless, remote (`tcp://`, `ssh://`) and context-based engines, and
blocks creating or starting servers when the daemon's security features (seccomp, rootful mode)
cannot be confirmed.

## How each server is confined

Every container is created from a fixed, versioned profile and **checked against it before every
start** (anything unexpected — capabilities, mounts, devices, namespaces, ports, environment,
entry point, security options — and the server is not started):

- runs as your user ID, never root; all Linux capabilities dropped; `no-new-privileges`; Docker's
  default seccomp profile;
- read-only root filesystem; only the server's own data folder is writable, plus a small `noexec`
  `/tmp`;
- memory limit without swap, CPU limit, at most 512 processes;
- its own bridge network; the game port is published on `127.0.0.1` unless you choose otherwise;
  RCON is never published, its password is a read-only file inside the container;
- the image is pinned to an exact digest; logs are size-limited.

Console commands are passed to `rcon-cli` as a single argument after `--` (never through a shell).
RCON passwords are redacted from displayed logs.

## Downloads and add-ons

- Version metadata (Mojang, Fabric, Paper) and Modrinth are reached through a hardened HTTP client:
  per-provider host and path allowlist, no redirects, public IP addresses only, size, time and JSON
  depth limits.
- Add-on files come only from `cdn.modrinth.com`, are size-capped and verified by SHA-512 before
  use; a file that does not match is deleted.
- Local `.jar` files are copied first and only the copy is inspected; their code is never loaded by
  Bonfire. Unknown files are marked unverified.
- Files written by a server are treated as untrusted: symbolic links are never followed, only
  regular files are read, and directories are checked so a server cannot redirect Bonfire's file
  operations outside its folder.

## Your data

Application folders are created with mode `0700` and files with `0600`. Backups contain
`server.properties`, which includes the RCON password: keep backups private. Bonfire never deletes
worlds on its own; removing a server's container keeps its data.

## Release builds

Release archives are built reproducibly (identical input → identical archive) from locked package
versions and audited for known vulnerabilities at build time. They bundle the .NET runtime: a .NET
security fix therefore requires a new Bonfire release. OpenSSL, CA certificates, fontconfig and X11
always come from your distribution. Third-party licenses: `THIRD-PARTY-NOTICES.txt`.

## Reporting a problem

Please report security problems privately through GitHub's private vulnerability reporting at
<https://github.com/JamJan05/bonfire/security/advisories/new> rather than in a public issue, with
steps to reproduce if possible.
