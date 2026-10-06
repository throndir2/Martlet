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
- **Joining by signing in.** A computer away from home pairs with a host by
  signing in (below) instead of a code shown on the host. The host issues the
  same `voice` credential pairing does and remembers which identity enrolled
  that device; its answer to members (`GET /martlet/v1/network`) marks that
  device's join request with the identity (`sign_in`: provider, subject,
  label, when). A member desktop lets such a request in by itself
  (`NetworkSyncEngine.ApproveSignedIn`) when the attesting host is an active
  host of its roster: the owner set the sign-in up at home (the owner account
  with its authenticator, or an identity the owner allowed on that host), so
  that sign-in is the owner's approval, as an Allow would be. Only member
  desktops still sign roster entries; the host only attests. Requests without
  an attestation, or through a host outside the roster, still need the check
  number. The attestation lasts while the device is paired with that host and
  its identity is still allowed there.
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
  that signed in through it, and only those whose sign-in the owner removed.
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
configured provider with at least one allowed identity. Sign-in owns that rule
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
  gets `signin.not_allowed`. Removing an identity, a provider or the owner
  account revokes the computers it enrolled on that host and removes them from
  the network (see *Removing a sign-in removes its computers* in the trust
  model), so a laptop that joined loses every host, not just that one.
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
   come with a client secret.
2. At home: **Sign-in from outside** › *Sign-in providers*: choose *OpenID
   Connect* (an ID such as `authentik`, a name, the issuer URL such as
   `https://auth.example.net/application/o/martlet/`) or *Google* (filled in:
   `https://accounts.google.com`), the client ID and secret, **Save provider**.
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

Limits, for now: sign-in is per host (set it up on the host the laptop reaches
from outside); the laptop reaches the network's other hosts only where they
have outside addresses ([above](#reaching-your-network-from-outside-home)).

## Where it lives

| Piece | Location |
| --- | --- |
| Roster format and rules | `Martlet.Core.Network` (`NetworkRoster`, `NetworkKey`, `NetworkPairing`) |
| Host side | `Martlet.Gateway` `GatewayNetwork.cs`: `GET`/`POST /martlet/v1/network` (roster, join requests, the computers paired with the host, the Martlet release it runs), `/network/join`, `/network/deny`, `/pair/member` ([gateway contract](../src/Martlet.Gateway/README.md#martlet-network-member-pairing)); `network.json` on Linux hosts ([Linux gateway](../src/Martlet.Gateway.Host.Linux/README.md)) |
| Desktop sync | `Martlet.Avatar.Audio2Face` `Remote/NetworkSync.cs` (`NetworkSyncEngine`, `NetworkLocalState`; `ReadOnlyAsync` for a host PC that only watches), `Remote/HostNetwork.cs` (client calls, pairing by itself) and `Remote/HostRelease.cs` (what an announced release means to this PC) |
| Desktop UI | `MainWindow.Network.cs`, the **Your Martlet network** card on the Devices page; `NetworkMap.cs` draws the network's computers on the Devices map; `MainWindow.HostReleases.cs` takes the releases hosts announce; the host PC's Home steps in `MainWindow.Shell.cs`; `NetworkIdentity.cs` for the key and `network.json` |
| Diagnostics | The desktop log records the network as this PC sees it whenever it changes (membership, requests to join, who each host is paired with) and each host's note (`Martlet network: ...` lines on the Diagnostics page or MCP `logs_tail`) |
| MCP | `network_status` (this PC's network from a data directory), `network_selftest` (end-to-end rehearsal on loopback, `Martlet.NodeLinkCheck network`) and `exposure_selftest` (the gateway's guard for a host reachable from outside home, `Martlet.NodeLinkCheck exposure`); card IDs in [MCP](MCP.md) |
| Sign-in | `Martlet.Gateway` `GatewaySignIn.cs` (`GatewaySignInService`, settings rules, `signin.json`), `GatewayAccounts.cs` (password verifier, recovery codes), `GatewaySignInHttp.cs` (`/martlet/v1/signin`, `/begin`, `/complete`, `/settings`); `Martlet.Core` `Access/Totp.cs` and `Network/NetworkInvite.cs`; desktop `Remote/HostSignIn.cs`, `SignInJoinWindow`, `SignInSettingsWindow`; Linux `HostSignIn.cs` (`owner-signin-*`, `owner-invite`); MCP `signin_selftest` |
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
revokes it too, revocation, audit), the gateway, Core, desktop and Linux
gateway unit tests (RFC 6238 vectors, lockout, allow list, removal records,
readiness, outside addresses kept with pairings, `martlet-host owner-signin-*`
and `owner-invite` on a fixture file system), the real `martlet-host` process
in Docker on this PC (owner account set up over stdin, status, invite,
`signin.json` 0600, sign-in over pinned TLS against the approved service), and
the desktop itself through MCP `signin_lab` on a disposable data folder: the
desktop pairs with a live lab host (secret in the lab credential folder),
founds the network, lets the signed-in laptop in on the host's attestation,
signs the host's outside address into the roster and keeps it with its pairing,
shows the live host in **Sign-in from outside**, and **Remove and remove its
computers from the network** makes it remove the laptop, which loses the host.
**NOT RUN:** a laptop signing in over the real internet, Windows Credential
Manager itself in that flow (the lab folder stands in), a native (non-Docker)
Linux host or the packaged `martlet-host` wrapper, and signing in at a real
Authentik, Authelia, Keycloak, Pocket ID, Google, Discord or Steam (no
disposable accounts on the development PC).

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
