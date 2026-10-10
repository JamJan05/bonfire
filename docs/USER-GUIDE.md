# Bonfire user guide

## First start

The sidebar shows the Docker connection. When Docker is not reachable, Bonfire says why (daemon
not running, no permission for the socket, rootless or remote engine, unsupported API version) and
keeps everything else usable. **Settings** switches the language (English / Polish) and sets how
many automatic backups are kept.

## Creating a server

**Servers → New server**:

1. Choose the type — **Vanilla**, **Fabric** (mods) or **Paper** (plugins) — and a Minecraft
   version. Versions and loader builds come from Mojang, Fabric and Paper; only stable Paper builds
   are offered.
2. Set the name, port, memory and whether the server is reachable from other computers
   (`0.0.0.0`) or only from this one (`127.0.0.1`).
3. Accept the Minecraft EULA. Bonfire downloads the `itzg/minecraft-server` image for the
   required Java version, pins it to an exact image digest and creates the container.

Every server runs in a hardened container (see [SECURITY.md](../SECURITY.md)): as your user,
without Linux capabilities, with a read-only root filesystem, memory and process limits, in its own
network. RCON is never published outside the container.

## Everyday use

The server card has **Start**, **Stop**, **Restart** and — while a stop takes long — **Force stop**
(kills the server; unsaved world changes may be lost). **Details** opens the tabs:

- **Logs** — live log with a text filter; passwords are redacted.
- **Console** — commands through RCON inside the container; typing `stop` stops the server as the
  Stop button does.
- **Players** — who is online.
- **Settings** — MOTD, game mode, difficulty, players, whitelist, restart policy and more. Applying
  changes recreates the container (world and data stay); a running server is restarted.
- **Backups** — see below.
- **Add-ons** — mods (Fabric) or plugins (Paper), see below.
- **Information** — versions, image, container, data folder.

## Backups

**Back up now** creates a compressed archive of the server's data, add-on records and disabled
add-ons, verified after writing. A running server is stopped cleanly first and started again
afterwards; if it did not stop cleanly, no backup is made.

**Restore** needs a stopped server and a backup of the same game and loader versions. The current
data and add-ons are kept as the newest **rollback** folder (button *Open rollback folder*), and the
server is never started automatically. Older backups made before add-on records were included
restore the world only; the current add-on records are then moved to the rollback folder.

## Add-ons (mods and plugins)

Changes need a stopped server. Downloads come only from Modrinth and are verified by SHA-512.

- **Search** Modrinth, then **Install**: Bonfire shows a plan — files, sizes, required dependencies
  that are added automatically, and warnings (test versions, client-only add-ons, unknown server
  support). Nothing happens until you confirm.
- **Add local file…** installs a `.jar` you have. It is copied and inspected first; if Modrinth
  does not know it, it is marked *unverified*.
- **Disable / Enable** moves a file out of the server's reach and back. **Remove** deletes it.
  Bonfire refuses when another active add-on still needs it.
- **Check for updates**, then **Update** or **Update all**. Only newer stable versions are offered
  (tick *Include test versions* for betas). A version that is no longer offered is reported, never
  silently replaced.
- **Pin** keeps an add-on at its version, also when another update would need a different one.
- **History of changes → Undo last change** reverts the newest change (files and records). It is
  available only while nothing has changed since; changes are undone newest first. Undo does not
  revert changes the running server made to the world.

Files are identified only by their checksum, never by name. A file changed after installation
loses its recorded identity.

## When something was interrupted

Every multi-step change (settings, restore, add-ons) is recorded before it starts. If Bonfire, the
computer or Docker stops in the middle, the server shows as *incomplete* and cannot be started; use
**Repair** on its card. Repair finishes or rolls back the change — it never starts the server and
never overwrites files that do not match what it expects.

## Troubleshooting

- **Docker not connected / permission denied** — see [INSTALL.md](INSTALL.md); log out and back in
  after adding yourself to the `docker` group.
- **Port in use** — another program or server uses the port; choose another in Settings.
- **"The container's configuration does not match what Bonfire expects"** — it was changed
  outside Bonfire or must be recreated after an update; use Repair (world and data stay).
- **Logs** — `~/.local/state/bonfire/logs/`.
