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

## Verification

- **Unit and integration tests** (`dotnet test`, no Docker or network): ~520 tests covering the
  model, validation, container spec and verifier, Docker error classification, registry and file
  safety, metadata parsing and limits, log assembly and redaction, RCON, settings, backups
  (round trip, corruption, crash points, format 1 and 2), the add-on engine (resolver, transactions
  with failure injection at each step, crash recovery, journal validation, symlink attacks, updates,
  pins, history and undo), and view models.
- **Headless UI tests** with real mouse clicks: opening server details (a past crash), every tab
  rendering, the add-ons tab flow search → install → check updates → update → pin → undo.
- **Real Docker tests** (`BONFIRE_DOCKER_LIFECYCLE_TESTS=1`): full lifecycle for Vanilla, Fabric
  and Paper with the hardened profile; creation from live metadata; settings change and whitelist
  semantics; backup and restore on a running world; mods from Modrinth on a Fabric server (Chunky +
  automatically added Fabric API + Lithium load; dependency protection; disable/remove; restart).
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

## Known limitations

- Rootless Docker, remote engines and arm64 are not supported.
- No CPU/RAM statistics in the server list; no permanent deletion of a server's data from the UI.
- Pinning an add-on changes the recorded state, so it blocks undo until unpinned (by design).
- Modrinth project icons are not downloaded (deliberately: another untrusted image decoder).
- Release checksums come from the same place as the files; signing with an independently
  distributed key is a possible next step.
