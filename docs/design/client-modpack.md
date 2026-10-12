# Design: client modpack for Fabric servers

Status: **stage A (network exposure) implemented 2026-10-12; the client modpack itself is still a
proposal.** Written 2026-10-11, reviewed by a second AI reviewer (OpenAI Codex); the agreements
and corrections are noted at the end. Stages: A network exposure (done), B modpack export, C open
in launcher, D server address in the pack, E Prism instance sync (later, separate decision).

## Problem

Many Fabric mods add content the vanilla client does not know (blocks, items, GUIs, packets).
Those mods must be installed on every player's client as well, in the same version. Today each
player collects the files by hand.

The Minecraft protocol cannot push mods: a server may only require a *resource pack*
(`resource-pack` + `require-resource-pack` in `server.properties`). Mods are arbitrary code, and
in-game "mod downloader" mods are both a chicken-and-egg problem (the downloader itself has to be
installed first) and remote code execution by design. Every large modded server therefore
distributes mods through a **launcher**: a modpack file imported once (Prism Launcher, Modrinth
App, MultiMC, ATLauncher), or a self-hosted pack that the launcher synchronises on each start
(packwiz, custom launchers). Servers that need nothing on the client run Paper plugins or
server-side-only mods.

Bonfire already knows everything needed to produce such a pack: the content lock identifies each
mod by SHA-512 and Modrinth project/version, and Modrinth declares for every version on which
side it runs.

## Scope

| Stage | Feature | Decision |
|---|---|---|
| v1 | **Export client modpack** — a `.mrpack` with metadata only | proposed |
| v1 | **Open in launcher** — hand the `.mrpack` to a detected launcher's import | proposed |
| v1 | **Server address in the pack** — optional `servers.dat` override | proposed |
| v1 | **Connection info** and **exposure checklist** — see *Network exposure* | **done** (stage A) |
| v1 | **Port on a specific host address** (VPN on the host) — third `PortExposure` | **done** (stage A) |
| v2 | **Sync a Prism instance** — Bonfire maintains a dedicated client instance | later, Prism only |
| — | Official Mojang launcher, packwiz/HTTP hosting, configs, resource packs, JAR bundles | out of scope |

Fabric servers only. Paper plugins never need a client counterpart; Vanilla has no add-ons.

## v1a: Export client modpack (`.mrpack`)

The pack is a zip containing only `modrinth.index.json`: the list of mods, their hashes, sizes
and Modrinth CDN URLs. The launcher downloads the files itself. Bonfire copies no JARs, so the
pack is a few kilobytes and nothing is redistributed.

### Input

- The server's **verified inventory** (files on disk whose SHA-512 matches the lock), not the lock
  alone — the same check every add-on operation makes.
- `MinecraftVersion` and `FabricLoaderVersion` from the server definition (both already stored;
  the installer/launcher version is not what the pack needs).
- Preconditions: Fabric server, no pending recovery, no add-on change in progress. The server may
  be running: the export only reads.

### Classification: which mods the client needs

The version-level `environment` decides; the project-level `client_side` is the fallback when a
version does not declare one. A recognised version environment is never overridden by a legacy
project value.

| Version environment | In the client pack |
|---|---|
| `client_and_server`, `client_only_server_optional` | included, `env.client: required` |
| `server_only`, `dedicated_server_only`, `singleplayer_only` | excluded (server-side) |
| `client_or_server`, `server_only_client_optional`, `client_or_server_prefers_both` | excluded by default — the server already provides the feature; listed under "optional" |
| `client_only` found among server mods | included, but flagged: it should not be on the server |
| none → `client_side` `required` / `optional` / `unsupported` | included / optional / excluded |
| none → nothing known | **needs review**; the summary lists it, the user decides |
| `Disabled` lock entries | excluded |

"Client-required" describes functionality, not always the ability to join; the summary states
that the export follows declared metadata.

### Planner

A **new, small planner** rather than a reuse of `ContentResolver`: the resolver plans an
*installation into the server inventory* — installed entries stop the traversal and the
environment is only a warning. The export needs the opposite view.

- Roots: the included mods, **frozen** to the installed version and to the file with the same
  SHA-512 (not the version's current primary file).
- Traversal: `Required` dependencies recursively, deduplicated, with the same limits as the
  resolver (projects, API calls). `Optional` and `Embedded` dependencies are ignored in v1.
- A dependency given by project only: first the version already present on the server, otherwise
  the newest compatible release for the client profile (`fabric`, the server's game version).
- Version conflicts, declared `Incompatible` pairs, and a required dependency that is
  `unsupported` on the client **block the export** with a clear message. No backtracking, no
  silent dropping.

### Local and detected files

- `Provider = Local` or a file that Modrinth's hash lookup does not identify cannot be put in an
  `.mrpack` without copying the JAR (licensing, size). They are excluded and listed as *"not
  included — check whether clients need these; share them separately"*. Not every such file is a
  client mod, so the wording must not imply it has to be shared.
- `Origin = Detected` with a Modrinth hash match is an identified mod and is treated normally.
- Download URLs are accepted only from `cdn.modrinth.com` — narrower than the mrpack
  specification's host list, matching the HTTP client's allowlist.

### Output

`modrinth.index.json`, `formatVersion` 1, `game` `minecraft`:

- `name`: editable, default `<server name> — Client` (server names can reveal information, so the
  user sees and may change it).
- `versionId`: a short hash of the canonical pack contents (file list with hashes, Minecraft and
  loader versions); the date is shown separately. A raw lock hash would change with server-only
  state and miss resolved dependencies.
- `dependencies`: `minecraft`, `fabric-loader` (exact versions).
- `files[]`: `path` `mods/<name>.jar` with collision-checked safe names, `hashes` (`sha512`,
  `sha1`) and `fileSize` from the **same** matched artifact, `env`, `downloads` with one validated
  CDN URL (no credentials, query tokens or fragments).
- No JARs in `overrides/`. The only override is the optional `servers.dat` below.

#### Server address in the pack (optional)

The mrpack format allows `client-overrides/servers.dat` (client-only overrides; the review
corrected the earlier `overrides/` — see *Review notes*), the client's multiplayer server list.
With it the server appears in the player's list right after the import — no address to type.
Importing an override does not promise merging with an existing list; the documentation must
say so. Rules:

- **Off by default.** The user switches it on in the preview and chooses the address: for a LAN
  server Bonfire suggests the host's detected IPv4 addresses (see *Network exposure*), for an
  internet server the user enters their own hostname or address; the port is the server's.
- The address is shown in the preview, because a pack sent to friends may travel further.
- Only this one entry is written: name (the pack name), `ip` as `host:port` (an IPv6 literal in
  brackets: `[fd00::1]:25565`; a hostname is validated separately from address literals); nothing else.
- NBT is written by a small write-only encoder: an uncompressed root compound containing a
  `servers` list of compounds with the `name` and `ip` strings, strings in Java's *modified UTF-8*
  (supplementary characters as surrogate pairs, `U+0000` as two bytes — plain UTF-8 is not enough
  for every pack name). Fixture-tested against bytes a real client wrote. Bonfire never reads an
  existing `servers.dat`.
- The address is not part of the `versionId` hash, so changing it does not look like a mod update
  (`versionId` means "this mod set", not "these exact archive bytes").

The manifest is a dedicated type; the server definition, lock or inventory are never serialised
directly. No paths, UUIDs, addresses or diagnostics are included. Written with
`System.IO.Compression` (write only — no untrusted decoder), to a temporary file followed by an
atomic rename; cancellation leaves no half-written pack. File name:
`<safe server name>-client.mrpack`, chosen through the standard file picker.

### UI

**Export client modpack…** next to the installed-list actions in the Add-ons tab, visible for
Fabric servers. A preview dialog before saving:

- included mods with versions; dependencies added, each with its parent,
- excluded: server-side, optional, disabled,
- not included (local/unidentified) and entries that need review,
- Minecraft and Fabric Loader versions, estimated download size,
- **Copy summary** (plain text) — the text form for players who need the list.

The documentation states: the pack contains the declared mod dependencies; joining may still
need configuration or mods whose metadata is incomplete.

## v1b: Open in launcher

Bonfire hands the pack to a launcher through the launcher's **own import path** and never
touches another application's data. The launcher validates, downloads and owns the instance.

| Launcher | Hand-off | Detection |
|---|---|---|
| Prism Launcher | `prismlauncher --import <file>`; Flatpak `org.prismlauncher.PrismLauncher` through `flatpak run --file-forwarding … --import @@ <file> @@` | `.desktop` entry, executable in `PATH`, user and system Flatpak lists |
| MultiMC | `--import <file>` | as above; data directories vary between packagings |
| Modrinth App | `.mrpack` import exists; no verified CLI or deep link → open the file with `xdg-open` and tell the user to import in the app | `~/.local/share/ModrinthApp`, Flatpak `com.modrinth.ModrinthApp` |
| ATLauncher | no known CLI → save the file and show the GUI import hint | Flatpak `com.atlauncher.ATLauncher` |
| Official launcher | out of scope: no modpack import; Fabric needs its own installer (libraries, game directory) | — |

Rules:

- Detected launchers appear as buttons plus **Choose program…** for portable installations.
  Directory existence alone does not count as detection.
- Processes are started with argument arrays, never a shell command string.
- The pack is written to Bonfire's private cache (a Flatpak launcher may not see `/tmp`) and kept
  until a later cleanup: the launcher process may exit immediately after forwarding the request to
  a running instance.
- The result is reported as **"Import opened in <launcher>"**, not "installed".
- After the server's mods change, the offer is **"Import the updated pack as a new instance"**.
  Launchers have no in-place update from a local file; preservation of saves and settings is not
  promised.

The CLI facts above come from the launchers' documentation and were not tested here; the
implementation must verify `--import` against the native and Flatpak Prism builds.

## v2: Sync a dedicated Prism instance (later)

Bonfire creates and maintains one instance per server in Prism
(`instances/<name>/mmc-pack.json`, `minecraft/mods/`), downloading the JARs itself with the
existing hash-verified downloader. Minimal safe contract:

- **Ownership record in Bonfire's storage** (instance identity, safe relative paths, last
  installed SHA-512), plus a marker in the instance. The marker alone never authorises deletion.
- Replace or remove **only recorded files whose current hash still matches**. Modified, missing,
  renamed or disabled managed files raise a review conflict; a mod the user disabled is never
  silently restored.
- The user's extra mods (shaders, minimap…) are preserved. File-name collisions and identifiable
  duplicate project/version conflicts block the sync; unknown extras are reported as unverified.
- Transactions as for server add-ons: stage verified downloads, recheck the baseline, journal with
  rollback copies, publish the record last; symlinks and special files rejected throughout.
- **Prism and the game must be closed.** There is no mutual exclusion with the launcher, so this
  is an explicit, user-triggered action — never a background sync.
- `mmc-pack.json` pins Minecraft and Fabric Loader to the server's versions; Prism provisions
  libraries and the Java runtime. A version change goes through a fresh import, not an edit.

Other launchers stay on the import path.

## Sharing with friends

The same `.mrpack`, through **Export client modpack…**. The friend needs a compatible launcher and
network access; the launcher downloads from Modrinth. A zip with the JARs (`overrides/`) was
rejected: it adds redistribution decisions and size, and does not solve the only real gap —
local mods without metadata — which the copyable summary already covers.

## Network exposure

Bonfire publishes the game port either on `127.0.0.1` (this computer only) or on `0.0.0.0`
(every IPv4 interface). There is **no LAN-versus-internet distinction**: both are the same Docker
port binding, and whether a `0.0.0.0` server is reachable from the internet depends on the router
(port forwarding, carrier-grade NAT), which Bonfire cannot see or change. A separate "LAN" option
would be a label pretending to be a safeguard, so it stays documentation. What is worth adding:

### Connection info panel

In the server details: the host's IPv4 addresses with the port, each with a copy button, so a LAN
player can be told where to connect. Addresses are read from the host's interfaces (link-local,
loopback and Docker bridge addresses filtered out); the panel is the source of the suggestions
for `servers.dat` above. With `0.0.0.0` the panel adds two facts players and operators usually do
not know:

- reaching the server from the internet over IPv4 needs a port forward on the router; behind
  carrier-grade NAT (router WAN address in `100.64.0.0/10`, or another private range when a
  second router sits in front) the user cannot forward a port — a VPN or a relay is the way;
- **ports published by Docker may bypass ordinary host firewall rules**: Docker rewrites the
  packets in the `nat` table before the host's `INPUT` chain is consulted, so a `ufw`/`firewalld`
  rule that closes the port on the host does not close it for the container (filtering published
  traffic belongs in Docker's `DOCKER-USER` chain). SECURITY.md says the same, with the same
  qualification — "does not close the port" is not true as an absolute.

IPv6 (verified 2026-10-12 on Docker 29.8, iptables backend, `userland-proxy` on, `ip6tables`
chains present, no IPv6 on the bridge): an **explicit `0.0.0.0` binding is IPv4 only** — the proxy
listens on `0.0.0.0` alone, `[::1]` is refused and `NetworkSettings.Ports` lists only the IPv4
entry. Only a binding *without* a host address publishes on `0.0.0.0` and `[::]` (then reachable
over `::1` and even the link-local address), which Bonfire never does. The panel therefore lists
the host's IPv6 addresses marked "not published by this binding", and SECURITY.md states the
IPv4-only fact for this daemon version. The post-start check tolerates a daemon that adds the
`[::]` twin of a `0.0.0.0` binding (same port) — "every interface" still holds — and refuses any
other deviation. A server published beyond loopback should expect scanners, so the checklist
below is shown whenever `0.0.0.0` is chosen.

### Private play over the internet: VPN on the host, port bound to its address

The recommended way to play with friends on different networks without exposing anything: an
overlay VPN (Tailscale, ZeroTier, WireGuard) **on the host**, and the game port published only on
the VPN address. No port forwarding, invisible to scanners, encrypted transport; friends install
the VPN client, join the network and connect to the VPN address or name.

Such a network is not an anonymising VPN: every member gets its own private address (e.g.
`100.x.y.z` or `10.8.0.x`) next to its normal one, traffic between members is encrypted, and only
members can reach those addresses. Membership is the guest list. Everything below is free of
charge; the host plays the server, so the only costs are electricity and the home uplink
(roughly 0.1–1 Mbit/s per player).

| Option | Cost | Needs a public address at home? | Notes |
|---|---|---|---|
| Tailscale | free Personal plan: up to 6 users, unlimited user devices (pricing page, checked 2026-10-12); friends join by device sharing or invitation | no — its relays pass CGNAT | fewest steps; depends on a third-party coordination service |
| ZeroTier | free Personal plan: 10 devices, one network, non-commercial (checked 2026-10-12) | no | similar |
| WireGuard, self-hosted | free, no account anywhere | **yes** — one forwarded UDP port (IPv4) or IPv6; free dynamic DNS for a changing home address | the host's own PC is the VPN endpoint; the user generates a key pair and a config file (or QR code) per friend; removing a key removes the guest |
| Headscale | free | the *coordination server* must be publicly reachable — it can run elsewhere (a small VPS), the game host itself needs no public address | a self-hosted coordination server for Tailscale clients; more than a group of friends needs |

The forwarded WireGuard port is not comparable to a forwarded game port: WireGuard answers only
packets signed with a known key, so to a scanner the port looks dead, and it is a small, audited
kernel component rather than a large Java application loading third-party code. Behind
carrier-grade NAT (router address in `100.64.0.0/10`, no port forwarding possible) a self-hosted
endpoint is not an option; Tailscale or a friend with a public address hosting the WireGuard
endpoint are.

Idea for much later, not in scope: an *Invite a friend* action generating a WireGuard peer
config and QR code. Bonfire never runs as root, so it could only generate files; configuring the
host interface stays with the user (NetworkManager and systemd-networkd both support WireGuard).

A VPN **inside the container** is rejected: WireGuard and Tailscale need `NET_ADMIN` and
`/dev/net/tun`, and the hardening profile grants no capabilities and no devices. A userspace
networking Tailscale sidecar sharing the game container's network namespace would need neither,
but it is a separate, larger piece of work — possible later, not here.

Implemented (stage A, 2026-10-12): a third `PortExposure.SpecificAddress` with
`ServerDefinition.HostAddress` (record schema 5).

- The wizard and *Settings → Network* list the host's addresses (System.Net.NetworkInformation,
  no process spawned), VPN-looking ones first: interface names `tailscale*`, `wg*`, `zt*`, `tun*`,
  `nebula*` or an IPv4 in `100.64.0.0/10`. The label is a hint, never security evidence.
  Loopback, link-local, Docker's `docker0`/`br-<12 hex>`/`veth*`/`virbr*` and down interfaces are
  not offered; `br0`-style real bridges are. The address is stored canonical (plain dotted quad;
  IPv6 lower-case, no brackets, no scope; IPv4-mapped IPv6 reduced to IPv4) and validated as
  bindable unicast; `HostIp()` is an exhaustive switch, so an unknown mode can never become `0.0.0.0`.
- The chosen address becomes the port binding's `HostIp`; Docker's DNAT rule
  (`-d <addr>/32 ! -i <bridge> -p tcp --dport <port> -j DNAT`) matches the **destination
  address**, not the ingress interface: connections to the LAN or public address are refused, but
  it is not an interface restriction (Docker documents that a host with IP forwarding can receive
  such a packet over another interface). `HardeningVerifier` compares the configured binding before
  every start as before.
- Starting is **refused while the address is not assigned** to any interface (up or not; a
  WireGuard interface reports "unknown"), with the address in the message — never a fallback. The
  check runs in `ServerOperationsService` (Start, the second half of Restart, the restart after a
  settings change) and again in `DockerServerRuntime.StartAsync` immediately before the Docker
  start, each time enumerating afresh; an enumeration failure refuses the start with its own error.
  It covers starts Bonfire performs; restart policies are therefore not allowed in this mode
  (validation), and nothing else should start Bonfire's containers.
- After every start the runtime reads what Docker really published (`NetworkSettings.Ports`) and
  requires exactly the requested binding; otherwise it disables the restart policy, kills the
  container (each step with its own bounded timeout, independent of the caller's cancellation) and
  raises `PortNotPublished`, whose message says to use *Remove container* and then *Repair*
  (deliberately not automatic: Repair must not replace every apparently valid stopped container).
- The settings editor changes the binding through `ServerSettingsPatch` with the same
  transactional container replacement as game settings; the port check ignores the server's own
  record and container and skips the host probe when the new binding overlaps the current one
  (the old container's listener is not a conflict; a host program that took the port is still caught
  by Docker at the next start, reported as "could not be started again: port in use").
- The pack's `servers.dat` (stage D) can carry this address; the user guide's "Playing with
  friends over the internet" now ends with this mode instead of the `0.0.0.0` workaround.

Verified during implementation (Docker 29.8.0, iptables-legacy backend, `userland-proxy` on,
OpenRC host without firewalld/ufw; dummy interface added through a privileged host-network
container because the session had no root):

- *Address appearing after `dockerd` started:* a dummy interface with `10.200.0.1/32` added after
  the daemon was running works — the proxy binds it, the DNAT rule is installed, the host and other
  containers reach the port through that address only.
- *Address absent at start:* `docker start` fails with "failed to bind host port
  10.200.0.1:P/tcp: cannot assign requested address"; the container stays created/exited.
- *Address removed while running:* the container keeps running with its socket bound; connections
  time out; `docker restart` fails with the same bind error and leaves the container exited (137).
- **After any failed start caused by the missing address, every later start — also after
  stop/start, also for a container that had run correctly before — succeeds with
  `NetworkSettings.Ports` empty and no listener; `HostConfig.PortBindings` still shows the
  binding.** Reproduced three times. This is why Bonfire checks the effective publication after
  every start and why automatic restarts are refused in this mode. Recorded for this exact
  daemon/backend; not claimed for other versions.
- *Local connections:* a connection from the host to its own non-loopback address is DNATed
  straight into the container (`nat OUTPUT`), so it is refused until the game server listens — the
  real-Docker test waits for the health check first.
- *IPv6:* see above (explicit `0.0.0.0` is IPv4 only).
- *`userland-proxy=false`:* not testable here (needs a daemon restart); from the moby source the
  daemon reserves host ports with bound sockets even without the proxy, so a missing address fails
  the start in either mode — Bonfire's own presence check does not depend on it.
- *firewalld reload:* not testable here (no firewalld). Docker's release notes record a fix in
  28.1.0 for stale rules after a reload and a security fix in 28.3.3 (CVE-2025-54388: after a
  firewalld reload, published ports were reachable from the LAN even when bound to loopback).
  Documented, not verified; Bonfire's API floor (1.44) does not guarantee these fixes, so
  SECURITY.md does not promise anything about firewalld.
- *Docker 28+ hardening:* the FORWARD chain on this host carries Docker's `DOCKER-FORWARD` /
  `DOCKER-USER` chains with policy DROP, and direct routing to container addresses is filtered by
  default since 28.0.0; irrelevant to Bonfire's binding but worth knowing when reading the rules.

Public alternatives, documented as such and never called secure:

- **Relay services** (playit.gg, e4mc, ngrok): a tunnel through a third party; the server is
  public under a random address and the relay sees the traffic. Convenient for friends who will
  not install a VPN; whitelist and online mode remain required.
- **Port forwarding**: the server is public, bots appear in the logs, the home address is known
  to everyone who connects. Checklist below.

### Checklist before exposing to the internet

Shown when the user selects `0.0.0.0` (wizard and settings), advisory only, never blocking:

- online mode on — with it off anyone can join under any name, including a whitelisted one, so
  the whitelist protects nothing;
- whitelist on and enforced;
- backups made recently (the panel shows the date of the newest one).

All three settings exist; the checklist only reads them.

### Hosting a client pack (if it ever comes)

A packwiz-style HTTP endpoint serving the mod list would inherit the server's exposure setting,
so the list is never published more widely than the game port itself.

### Rejected

- **Public reachability test** through a third-party "port check" service: a new host in the
  HTTP allowlist, and it sends the user's public address and port to someone else.
- **UPnP / automatic port forwarding**: opens the router without the user seeing it — the
  opposite of what this project's threat model allows.
- **VPN inside the container**: needs capabilities and a TUN device the hardening profile does
  not grant (see above).

## Review notes

Agreed with the second reviewer on: a separate planner instead of reusing `ContentResolver`;
metadata-only pack; hand-off before any direct launcher integration; sync for Prism only, with
the ownership rules above. Corrections adopted from the review: not every client-capable
environment is needed to join (`client_or_server` and similar are excluded by default); `Detected`
is not the same as unidentified — the hash decides; Modrinth App's data directory is
`~/.local/share/ModrinthApp`; the official launcher is out of scope entirely; Flatpak hand-off
needs document-portal file forwarding.

The *Server address in the pack* and *Network exposure* sections were reviewed by the second
reviewer on 2026-10-12, before stage A. Corrections adopted: an explicit `0.0.0.0` binding is not
dual-stack (the earlier text implied unintended IPv6 publication; the empirical check agreed);
"a host firewall rule does not close the port" qualified to "may bypass ordinary host firewall
rules"; a specific-address binding is a destination restriction, not VPN isolation, so the UI
says "VPN" only when the address lives on a VPN-looking interface; `br-*` alone is not proof of a
Docker bridge (only `br-<12 hex>` is filtered); restart policies are refused in the specific-address
mode; the start guard runs in the service and in the runtime, and the effective publication is
checked after every start; the port check must ignore only the server's own container, never every
matching binding; the checklist reflects unsaved form values and treats "no backups" and "could not
read" differently; Tailscale/ZeroTier plan numbers and the Headscale requirement were wrong and are
now dated; the CGNAT diagnostic in the user guide was too categorical (a private WAN address can
also mean a second router) and omitted `172.16.0.0/12`; `servers.dat` belongs in
`client-overrides/`, needs Java modified UTF-8 and bracketed IPv6. Reviewer claims checked and
*not* adopted: "with `userland-proxy=false` only a DNAT rule is installed" was my own hypothesis,
not the reviewer's — the reviewer corrected it (ports are reserved with sockets either way) and the
document now says so; the reviewer's suggestion to let Repair recreate an unpublished container
automatically was declined (see the stage A bullets) and the reviewer agreed.
