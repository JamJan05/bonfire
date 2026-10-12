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
- its own bridge network; the game port is published on `127.0.0.1` unless you choose otherwise
  (see *Network exposure* below); RCON is never published, its password is a read-only file inside
  the container;
- the image is pinned to an exact digest; logs are size-limited.

## Network exposure

The game port is published in one of three ways, chosen per server:

- **This computer only** (`127.0.0.1`).
- **All network interfaces** (`0.0.0.0`): every IPv4 address of the host. Whether that includes
  the internet depends on your router (port forwarding, carrier-grade NAT), not on Bonfire.
  On this project's Docker 29.8 setup an explicit `0.0.0.0` binding was observed to publish over
  **IPv4 only**; whether a daemon also binds `[::]` for it depends on the daemon (Bonfire tolerates
  that twin binding, so treat the host's IPv6 addresses as possibly reachable). Bonfire shows an
  advisory checklist (online mode, whitelist, backup) when you choose this mode; it never blocks you.
- **One address of this computer**: the port is bound to one host address, typically a VPN
  interface's (Tailscale, WireGuard). Docker's NAT rule matches only traffic addressed to it, so
  connections to the host's LAN or public addresses are refused. This is a *destination-address*
  restriction, not an interface restriction: a packet for that address that reaches the host over
  another interface is still forwarded (Docker documents this for hosts with IP forwarding). Bonfire
  refuses to start the server while the address is not assigned to any interface (VPN down) and
  never falls back to another address; automatic restart policies are not allowed in this mode.
  Starts that Bonfire does not perform (nobody else should start its containers) are not covered.

Two facts about Docker that matter for anything published beyond loopback:

- **Published ports may bypass ordinary host firewall rules.** Docker rewrites the packets in the
  `nat` table before the host's `INPUT` chain is consulted, so a `ufw` or `firewalld` rule that
  closes the port on the host does not close it for the container. Filter published traffic in
  Docker's `DOCKER-USER` chain or rely on the exposure mode above.
- After a start that failed to bind the host address, **Docker 29.8 runs the container with no
  port published at all** (reproduced three times on this project's development host, with the
  configured binding still shown). After every start Bonfire therefore compares what Docker really
  published with what was requested; a mismatch stops the container again and asks you to remove
  and recreate it (world and data stay).

Bonfire never changes the host's firewall, routing, or router (no UPnP), and never contacts a
third-party "port check" service.

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
