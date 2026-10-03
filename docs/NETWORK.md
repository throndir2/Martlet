# Your Martlet network

Pair a host once and every computer you own can use it. The owner's desktops
and hosts form one **Martlet network**: hosts trust every member desktop, and
member desktops pair with every host of the network by themselves. A Linux
machine you set up over SSH from one PC is ready on all your PCs a moment
later, without anyone logging in to it or typing a code on the other PCs.

## How it works for you

| You do | What happens |
| --- | --- |
| Pair your first host (Add a computer: this PC with Docker Desktop, a Linux computer over SSH, or a typed code) | This PC starts your network and adds the host to it |
| Pair another host on any member PC | It joins the network; every other member pairs with it by itself within a minute |
| Install Martlet on another PC and choose it under Add a computer › *Martlet on your network* on that PC (or pair it with any one host of the network by code or SSH) | Both screens show a six-digit check number. **Allow** it on the computer with the hosts (*Martlet on your network*, or **Devices › Your Martlet network** for a PC that paired another way; **Turn down** refuses). Allowing it from *Martlet on your network* also lets it into the network, with no second Allow. Then the new PC pairs with every host by itself, including hosts added later |
| **Remove from network** on another PC | Every host revokes it; it forgets the network's hosts. To come back it asks again (with a new key) |
| **Remove from network** on a host | It stops trusting the network's PCs and they forget it. Pair it again with Add a computer to bring it back |
| **Forget** a host on one PC | Only that PC stops using it (and won't pair with it again by itself); the rest of the network keeps it |

Sync runs every 20 seconds while Martlet is open (and right after a pairing or
a change on the card), next to the [who does what](CLUSTER.md) sync. A host
that runs an older Martlet still works for the PCs paired with it, but can't
join the network until it is updated (**Update host**).

## Who is connected

Every computer shows which computers use each of its hosts, whether or not they
are in the network yet. Each host reports the computers paired with it and when
each last used it (*active now* within two minutes, otherwise the time; a host
that restarted knows only from the next request). The Devices page shows them on
each host (*Computers using it*, or *Used by* on this PC's own host service),
and **Your Martlet network** lists members with where they were last active and
computers that use a host but are not members. A host PC's Home lists the
computers paired with its host service under *Pair your main PC*.

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
  allowing PC without asking again.
- **Revocation.** Hosts revoke every credential of a removed desktop. A removed
  host revokes every network desktop and keeps the roster, so the PCs learn of
  the removal and forget it.

Limits, by design for a home network:

- Changes a stolen PC made before you removed it stay; remove anything you
  don't recognize too (everything is listed on the card).
- A PC learns about changes through its hosts. A PC whose only host is
  unreachable learns about them when it reaches one.
- At most 64 entries per roster, removed ones included.
- Not yet: a join code, *Leave network* on the PC itself, sharing cloud keys or
  setups (see [user stories B1-B7](USER_STORIES.md#b-joining-a-network)).

## Where it lives

| Piece | Location |
| --- | --- |
| Roster format and rules | `Martlet.Core.Network` (`NetworkRoster`, `NetworkKey`, `NetworkPairing`) |
| Host side | `Martlet.Gateway` `GatewayNetwork.cs`: `GET`/`POST /martlet/v1/network` (roster, join requests, the computers paired with the host), `/network/join`, `/network/deny`, `/pair/member` ([gateway contract](../src/Martlet.Gateway/README.md#martlet-network-member-pairing)); `network.json` on Linux hosts ([Linux gateway](../src/Martlet.Gateway.Host.Linux/README.md)) |
| Desktop sync | `Martlet.Avatar.Audio2Face` `Remote/NetworkSync.cs` (`NetworkSyncEngine`, `NetworkLocalState`; `ReadOnlyAsync` for a host PC that only watches) and `Remote/HostNetwork.cs` (client calls, pairing by itself) |
| Desktop UI | `MainWindow.Network.cs`, the **Your Martlet network** card on the Devices page; the host PC's Home steps in `MainWindow.Shell.cs`; `NetworkIdentity.cs` for the key and `network.json` |
| Diagnostics | The desktop log records the network as this PC sees it whenever it changes (membership, requests to join, who each host is paired with) and each host's note (`Martlet network: ...` lines on the Diagnostics page or MCP `logs_tail`) |
| MCP | `network_status` (this PC's network from a data directory) and `network_selftest` (end-to-end rehearsal on loopback, `Martlet.NodeLinkCheck network`); card IDs in [MCP](MCP.md) |

Apps and scripts outside the network (Home Assistant, your own scripts) don't
join it: they use [API keys](API.md), which belong to the network too. A key
made or revoked on any member PC reaches every host the same way as the who
does what plan, and grants nothing in the network itself.

## Qualification

Checked on the Windows development PC through Martlet MCP: `network_selftest`
(three real gateways on 127.0.0.1 with two simulated desktops and a simulated
host PC: founding, binding, joining with a check number, a host telling a paired
computer outside the network who uses it and when each was last active, a host
PC outside the network watching without starting or joining one, pairing by
itself, refusing forged keys and rosters, removing a host and pairing it back,
removing a desktop) and the desktop's Devices card on a disposable data folder
(members listed, a pairing attempt to an unreachable host reported, **Remove
from network** signed and saved; as a host PC that started the network, the sync
running and logged; as a host PC in no network, watching only, with no key or
`network.json` made). The gateway and Linux gateway unit tests pass. **NOT RUN:**
a native or Docker Linux host keeping `network.json`, `martlet-host
network-reset`, the SSH flow adding a real Linux host to a network, the desktop
window listing computers reported by a live host (it needs a pairing secret in
Windows Credential Manager) and two physical PCs on a real LAN.
