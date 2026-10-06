# Your Martlet network

Pair a host once and every computer you own can use it. The owner's desktops
and hosts form one **Martlet network**: hosts trust every member desktop, and
member desktops pair with every host of the network by themselves. A Linux
machine you set up over SSH from one PC is ready on all your PCs a moment
later, without anyone logging in to it or typing a code on the other PCs.

The network is what makes Martlet [one app on all your computers](CLUSTER.md):
each host you add brings what it runs (thinking, listening, speaking, lip-sync,
Home Assistant) to the whole app, and its gateway keeps a private copy of
Martlet's shared settings, memories, people, voices and characters for your
other computers.

## How it works for you

| You do | What happens |
| --- | --- |
| Pair your first host (Add a computer: this PC with Docker Desktop, a Linux computer over SSH, or a typed code) | This PC starts your network and adds the host to it |
| Pair another host on any member PC | It joins the network; every other member pairs with it by itself within a minute |
| Install Martlet on another PC and choose it under Add a computer › *Martlet on your network* on that PC (or pair it with any one host of the network by code or SSH). On a new PC, Home offers this first: *Connect to your other computers* | Both screens show a six-digit check number. **Allow** it on the computer with the hosts (*Martlet on your network*, or **Devices › Your Martlet network** for a PC that paired another way; **Turn down** refuses). Allowing it from *Martlet on your network* also lets it into the network, with no second Allow. Then the new PC pairs with every host by itself, including hosts added later. Nothing needs to be set up on the new PC first: it pairs, then follows [who does what](CLUSTER.md) and your shared settings, so its jobs go to your hosts |
| Pair your main PC with a member PC's own host service (*Pair your main PC* on a host PC, or this PC's host service on a companion) | It joins the network by itself: the pairing code (or Allow) showed only on that member PC, so the owner already approved it there and no second Allow is asked. It then pairs with every host by itself |
| **Remove from network** on another PC | Every host revokes it; it forgets the network's hosts. To come back it asks again (with a new key) |
| **Remove from network** on a host | It stops trusting the network's PCs and they forget it. Pair it again with Add a computer to bring it back |
| **Forget** a host on one PC | Only that PC stops using it (and won't pair with it again by itself); the rest of the network keeps it |

Sync runs every 20 seconds while Martlet is open (and right after a pairing or
a change on the card), next to the [who does what](CLUSTER.md) sync. A host
that runs an older Martlet still works for the PCs paired with it, but can't
join the network until it is updated (**Update host**).

## When a computer is updated

Every host announces the Martlet release it runs in its network answer, so an
update reaches every computer that syncs with it on its next sync (within
20 seconds), whoever made it: this PC, another of your computers, or Martlet
on that host PC updating itself and then its host service. Each PC then shows the new release
on the host's card (*Details › Martlet*), drops the *runs an older Martlet*
item on Home and the *Update available* mark on the map, turns the card's note
on an update it had asked for into *Updated to Martlet 0.22.0 (seen at ...)*,
and says so in the status line (*gpu-pc was updated to Martlet 0.22.0.*). It
stops retrying an update the host no longer needs. A PC on an older release
than the host sees *0.23.0, newer than this PC (0.22.0). Update this PC to
0.23.0.* instead. Hosts from before this announcement report their release only
when a PC checks them (**Check connection**), as before.

## Who is connected

Every computer shows which computers use each of its hosts, whether or not they
are in the network yet. Each host reports the computers paired with it and when
each last used it (*active now* within two minutes, otherwise the time; a host
that restarted knows only from the next request). The Devices page shows them on
each host (*Computers using it*, or *Used by* on this PC's own host service),
and **Your Martlet network** lists members with where they were last active and
computers that use a host but are not members. A host PC's Home lists the
computers paired with its host service under *Pair your main PC*.

The Devices map draws the same computers on every one of them: this PC in the
middle, every other member of the network (and every computer asking to join or
using your hosts outside it) as its own device with where it was last active,
your hosts, and the cloud services your jobs use. Each computer is one device:
a PC that runs its own host service shows its device ID and its host service
together (*IMOUTO, desktop-imouto · imouto-host*), named by what it is
(*Martlet companion* or *Martlet host PC*). Each PC tells the others what it is
through the [shared settings](CLUSTER.md#one-martlet-on-every-computer), under
its own `pc.<device ID>` entry (its role and the host service Martlet runs on
it), so a companion PC that also runs a host service still shows the jobs it
does itself (Parakeet listening, say) and a host PC shows none. A computer on
an older Martlet hasn't said, so it shows as *Martlet app* and its host service
is matched by name (*DIVA* runs `diva-host`). A host PC uses no jobs, so its map shows who does what from the
[shared plan](CLUSTER.md) (a job a host does on that host, a cloud job on its
cloud service, a job that runs on each companion PC on those PCs) rather than
the Setup choice it kept from before it became a host. Before this, a host PC's
map showed only itself and its own old choices, and no computer showed the
other desktops. On any computer, another member's row on the map has **Make it
a host PC** (or **Make it a companion PC**) to [switch that
computer](CLUSTER.md#switching-another-computer-between-companion-and-host)
from here.

## A PC set up as a host

A PC set up as a host (*Use as a Martlet host*) takes part in the network too:

- If it is already in a network (for example it started the network while it was
  a companion PC), it keeps syncing like any member, so it can still let your
  other computers in. Their requests to join show on its Home, right under *Pair
  your main PC*, with **Allow** and **Turn down**, as well as on the Devices card.
- If it is in no network, it only watches: it never starts or asks to join one
  by itself (and makes no network key), so the network of the main PC that pairs
  with its host service takes that host. It still lists who uses its hosts.

Before this, a host PC skipped the network sync entirely. A network started on a
PC that later became a host could then let nobody in: the other PCs' requests
waited for an Allow that no screen showed.

Linux hosts are managed from Windows (SSH setup, roles, updates, Prepare this
computer, see [the host guide](../deploy/host/README.md)); they have no web UI
of their own. Every PC in the network can use a host; the PC that reaches it
over SSH also manages it.

## Trust model

- **Network key.** Each desktop has its own ECDSA P-256 network key in
  `network\device_ecdsa` in Martlet's data folder (readable by your Windows
  user only). Hosts only ever see public keys.
- **Roster.** The network is a roster of desktops (device ID, name, public key)
  and hosts (host ID, address, TLS key fingerprint). Every entry is signed by
  the member desktop that wrote it. The network ID is derived from the founding
  desktop's key. Each desktop keeps its copy in `network.json` beside its other
  preferences; each host keeps its copy in `network.json` beside `host.json`.
  It holds no secrets.
- **Accepting changes.** A host or desktop accepts an incoming entry only when
  its signer is an active member desktop in the roster it already accepted
  (removals first). Per computer the newest entry wins (hybrid millisecond
  revisions, then writer, then content). A removed desktop key never becomes
  active again, so a removed PC cannot sign itself back in; rejoining needs a
  new key, which Martlet makes by itself after a removal.
- **Binding a host.** A host in no network is bound by the first paired desktop
  that brings a roster in which it is a member and the host is listed with its
  own key. That pairing was approved by the owner on the host (a typed code,
  this PC's host dashboard or the owner's SSH session), so its network is the
  owner's. A host belongs to one network; a host of another network says so,
  and `martlet-host network-reset` lets it join another one.
- **Pairing by itself.** A member desktop signs the host's ID and fingerprint,
  the network, its device ID and name, a timestamp (within two minutes) and a
  fresh nonce with its network key, over TLS pinned to the fingerprint in the
  roster. The host checks the key against its roster and issues one `voice`
  credential (replacing older ones of that PC). The credential is kept in
  Windows Credential Manager like any other pairing.
- **Joining.** Only a PC already paired with a host of the network (with the
  owner's approval on that host, or through *Martlet on your network*) can ask
  to join, and the request names the device ID of that pairing. The check
  number (six digits from the network, device ID and key) appears on both
  screens; allow only when they match. A PC the owner just allowed from *Martlet
  on your network* (same device ID, within 15 minutes) is let in by the
  allowing PC without asking again. A PC that asks through a member PC's own
  host service (the host service Martlet runs on that PC's Docker Desktop) is
  let in by that member PC by itself: every pairing with it was approved on
  that PC (its pairing code or Allow shows only there), and the host service
  runs on that same computer, so it vouches for the joining key as that PC
  itself would. Before this, a main PC paired through *Pair your main PC* on a
  host PC still waited for an Allow on that same host PC. Requests through any
  other host still need the check number and an Allow.
- **Revocation.** Hosts revoke every credential of a removed desktop. A removed
  host revokes every network desktop and keeps the roster, so the PCs learn of
  the removal and forget it.

Limits, by design for a home network:

- Changes a stolen PC made before you removed it stay; remove anything you
  don't recognize too (everything is listed on the card).
- A PC learns about changes through its hosts. A PC whose only host is
  unreachable learns about them when it reaches one.
- At most 64 entries per roster, removed ones included.
- Not yet: a join code, *Leave network* on the PC itself (see
  [user stories B1-B7](USER_STORIES.md#b-joining-a-network)). Cloud keys and
  setups travel as [shared settings](CLUSTER.md#one-martlet-on-every-computer)
  through the network's hosts.

## Reaching your network from outside home

A laptop at work or on the road can be a companion (microphone, speakers,
character) while Thinking, voices and listening keep running on the hosts at
home. Martlet's trust model does not change: every connection is pinned TLS to
the host key in the roster, every request is signed with a per-device
credential, and pairing still needs the owner. What changes is *how the laptop
reaches the hosts* and *who else can knock*.

**Threat model.** At home only computers on your network can reach a host. Once
a host is reachable from outside, anyone on the internet can open a TLS
connection to it. They cannot read or forge traffic (pinned TLS, signed
requests, one-use pairing windows), so what they can do is *guess*: short
pairing codes, credentials, sign-in passwords and one-time codes; and *flood*
the routes anyone may call. Martlet answers with the gateway's guard (below)
and by keeping pairing codes on the home network. A stolen laptop is handled as
before: remove it from the network and every host revokes it.

**Routes, in order of preference.**

1. **Overlay network (recommended):** Tailscale, ZeroTier or your own
   WireGuard. The laptop and hosts share a private network wherever they are,
   nothing is open to the internet, and pinned TLS works unchanged (the overlay
   address is just another way to the same host key). Add each host's overlay
   address (for example `100.101.102.103:9443` or `gpu-box.tailnet.ts.net:9443`)
   as an outside address.
2. **Port forward on your router:** forward one public TCP port to a host's
   gateway port and add `your-name.example.net:<port>` (or your public IP) as
   that host's outside address. Turn on the host's *reachable from outside*
   protection (it is on whenever a host has outside addresses). Use a dynamic
   DNS name if your public IP changes. Forward only the gateway port, never a
   role service.
3. **Not supported:** HTTP tunnels and reverse proxies that end TLS themselves
   (Cloudflare Tunnel's HTTP mode, ngrok HTTP, nginx `proxy_pass https://`).
   They present their own certificate, so the host key pin fails by design.
   Only plain TCP passthrough (Cloudflare Spectrum or a TCP tunnel, nginx
   `stream`) keeps the pin; if you use one, the host can't see the real source
   address, so set *treat every connection as outside home* on it.

**Several hosts.** Each host the laptop should use from outside needs its own
outside address (its own forwarded port, or its overlay address). The simplest
workable setup is an overlay on every host; with port forwarding, forward one
port per host (for example 9443 and 9444). A host with no outside address is
simply not used while away.

**Which address a desktop uses.** A host keeps its home address in the roster
and gets any number of owner-set outside addresses (hostname or IP with a port).
The desktop tries the home address first and the outside addresses only when it
doesn't answer, always pinned to the same host key, and remembers the address
that worked so the home path never waits on an outside one.

**The gateway's guard** (`GatewayGuard.cs`), always on:

- Requests are classed by route: `health` (liveness), `pair` (pairing cards and
  typed codes), `join` (a member desktop's signed key), `signin` (sign-in
  points) and `credential` (everything signed or with an API key). The source
  is the connection's address, never a forwarded header: `loopback`, `home`
  (private and link-local addresses) or `outside` (anything else, including
  overlay addresses such as Tailscale's 100.64.0.0/10).
- **Pairing stays at home.** A pairing card or typed code used from outside
  home is refused (`pair.outside_home`, 403) unless the owner turns on *Allow
  pairing from outside home*; a short code can be guessed over the internet.
  Joining and pairing by itself use the desktop's network key, which can't be
  guessed, so they work from anywhere. Enrolling a new laptop from outside is
  what sign-in is for.
- **Lockout.** Failed authentication (`auth.*`, `pairing.*`, `network.denied`,
  `key.*`, sign-in failures) counts per route class and source address, and for
  sign-in also per account: five free failures in 15 minutes, then the address
  or account is locked out for 1 s, doubling with each further failure up to 15
  minutes (`auth.throttled`, 429, with `Retry-After`). A success clears it.
- **Budget.** Each outside address may make 120 requests a minute to the routes
  anyone may call (liveness, pairing, joining, sign-in).
- **Limits.** Pairing and sign-in bodies are at most 8 KB and must arrive within
  10 seconds (`request.timeout`); request headers within 5 seconds; 64
  connections at once.
- Requests from this computer and the home network to signed routes pass the
  guard without a lookup, so the conversation path never slows down. Once a host
  is *reachable from outside*, the limits apply to every source.
- **Audit.** Every decision (success, failure, throttled, refused: route class,
  source address and kind, code, device or account, never a secret) is kept in
  the last 256 entries, served to paired desktops at
  `GET /martlet/v1/security/audit`, and written to the host log (`Refused a
  pairing attempt from ...`, `Locked out ... from ...`, `Throttled ...`), which
  the Diagnostics page and MCP `logs_tail` show. Failed requests in the host log
  name their source address.
- Sign-in points register their routes through the same guard
  (`IGatewayRequestGuard.TryAdmit` and `Record`).

Exposure choices (`GatewayExposure`): *internet reachable* (limits apply to
every source), *allow pairing from outside home* (off by default) and *treat
every connection as outside home* (for a host behind a port proxy or TCP relay
that hides the real source).

## Where it lives

| Piece | Location |
| --- | --- |
| Roster format and rules | `Martlet.Core.Network` (`NetworkRoster`, `NetworkKey`, `NetworkPairing`) |
| Host side | `Martlet.Gateway` `GatewayNetwork.cs`: `GET`/`POST /martlet/v1/network` (roster, join requests, the computers paired with the host, the Martlet release it runs), `/network/join`, `/network/deny`, `/pair/member` ([gateway contract](../src/Martlet.Gateway/README.md#martlet-network-member-pairing)); `network.json` on Linux hosts ([Linux gateway](../src/Martlet.Gateway.Host.Linux/README.md)) |
| Desktop sync | `Martlet.Avatar.Audio2Face` `Remote/NetworkSync.cs` (`NetworkSyncEngine`, `NetworkLocalState`; `ReadOnlyAsync` for a host PC that only watches), `Remote/HostNetwork.cs` (client calls, pairing by itself) and `Remote/HostRelease.cs` (what an announced release means to this PC) |
| Desktop UI | `MainWindow.Network.cs`, the **Your Martlet network** card on the Devices page; `NetworkMap.cs` draws the network's computers on the Devices map; `MainWindow.HostReleases.cs` takes the releases hosts announce; the host PC's Home steps in `MainWindow.Shell.cs`; `NetworkIdentity.cs` for the key and `network.json` |
| Diagnostics | The desktop log records the network as this PC sees it whenever it changes (membership, requests to join, who each host is paired with) and each host's note (`Martlet network: ...` lines on the Diagnostics page or MCP `logs_tail`) |
| MCP | `network_status` (this PC's network from a data directory), `network_selftest` (end-to-end rehearsal on loopback, `Martlet.NodeLinkCheck network`) and `exposure_selftest` (the gateway's guard for a host reachable from outside home, `Martlet.NodeLinkCheck exposure`); card IDs in [MCP](MCP.md) |
| Outside home | `Martlet.Gateway` `GatewayGuard.cs` (guard, exposure choices, audit) and `GatewaySecurityAudit.cs` (`GET /martlet/v1/security/audit`); desktop `Remote/HostSecurity.cs` (`ReadSecurityAuditAsync`) |

Apps and scripts outside the network (Home Assistant, your own scripts) don't
join it: they use [API keys](API.md), which belong to the network too. A key
made or revoked on any member PC reaches every host the same way as the who
does what plan, and grants nothing in the network itself.

## Qualification

Checked on the Windows development PC through Martlet MCP: `network_selftest`
(three real gateways on 127.0.0.1 with simulated desktops and a simulated
host PC: founding, binding, joining with a check number, a host telling a paired
computer outside the network who uses it and when each was last active, a
member letting in a desktop paired with its own host service without a second
Allow while one asking through another host still waits, a host
PC outside the network watching without starting or joining one, pairing by
itself, refusing forged keys and rosters, removing a host and pairing it back,
removing a desktop, every host announcing the Martlet release it runs and a
desktop taking an announced update) and the desktop's Devices card on a
disposable data folder
(members listed, a pairing attempt to an unreachable host reported, **Remove
from network** signed and saved; as a host PC that started the network, the sync
running and logged; as a host PC in no network, watching only, with no key or
`network.json` made). The Devices map was checked the same way on disposable
host-PC and companion data folders with a signed roster, a shared plan and the
other computers' `pc.<device ID>` entries: the other member computers shown as
devices, each computer that runs a host service shown as one device with both
IDs (a companion with its own Parakeet listening, a host PC with none), a host
PC's jobs placed from the plan (its own host service speaking and doing
lip-sync, the cloud model) and one host service row. The gateway and Linux gateway unit tests pass. **NOT RUN:**
a native or Docker Linux host keeping `network.json`, `martlet-host
network-reset`, the SSH flow adding a real Linux host to a network, the desktop
window listing computers reported by a live host, letting a computer in through
its own live host service or following a live host's
update (all need a pairing secret in Windows Credential Manager) and two
physical PCs on a real LAN.
