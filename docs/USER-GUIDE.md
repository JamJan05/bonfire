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
2. Set the name, port, memory and where the port is published: only on this computer
   (`127.0.0.1`), on all network interfaces (`0.0.0.0`, with an advisory checklist for exposing a
   server), or on **one address of this computer** — the address of a VPN interface such as
   Tailscale or WireGuard, listed with its interface name (see *Playing with friends* below).
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
- **Settings** — MOTD, game mode, difficulty, players, online mode, whitelist, and under
  *Network* the port, where it is published and the restart policy. Applying changes recreates the
  container (world and data stay); a running server is restarted.
- **Backups** — see below.
- **Add-ons** — mods (Fabric) or plugins (Paper), see below.
- **Information** — *Connection*: the addresses players connect to, read from this computer's
  interfaces right now, each with a *Copy* button, plus what the chosen mode means for the LAN,
  the internet and the host firewall; below it versions, image, container and data folder.

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

## Playing with friends over the internet

A server published on `0.0.0.0` and forwarded on the router is public: address-space scanners
find it within hours. The safer way is a private network on top of the internet: every member
gets a private address, only members can reach it, and nothing else is exposed. Two free options:

- **Tailscale** — fewest steps, works behind carrier-grade NAT: install it on the host and on
  every friend's computer, share the host device with them, connect to its `100.x.y.z` address.
- **WireGuard, self-hosted** — no account anywhere; your computer is the endpoint. It needs a
  public address at home: if the router's WAN address is in `100.64.0.0/10`, `10.0.0.0/8`,
  `172.16.0.0/12` or `192.168.0.0/16`, there is another NAT in front of you (your provider's
  carrier-grade NAT, or a second router) and a forwarded port on your router alone will not
  work — use Tailscale, or forward on the outer router if it is yours.

The WireGuard recipe below assumes Linux on the host. Friends on **Windows, macOS, Android or
iOS** use the official WireGuard apps from <https://www.wireguard.com/install/>; it is the same
protocol.

### Host

1. Install the tools (the kernel module is part of Linux since 5.6): Debian `wireguard`,
   Fedora/Arch `wireguard-tools`, Gentoo `net-vpn/wireguard-tools`.
2. Keys — the private key never leaves this computer:
   ```sh
   sudo mkdir -p /etc/wireguard && sudo chmod 700 /etc/wireguard
   cd /etc/wireguard
   sudo sh -c 'umask 077; wg genkey | tee host.key | wg pubkey > host.pub'
   ```
3. `/etc/wireguard/wg0.conf` (mode `600`), one `[Peer]` per friend:
   ```ini
   [Interface]
   Address    = 10.8.0.1/24
   ListenPort = 51820
   PrivateKey = <contents of host.key>

   [Peer]
   # friend 1
   PublicKey  = <friend 1's public key>
   AllowedIPs = 10.8.0.2/32
   ```
   Deliberately no IP forwarding and no NAT: friends reach only this computer (`10.8.0.1`),
   neither your home network nor the internet through you.
4. Start it, one of: NetworkManager — `sudo nmcli connection import type wireguard file
   /etc/wireguard/wg0.conf` (starts with the system; `nmcli connection up|down wg0`); or
   `wg-quick` — systemd `systemctl enable --now wg-quick@wg0`, OpenRC
   `ln -s /etc/init.d/wg-quick /etc/init.d/wg-quick.wg0 && rc-update add wg-quick.wg0 default
   && rc-service wg-quick.wg0 start`. Check with `sudo wg show`.
5. Host firewall, if you run one: allow incoming UDP 51820.
6. Router: give the computer a fixed DHCP lease, forward **UDP 51820** to it — and nothing
   else; the game port is not forwarded. A changing home address: a free dynamic DNS name
   (many routers have a client built in).

### Friend

Either you generate the key pair and send the whole file over a private channel (the friend
deletes it after importing), or — better — the friend generates the keys in their app (*Add
empty tunnel* on Windows shows the public key) and sends you only the public key for the
`[Peer]` entry. File for friend 1:

```ini
[Interface]
PrivateKey = <friend 1's private key>
Address    = 10.8.0.2/32

[Peer]
PublicKey           = <contents of host.pub>
Endpoint            = your-name.duckdns.org:51820
AllowedIPs          = 10.8.0.0/24
PersistentKeepalive = 25
```

`AllowedIPs = 10.8.0.0/24` sends only traffic for your network through the tunnel; the friend's
internet is unchanged. Windows: *Import tunnel(s) from file*, *Activate*. Phone: a QR code
(`qrencode -t ansiutf8 < friend1.conf`). After editing `wg0.conf` on the host:
`sudo wg syncconf wg0 <(sudo wg-quick strip wg0)` or restart the connection.

In Minecraft the friend connects to `10.8.0.1` (and the server's port).

Why the forwarded WireGuard port is unlike a forwarded game port: WireGuard answers only
packets signed with a known key, so a scanner sees nothing; and removing a friend's key removes
their access.

### In Bonfire: publish the port on the VPN address only

In the wizard or in *Settings → Network* choose **One address of this computer** and pick the
VPN address — `10.8.0.1` on `wg0` for the recipe above, or the `100.x.y.z` address on
`tailscale0` (both are labelled *looks like a VPN*). Docker then forwards only connections
addressed to that address: friends in the VPN reach the server, your home network and the
internet do not, and nothing is forwarded on the router. The *Information* tab shows the address
with the port and a *Copy* button.

What to expect in this mode:

- Bonfire refuses to start the server while the address is missing (the VPN is down) and never
  falls back to another address; the message says which address it waited for.
- Automatic restarts are off: a restart before the VPN is up would leave the port unpublished.
- If a start ever fails to bind the address, Docker 29.8 afterwards runs the container with no
  port published at all. Bonfire detects this after every start, stops the container again and
  asks you to use *Remove container* and then *Repair* — world and data stay.
- The guarantee is about the destination address, not the network interface: keep online mode
  and the whitelist on anyway. A player on a VPN computer can still be anyone with that computer.

The client modpack export described in [docs/design/client-modpack.md](design/client-modpack.md)
is not implemented yet; friends install the mods by hand for now.

## When something was interrupted

Every multi-step change (settings, restore, add-ons) is recorded before it starts. If Bonfire, the
computer or Docker stops in the middle, the server shows as *incomplete* and cannot be started; use
**Repair** on its card. Repair finishes or rolls back the change — it never starts the server and
never overwrites files that do not match what it expects.

## Troubleshooting

- **Docker not connected / permission denied** — see [INSTALL.md](INSTALL.md); log out and back in
  after adding yourself to the `docker` group.
- **Port in use** — another program or server uses the port; choose another in Settings.
- **"The server's address is not assigned to any network interface"** — the server is published on
  one address (VPN mode) and that address is gone: start the VPN (`wg-quick up wg0`,
  `tailscale up`) and start the server again. Bonfire never switches to another address by itself.
- **"Docker started the container without publishing the game port"** — a known Docker 29.8
  behaviour after a start that failed to bind the address. Use *Remove container*, then *Repair*;
  world and data stay, only the container's own log is lost.
- **"The container's configuration does not match what Bonfire expects"** — it was changed
  outside Bonfire or must be recreated after an update; use Repair (world and data stay).
- **Logs** — `~/.local/state/bonfire/logs/`.
