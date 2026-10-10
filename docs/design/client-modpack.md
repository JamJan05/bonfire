# Design: client modpack for Fabric servers

Status: **proposal, not implemented.** Written 2026-10-11, reviewed by a second AI reviewer
(OpenAI Codex); the agreements and corrections are noted at the end.

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
| v1 | **Connection info** and **exposure checklist** — see *Network exposure* | proposed |
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

The mrpack format allows `overrides/servers.dat`, the client's multiplayer server list. With it
the server appears in the player's list right after the import — no address to type. Rules:

- **Off by default.** The user switches it on in the preview and chooses the address: for a LAN
  server Bonfire suggests the host's detected IPv4 addresses (see *Network exposure*), for an
  internet server the user enters their own hostname or address; the port is the server's.
- The address is shown in the preview, because a pack sent to friends may travel further.
- Only this one entry is written: name (the pack name), `ip` as `host:port`; nothing else.
- NBT is written by a small write-only encoder; Bonfire never reads an existing `servers.dat`.
- The address is not part of the `versionId` hash, so changing it does not look like a mod update.

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

- reaching the server from the internet needs a port forward on the router; behind carrier-grade
  NAT it is not possible without a tunnel or a hosted relay;
- **ports published by Docker bypass the host firewall** (the `DOCKER` iptables chain is
  consulted before `ufw`/`firewalld` rules), so a host firewall rule does not close the port.
  SECURITY.md should say the same.

IPv6: `0.0.0.0` publishes IPv4 only; depending on the Docker daemon's configuration the port may
or may not also be reachable over IPv6. To be verified and documented before the panel claims
anything about IPv6.

### Checklist before exposing to the internet

Shown when the user selects `0.0.0.0` (wizard and settings), advisory only, never blocking:

- online mode on — with it off anyone can join under any name;
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

## Review notes

Agreed with the second reviewer on: a separate planner instead of reusing `ContentResolver`;
metadata-only pack; hand-off before any direct launcher integration; sync for Prism only, with
the ownership rules above. Corrections adopted from the review: not every client-capable
environment is needed to join (`client_or_server` and similar are excluded by default); `Detected`
is not the same as unidentified — the hash decides; Modrinth App's data directory is
`~/.local/share/ModrinthApp`; the official launcher is out of scope entirely; Flatpak hand-off
needs document-portal file forwarding.

The *Server address in the pack* and *Network exposure* sections were added later without the
second reviewer (usage limit) and should get its review before implementation.
