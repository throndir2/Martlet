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
from here; Settings › *What this PC is for* lists the same computers with the
same button. A host PC with no host service yet says so on its row, because it
does no work for your other computers until someone at it sets one up.

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
- **Device ID.** Each Windows user's Martlet has its own device ID, kept in
  `device.json` in Martlet's data folder so it never changes:
  `desktop-<pc name>-<6 of [a-z0-9]>`, so two Windows users on one PC are two
  desktops. A data folder that was already paired or in a network keeps the ID
  its pairings use (an older Martlet's `desktop-<pc name>`).
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
- **Joining by signing in.** A computer away from home pairs with a host by
  signing in (below) instead of a code shown on the host. The host issues the
  same `voice` credential pairing does and remembers which identity enrolled
  that device; its answer to members (`GET /martlet/v1/network`) marks that
  device's join request with the identity (`sign_in`: provider, subject,
  label, when, and `account_id`, the household account the sign-in proves). A
  member desktop lets such a request in by itself
  (`NetworkSyncEngine.ApproveSignedIn`) when the attesting host is an active
  host of its roster: the owner set the sign-in up at home (the owner account
  with its authenticator, or an identity the owner allowed on that host), so
  that sign-in is the owner's approval, as an Allow would be. Only member
  desktops still sign roster entries; the host only attests. Requests without
  an attestation, or through a host outside the roster, still need the check
  number. The attestation lasts while the device is paired with that host and
  its identity is still allowed there. An identity allowed as a friend gets no
  attestation and can't ask to join: a friend's computer never joins the
  network ([sharing a host with friends](#sharing-a-host-with-friends)).
- **Removing a sign-in removes its computers.** When the owner removes an
  allowed identity, a provider or the owner account on a host (in the
  window, **Remove and remove its computers from the network**, or `martlet-host
  owner-signin-disallow`), that host revokes the computers the sign-in
  enrolled and records each one, with the network key it asked to join with
  (`signin.json` `removed`). Member desktops read the records in the host's
  network answer (`sign_in_removals`, members only) and remove those desktops
  from the roster on their next sync, signed by them as any removal, so every
  host revokes them and they leave the network (a new key and a new sign-in
  are needed to come back). A desktop is removed only when its roster entry
  still has that key (or the host never saw one), never the syncing PC itself,
  and only on the word of an active member host; the host drops a record once
  the roster shows the removal. So a host can at most ask to remove computers
  that signed in through it, and only those whose sign-in the owner removed. A
  friend's computer never joined, so removing a friend only revokes it on that
  host.
- **Outside access needs sign-in.** A host is reachable from outside home only
  while someone can sign in to it: an owner account (always with an
  authenticator) or a provider with at least one allowed identity
  (`GatewaySignInSettings.BlockedReason`: `signin.not_set_up`,
  `signin.no_allowed_identity`; `blocked_reason` and `usable` in the sign-in
  settings and `martlet-host owner-signin-status`). The guard pauses outside
  access while it isn't (its outside addresses are kept). **Sign-in from
  outside** asks before a change that would leave nobody able to sign in on a
  host with outside addresses, and says when outside access is paused
  (`SignInOutsideWarning`).
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

**Setting outside addresses.** On any member PC: Devices › *Your Martlet*
*network* › the host's **Outside addresses** (up to four, for example
`gpu-box.tailnet.ts.net:9443`, `100.101.102.103:9443` or
`home.example.net:9443`). On a Linux host: `martlet-host owner-exposure --config
<dir>/host.json --outside <name:port> [--outside ...]` (`--clear-outside` removes
them; `--allow-pairing-outside-home yes|no` and `--treat-all-as-outside yes|no`
set the other choices), saved in `exposure.json` beside `host.json` and applied
when the service restarts. The host advertises what was set on it
(`GET /martlet/v1/network`: `advertised_addresses`, `advertised_at`), and the
next member desktop that syncs signs it into the host's roster entry when the
entry has none or it is newer. Outside addresses are part of the signed entry
(`NetworkMember.Addresses`; entries without them sign exactly as before, so
existing rosters are unchanged). Martlet before this release refuses a roster
that lists outside addresses, so update every computer (Update host) before
adding them.

**Which address a desktop uses** (`Remote/HostRoutes.cs`). Requests keep the
host's home origin, so TLS still checks the pinned key and signatures are
unchanged; only the TCP connection dials elsewhere. The home address goes
first; outside addresses are tried (all at once) only when it doesn't connect
within 1.5 seconds, and only for hosts that have them. The address that worked
is remembered, so later connections go straight to it, while the home address
gets a 0.1-second head start and wins as soon as this PC is back home. A home
address that answers with another key (another network's computer with the same
address, at work) is skipped for five minutes. When nothing answers, the error
names every address tried and what to check. A host's row on the Devices card
says how many outside addresses it has and whether it was last reached at home,
from outside or not at all, and the card's status line counts the hosts reached
from outside right now. The desktop log records each change of route (`Martlet
network: reaching gpu-box from outside home ...`, `... at home again`, or the
addresses tried when nothing answered). Every five minutes the desktop reads the
security audit of each paired host that has outside addresses; the host's row
shows its guard's totals and the log names new failures, lockouts and refused
pairings with their sources. MCP `outside_reachability_check` (with
`contactHosts: true`) probes every host's home and outside addresses with the
pinned key and `GET /health/live`, without a credential.

**Kept connections.** Every paired connection to the same host (same home
origin and pinned key) shares one pool of kept TCP and TLS connections
(`Audio2FaceHostClient.CreateHttpClient`), so the desktop's regular checks and
syncs (every 5 to 30 seconds, per host) do not open a new connection each time.
A kept connection closes after 20 seconds idle (shorter than the gateway's
30-second keep-alive) and is replaced after 2 minutes, so a host reached from
outside is reached at home again within about 2 minutes once home answers. At
most 32 connections to one host are open at the same time (the gateway takes 64
from all computers together). A response left unread (a reply stopped) closes
its connection at once, so the host stops that job at once. `HostRoutes` counts
the connections opened to each host (`HostRouteStatus.Connections`); MCP
`host_connections_selftest` checks the reuse on loopback. When this PC itself
runs out of network resources (Windows `NoBufferSpaceAvailable`: no free
connection ports or socket buffers), the log and the error say so (*This PC ran
out of network resources ... The host may be fine.*) instead of saying the host
didn't answer.

**The gateway's guard** (`GatewayGuard.cs`), always on:

- Requests are classed by route: `health` (liveness), `pair` (one-use cards),
  `pair-code` (typed codes), `join` (a member desktop's signed key), `signin` (sign-in
  points) and `credential` (everything signed or with an API key). The source
  is the connection's address, never a forwarded header: `loopback`, `home`
  (private and link-local addresses) or `outside` (anything else, including
  overlay addresses such as Tailscale's 100.64.0.0/10).
- **Typed codes stay at home.** A short typed pairing code used from outside
  home is refused (`pair.outside_home`, 403) unless the owner turns on *Allow
  typed pairing codes from outside home*; a short code could be guessed over
  the internet. A one-use card opened for one named device (a 32-byte token, as
  Martlet uses to pair with its own host service), joining and pairing by itself
  (the desktop's network key) can't be guessed, so they work from anywhere. Enrolling a new laptop from outside is
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
every source), *allow typed pairing codes from outside home* (off by default)
and *treat every connection as outside home* (for a host behind a port proxy or
TCP relay that hides the real source, such as a gateway Docker publishes).

**Sign-in comes first.** Sign-in stays optional: a host with no outside addresses
(and typed codes kept at home) works exactly as before and needs no sign-in. A
host becomes a public endpoint only once sign-in is set up on it, meaning at
least one usable method: an owner account with an authenticator (TOTP), or a
configured provider with at least one allowed identity (a friend counts). Sign-in owns that rule
(`GatewaySignInSettings.BlockedReason`: `signin.not_set_up` or
`signin.no_allowed_identity`); outside access only reads it
(`GatewayOutsideAccess`).

- The host enforces it. `martlet-host owner-exposure` (and `martlet-host
  exposure`, the Outside access dialog and the `host.exposure` command, which all
  run it) refuses with exit 5 and `outside.needs_signin` to add an outside
  address it doesn't already have, or to allow typed codes from outside, while
  sign-in isn't usable. It reads `signin.json` as it is, without starting
  sign-in's service. Removing addresses, keeping them and *treat every
  connection as outside home* never need sign-in. That last one only says how
  requests are classified (for Docker-published gateways), not that the host is
  public.
- If sign-in later loses its last usable method (the owner account removed, the
  last allowed identity or provider removed, `signin.json` gone) while the host
  has outside addresses or allows typed codes from outside, the addresses are
  kept and outside access is **paused**. The guard refuses every request from
  outside home with `outside.paused` (403), audits it as `refused` and logs
  *Refused a ... request from ... (outside home): outside access is paused
  because sign-in isn't set up on this host. The outside addresses are kept.*
  Home and this computer are unaffected. The one exception is the sign-in
  settings route (`/martlet/v1/signin/settings`, a paired desktop's signed
  request), so the owner can set sign-in up again from anywhere. The guard reads
  sign-in at most every five seconds. `serve`, `martlet-host pair` and
  `owner-exposure` print *Outside access paused: ...* while it lasts.
- The security audit (`GET /martlet/v1/security/audit`) carries
  `outside_access_blocked_reason` (absent when sign-in is usable) and
  `outside_access_paused`. The Outside access dialog shows why it is limited
  (`OutsideAccessBlockedReason`, starting *Outside access paused: sign-in is
  off.* when it is) and a **Set up sign-in first** button
  (`OutsideAccessSetUpSignIn`) that opens the host's sign-in settings. Until
  then it only lets addresses be removed, typed codes be turned off and the
  connection treatment change. The Devices map's *Outside home* detail and the
  host's row in *Your Martlet network* say *Outside access paused: sign-in is
  off* too. Sign-in's own window warns before a change that would leave a host
  with outside addresses without sign-in (`SignInOutsideWarning`).

**This PC's host service and other hosts Martlet manages.** On the Devices map,
select this PC, a host Martlet reaches over SSH, or a host whose own Martlet runs
its commands (the `host.exposure` command between computers; an older Martlet
there is asked to update first) and choose **Outside access**
(`NodeAction-OutsideAccess`): outside addresses, *Allow typed pairing codes from
outside home* and *Treat every connection as coming from outside home*. Martlet
runs `martlet-host exposure` there (`martlet-host pair` serves the same choices) (`--outside`, `--clear-outside`,
`--allow-pairing-outside-home`, `--treat-all-as-outside`), which saves
`exposure.json` through the gateway and restarts it; the network's next sync
signs the addresses into the roster. Docker publishes the gateway's port, so a
Docker host sees every connection, from home or outside, as coming from Docker's
own address: the dialog keeps *Treat every connection as coming from outside
home* on for Docker hosts. Then typed pairing codes need *Allow typed pairing
codes from outside home* even at home; Martlet pairing with its own host
service (a one-use card) doesn't. The Devices map's *Outside home* detail
(`SelectedDeviceOutside`) shows the count of outside addresses and, once read
from the host, its two choices.

## Joining from outside home by signing in

Pairing codes stay at home, so a laptop that is already away joins by signing
in. Signing in gates only that first pairing: what it gets is exactly what
pairing gets (a `voice` credential bound to that host's pinned TLS key, used to
sign every request, never expiring, revoked like any pairing), and the laptop
then joins the network on that host's attestation, with no check number.

| You do | Where |
| --- | --- |
| Set up the owner account on a host: a name, a password (12+ characters) and an authenticator app (any TOTP app: Google Authenticator, Aegis, 1Password, Bitwarden, Microsoft Authenticator). The app must show a correct code before the account is saved; you get ten one-use recovery codes, shown once | At home, on a member PC: **Devices › Add a computer** with the host selected › **Sign-in from outside**; on a Linux host: `martlet-host owner-signin-owner --config ... --user owner` (password on the first stdin line, then the code) |
| Make an invite: the host's ID, its TLS key fingerprint, its home address, the outside addresses it answers on and the network ID. It holds no secret, so it can be reused and kept in a password manager or note | **Sign-in from outside** › **Make invite** (it starts with the host's outside addresses from **Your Martlet network › Outside addresses**; set them there so the laptop keeps reaching the host once it has joined); `martlet-host owner-invite --config ...` (the addresses from `owner-exposure`, or `--address name:port`) |
| On the laptop: paste the invite and sign in (account name, password and the code your app shows, or a recovery code) | **Devices › Add a computer › Join with an invite** |
| Take access away | Remove the laptop from the network (as always), or **Remove owner account** / **Remove and remove its computers from the network** for an allowed identity on the host (`martlet-host owner-signin-disallow`): the host revokes every computer that signed in with it and your member computers remove those computers from the network on their next sync, so they lose every host |

How it is protected:

- **Pin first.** The laptop connects only to the key in the invite: the TLS
  certificate must have that SPKI fingerprint before anything is sent (it names
  the host's home address, so a name mismatch from outside is expected; the pin
  is the authority, as for every pairing). The outside addresses are tried
  first, then the home address.
- **Two factors.** The password is kept on the host only as a PBKDF2-SHA256
  verifier (600,000 iterations, 16-byte salt). Every sign-in also needs a
  current authenticator code (RFC 6238: 30-second steps, one step of drift,
  each code accepted once) or an unused recovery code (kept as SHA-256
  verifiers, each works once). Wrong sign-ins count toward the guard's lockout
  per address and per account (five free, then 1 s doubling up to 15 minutes),
  from home too, and every outcome is in the host's security audit.
- **One-use attempts.** Each sign-in starts with `POST /martlet/v1/signin/begin`
  (an attempt with its own state and nonce, ten minutes, one use) and ends with
  `POST /martlet/v1/signin/complete`. The proof travels only over the pinned
  connection.
- **Secrets stay on the host.** `signin.json` beside `host.json` (0600, service
  owner) holds the verifiers, the authenticator secret, provider client
  secrets and the allow list. The desktop never reads a secret back
  (`GET /martlet/v1/signin/settings` returns names, counts, providers with
  `has_client_secret`, allowed identities and the computers that signed in).
  Only an active member desktop of the host's network (any paired desktop while
  the host is in no network) may read or change it (`signin.denied` otherwise);
  `martlet-host owner-signin-*` edits it on the host and the running gateway
  picks the change up on its next sign-in check.
- **Allow list.** The owner account is always allowed. Any other identity (a
  provider set up on the host and the account's stable subject, with a label
  for people) must be on the host's allow list; one that signs in but isn't
  gets `signin.not_allowed`. Each allowed identity is one of your own
  (`member`, the default: its computers join your network) or a friend's
  (`friend`: it [shares only this host's engines](#sharing-a-host-with-friends));
  changing that revokes the computers it signed in with the old access. Removing an identity, a provider or the owner
  account revokes the computers it enrolled on that host and removes them from
  the network (see *Removing a sign-in removes its computers* in the trust
  model), so a laptop that joined loses every host, not just that one.
- **No taking over another computer's ID.** A sign-in names the computer's
  device ID, so the host refuses (`signin.device_taken`) an ID that belongs to
  a member desktop of its network (those pair by their network key and never
  sign in) or that is paired another way (a code, a card) or by another
  identity. A computer can sign in again only to replace its own earlier
  sign-in by the same identity. Before this, a signed-in computer that named
  another computer's ID revoked that computer's pairing on the host and was then
  taken for it.
- **Addresses travel with the pairing.** The invite's outside addresses are
  kept with the pairing in `hosts.json` (`outsideAddresses`) and are dialed
  from the next start on, so a laptop that has never been on the home network
  reconnects whatever its network state. Once it is a member, every sync keeps
  them in step with the host's roster entry, which wins. The home address
  still goes first and outside addresses only after 1.5 seconds, so nothing is
  slower at home.

### Signing in with your identity provider or Google

Instead of (or as well as) the owner account, a host can let you sign in in
the browser with any OpenID Connect issuer: a self-hosted Authentik, Authelia,
Keycloak or Pocket ID, or Google. Martlet itself runs the flow: the laptop opens
the system browser with an authorization-code request with PKCE (S256) and a
loopback redirect `http://127.0.0.1:<free port>/` (RFC 8252), the host builds
that URL from the issuer's discovery document and the attempt's `state` and
`nonce`, and once the browser comes back the laptop forwards what it brought
and its PKCE verifier. The host exchanges the code at the issuer's token
endpoint itself, with the client secret only it keeps (`client_secret_post`,
or `client_secret_basic` when that is all the issuer offers; none for a public
client), and accepts the ID token only when its signature checks against the
issuer's published keys (RS256/384/512, PS256/384/512, ES256/384; an unknown
key ID refreshes the key set once) and its issuer, audience (or authorized
party), expiry and nonce match. The identity is the issuer's stable `sub`; a
verified email, the user name or the name is its label.

1. At the provider, register a client for Martlet: a public or confidential
   OAuth2/OpenID client whose redirect URI is `http://127.0.0.1` (loopback,
   any port; Authentik: *Redirect URIs* `regex:http://127\.0\.0\.1:\d+/`;
   Keycloak: `http://127.0.0.1:*`; Authelia: `http://127.0.0.1/` with
   `redirect_uris` loopback ports allowed; Pocket ID: `http://127.0.0.1:*`).
   For **Google**: Google Cloud console › APIs & Services › Credentials ›
   *Create credentials* › *OAuth client ID* › *Desktop app* (configure the
   consent screen first, and add your Google account as a test user while the
   app is in testing). Google's desktop clients take any loopback port and
   come with a client secret. For **Microsoft** (personal Microsoft accounts:
   Outlook.com, Xbox, Windows): Microsoft Entra admin center › *App
   registrations* › *New registration* (*Personal Microsoft accounts only*),
   then *Authentication* › *Add a platform* › *Mobile and desktop
   applications* with the redirect `http://127.0.0.1` (any port). It is a
   public client: no secret. A work or school account uses its own tenant's
   issuer (`https://login.microsoftonline.com/<tenant ID>/v2.0`) under *OpenID
   Connect*.
2. At home: **Sign-in from outside** › *Sign-in providers for your household*:
   choose *OpenID Connect* (an ID such as `authentik`, a name, the issuer URL
   such as `https://auth.example.net/application/o/martlet/`), *Google*
   (filled in: `https://accounts.google.com`) or *Microsoft (personal
   accounts)* (filled in:
   `https://login.microsoftonline.com/9188040d-6c67-4c5b-b112-36a304b66dad/v2.0`),
   the client ID and secret, **Save provider**. Martlet saves it on every host
   of your network at once ([household providers](#household-sign-in-providers)).
3. Sign in with it once from the computer you want to let in (from home or
   away): the host refuses (`signin.not_allowed`) and lists the identity under
   *Signed in but not allowed yet*; **Allow the newest** (or type the provider
   ID and subject under *Other allowed sign-ins*). From then on that identity
   pairs, and the computer joins your network as above.

### Signing in with Discord or Steam

- **Discord** (OAuth2, no ID token): in the Discord Developer Portal create an
  application, and under *OAuth2* add the redirect `http://127.0.0.1:53682/`
  (Discord only takes redirects registered exactly, so the laptop listens on
  that port; pick another in **Loopback port** and register that instead) and
  copy the client ID and secret. At home choose *Discord* under *Sign-in
  providers*, paste them and **Save provider**. The laptop signs in in the
  browser (scope `identify`, PKCE); the host exchanges the code with the
  secret it keeps and asks Discord who the token belongs to (`/users/@me`).
  The identity is the Discord user ID; the user name is its label.
- **Steam** (OpenID 2.0): nothing to register. Choose *Steam* and **Save
  provider**. The laptop signs in at steamcommunity.com in the browser, which
  sends it back to the laptop's loopback address with a signed assertion; the
  host checks that it answers this attempt (its `return_to` carries the
  attempt's state, the realm is the loopback address, it was signed over the
  endpoint, claimed ID, return address and nonce) and asks Steam to confirm it
  (`check_authentication`). The identity is the SteamID64.

Allow either the same way: sign in once, then **Allow the newest** at home (or
type the Discord user ID or SteamID64 under *Other allowed sign-ins*).

Limits, for now: allowed identities are per host (allow one on the host the
laptop reaches from outside); the laptop reaches the network's other hosts
only where they have outside addresses
([above](#reaching-your-network-from-outside-home)). Providers are the
household's: set up once, they are on every host.

### Household sign-in providers

You set a sign-in provider up once for the whole household, and every host of
your network has it, so a person or a computer can sign in with it through any
host.

- **Save provider** and **Remove provider** in any host's **Sign-in from
  outside** change every host of your network at once (every paired host of
  yours while this PC is in no network). The status line names the hosts that
  saved it and why the others didn't (*Not changed on linux-box (didn't answer
  in time)*). Removing asks first: computers and friends that sign in with it
  lose access to those hosts.
- **Where it is.** The window shows each provider and its hosts
  (`SignInHouseholdProviders`, for example *Google (google): on gpu-box and
  this PC's host; missing on linux-box.*), including hosts with other settings
  under the same ID and hosts without the client secret it needs.
- **The secret.** Hosts keep the client secret, as before; the desktop never
  reads it back. The PC where you saved the provider also keeps a copy in
  Windows Credential Manager (`Martlet/v3/signin-provider/<id>`), only to add
  the provider to a host that misses it.
- **New hosts.** When Devices › Friends reads your hosts' sign-in settings (the
  Devices page shows, **Check now**, at most every two minutes) and after a
  network sync pairs this PC with a new host, your PC adds the household's
  providers to the hosts that miss them: the configuration most hosts have,
  with the kept secret when the provider needs one (Discord, or a confidential
  OpenID Connect client) and this PC kept it for that client ID. Other PCs add
  only providers that need no secret (Steam, public clients). A provider
  removed from every host is never added back. **Add to the other hosts**
  (`SignInProvidersPush`) does it now and says which provider still needs its
  secret typed. This never runs on a reply's path.
- **Friends stay friends.** Allowed identities (yours and friends') stay per
  host; the household push never adds, changes or removes one.
- **What this PC knows.** `household-signin.json` in the data folder keeps the
  providers this PC set up (no secret) and what it last read of every host;
  MCP `network_status` shows it as `householdSignIn`.

### Signing in to your household account

A computer can join as one of a person's computers by signing in to their
household account ([accounts](ACCOUNTS.md)) instead of as one of the owner's.

- **Join with an invite** lists *Your household account (Martlet password)*
  when the host keeps household logins (`martlet_sign_in`), the owner account
  and the household's providers. A provider identity that is a login of an
  account (`account_id` on the host's allow list) signs in as that account.
- The host pairs the computer as before and answers the account and its
  attestation. The computer keeps them in `joined-account.json` (no secret).
  Once it is a member of the network, it signs that account in with the
  attestation (`AccountSession.SignIn`), reads the household's account
  directory and switches to the account between replies (*Hi, Sam*). The
  attestation lasts ten minutes; when it can no longer be used, the person
  signs in from the account button instead.
- The host's network answer to member desktops names the account
  (`sign_in.account_id` with the join request). A member desktop lets the
  computer in by itself, as for any attested sign-in, and says whose it is:
  *LAB-NEW-PC (signed in as sam@example.net, Sam's computer) joined your Martlet
  network by itself*.
- A new Windows user on a PC that is already in the network joins the same
  way: an admin makes an invite (**Make invite**) and the new user pastes it.
  Joining at home without an invite is not offered: a host found on the network
  has no pinned key yet, so a password or code could go to the wrong computer.
- To link a provider login to an account, Martlet proves the account first
  (an attestation from a Prove sign-in on this computer), then signs in with
  the provider through a host (a Prove sign-in too). A login the host doesn't
  allow yet is refused there and listed for this computer, which reads it back
  and allows it as that account's login on every host
  (`HouseholdSignIn.LinkInBrowserAsync`). A login that already proves another
  account, or is a friend's, is refused (`signin.login_taken`). Unlinking
  removes it from every host like any removed sign-in, and Martlet keeps at
  least one login that proves the account on another computer.

### Household accounts on a host

A host also keeps sign-ins for the other people of the household
([accounts and households](ACCOUNTS.md)). Each one belongs to an account ID.

- **The owner's account.** The owner login and every allowed identity that is
  one of yours (`member`) sign in as the household owner's account. A desktop
  can name it (`owner_account_id`, change `owner-account`, or `account_id` with
  `owner`). Until then the host uses the account derived from its network ID
  (`OwnerAccount.IdFor`: UUID version 5 of `martlet-household-owner\n<network
  ID>`), so existing owner logins keep working with no change.
- **Martlet password logins** for other accounts (`accounts` in `signin.json`):
  a user name that is unique on the host whatever its case, a password kept as
  the same PBKDF2 verifier and, optionally, an authenticator with ten recovery
  codes. They sign in with provider `martlet` (`/signin/begin`), which also
  takes the owner login. Without an authenticator a login proves its account
  on a computer that is already paired, but it never adds a computer
  (`signin.needs_authenticator`).
- **Provider identities** can name the account they sign in as (`account_id`
  with `allow`, or change `link`). A friend never has an account.
- Outside access rules don't change: a host is usable from outside only with an
  owner login (always with an authenticator) or a provider with an allowed
  identity. Household password logins alone don't open it.

The sign-in settings changes (from a member desktop, `POST
/martlet/v1/signin/settings`):

| Action | Fields | Does |
| --- | --- | --- |
| `owner-account` | `account_id` | Names the owner's account |
| `account` | `account_id`, `user`, `password`, optional `totp_secret` and `code` | Adds another account's login, or changes it (the password may be left out of a change). An authenticator needs a current code; the answer carries new recovery codes once |
| `remove-account-authenticator` | `account_id` | Removes that login's authenticator and recovery codes |
| `remove-account` | `account_id` | Removes the login; the host revokes the computers it added and your member computers remove them from the network |
| `recovery-codes` | optional `account_id` | New recovery codes for the owner, or for that account's login |
| `allow` | optional `account_id` | Allows an identity as that account's (members only); allowing again without one keeps the link |
| `link` | `provider`, `subject`, optional `account_id` | Links an allowed identity to an account, or (no `account_id`) back to the owner's |

On a Linux host: `martlet-host owner-signin-account --config ... --user sam
--account <account ID> [--authenticator yes|no]` (password on the first stdin
line, then the code), `owner-signin-remove-account --account <account ID>`,
and `--account` on `owner-signin-owner` and `owner-signin-allow`.
`owner-signin-status` lists the logins.

### Proving an account: host attestations

A **Prove** sign-in ([accounts](ACCOUNTS.md#logins)) on a computer that is
already paired with the host: `POST /martlet/v1/signin/begin` (provider
`martlet`, `owner` or a provider ID), then `POST /martlet/v1/signin/prove`,
signed by that computer's credential, with `attempt_id`, `proof` (the same
proof as `/complete`) and optional `lifetime_seconds` (60 to 2,592,000; 600
when absent). A friend's computer gets `access.friend`. The host answers
`account_id`, `signed_in` and an `attestation`, and issues no new credential.
`/signin/complete` also answers `account_id` and an `attestation` for the
computer that just signed in, when the identity has an account.

The attestation (`AccountAttestation` in `Martlet.Core`) says that the account
proved itself on that device: network ID, host ID, account ID, device ID, the
login (`kind`, `provider`, `subject`), the issue and expiry times, and a
signature. The host signs it with its **TLS key**, the key whose SPKI
fingerprint the network roster pins for that host, so no new key is handed
out: ECDSA P-256 (`ES256`, every Martlet host) or RSA-PSS (`PS256`). It also
carries the public key; a checker hashes it and compares the hash with the
pin. Any member desktop or host checks it with `AccountAttestation.Check`
against the roster it already accepted. The check refuses another network, a
host that isn't an active member, a key that isn't the pin, a changed field, a
statement more than five minutes in the checker's future and an expired one.
The signed bytes start with `martlet-account-attestation-v1`, so they can't be
taken for a TLS signature. `ToText()` gives one line of base64url (at most
4,096 characters) for the account directory. A host in no network attests
nothing (`signin.no_network`); an identity with no account gets
`signin.no_account`.

## Sharing a host with friends

A friend with their own Martlet can use one of your hosts for its thinking,
listening, speaking, lip-sync and reading. The friend signs in with their own account
(Google or another OpenID Connect provider, Discord or Steam). They never join
your Martlet network. They never see your settings, memories, voices, logs, API
keys or other computers. Sharing is per host: a friend uses only the hosts that
allow them.

| You do | Where |
| --- | --- |
| Set up a sign-in provider on each host you share (one your friend has an account with) | At home: **Devices › Add a computer** with the host selected › **Sign-in from outside** › *Sign-in providers* ([above](#signing-in-with-your-identity-provider-or-google)) |
| Give your friend that host's invite and a way to reach it | **Sign-in from outside** › **Make invite**, and an [outside address](#reaching-your-network-from-outside-home). An overlay network is simplest: Tailscale, for example, can share one machine with another person's tailnet |
| Your friend signs in once. The host refuses them until you share it with them, and lists them | On your friend's Martlet: **Devices › Add a computer › Join with an invite**: paste the invite, **Connect**, choose the provider and **Sign in** |
| Share the host with them | **Devices › Friends** lists them as asking: **Share** *host*. Or **Sign-in from outside** › **Allow the newest as a friend**, or type their provider ID and subject under *Allowed sign-ins* and choose *A friend*. On a Linux host: `martlet-host owner-signin-allow --config ... --provider <id> --subject <subject> --label <name> --access friend` |
| Your friend signs in again | Their Martlet keeps the host under **Devices › Hosts shared with this PC** ([below](#on-your-friends-martlet)) |
| See who uses what | **Devices › Friends**: each person, the hosts shared with them and their computers there. *Your Martlet network* and the Devices map show their computers as a friend's computer |
| Stop sharing | **Devices › Friends** › **Stop sharing** *host*, or **Remove** in **Sign-in from outside** (`martlet-host owner-signin-disallow`): the host revokes their computer at once |
| Make an identity one of your own computers instead, or the other way round | **Sign-in from outside** › **Make one of my computers** or **Make a friend**. The host revokes the computers it signed in with the old access, and they sign in again to get the new one. Your own computers among them also leave your network on your computers' next sync, so they lose every host |

What a friend's computer gets on a shared host:

- **The engines only.** Its sign-in gives it a friend's credential
  (`GatewayAccess.Friend`, kept with the credential, so it stays a friend's even
  if `signin.json` is lost). The host lets it read the version, the capabilities
  and the status, use the engines that serve only the request that asks
  (thinking, listening, speaking, lip-sync and reading), and cancel its own
  requests. Pictures and singing keep the whole host's queues, results and
  models, so they stay yours: the host leaves them out of a friend's
  capabilities and refuses them. Every other route refuses a friend too
  (`access.friend`) before anything is read: pairing, the network, the hardware
  report, who does what, voices, speaking voices, characters, creations, Home
  Assistant, settings, memories, API keys, commands, logs, GPU priority, the
  security audit and the sign-in settings. These refusals never lock a friend
  out.
- **A few computers each.** A friend keeps at most three computers on a host.
  Signing in on a fourth replaces (revokes) their oldest. All friends together
  keep at most 32 computers on a host; then a new one is refused
  (`signin.friends_full`). So friends can never fill the room the host keeps
  for your own computers' pairings.
- **Never in your network.** The host never attests a friend's computer, and a
  friend can't ask to join. A friend can't sign in under another computer's ID
  (`signin.device_taken`).
- **Your requests first.** A friend's request ranks below all of your work.
  Any of your requests that needs the same graphics card stops it
  (`job.preempted`), and while your work uses the card the host turns the friend
  away (`job.busy`, detail `owner`). When your request needs the same engine, the
  friend's request stops and yours takes its place within moments. So sharing a
  host never makes your own replies wait for a friend.
- **Voices stay apart.** A friend's speaking requests carry the friend's own
  recordings. They never reach your speaking voices, and the host keeps nothing
  a friend sends.
- **You can see them.** The host's answer to your computers marks a friend's
  computer (`access` `friend`), the sign-in settings list the friend's computers,
  and the host log names their requests (*request from friend
  &lt;device&gt;*). In the app, *Your Martlet network* and the Devices map show
  them as a friend's computer (never as one of yours, and never as a computer
  to let in), and **Devices › Friends** lists each person.
- **Taking access away.** Removing the friend's identity revokes their computer
  on that host at once (from a member desktop) or within 5 seconds
  (`martlet-host` editing `signin.json` while the host runs). A running request
  of theirs stops too. Nothing is recorded for your network: they never joined it.

**Devices › Friends** reads the sign-in settings of each of your hosts (only
while the Devices page shows, at most every two minutes, or on **Check now**;
never on a reply's path, so it adds no conversation latency). It lists each
person who is a friend on one of your hosts or who signed in to one and waits:
the hosts shared with them, their computers there, and **Share** or **Stop
sharing** for each of your hosts where their provider is set up. It works the
same for every host you manage from the app: this PC's own host service, a
Linux host and a Windows host. An identity that is one of your own computers'
sign-ins somewhere is not listed; change it in that host's **Sign-in from
outside**. A host that runs a Martlet older than friend sharing can't share:
the app says to update it first. Allowing one of your own computers there works
as before.

### On your friend's Martlet

When the host's sign-in answer says `friend`, your friend's Martlet keeps the
pairing as a host shared with that PC (`access` `friend` with the pairing in
`hosts.json`). It never adopts it into its own Martlet network and never asks
to join through it.

- **Devices › Hosts shared with this PC** lists each one: who they signed in
  as, the engines it offers that PC (its capabilities, read on start, when the
  page shows and on **Check now**), and what that PC uses it for. **Use for
  thinking**, **listening**, **speaking**, **lip-sync** or **reading** hand that
  job to it; the job choices on the Devices page and the Companion pages list
  it too, as *shared by a friend*. **Forget** gives the jobs back to what they
  used before and removes the pairing.
- **This PC only.** A job on a shared host is that computer's own choice: it is
  never recorded in your friend's shared plan (*who does what*), never followed
  by their other computers, and never moved by the plan. Leaving it, the
  computer follows the plan again. A job that still names a shared host after
  it was forgotten never goes into the plan either.
- **Never mixed with their own hosts.** A shared host and one of your friend's
  own hosts never replace each other under the same name. **Join with an
  invite** refuses a friend's host named like one of theirs. Their network
  doesn't pair one of their own hosts named like a shared host (the log says
  so) until they forget the shared one. Even with the same home address (two
  homes can use the same private address), each is reached on its own route.
- **Engines only.** Nothing between your friend's own computers talks to a
  shared host: not the network sync, the plan, shared settings, memories,
  voices, speaking voices, characters, creations, Home Assistant, API keys,
  logs, commands, the security audit, live GPU holds, or hardware and update
  reads. So normal use never meets `access.friend`. Pictures and singing never
  use a shared host.
- **Your work first.** When the host turns your friend's request away for your
  work (`job.busy` with detail `owner`, or `job.preempted`), their Martlet tries
  another of their own computers that runs the engine at once, or tries the host
  again on the next request; background work goes on later. It never waits in
  line for your host. The first time, it says so in plain words.
- **Voices stay theirs.** Speaking on a shared host sends the voice's recording
  with each sentence from the first one (the host keeps nothing).
- **Access taken away.** When you stop sharing, the host answers
  `auth.revoked`, and the card says that its owner stopped sharing it.

## Where it lives

| Piece | Location |
| --- | --- |
| Roster format and rules | `Martlet.Core.Network` (`NetworkRoster`, `NetworkKey`, `NetworkPairing`) |
| Host side | `Martlet.Gateway` `GatewayNetwork.cs`: `GET`/`POST /martlet/v1/network` (roster, join requests, the computers paired with the host, the Martlet release it runs), `/network/join`, `/network/deny`, `/pair/member` ([gateway contract](../src/Martlet.Gateway/README.md#martlet-network-member-pairing)); `network.json` on Linux hosts ([Linux gateway](../src/Martlet.Gateway.Host.Linux/README.md)) |
| Desktop sync | `Martlet.Avatar.Audio2Face` `Remote/NetworkSync.cs` (`NetworkSyncEngine`, `NetworkLocalState`; `ReadOnlyAsync` for a host PC that only watches), `Remote/HostNetwork.cs` (client calls, pairing by itself) and `Remote/HostRelease.cs` (what an announced release means to this PC) |
| Desktop UI | `MainWindow.Network.cs`, the **Your Martlet network** card on the Devices page; `NetworkMap.cs` draws the network's computers on the Devices map; `MainWindow.HostReleases.cs` takes the releases hosts announce; the host PC's Home steps in `MainWindow.Shell.cs`; `NetworkIdentity.cs` for the key and `network.json` |
| Diagnostics | The desktop log records the network as this PC sees it whenever it changes (membership, requests to join, who each host is paired with) and each host's note (`Martlet network: ...` lines on the Diagnostics page or MCP `logs_tail`) |
| MCP | `network_status` (this PC's network from a data directory), `network_selftest` (end-to-end rehearsal on loopback, `Martlet.NodeLinkCheck network`) and `exposure_selftest` (the gateway's guard for a host reachable from outside home, `Martlet.NodeLinkCheck exposure`); card IDs in [MCP](MCP.md) |
| Sign-in | `Martlet.Gateway` `GatewaySignIn.cs` (`GatewaySignInService`, settings rules, `signin.json`, household account logins), `GatewayAccounts.cs` (password verifier, recovery codes), `GatewaySignInHttp.cs` (`/martlet/v1/signin`, `/begin`, `/complete`, `/prove`, `/settings`); `Martlet.Core` `Access/Totp.cs`, `Accounts/AccountAttestation.cs` and `Network/NetworkInvite.cs`; desktop `Remote/HostSignIn.cs` (`ProveAsync`, `ProveWithPasswordAsync`, `ProveInBrowserAsync`), `SignInJoinWindow`, `SignInSettingsWindow`; Linux `HostSignIn.cs` (`owner-signin-*`, `owner-invite`); MCP `signin_selftest`, `signin_lab` mode `account` |
| Friends | `GatewayAccess` (`GatewayContracts.cs`), kept with each credential (`GatewayPairing.cs`, `StoredGatewayCredential.Access`); deny by default in `GatewayAuthentication.cs` (`GatewayRequestAuthenticator`) with `GatewayApiAccess.Friends` per route (`GatewayApiKeys.cs`); `GatewaySignInService.FriendAllowed`; owner first in `GatewayInferencePriority.cs` and `GatewayInferenceRegistry.cs` (`BeginAsync`). Desktop: `HostSignInAccess` (`AllowChange`) and `access` on sign-in answers, settings and network devices (`Remote/HostSignIn.cs`, `Remote/HostNetwork.cs`); `HostRoutes.KeepApart` (a shared host's routes apart from your own host at the same home address); `SignInSettingsWindow` (access per identity, switch, remove); `MainWindow.Friends.cs` and `Friends.cs` (`FriendsOverview`: Devices › Friends and Hosts shared with this PC); `PairedHost.Access` (`HostControl.cs`); `ClusterSync.Local` (`LocalJob.Shared`) and `ClusterSync.Recordable`; `WorkSharingRoster.Classify` and `WorkRefusal.Owner` (owner first); MCP `network_status` (`pairedHosts[].access`, `friends`) and `signin_lab` (`mode` `owner` or `friend`) |
| Outside home | `Martlet.Gateway` `GatewayGuard.cs` (guard, exposure choices, audit) and `GatewaySecurityAudit.cs` (`GET /martlet/v1/security/audit`); desktop `Remote/HostSecurity.cs` (`ReadSecurityAuditAsync`), `Remote/HostRoutes.cs` (which address a connection dials) and the **Outside addresses** button (`MainWindow.Network.cs`); `NetworkMember.Addresses` in `Martlet.Core`; Linux `HostExposure.cs` (`martlet-host owner-exposure`, `exposure.json`) |

Apps and scripts outside the network (Home Assistant, your own scripts) don't
join it: they use [API keys](API.md), which belong to the network too. A key
made or revoked on any member PC reaches every host the same way as the who
does what plan, and grants nothing in the network itself.

## Qualification

Joining from outside home by signing in is checked through Martlet MCP's
`signin_selftest` (a real gateway on 127.0.0.1: owner account with a real
authenticator secret, invite pinned by an outside name, refusals, sign-in,
joining on the host's attestation without a check number, an OpenID Connect
provider with an issuer in the process and a simulated browser through the
desktop's real loopback redirect, a Steam assertion confirmed by the host,
removing an identity removing its tablet from the network so a second host
revokes it too, revocation, audit, and a host shared with a friend: the
friend's computer signs in with a friend's credential, lists the engines, is
refused on every other route (`access.friend`) and never joins, a sign-in under
the home PC's ID is refused (`signin.device_taken`) and stopping sharing revokes
the friend at once), the gateway, Core, desktop and Linux
gateway unit tests (RFC 6238 vectors, lockout, allow list, removal records,
readiness, outside addresses kept with pairings, `martlet-host owner-signin-*`
and `owner-invite` on a fixture file system, friend access on every route
(pictures and singing refused and left out of a friend's capabilities), at most
three computers per friend and 32 for all friends,
device ID takeover, access changes, a friend's access kept across a restart of
the protected store, and the owner first on a fixture Ollama: the owner's reply
stops a friend's request on the same worker and takes its slot, a friend is
turned away while the owner uses the card, and the owner's pool work stops a
friend's reply), the real `martlet-host` process
in Docker on this PC (owner account set up over stdin, status, invite,
`signin.json` 0600, sign-in over pinned TLS against the approved service), and
the desktop itself through MCP `signin_lab` on a disposable data folder: the
desktop pairs with a live lab host (secret in the lab credential folder),
founds the network, lets the signed-in laptop in on the host's attestation,
signs the host's outside address into the roster and keeps it with its pairing,
shows the live host in **Sign-in from outside**, and **Remove and remove its
computers from the network** makes it remove the laptop, which loses the host.
Sharing a host with friends on the desktop side is checked by `signin_selftest`
(the desktop's client reads `access` back from the sign-in answer, the sign-in
settings and the host's network answer, and a friend change that the host
can't read is reported as *update it first*, `signin.friends_unsupported`), the
desktop, Core and Audio2Face unit tests (`SharedHostsTests`: a shared pairing
kept with its access, never one of your hosts on the map or in the plan (not
even after it is forgotten), a friend's computer shown as one, a shared host
and your own host with the same home address reached apart, a shared host and
your own pairing never replacing each other under one name, the owner-first
refusal moving a request on, Devices › Friends' per-person overview;
`WorkSharingTests`: a live request passes a shared host whose owner needs it at
once, and background work goes on later; `HostSignInSettingsTests`: a member
allow sends no `access`, so older hosts take it), and the
running desktop against a live lab host through MCP `signin_lab`, headless:
with `mode` `friend` and `signInDesktop`, a desktop holding a shared pairing
read only the host's engines (*it offers this PC: Thinking*) and, over 80
seconds of its background loops, the host refused it nothing
(`accessFriendRefusals` 0), it never asked to join and it made no network
key; the same build with the pairing's `access` removed (as before this
change) was refused 32 times in 50 seconds (`access.friend`). With `mode`
`owner` and `shareWithFriend`, the owner's desktop founded its network, let
its laptop in, named the friend's computer *a friend's computer* in its network
log, and never let it in.
**NOT RUN:** a laptop signing in over the real internet, Windows Credential
Manager itself in that flow (the lab folder stands in), a native (non-Docker)
Linux host or the packaged `martlet-host` wrapper, signing in at a real
Authentik, Authelia, Keycloak, Pocket ID, Google, Discord or Steam (no
disposable accounts on the development PC), `martlet-host owner-signin-allow
--access friend` in a real `martlet-host` process (the fixture file system
stands in), and driving the new windows and cards through UI Automation
(**Devices › Friends**, **Sign-in from outside**'s access buttons, **Join with
an invite** as a friend, **Hosts shared with this PC** and a reply through a
shared host from the talk window): the development PC's Windows session was
locked, so `ui_connect` couldn't see the desktop window.

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

Reaching the network from outside home is checked on real sockets with MCP
`outside_path_check`: this checkout's Linux gateway (`owner-init`,
`owner-exposure`, `owner-pair`, `serve`) in disposable containers on a Docker
network numbered from TEST-NET-3, its port published on 127.0.0.1, so it sees
every connection coming from 203.0.113.1 (outside). The desktop's real pairing
client, connection, network sync and route fallback reach it with its home
address dead: a typed code is refused from outside and a device card pairs, the
roster signs the address the host advertises, home fails and the outside
address answers (the next connection goes straight there), a stranger is locked
out on the sixth request and the audit and host log name 203.0.113.1;
`owner-exposure` refuses an outside address before sign-in (exit 5) and accepts
it once `owner-signin-owner` set an owner account with an authenticator; and with
`signin.json` removed from the running host, the paired desktop and a stranger
get `outside.paused` while sign-in's settings still answer the owner. Also
`exposure_selftest` (loopback, every guard rule), `host_engine_check`'s
exposure steps (the engine) and `node_link_check`'s `exposure-command` (the
`host.exposure` command through a real gateway and the desktop's agent loop).
**NOT RUN:** a real router port forward, overlay network or internet path, and
saving Outside access against a live host service (it restarts that service).
