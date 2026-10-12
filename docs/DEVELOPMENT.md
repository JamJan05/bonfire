# Development notes

Architecture, the main design decisions and how each part was verified. Every stage was reviewed
before committing by a second AI reviewer (OpenAI Codex) in addition to the tests; the review
findings are summarised per area below.

## Status

| # | Stage | State |
|---|---|---|
| 1 | Avalonia shell, MVVM, DI, logging, English/Polish UI | done |
| 2 | Docker connection and permission diagnostics | done |
| 3 | Creating and controlling servers (model, list, wizard) | done |
| 4 | Live logs, RCON console, players, settings editor | done |
| 5 | Backups and restore | done |
| 6 | Modrinth, local JARs, transactional add-on changes | done |
| 7 | Dependency resolver, updates, pinning, history and undo, complete backups | done |
| 8 | Release payload, packages (deb, rpm, Arch, Gentoo, AppImage), documentation | done |
| 9 | Network exposure: port on one host address (VPN), start guard, connection panel, checklist ([design](design/client-modpack.md), stage A) | done |
| 10 | Client modpack export, launcher hand-off, server address in the pack (stages B–D) | planned |

## Architecture

| Project | Contents |
|---|---|
| `Bonfire.Core` | Pure logic: server model and validation, container spec and hardening verifier, catalog/status rules, log assembly and redaction, RCON parsing, backup model, add-on model (lock, inventory, resolver, transactions, history). |
| `Bonfire.Infrastructure` | Linux interop (`flock`, `fsync`, `fstat`, `renameat2`, no-follow file access), Docker (Docker.DotNet.Enhanced behind interfaces), server registry, hardened HTTP metadata client, Modrinth client and downloader, backup store, add-on file layout and stores. |
| `Bonfire.App` | Avalonia 12 UI (compiled bindings, explicit data templates), view models (CommunityToolkit.Mvvm), services that orchestrate operations, localization. |
| `Bonfire.Tests` | xunit.v3 on Microsoft.Testing.Platform; fakes for Docker and Modrinth; headless Avalonia tests with real mouse clicks; opt-in real Docker / network tests. |

Platform: .NET 10 (SDK pinned in `global.json`), Linux only, x86_64. NuGet: lock files committed
(including the `linux-x64` runtime), `nuget.org` as the only source, vulnerability audit of all
packages as a build error, warnings as errors.

## Key decisions

| Decision | Reason |
|---|---|
| Only a local Docker Unix socket, explicit endpoint, API 1.44–1.56 negotiated | `DOCKER_HOST`/contexts must not redirect to a remote engine; errors classified by type and errno, never by (localized) message text. |
| Creating/starting requires confirmed daemon security (rootful, seccomp); stop/kill/remove always allowed | Fail closed for anything that runs code, but an affected server can always be contained. |
| Hardened container profile v1, verified against an allowlist before every start | Runs as the user's UID/GID, `CapDrop=ALL`, `no-new-privileges`, default seccomp, read-only root filesystem + small `noexec` `/tmp`, memory without swap, CPU limit, 512 PIDs, per-server bridge network, RCON unpublished with its password as a read-only file, `Mounts` (not `Binds`), size-limited logs, image pinned by digest. Verified empirically for Vanilla, Fabric and Paper. |
| Graceful stop: `STOP_DURATION=0` + Docker stop with no timeout; force stop escalates a pending stop | The server saves the world; only the user decides to kill it. |
| Labels `io.github.jamjan05.bonfire.*`, names `bonfire-<uuid>`, per-installation ID | Bonfire only ever touches its own resources. |
| Resumable operations: every multi-step change persists its intent in `server.json` first | Provisioning, settings changes, restores and add-on transactions are finished or rolled back by *Repair*; starting is blocked while one is pending. The restart policy is disabled during such changes. |
| Metadata from Mojang, Fabric, Paper, Modrinth through one hardened HTTP client | Per-provider host/path allowlist, no redirects, public IPs only (checked at connect time), deadline over body and parsing, decompressed size cap, JSON depth limit, rate-limit handling. Exact Java tag from metadata; only stable Paper builds. |
| RCON through `rcon-cli -- <command>` inside the container via Docker exec | Single argument, never a shell; a timeout means "outcome unknown" and is never retried. |
| Settings editor manages only fields Bonfire owns; MOTD rejects `%`, `$`, `\` and control characters | The image expands `%env:VAR%` — otherwise an MOTD could leak the RCON password. |
| Files written by a server are untrusted | `O_NOFOLLOW` + `fstat` regular-file checks, links never followed, directories verified before every add-on or restore operation. |
| Backups: tar.gz with per-file SHA-256, verified after writing, published by one directory rename | Restore only into the same game/loader versions, in one verify-and-extract pass; the previous state is kept as a rollback set; the server is never started automatically. |
| Add-on identity only from checksums | A lock entry is trusted only while the file's SHA-512 matches; otherwise Modrinth's hash lookup decides. File names never decide. |
| Add-on changes are journaled renames (`renameat2(RENAME_NOREPLACE)`) between enum-named locations | `Prepared` → roll back with per-file evidence checks; `Committed` → roll forward; `RolledBack` before evidence is deleted; mismatches keep the journal and block starting. |
| Resolver: bounded (50 projects, 200 API calls, 1 GB), exact versions first, restarts on late constraints | Honest "limit exceeded" instead of a wrong answer; incompatibilities checked both ways (also version-only declarations); disabled dependencies block; pins are never changed by a plan. |
| Updates: newer stable versions only by default; a vanished installed version is reported, not "updated" | Prevents silent downgrades; a failed check is "incomplete", never "up to date". |
| History: one directory per change, monotonic sequence, preimages verified before publishing | Undo only the newest change, only while the complete add-on state (files, lock incl. pins, server versions) is exactly what it left; restores clear the history. |
| Backup format 2 includes the add-on lock and disabled files | Restored together under one recovery marker; format-1 backups move the current add-on state aside instead of mixing snapshots. |
| No blocking waits on the UI thread; add-on disk work on the thread pool | A sync-over-async wait once froze the app; a source-scan test now forbids new ones. |
| The bundled Inter font is the default UI font | A system without fonts crashed Avalonia (found by the Arch package test). |
| Self-contained, reproducible release payload; distro packages repackage it | One tested artifact; identical input → identical archive. |
| English UI by default, Polish available; localization via `.resx` generated from `tools/gen-strings.py` | One source of truth for both languages; tests check keys and placeholders match. |
| Three exposure modes: `127.0.0.1`, `0.0.0.0` (IPv4 only) or one host address (`HostAddress`, canonical literal, record schema 5); `HostIp()` is exhaustive | A VPN address makes the server private without port forwarding; an unknown mode can never become a wildcard. Details and verified Docker facts in [design/client-modpack.md](design/client-modpack.md). |
| A specific-address server starts only while the address is assigned (checked afresh in the service and in the runtime); automatic restarts refused in that mode | Never a silent fallback to another address. A daemon restart before the VPN is up would fail the bind, after which Docker 29.8 runs the container with nothing published. |
| After every start the effective publication (`NetworkSettings.Ports`) must equal the requested binding, else the container is contained (policy off, kill) and `PortNotPublished` reported | Reproduced on Docker 29.8: once a start failed to bind the address, later starts publish nothing while the configuration still shows the binding. Recreating the container is left to the user (Remove container → Repair). |
| Settings change the binding through the same transactional replacement as game settings; the port check ignores only the server's own record and container | A false conflict with its own listener would block every move of the port; ignoring every matching binding would hide real ones. |
| Connection panel and checklist are advisory and read-only | Addresses come from the host's interfaces at that moment; the checklist (online mode, whitelist, backup) never blocks saving. |

## Verification

- **Unit and integration tests** (`dotnet test`, no Docker or network): ~640 tests covering the
  model, validation, container spec and verifier, Docker error classification, registry and file
  safety, metadata parsing and limits, log assembly and redaction, RCON, settings, backups
  (round trip, corruption, crash points, format 1 and 2), the add-on engine (resolver, transactions
  with failure injection at each step, crash recovery, journal validation, symlink attacks, updates,
  pins, history and undo), network exposure (address canonical form and bindability, suggestions
  and VPN hints, start guard incl. enumeration failure and force stop during the check, settings
  patch and port-check scope, effective-publication verifier, record schema pre-check), and view models.
- **Headless UI tests** with real mouse clicks: opening server details (a past crash), every tab
  rendering, the add-ons tab flow search → install → check updates → update → pin → undo, the
  connection panel's Copy button (IPv6 in brackets), the settings network card (mode switch by
  clicking, checklist, save) and the wizard's address mode.
- **Real Docker tests** (`BONFIRE_DOCKER_LIFECYCLE_TESTS=1`): full lifecycle for Vanilla, Fabric
  and Paper with the hardened profile; creation from live metadata; settings change and whitelist
  semantics; backup and restore on a running world; mods from Modrinth on a Fabric server (Chunky +
  automatically added Fabric API + Lithium load; dependency protection; disable/remove; restart);
  a server published on the host's LAN address only (reachable there, refused on loopback,
  effective binding read back) and the refusal to start on an address nobody has. The VPN-down
  sequence (address added after `dockerd` started, removed while running, failed start, address
  back) needs root and was run by hand; the results are in the design document.
- **Real network tests** (`BONFIRE_NETWORK_TESTS=1`): Mojang, Fabric, Paper and Modrinth through the
  production client, including a verified download.
- **Packages** (`TEST=1 packaging/build-all.sh`): install → GUI smoke test as an unprivileged user →
  upgrade → remove in clean Debian 12/13, Fedora 43/44 and Arch containers; the AppImage on systems
  without ICU; the Gentoo ebuild with `ebuild`. See [PACKAGING.md](PACKAGING.md).
- **Manual checks** in the real desktop session for every screen added, and by playing on the
  servers with a real client.

## Notable review findings (all fixed)

- **Docker and lifecycle:** verifier gaps (protocol of ports, publish-all, restart retries, image
  defaults), force-stop escalation, capability state invalidated after Docker errors.
- **Metadata and logs:** cancellation of cached requests, partial-secret redaction at truncated
  line ends, an MOTD placeholder that would leak the RCON password.
- **Files:** FIFO blocking on reads (now `O_NONBLOCK` + `fstat`), `$` in validation regexes
  accepting a trailing newline (now `\z`, which also fixed older validators).
- **Backups:** restore recovery not stopping a restarted container, a malformed `backup.json`
  breaking the list, a cancelled backup leaving the server stopped, "clean stop" requiring exit code 0.
- **Add-ons:** recovery evidence deleted before the rollback was durable, ancestors not rechecked
  during recovery, version-only dependencies ignored, history baselines computed at repair time,
  unvalidated published entries, an invalid lock inside a backup detected only after the data swap.
- **Packaging:** license texts and copyright notices, the runtime version taken from the cache
  instead of the build, smoke tests checking only view-model state, Debian 12's too old Docker,
  the AppImage's higher glibc baseline.
- **Network exposure (stage 9):** post-start inspection could escape containment on cancellation
  or a transport error (now its own bounded time, every failure contains); the kill was skipped
  when disabling the restart policy failed (now attempted regardless, both failures reported);
  the wizard lost a vanished address through the ComboBox's two-way binding (now kept as an
  "absent" item, like the settings editor); an enumeration failure in the settings editor dropped
  the saved address from the patch; an incomplete pending definition reached the validator and
  would have broken loading of every server; `\d` in the IPv4 regex accepted non-ASCII digits that
  `IPAddress.Parse` then threw on; the "friends connect through the VPN" note was shown for any
  specific address; overlapping panel loads could publish stale rows. Second round: a start whose
  Docker answer was lost (cancellation, transport error) skipped the publication check — now an
  inspection with its own time decides, and only a confirmed "not running" skips it; "stopped it
  again" was claimed even when the kill failed — now a separate `ContainmentFailed` error that says
  the server may still run; a force-stop test fired before the enumeration it meant to interleave
  with, and a "verifier" test never called the verifier. Disagreements: the reviewer
  wanted Repair to recreate a container whose start published nothing — declined, because Repair
  cannot tell from a stopped container that its next start is broken and must not replace every
  valid stopped container; the explicit two-step stays. The reviewer's claim that an explicit
  `0.0.0.0` binding is IPv4-only was confirmed empirically against Docker's own docs, which say
  the opposite.

## Known limitations

- Rootless Docker, remote engines and arm64 are not supported.
- No CPU/RAM statistics in the server list; no permanent deletion of a server's data from the UI.
- Pinning an add-on changes the recorded state, so it blocks undo until unpinned (by design).
- Modrinth project icons are not downloaded (deliberately: another untrusted image decoder).
- Release checksums come from the same place as the files; signing with an independently
  distributed key is a possible next step.
