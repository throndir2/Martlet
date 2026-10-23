# MCP: Computers, network and accounts

Part of [MCP in Martlet](../MCP.md): the tools and screens of the *Computers, network and accounts* area of `martlet_guide` (your computers, hosts, the network, accounts, sign-in and sharing). The [index](../MCP.md#pages) lists the other pages.

## Local MCP control (Windows)

`cluster_status` reads [shared who does what](../CLUSTER.md) from a data directory
(optional absolute `dataDirectory`, default the current user's): `sync` is
`on (default)` when `cluster-sync.txt` is missing, `on`, or `off` once the owner
unticked **Keep Martlet the same on all my computers**; `plan` is this PC's `cluster.json`
(`state` `none`, `loaded` or `unreadable`; when loaded its revision, each job's
`host` (null for this PC's own choice), `off`, `failover`, `movedFrom`,
`updatedBy` and `updatedAt`, and each host's ID, roles and `removed`). It never
returns host addresses or keys and contacts nothing.

`accounts_sync_selftest` (no arguments) rehearses the household's
[account directory](../ACCOUNTS.md#account-directory) end to end with the
production code (`src\Martlet.NodeLinkCheck`, mode `accounts`,
`AccountRehearsal.cs`; returns `{exitCode, report}`): three real gateways
(`lab-accounts-1`, `lab-accounts-2`, `lab-accounts-3`; Kestrel, pinned TLS, signed requests, an
in-memory `network.json` and `accounts.json`) bound to one lab network, two
member desktops (`lab-desktop-a`, `lab-desktop-b`) and one outside computer
(`lab-intruder`) with real network keys and the desktop's paired client
(`HostAccounts.cs`). Its steps: A founds the network and binds both hosts; A
adds the owner account (ID from `OwnerAccount.IdFor`, created by the founder,
its Windows login signed in on A) and both hosts keep it, signed by A, with
digests that match A's copy; B checks every entry against its roster and takes
it; B adds a person through one host only and every copy ends the same; offline
renames on A and B (the later wins, the creator stays); an offline edit can't
undo a removal; after B binds itself to the owner account and A writes it
last, a third host (`lab-accounts-3`) that never saw it takes it with both
bindings (`rejected` 0); the outside computer's own entry and a forged change of the
owner account are refused (`rejected` 2, the host copy unchanged); a restarted
host serves the same directory from its saved `accounts.json`; and an unsigned
request is refused (HTTP 401). Synthetic accounts, loopback only. A friend's
credential is covered by `Martlet.Gateway.Tests` (`AccountDirectoryRouteTests`).

`account_security_status` reads [account security](../ACCOUNTS.md#account-security)
from a data directory (optional absolute `dataDirectory`), read-only and
without the desktop: `device` (this data folder's device ID), `network`
(whether `network.json` has a roster), `directory` (`none`, `read` or
`unreadable`), per account in `accounts.json` its `id`, `name`, `role`,
`removed`, `mergedInto`, `createdBy`, `logins` (how many of each kind),
`windowsLoginOfThisPc`, `emailHints` (a count) and `devices` (`deviceId`,
`thisPc`, the `login` kind, whether it carries an `attestation`, and its
`proof` under the binding rule: `creator`, `attestation`, `migration`,
`same-name`, `merged`, `none` when a host or desktop would refuse it, or
`no_network`);
`signedInHere`; and `locks`, per account with an unlock file in
`account-locks` (this PC only): `methods` (`pin`, `password`, `hello`),
`askOnThisPc`, `remember`, `encrypt`, `damaged` (the unlock file is there but
can't be read, so the account fails closed and needs a sign-in),
`encryptedFiles` and `plainFiles` in
the account's folder, `failures`, `waitUntil` and whether the DPAPI-protected
part exists. Never a PIN, password, verifier, attestation, e-mail, login user
name or SID.

The account windows ([account security](../ACCOUNTS.md#account-security)):
**Account…** (`AccountOpen`) and the page's **Close** (`AccountClose`), the
Unlock window's **Choose another account** (`UnlockOther`) and the Prove
window's **Close** (`ProveClose`) are passive clicks. Everything else on them
changes or proves something, so it needs `--allow-ui-effects`: the password
(`AccountPasswordUser`, `AccountPassword`, `AccountPasswordRepeat`,
`AccountAuthenticator`, `AccountAuthenticatorCode`, `AccountPasswordSave`,
`AccountAuthenticatorRemove`), `AccountLinkWindows`, `AccountUnlinkWindows`,
`AccountAskPassword`, `AccountPin`, `AccountPinSet`, `AccountPinRemove`,
`AccountHello`, `AccountEncrypt`, `AccountRemember`, `AccountLockNow`,
`AccountSignOut`, `AccountMergeChoice`, `AccountMerge`, and (W13) each
household provider's **Link** (`AccountLinkProvider-<provider ID>`) and each
linked login's **Unlink** (`AccountUnlinkLogin-<provider>-<subject>`); Unlock's `UnlockPin`,
`UnlockPassword`, `UnlockSubmit`, `UnlockHello`, `UnlockSignIn`; Prove's
`ProveHost`, `ProveMethod-martlet`, each household provider
(`ProveMethod-<provider ID>`, signed in in the browser), `ProveUser`, `ProvePassword`, `ProveCode`,
`ProveRemember`, `ProveSubmit`; the account menu's `AccountSignInOther`
and `AccountLock`; *Continue as ...?* (`ContinueAsDialog`: `ContinueAsYes`,
`ContinueAsNo`) and *Choose an account* (`AccountChooseDialog`:
`UnlockChoose-<32 hex>`, `UnlockChooseSignIn`). `ui_connect` also attaches
while one of these windows shows at start, before the main window (Unlock,
*Continue as*, *Choose an account*, Prove). `ui_snapshot` returns these values: `AccountName`,
`AccountLogins`, `AccountLockState`, `AccountUnlockMethods`,
`AccountPasswordState`, `AccountWindowsState`, `AccountHelloState`,
`AccountEncryptState`, `AccountStatus`, `AccountProviderLogins` (the linked
provider logins, *Linked: Authentik (lab) as me@example.net.*, and the result
of the last Link or Unlink), `UnlockAccountName`, `UnlockStatus`,
`ProveTitle` and `ProveStatus`; never the user name, the authenticator key
(`AccountAuthenticatorSecret`) or the recovery codes (`AccountRecoveryCodes`).
The help marks (passive clicks) are `Help-AccountProveHow` (Prove),
`Help-AccountProviderSignIns`, `Help-AccountVRoidHub`, `Help-AccountHello`,
`Help-AccountEncrypt` and `Help-AccountMerge` (the Account page) and
`Help-AddPersonHow` (*Add a person*).
`network_status` reads the [Martlet network](../NETWORK.md) from a data directory
(optional absolute `dataDirectory`, default the current user's): `state`
(`none`, `member`, `waiting` or `unreadable`), `key` (whether
`network\device_ecdsa` exists), `networkId`, the roster's `revision` and
`founder`, `waiting` (`hostId`, `checkNumber`, `since`) while this PC asks to
join, each `desktops` and `hosts` entry (`id`, `name`, `removed`, `updatedBy`,
`changedAt`; hosts also `outsideAddresses`, how many outside addresses the roster
lists), `adopt` (hosts paired here on purpose, added to the network on
the next sync), `ignored` (network hosts forgotten here) and `removedFrom`;
`pairedHosts`, each pairing in `hosts.json` with the name its computer goes by
(`computerName`: the name the host reported in `host-hardware.json`, with
`computerNameFrom` `report`, else the name its host ID was made from,
`host-id`: *Nahida-host* gives *Nahida*), how many outside addresses are
kept with it and its `access` (`member` for your own hosts, `friend` for a host
a friend shares with this PC: its engines only, never in this PC's network),
and its `recognition`, what this PC saved to recognize the host's computer
after an OS reinstall ([NETWORK](../NETWORK.md#when-a-host-is-reinstalled)):
`tpmKeys` (the kinds, `tpm-endorsement` and `tpm-owner`, never the keys),
`noTpmKey` (why the host has none), `hardwareFacts` (how many fact hashes),
`roles`, `keysCheckedAt`, `factsCheckedAt`, `lastReinstall` and
`lastReinstallAt` (null before the first check);
and `friends`, what **Share › Friends** last read from each of your hosts
(`friends.json`: `checkedAt`, and per host `hostId`, `read`, `problem`, each
friend's `label`, `provider` and `computers`, and how many are `asking`), or
null before it read anything. `householdSignIn` is the household's sign-in
providers as the desktop last read them from its hosts
(`household-signin.json`: `kept`, the providers this PC set up with their
configuration and whether it keeps the client secret, never the secret;
`checkedAt`; `providers`, each with `id`, `kind`, `name`, the hosts it is `on`,
`missing`, with `different` settings or `withoutSecret`, and `keptHere`; and
`problems` per host), or null before it read anything. `shareLinks` is
`share-links.json` in short ([sharing with a link](../NETWORK.md#sharing-with-a-link)):
`made`, each link this PC made with its friend's `label`, `allHosts` and
`hosts` and `createdAt`; `accepted`, each link a friend sent that this PC
accepted with `from`, `label`, the `hosts` it signed in to and the hosts you
`forgotten`; `protocolRegistered` (whether `martlet://` links open Martlet for
this Windows user) and `pendingHandOver` (a clicked link another start left for
the running Martlet). Never a link ID or secret. `routerPort` is the port
Martlet opened on the router for this PC's host service
([opening a port on your router](../NETWORK.md#opening-a-port-on-your-router),
`router-port.json`: `hostId`, `router`, `externalPort`, `internalPort`,
`openedAt`, `renewedAt`, `problem` and `answeredFromInside`; never an address),
or null. `device` is this data folder's device ID (`id`
and `source`): `saved` in `device.json` (one per Windows user, so two Windows
users on one PC never share one); `pairings` or `legacy` when the desktop hasn't
saved one yet and keeps the ID its pairings use or an older Martlet's
`desktop-<pc name>`; or `id` null with `new` when it picks a new
`desktop-<pc name>-<6 of [a-z0-9]>` on its next start; and `computerName`, the
name this computer gives itself and shows to your other computers (its host
name in the case its owner set, such as *Miko*, not the capitalized NetBIOS
name *MIKO*). `windowsLogin` is the
Windows login the server runs under: `kind` (`microsoft`, `work` or `local`),
`hasEmailHint` and `hasSid`, never the e-mail, the name or the SID. It never
returns keys, signatures or host addresses and contacts nothing.

`accounts_status` reads who uses Martlet on a desktop's data directory
([accounts](../ACCOUNTS.md#desktop-account-session); optional absolute
`dataDirectory`, default the current user's): `device` (this device's ID),
`session` (`none`, `loaded` or `unreadable`: `accounts\session.json`),
`windowsLogin` (`windows`, or `fixture` with `MARTLET_SIMULATE_WINDOWS_LOGIN`),
`current` and each `signedIn` account (`id` as 32 hex digits, `name`, `role`,
`pending` while the household's directory doesn't have it, `removed`,
`current`, `folder`: whether `accounts\<id>` exists), how many are `pending`,
the household's `owner`, `householdOwner` (the household's owner account
that every desktop can bind to: the network's own owner account or the
founder's), `ownerJoin` (`null`, or `{owner, own, ownWritten}` while this PC,
let in with the owner's approval, still has to sign in to the household's
owner account and fold the account it made before it joined), `samePerson`
(`null`, or `{keep, own, ownWritten}` while an account this PC made with its
Windows login has the same name as another account of the household, `keep`,
and still has to fold into it: Windows logins with the same name are one
person), and this PC's
copy of the account directory:
`directory` (`none`, `loaded` or `unreadable`: `accounts.json`), how many
`accounts`, `removed` and `owners`, its `revision` and each of its `entries`
(`id`, `name`, `role`, `removed`, the `logins` kinds, how many `devices` it is
signed in on, `thisDevice`, `createdBy`, `updatedBy`, `changedAt`). It never
returns a SID, e-mail, e-mail hint, signature or attestation and contacts
nothing. In the desktop, the account button at the bottom of the navigation
rail is `AccountButton` (a safe click; its value reads *Account: Sam, Owner, 2
people on this PC*) with `AccountCurrent` (the name in use). It opens the
`AccountMenu` picker: `AccountSwitch-<32 hex>` for each account signed in on
this PC (value *Sam (in use)* or *Alex*; clicking one switches, ends the
conversation and saves `accounts\session.json`, so it needs
`--allow-ui-effects`), `AccountMenuNote` (why switching waits, such as a reply)
and `AccountAddPerson` (a safe click that opens `AddPersonDialog`: the
`AddPersonName` box, `AddPersonAdd`, which makes the account and switches to it
and needs `--allow-ui-effects`, `AddPersonCancel`, a safe click, and
`AddPersonProblem`, such as *Sam already uses this PC. Choose another name.*).
**Devices › Settings for all devices** shows the directory sync as
`AccountsSyncStatus` (*Accounts: 3 people in your household, the same on 2 of
2 hosts; checked 2:14 PM.*, or *Accounts: 2 people on this PC, shared with your
other computers once this PC is in a Martlet network.*). Launch the desktop with
`MARTLET_SIMULATE_WINDOWS_LOGIN` set to a display name so a disposable data
directory gets a fixture SID and that name instead of the real Windows login.

`network_selftest` (no arguments) rehearses the network end to end with the
production code: three real gateways (`lab-host-1..3`: Kestrel, pinned TLS,
volatile credentials, a throwaway certificate) on `127.0.0.1`, simulated
desktops driving the desktop's own client (`HostNetwork.cs`) and sync engine
(`NetworkSync.cs`), and a simulated host PC. Like `node_link_check` it runs `src\Martlet.NodeLinkCheck`
(mode `network`, `NetworkRehearsal.cs`) as its own process, because the gateway
needs the ASP.NET Core runtime, and returns `{exitCode, report}`. Its steps: desktop A pairs with a host by a typed code and
founds a network that binds it; A adds a second host to the same network; B
pairs with one host and asks to join with a check number; A sees the same
number; the host tells B (not yet a member) who is paired with it, A and B,
each with when it last made a signed request; A allows B, and B pairs with the
other host by itself; A, treating the second host as its own host service,
lets in D (paired with that host) with no second Allow
(`NetworkSyncEngine.ApproveThrough`) while E, asking through the first host,
still waits for one; every host announces the Martlet release it runs to A, B
and B reading only (the gateway's own release), and a desktop that last saw a
host on 0.0.1 (simulated) takes the announced release as an update that needs
nothing more, while a host still older than the desktop keeps needing one
(`HostRelease`); a host PC C outside the network pairs with the first host
and only watches (`NetworkSyncEngine.ReadOnlyAsync`): it sees A, B and itself
and starts, joins and asks nothing; A sets up a third
host and B is paired with it on its next sync; a key outside the network
(`network.denied`), a member's ID with the wrong key (`pairing.invalid`) and a
roster entry not signed by a member are refused; A removes a host (it stops
trusting the network's desktops, and A and B both forget it, B although the
host no longer answers it); A pairs that host again by a code and it rejoins
(B pairs with it again by itself); on a fourth host (`lab-host-4`) a typed code
still pairs 12 hours later on that host's clock (codes have no deadline), stops
working once used, and five wrong tries close another code so even the right
one gets `pairing.closed`; A removes B (every host revokes it, B leaves
and forgets its hosts and needs a new key). Then a host added where the other
computers can't reach ([NETWORK.md](../NETWORK.md#when-a-host-is-added-where-your-other-computers-cant-reach)),
on three more gateways: MIKO founds a network with `lab-diva-host` and DIVA
joins; MIKO can't reach `lab-diva-host` any more, and LUMINE joins through it
(allowed on DIVA), so MIKO never hears of LUMINE; MIKO adds `lab-nahida-host`
(a NAS) and says plainly who doesn't have it and why (`NetworkReach.Summary`:
*DIVA isn't paired with lab-nahida-host yet, and this PC couldn't reach
lab-diva-host to tell it ...*); LUMINE finds the NAS with a real find
(`HostFindResponder` and `HostFind.FindAsync` on loopback,
`NetworkHostFinder`), brings its roster because the NAS doesn't count it yet,
and pairs; MIKO, still cut off, learns LUMINE through the NAS, DIVA learns the
NAS through `lab-diva-host` and pairs with it, and all three see the same
computers and hosts; a host of another network (`lab-neighbor-host`) is left
alone with how to bring it in (`network-reset`). Each desktop's state goes through
`network.json`'s format between syncs. The report has `ok`, `passed`,
`total`, `seconds`, the `scope` and each step's `ok` and `detail`. Nothing
leaves loopback, nothing is written to disk or Windows Credential Manager, and
it does not cover the desktop window, `network.json` on a Linux host,
`martlet-host`, SSH or a real LAN.

`mac_host_check` (no arguments) checks the Mac host ([macOS](../MACOS.md#host)) as
far as a Windows PC can. In the `mcr.microsoft.com/dotnet/sdk:10.0.401` image
(never pulled; NuGet packages cached in `martlet-outside-check-nuget`) it
publishes this checkout's `Martlet.Gateway.Host.Linux` self-contained for
`osx-arm64` and `osx-x64` (the files the Mac app bundles), runs
`macos-setup help` and checks `macos-status` refuses with exit 4
(`macos.unsupported`) off a Mac. In-process it checks the platform catalog for a
Mac host's machine report: Ollama and whisper allowed, F5 and Audio2Face refused
for want of an NVIDIA GPU, roles not managed from the desktop. It returns
`{passed, exitCode, steps, notRun}`; steps `catalog-mac-host`,
`publish-osx-arm64`, `publish-osx-x64`, `macos-setup-help`,
`macos-commands-refused-off-a-mac`. Running on a Mac (launchd, APFS custody,
Metal, native Ollama and whisper.cpp) is always reported in `notRun`.

`outside_path_check` (no arguments) checks reaching a host from outside home on
real sockets ([NETWORK](../NETWORK.md#reaching-your-network-from-outside-home)). It
builds this checkout's `Martlet.Gateway.Host.Linux` in the
`mcr.microsoft.com/dotnet/sdk:10.0.401` image (NuGet packages cached in the
`martlet-outside-check-nuget` volume) and runs it as uid 1000 in disposable
`mcr.microsoft.com/dotnet/aspnet:10.0.12` containers with `host.json` and its
state on an ext4 named volume. The containers are on a Docker network numbered from TEST-NET-3
(`203.0.113.0/24`) with the gateway port published on `127.0.0.1` only, so the
gateway sees every connection coming from `203.0.113.1`: a real outside source.
The host's home origin, `https://192.168.77.20:9443`, answers nothing. It returns
`{exitCode, report: {passed, total, network, outsideSource, home, published,
steps}, notCovered}`. Steps: `gateway-built`; `config-valid`; `identity-created`
(`owner-init`); `owner-exposure` (an address with a shell character is refused
with exit 2, the published address is saved); `typed-code-refused-from-outside`
(`owner-pair` shows a code, the desktop's code pairing falls back to the outside
address and gets `pair.outside_home`, the code stays open until `cancel`);
`card-pairs-from-outside` (`owner-pair --device-id` and the desktop's real
pairing); `serve-healthy`; `roster-signs-outside-address` (two network syncs
found the network and sign in the address the host advertises);
`exposure-needs-signin-first` (before sign-in `owner-exposure --outside` exits 5
with `outside.needs_signin`; `owner-signin-owner` then sets an owner account
with an authenticator from the code the check computes);
`home-fails-outside-succeeds` (with no outside address the home address times
out; with the roster's, the paired connection reaches the host outside, and the
next connection goes straight there); `guard-locks-out-outside-source` (five
`401 auth.missing`, then `429 auth.throttled` with `Retry-After`);
`audit-names-outside-source` (`internet_reachable` from the roster, failures
from `203.0.113.1 (outside)`, the host log's lockout line);
`outside-paused-without-signin` (`signin.json` removed from the running host: the
paired desktop and a stranger get `outside.paused`, sign-in's settings still
answer the owner with `signin.not_set_up`); and
`probe-tells-home-and-outside-apart`. It never pulls (without Docker or the images
it returns `exitCode` 2 and `notRun`) and removes its containers, volumes and
network. Not covered: a real router, overlay or internet path.

`outside_reachability_check` checks how each host in a data directory's
network can be reached (optional absolute `dataDirectory`; `contactHosts`).
Without `contactHosts: true` it only lists each host's `id` and how many
`outsideAddresses` it has (`checkedNow: false`) and contacts nothing. With it,
it dials each host's home address and each outside address directly, checks the
TLS key against the roster's pin and asks `GET /health/live` (no credential):
per host `home` and `outside` (by `address` number) with `reachable`, `ms` and
`problem` (`refused`, `no answer in time`, `name not found`, `another key`,
or `answered 429 ...` when the key answered but the guard throttled the
address), and `wouldUse` (`home`, `outside <n>` or `none`, home first as the
desktop dials). It never returns the addresses themselves.

`exposure_selftest` (no arguments) rehearses a host reachable from outside
home ([NETWORK](../NETWORK.md#reaching-your-network-from-outside-home)) with the
production code: one real gateway (`lab-exposure`, Kestrel, pinned TLS, a
throwaway certificate) on `127.0.0.1`, a desktop that pairs while at home
through its paired client, then the host told to treat every connection as
outside home, and a stranger's pinned HTTPS client. It runs
`src\Martlet.NodeLinkCheck` (mode `exposure`, `ExposureRehearsal.cs`) and returns
`{exitCode, report}` like `network_selftest`. Its steps: a typed pairing code
used from outside is refused (`pair.outside_home`, also as the desktop's code
pairing sees it) and stays open; a one-use card opened for one named device
pairs from outside without the opt-in; once the owner allows typed codes from
outside without sign-in, outside access pauses (the code and the desktop's audit
read get `outside.paused`, `OutsideAccessBlockedReason` is `signin.not_set_up`),
the desktop sets up an owner account over the sign-in settings route (still
reachable) and the pause ends; then the same code pairs; five failed requests lock the
address out (`auth.throttled`, `Retry-After` 1 s) and the next failure doubles
it, after which the paired desktop's signed requests work again; liveness
answers 120 requests a minute per outside address; and the paired desktop reads
the security audit (`ReadSecurityAuditAsync`: refused, success, failure and
throttled entries with their source, plus `StoresHealthy`, `FailingStores` and
the authority save counts `AuthorityCommits`, `AuthorityCommitAverageMs` and
`AuthorityCommitLongestMs` and the nonce save counts `NonceSaves`,
`NonceSaveAverageMs` and `NonceSaveLongestMs`; the first step fails when a store is not healthy)
and the host log lines naming each source. A host on its real pairing store
(a temporary directory) then pairs a desktop that makes 20 signed audit reads:
the step passes when they took 20 nonce saves (journal appends) and at most
one full save, and reports their times.
Then outside addresses, played by other loopback ports: with the home address
closed the desktop reaches the host at its outside address, pinned to the same
key, and the next connection tries it first; with the home address answering it
is used at once (no wait on an outside one); with nothing answering the error
names every address tried (`host.unreachable`); outside addresses set on the
host itself are signed into the roster by a member desktop's network sync; the
reachability probe behind `outside_reachability_check` tells an answering, a
closed and a wrong-key address apart; and a
different computer with another key at the home address is skipped for the
outside address. When this PC has a Tailscale address (100.64.0.0/10), a last
step starts a second gateway (`lab-overlay`) on this PC's private home address
and pairs a laptop there; nothing answers on this PC's Tailscale address at
the gateway's port; the host gets an outside address (a MagicDNS name it can't
resolve) and listens on its Tailscale address too
(`GatewayOverlayListeners`); a pinned probe through that address is answered
`403` (`outside.paused`: a Tailscale source is outside home and sign-in is off);
the laptop sets up an owner account and the probe answers `200`; the audit's
`outside_listeners` and the host log line `Listening on <address> too (overlay
network) ...` name the address; and once the outside address is removed the
host stops listening there. Without a Tailscale or a private address that step
is skipped and the report's `notRun` says why. Not covered: a real internet
source, a router port forward or another computer on the overlay network.

`host_move_selftest` (no arguments) rehearses a host whose computer got
another address on the home network ([NETWORK](../NETWORK.md#when-a-computer-gets-a-new-address))
with the production code: one real gateway (`lab-moved-host`, Kestrel, pinned
TLS) on `127.0.0.3` whose certificate also names the address it was paired at
(`127.0.0.2`, where nothing answers), its real find answer (`HostFindResponder`
on UDP, bound to `127.0.0.3`) and a desktop's real paired connection
(`HostRoutes`). It runs `src\Martlet.NodeLinkCheck` (mode `move`,
`MoveRehearsal.cs`) and returns `{exitCode, report}` like `network_selftest`.
Its steps: the desktop pairs; the host answers a find with its host ID only
(fields `martlet`, `v`, `q`, `hosts`) and doesn't answer Nearby's `find`; with
the pairing saved at `127.0.0.2` the desktop finds the host at `127.0.0.3`,
connects with the same key and pairing, and raises the move it keeps with the
pairing (`HostRoutes.Moved`); a new connection dials the new address at once;
the pinned-key probe reaches it there; a computer with another key that
answers for the same host ID is never used (`host.unreachable`, nothing
moved); and a host back at its paired address is found there and the other
address forgotten. Finds go to the lab's answer on loopback, never to the home
network. The report's `notRun` names what needs a real network adapter (the
gateway following a real DHCP change).

`host_reinstall_selftest` (no arguments) rehearses a host whose computer was
reinstalled ([NETWORK](../NETWORK.md#when-a-host-is-reinstalled)) with the
production code: real gateways (`lab-reinstalled-host`, Kestrel, TLS) on
`127.0.0.6` with a simulated TPM (`SimulatedTpm`), the desktop's real proof
client (`HostComputerProof`, `GET /martlet/v1/hardware`) and its decision
(`ReinstallCheck`). It runs `src\Martlet.NodeLinkCheck` (mode `reinstall`,
`ReinstallRehearsal.cs`) and returns `{exitCode, report}`. Its steps: the
desktop saves the host's two TPM keys over its pinned TLS key; after the
reinstall (a new TLS key, the same chip) the pinned-key probe says
`another key`; the same chip proves it is the same computer (`Proven`,
verdict `Same`, on any route); another computer's chip is not trusted
(`OtherKeys`); and a host without a TPM (`NoKeys`) is trusted by its hardware
facts only at home or over Tailscale (`Same`), not from outside (`Unsure`),
and never with another board ID (`NotSame`). The last step signs and checks a
proof with this PC's own TPM; a PC without one says why in `notRun`. Not
covered: a real reinstall of a Linux computer and the desktop setting it up
again over SSH.

`hosts_find` (`dataDirectory` optional, `seconds` 0.5 to 5, default 2) asks
this PC's home network where Martlet hosts are, as Martlet does when a paired
host doesn't answer at its saved address: one `find-host` question on UDP
9444 to each private subnet's broadcast address, the limited broadcast and
loopback (never a subnet scan). It returns `port`, `listenedSeconds`, `tookMs`,
`targets` (the addresses it asked) and `answers` (`hostId`, the private
`address` the answer came from and, with `dataDirectory`, `where`: *not paired
here*, *at its paired address*, *at the address saved as where it moved to*,
*on this PC (answered over loopback)* or *somewhere new*), and `pairings`
(`loaded`, `none` or `unreadable`). It sends no keys or credentials and
changes nothing. `network_status`'s `pairedHosts[].movedOnHomeNetwork` says
whether a pairing keeps a new home address (`movedTo` in hosts.json), without
the address.

`signin_selftest` (no arguments) rehearses [joining from outside home by
signing in](../NETWORK.md#joining-from-outside-home-by-signing-in) with the
production code: one real gateway (`lab-signin-host`, Kestrel, pinned TLS, a
throwaway certificate, in-memory `signin.json` and `network.json`) on
`127.0.0.1`, a home PC, a laptop and another PC simulated with the desktop's
sign-in client (`HostSignIn.cs`) and network sync engine. It runs
`src\Martlet.NodeLinkCheck` (mode `signin`, `SignInRehearsal.cs`) and returns
`{exitCode, report}` like `network_selftest`. Its steps: the home PC pairs by
code and founds the network; it sets up the owner account (a wrong
authenticator code is refused, the right one gives ten recovery codes, the
password is kept only as a verifier); a computer outside the network can't
change sign-in (`signin.denied`); the laptop pins the host from an invite whose
outside address is `localhost:<port>` (the certificate names `127.0.0.1`, so
only the pin is trusted) and a forged pin reaches nothing; a wrong password and
the reused setup code are refused (`signin.invalid`); the laptop signs in with a
recovery code, is paired under the host's home origin and its signed requests
work; it asks to join and the home PC lets it in on the host's attestation
(`NetworkSyncEngine.ApproveSignedIn`) with no check number, while a PC paired by
code still waits for one; the home PC adds an OpenID Connect provider (its
client secret kept on the host, `has_client_secret` only) routed to an issuer in
the same process (`GatewayServer.UseSignInProviderHandler`); a tablet signs in
through a simulated browser that follows the redirect to the desktop's real
loopback listener (`LoopbackRedirect`), is refused (`signin.not_allowed`) until
the home PC allows the identity listed under `refused`, then is paired (the
host exchanged the code with the client secret and checked the ID token) and
let into the network the same way; a Steam account allowed by its SteamID64
signs in through the simulated browser with an OpenID 2.0 assertion the host
confirms with (simulated) Steam; the home PC allows the same OpenID Connect
identity as a friend (`access` `friend`, [sharing a host with
friends](../NETWORK.md#sharing-a-host-with-friends)) and the desktop's client reads
it back as a friend's: FRIEND-PC signs in through the simulated browser, its
sign-in answer says `friend`, the host keeps a friend's credential for it and
it lists the engines, and the home PC reads it as a friend's computer in the
sign-in settings and in the host's network answer (`devices[].access`), while 15 other routes (network, hardware, who does what,
settings, memories, voices, speaking voices, characters, creations, Home
Assistant, API keys, commands, GPU priority, security audit, sign-in settings)
refuse it with `access.friend` and it never asks to join; a sign-in under
HOME-PC's ID is refused (`signin.device_taken`) while HOME-PC keeps its
pairing; stopping sharing revokes FRIEND-PC at once (`auth.revoked`) and records
no network removal; the home PC makes a [share
link](../NETWORK.md#sharing-with-a-link) for all of its hosts (both hosts keep only
its verifier, never the secret), LINK-FRIEND pastes it, is refused with a wrong
secret (`signin.link_unknown`), signs in as a friend on the host the link names
(provider `link`; every other route refuses it with `access.friend`), asks that
host which hosts the link covers (`GET /martlet/v1/signin/shared`, from the
roster) and signs in to the second host it didn't know, and stopping the link
revokes it on both (`auth.revoked`); removing the owner account revokes the
laptop (`auth.revoked`); the host's security audit holds the sign-in successes
and failures and no secret. Not covered: the desktop windows, Windows
Credential Manager, a host reached over the internet, a real browser and a real
issuer.

`signin_lab` (`action` `start`, `status` or `stop`; `mode` `owner`, the
default, or `friend` with `start`; `dataDirectory` filled in by the script)
runs a live sign-in lab for the desktop on a disposable data directory, so
**Sign-in from outside**, **Share › Friends** and **Join with an invite** can
be driven against a real host. Mode `owner`: `src\Martlet.NodeLinkCheck` mode
`signin-lab` (`SignInLab.cs`) starts a gateway on 127.0.0.1 with an owner
account, an OpenID Connect provider (an issuer in that process), an allowed
identity and an outside address set on the host; it pairs the desktop of the
data directory (`hosts.json` there, the secret in the lab credential folder,
under the device ID that desktop already named itself by in `device.json`, so
its account bindings match its roster entry), a
simulated laptop signs in and keeps syncing, and a simulated friend's computer
(`lab-friend-pc`, identity `ana@example.net`, `authentik` subject
`lab-friend-7`) signs in once and is refused, so it waits under *Signed in but
not allowed yet* and in **Share › Friends**; once the owner shares the host
with it (as a friend) it signs in again. `status` returns what the lab sees
(`network`, `laptopMember`, `laptopWasMember`, `laptopRemoved`,
`laptopCanUseHost`, the laptop's network events, and `friend`: `allowedAs`,
`state`, `access`, `hostKeepsAs`, `canUseEngines`, `failure`,
`refusedElsewhere` (routes that answered `access.friend`), `otherwiseAnswered`
and `inRoster`). Mode `friend` (`SignInLab.RunFriendAsync`): the desktop of the
data directory is the friend. The gateway (`lab-shared-host`) belongs to a
simulated owner, `lab-owner`, who started their own network with it, set up the
provider and allowed `lab-user-42` (`me@example.net`) as a friend; it runs a
fixture Ollama Thinking route (`fixture-model:1b`, canned text, NOT AI). The
start answer and `status` carry the `invite` to paste in **Join with an
invite** and a `shareLink` (`martlet://share/...`, all of the owner's hosts) to
accept with **Accept a share link** or as the desktop's last start argument;
`status` counts `signedInWithShareLink`; the lab's simulated browser answers the desktop's sign-in (the desktop
writes the sign-in page's address to `signin-browser.url` in the
`MARTLET_LAB_BROWSER` folder, only in a lab run). `status` returns
`desktopSignedInAsFriend`, `friendComputers`, `ownersRoster` (the owner's
network members: never the friend), `askedToJoin`, `refusals` and
`accessFriendRefusals` (what the host refused, which stays 0 in normal use) and
`thinkingRequests` (chat requests that reached the fixture). For a locked or
headless session, where the window can't be driven: `signInDesktop` `true`
(with `mode` `friend`) has the lab sign the data directory's desktop in as the
friend itself, under the device ID that desktop names itself by, and keep the
pairing as **Join with an invite** does (`hosts.json` with `access` `friend`),
so a desktop started on that data directory afterwards runs with a shared host;
`shareWithFriend` `true` (with `mode` `owner`) has the lab's own admin desktop
share the host with the simulated friend from the start, so the friend's
computer is in the host's network answer from the desktop's first sync. Mode
`account` (`SignInLab.RunAccountAsync`, headless: no desktop is involved and the
data directory only takes `signin-lab.json`) rehearses [household accounts on a
host](../NETWORK.md#household-accounts-on-a-host) and [host
attestations](../NETWORK.md#proving-an-account-host-attestations): a gateway
(`lab-account-host`) with an ECDSA P-256 TLS key like every Martlet host; its
owner (`lab-owner`) pairs, starts a network with it and sets up the owner login
(its account derived from the network), Sam's login (`5a6e…005a`, password and
authenticator), Alex's (`a1e4…00a1`, password only), the lab OpenID Connect
provider with `lab-user-42` linked to Sam and `lab-friend-7` as a friend. Each
Prove sign-in (`/signin/prove`: the owner, Sam, Alex and Sam through the
provider) and a laptop's sign-in as Sam with a recovery code must give an
attestation that `AccountAttestation.Check` accepts against the owner's roster;
a friend's identity (`signin.no_account`), a wrong password (`signin.invalid`)
and Alex adding a computer (`signin.needs_authenticator`) must be refused, and a
changed attestation (`BadSignature`), an expired one (`Expired`) and another key
than the roster's pin (`KeyNotPinned`) must fail. The start answer and `status`
carry `ok`, the 14 `checks` (`name`, `ok`, `detail`) and the `attestations`
(account, device, login, algorithm, times, check, text length). Example: start
with `mode` `account`, then check `"ok":true`. With `household` `true` (mode
`account`; `SignInAccountLab.cs`) the lab involves the desktop of the data
directory instead and rehearses [household sign-in
providers](../NETWORK.md#household-sign-in-providers) and [signing in to your
household account](../NETWORK.md#signing-in-to-your-household-account): two
gateways (`lab-host-a`, `lab-host-b`, ECDSA keys, both routed to the lab
issuer) paired with that desktop (`hosts.json`, secrets in the lab credential
folder). Before the desktop binds them, the lab's admin desktop sets the
household provider `authentik` up on `lab-host-a` only, as a public client, so
the desktop adds it to `lab-host-b` by itself when **Share › Friends** reads
its hosts. Once both are bound, the lab acts with the desktop's own pairings:
it gives Sam a Martlet password with an authenticator on both hosts, proves
Sam on `lab-host-a` and links `lab-user-42` (`me@example.net`) to Sam on every
host with `HouseholdSignIn.LinkInBrowserAsync`. When `lab-host-b` has the
provider, it proves Sam there with that login and a new computer
(`lab-new-pc`) signs in to Sam's account through `lab-host-b`, gets Sam's
attestation and asks to join; a second computer (`lab-sam-pc`) reads what
**Join with an invite** offers there (`joinChoices`, with `martlet`) and joins
with Sam's Martlet password and a recovery code (`SignInWithPasswordAsync`,
the window's call). The desktop lets both in by itself and its log says whose
computer each is. `status` returns `phase` (`done` once both are members), `failure`, `network`,
`providers` (per host: `Id`, `Kind`, `Name`, `HasClientSecret`; never the
secret), `providerOnHosts`, `clientSecretOnHosts`, `linkedLogins` (per host:
`provider`, `subject`, `account`), `link` (`account`, `provedWith`,
`identity`, `savedOn`, `problems`), `providerProve`,
`accountSignIn` and `passwordSignIn` (`account`, `check` or `attestation`,
`verified`), `joinChoices`, `joinAttested` (the join request's `sign_in` as the
desktop's client read it, with `account`), `newDeviceMember`,
`newDeviceWasMember`, `newDeviceWaiting`, `secondDeviceMember`, `browsedAs`
(whom the lab's simulated browser signed in as for the desktop) and `events`.
The desktop hands its browser sign-in pages to the lab (`MARTLET_LAB_BROWSER`):
the first one signs in as the desktop's own person (`lab-desktop-7`,
`owner@example.net`), the next ones as Sam (`lab-user-42`). Example (with `-Desktop -LabCredentials -AllowUiEffects`): start
with `mode` `account` and `household` `true`, click `TourSkip`, `NavDevices`
and `RefreshDevices` (the desktop reads `hosts.json` again), poll
`network_status` until `"state":"member"` and `signin_lab status` until
`"link":{`, click `FriendsCheck`, poll `signin_lab status` until
`"providerOnHosts":["lab-host-a","lab-host-b"]` and then until
`"newDeviceMember":true` (the desktop syncs every 20 seconds). In the window:
open Add a computer › `HostsStepRoles` › `HostSignInSettings`, poll
`ui_snapshot` until `SignInHouseholdProviders` reads *Authentik (lab)
(authentik): on lab-host-a and lab-host-b.*, `ui_set_text`
`SignInProviderId` `authentik`, `SignInProviderName` `Authentik (lab)`,
`SignInProviderIssuer` `https://idp.lab.invalid`, `SignInProviderClientId`
`martlet-lab` and `SignInProviderSecret` `lab-client-secret`, click
`SignInProviderSave`, poll `SignInSettingsStatus` until *Saved Authentik (lab)
on lab-host-a and lab-host-b.*, `signin_lab status` until
`"clientSecretOnHosts":["lab-host-a","lab-host-b"]` and `network_status` until
`"keptHere":true`. The Account page: once `accounts_status` reads
`"pending":0`, click `AccountButton` and `AccountOpen`, poll `ui_snapshot` until
`AccountProviderLogins` reads *No sign-in from a provider is linked*, click
`AccountLinkProvider-authentik` (the desktop's account has only its Windows
login, so no Prove first; the lab browser signs in as `owner@example.net`),
poll until *Linked owner@example.net (authentik) on lab-host-a and lab-host-b.*,
`signin_lab status` until `lab-desktop-7` is in `linkedLogins` and
`accounts_status` until the account's `logins` list `oidc`; click
`AccountUnlinkLogin-authentik-lab-desktop-7` and `ConfirmationYes`, poll until
*Unlinked owner@example.net ...* and `"logins":["windows"]`. Signing in with a
provider: click `AccountClose`, `AccountButton`, wait for and click
`AccountSignInOther`, then `ProveMethod-authentik` and `ProveSubmit` (the lab
browser signs in as Sam) and poll `accounts_status` until `current` is Sam
(`5a6e…005a`). With `joinDesktop` `true` (mode `account`, not with
`household`; `SignInAccountLab.RunJoinAsync`) the desktop of the data
directory is a new computer that joins a household as Sam's: the gateway
(`lab-home-host`, ECDSA) belongs to a simulated owner (`lab-owner`) who started
the network with it, set the lab provider up with its client secret, gave Sam a
Martlet password with an authenticator, linked `lab-user-42`
(`sam@example.net`) to Sam and wrote Sam (with both logins) to the account
directory. The lab does for the desktop what **Join with an invite** does,
under the device ID that desktop names itself by (`device.json`): it signs in
through the lab provider as Sam and keeps the pairing (`hosts.json`, the secret
in the lab credential folder) and the account the host vouched for
(`joined-account.json`). `status` returns `desktop`, `account`, `signedInAs`,
`choices` (what the window offers: `martlet` and `authentik`), `askedToJoin`,
`joinRequest` (the host's `sign_in` with `account`), `desktopMember` and
`joinedAccountWaiting`. Example (with `-Desktop -LabCredentials
-AllowUiEffects`): start with `mode` `account` and `joinDesktop` `true`, click
`TourSkip`, `NavDevices` and `RefreshDevices`, poll `signin_lab status` until
`"desktopMember":true`, `network_status` until `"state":"member"` and
`accounts_status` until `current` is Sam (`5a6e…005a`, `"name":"Sam"`); the
desktop log says *Accounts: this PC joined the network as account 5a6e0000's
computer; signed it in here* and *Hi, Sam*. It needs
`Invoke-MartletMcp.ps1 -LabCredentials`, which points `MARTLET_LAB_CREDENTIALS`
of the desktop and MCP server at a `lab-credentials` folder in the data
directory, so pairing secrets go there (plaintext, thrown away with the folder)
instead of Windows Credential Manager, and `MARTLET_LAB_BROWSER` at a
`lab-browser` folder there. Example (owner): start the lab, click `NavHome`
(the desktop reads `hosts.json` again), poll `network_status` until
`"state":"member"`, `signin_lab status` until `"laptopMember":true`, open Add a
computer › `HostsStepRoles` › `HostSignInSettings`, type `authentik` /
`lab-user-42`, click `SignInDisallow`, then poll `network_status` until the
laptop is `"removed":true` and `signin_lab status` until
`"laptopRemoved":true`. Sharing with the friend: click `NavDevices`, then
`FriendsCheck`, and poll `FriendsStatus` until *1 person asked*; click
`FriendShare-lab-signin-host-authentik-lab-friend-7` and `ConfirmationYes`
(with `-AllowUiEffects`), then poll `signin_lab status` until
`"canUseEngines":true` and `network_status` until `friends` lists
`ana@example.net`. Example (friend): start with `mode` `friend`, open Add a
computer › `HostsJoinWithInvite`, `ui_set_text` `SignInInvite` with the
`invite`, click `SignInConnect`, `SignInProvider-authentik` and `SignInSubmit`,
poll `SignInJoinStatus` until *shared with this PC by a friend*, close the
windows, then poll `network_status` until `pairedHosts` shows `"access":"friend"`
and `SharedHostsStatus` until *1 host a friend shares with this PC*, click
`SharedHostUse-lab-shared-host-thinking` and `ConfirmationYes`, and check
`signin_lab status`: `"askedToJoin":false` and `"accessFriendRefusals":0`.

`role_lab` (`action` `start`, `status`, `ask` or `stop`; `dataDirectory` filled
in by the script) runs a live lab for [switching your computers between
companion and host PC](../CLUSTER.md#switching-another-computer-between-companion-and-host)
from both sides, for the desktop on a disposable data directory:
`src\Martlet.NodeLinkCheck` mode `role-lab` (`RoleLab.cs`) starts a gateway on
127.0.0.1 that keeps the network and the shared settings in memory, pairs the
desktop of the data directory under the device ID that desktop names itself by
(chosen by the lab and saved in that folder's `device.json`; `hosts.json` there, the secret in the lab credential
folder, so it also needs `Invoke-MartletMcp.ps1 -LabCredentials`), and runs a
simulated companion PC, `lab-companion` (*LAB-COMPANION*). Once the desktop has
bound the host to its network, the simulated PC asks to join it. Every
2 seconds it syncs the shared settings with the real sync engine as a desktop
does: it says what it is (`pc.lab-companion`) and follows an ask that it switch
(`role.lab-companion`). `ask` with `role` `host` or `companion` makes it ask the
desktop to switch (`role.<desktop device>`). `status` returns `companionRole`,
`companionMember`, `companionWaiting` (its check number while it waits to be
let in), what the host's copy says each computer is (`desktopSays`,
`companionSays`) and the newest ask about each (`desktopAsk`, `companionAsk`:
value, by, at), and its events. The values are JSON text, so an `until` that
looks inside them has to match its escaped form. Example (with `-Desktop
-LabCredentials -AllowUiEffects`): click `TourSkip`, start the lab, click
`NavDevices` and `RefreshDevices` (the desktop reads `hosts.json` again), poll
`network_status` until `"state":"member"` and `role_lab status` until
`"companionWaiting":"`, click `NetworkCheck`, `NetworkAllow-lab-companion` and
`ConfirmationYes`, poll until `"companionMember":true`; then click
`Node-pc:lab-companion`, `NodeAction-MakeHostPc` and `ConfirmationYes` and poll
`role_lab status` until `"companionRole":"host"` and `logs_tail` until *LAB-COMPANION
is a host PC now, as you asked*; `role_lab ask host` and poll `DeviceRoleSummary`
until *Host PC*.

`role_lab add_host` (`hostId`, default `lab-nas`) shows a host of the network
the desktop can't reach ([NETWORK.md](../NETWORK.md#when-a-host-is-added-where-your-other-computers-cant-reach)):
once the simulated PC is a member, it signs that host into the network at an
address nothing answers on (`https://10.255.255.1:9443`, a made-up key), as a
host added on a computer the desktop can't reach; `status` then lists it in
`addedHosts` and `networkHosts`. Example, after `"companionMember":true`: call
`role_lab add_host`, poll `role_lab status` until `lab-nas`, click
`NetworkCheck`, poll `ui_snapshot` until `reach lab-nas` (an `until` can't
contain an apostrophe, which the snapshot escapes): `NetworkGap` reads *This PC
couldn't reach lab-nas on its last check, so it may not have the newest network
...* and `NetworkMember-host-lab-nas` *... Not paired yet: it didn't answer in
time at 10.255.255.1:9443.*; then click `NavDevices` and poll until *Not paired
with this PC yet*: `Node-host:lab-nas` reads *lab-nas, 10.255.255.1. Not paired
with this PC yet.*, `SelectedDeviceHealth` the same after clicking it, and
`DevicesSummary` ends with the `NetworkGap` sentence. `-LabCredentials` also
sets `MARTLET_HOST_FIND_LOOPBACK=1` for the desktop and the server, so their
finds of hosts on the home network stay on loopback and a lab never pairs with
your real hosts.

Sign-in from outside in the desktop: Add a computer's **Join with an invite**
(`HostsJoinWithInvite`) opens `SignInJoinWindow` (invite `SignInInvite`,
`SignInConnect`, provider choices `SignInProvider-<id>`, with
`SignInProvider-martlet` for *Your household account (Martlet password)* when
the host keeps household logins, `SignInUser`,
`SignInPassword`, `SignInCode`, `SignInSubmit`, status `SignInJoinStatus`, the
checked host `SignInHost`, `SignInJoinClose`); a paired host's **Sign-in from
outside** (`HostSignInSettings`) opens `SignInSettingsWindow` (status
`SignInSettingsStatus`, owner state `SignInOwnerState`, `SignInOwnerUser`,
`SignInOwnerPassword`, `SignInTotpNew`, `SignInTotpSecret`, `SignInTotpLink`,
`SignInOwnerCode`, `SignInOwnerSave`, `SignInRecoveryNew`, `SignInOwnerRemove`,
`SignInRecoveryCodes`, `SignInAllowedList`, `SignInProvidersList`,
`SignInHouseholdProviders` (where each household provider is set up; a safe
value), `SignInProvidersPush` (**Add to the other hosts**), `SignInProviderKind`
(with *Microsoft (personal accounts)*), `SignInProviderId`, `SignInProviderName`,
`SignInProviderIssuer`, `SignInProviderClientId`, `SignInProviderSecret`,
`SignInProviderScopes`, `SignInProviderPort`, `SignInProviderSave` and
`SignInProviderRemove` (both on every host of your network), `SignInRefusedList`, `SignInRefusedAllow` (allow the
newest as one of your computers), `SignInRefusedAllowFriend` (as a friend),
`SignInRemovedList` (computers your member PCs still have to remove from the
network), `SignInOutsideWarning` (outside access paused or about to be),
`SignInAllowProvider`, `SignInAllowSubject`, `SignInAllowLabel`,
`SignInAllowAccess` (with `SignInAllowAccess-member` and
`SignInAllowAccess-friend`), `SignInAllow`, `SignInDisallow`, each allowed
identity's row `SignInAllowed-<provider>-<subject>` (one of your computers or a
friend's) with `SignInAccess-<provider>-<subject>` (**Make a friend** or **Make
one of my computers**) and `SignInRemove-<provider>-<subject>`,
`SignInEnrolledList` (each computer says whether it is a friend's),
`SignInInviteAddress`, `SignInInviteMake`, `SignInInviteText`,
`SignInInviteCopy`, `SignInSettingsClose`); identities in IDs keep letters,
digits, dots and dashes, anything else becomes `_`. Opening and closing both
windows are safe clicks; the status lines and lists are safe values. Everything
else contacts a host or changes it and needs `--allow-ui-effects`.

Sharing hosts with friends on the Share page (`MainWindow.Friends.cs`):
**Friends** (`FriendsCard`, shown with paired hosts; status
`FriendsStatus`, `FriendsCheck` reads each of your hosts' sign-in settings and
needs `--allow-ui-effects`, because it contacts the hosts and writes `friends.json`; reading them also adds the share links this PC made to hosts
that miss them) lists each person as `Friend-<provider>-<subject>` (*name
(provider). Shares gpu-box: their engines only · their computers: ...*, or that
they signed in to a host and wait) with `FriendShare-<host>-<provider>-<subject>`
and `FriendStop-<host>-<provider>-<subject>` (both ask first and change the
host, so they need `--allow-ui-effects`), and `FriendsHost-<host>` for a host
it couldn't read. **Hosts shared with this PC** (`SharedHostsCard`, always
shown; status `SharedHostsStatus`; it reads what
they offer by itself when the Devices page shows) lists each as
`SharedHost-<host>` (who this PC signed in as, what it offers this PC, what
this PC uses it for, or that its owner stopped sharing it) with
`SharedHostsCheck` and `SharedHostCheck-<host>` (read again and, like **Check
all hosts**, follow a model the owner changed for a job of this PC there),
`SharedHostUse-<host>-<job>` (`thinking`, `listening`, `speaking`, `lip-sync`,
`reading`) and `SharedHostForget-<host>`; all of them need
`--allow-ui-effects`. In *Your Martlet network* a friend's computer
is `NetworkFriend-<device ID>` (*A friend's computer that signed in to gpu-box as
a friend and uses only that host's engines*), never `NetworkPaired-`. The job
choices (`ThinkingOwner`, `ListeningOwner`, `SpeakingOwner`, `LipSyncOwner`)
and Companion's computer cards (`HostChoice-<job>-<host>`,
`SetupUseHost-<job>-<host>`) list a shared host as *shared by a friend*.

Share links ([sharing with a link](../NETWORK.md#sharing-with-a-link),
`MainWindow.ShareLinks.cs`): **Make a share link** (`FriendsMakeShareLink`, a
safe click) opens a form (`HostInputDialog`: `HostInput-name`, `HostInput-from`,
`HostInput-all` and `HostInput-host:<host>`); **Make link** (`HostInputOk`)
adds the link to your hosts and needs `--allow-ui-effects`. The link window
(`ShareLinkWindow`) shows `ShareLinkHeading`, `ShareLinkMessage`,
`ShareLinkWarning` (hosts with no outside address) and `ShareLinkStatus` as safe
values; the link itself (`ShareLinkText`) is a secret and is never read.
`ShareLinkCopy` copies it and `ShareLinkClose` (a safe click) closes the
window. A friend you share with a link is `Friend-link-<link ID>` (*Ana (share
link). Share link for all of your hosts, also the ones you add later · their
computers: ...*) with `ShareLinkCopy-<key>`, `ShareLinkStop-<key>` and, for a
link that names its hosts, `FriendShare-<host>-<key>` and
`FriendStop-<host>-<key>`; all need `--allow-ui-effects`. **Hosts shared with
this PC** is always shown: with none, `SharedHostsStatus` says how to accept a
link, and **Accept a share link** (`AcceptShareLink`, a safe click) opens the
paste form (`HostInput-link`). Starting the desktop with a link as its last
argument (`martlet://share/...`, as Windows does when a link is clicked) asks
to accept it (`ConfirmationDialog`); Cancel changes nothing.

Opening a port on the router for this PC's host service: the Outside access
dialog has `HostInput-router` (*Let Martlet open a port on my router*) and the
safe value `OutsideAccessRouterPort` (router name, port and times, never the
address). Saving with it needs `--allow-ui-effects`; it changes the router and
Windows Firewall.

`front_door_selftest` (no arguments) rehearses the [front
door](../OPENAI_ENDPOINT.md#the-front-door), a host's public listener on its own
port, end to end with the production code. It runs `src\Martlet.NodeLinkCheck`
(mode `front-door`, `FrontDoorRehearsal.cs`) and returns `{exitCode, report}`
like `network_selftest`. Two real gateways (`lab-front-door` with its front
door open on a free loopback port, and `lab-gpu-box`, each with the real Ollama
relay over a fixture Ollama, NOT AI) are joined in one network by a simulated
desktop, which makes an `openai` key (*Ana*) and a `voice` key on both. Plain
HTTPS clients on the front door's port only (as a proxy sends them) check: the
host's security audit names the front door's port and listeners; the public
page shows *Online* and the sign-in form, with no model, host or address, no
script and a `default-src 'none'` CSP; every private path (`/martlet/v1/...`,
`/health/ready`) answers 404, also with a key; `/v1/models` without a key
answers 401 `missing_api_key` (what *Check address* looks for) and a `voice`
key 403; Ana's key lists both hosts' models and a streamed chat is sent on to
`lab-gpu-box`; fifteen wrong keys answer 401 and never lock Ana out; sign-in
refuses a forged form token, another site's `Origin` and a wrong key, and
Ana's key gets a session whose page lists both models and the base URL; and
revoking Ana ends the session and the key at once. Loopback only; it writes
nothing to disk.

`openai_selftest` (no arguments) rehearses the [OpenAI-compatible
endpoint](../OPENAI_ENDPOINT.md) end to end with the production code. It runs
`src\Martlet.NodeLinkCheck` (mode `openai`, `OpenAiRehearsal.cs`) and returns
`{exitCode, report}` like `network_selftest`. Two real gateways
(`lab-front-door` and `lab-gpu-box`, each with the real Ollama relay over a
fixture Ollama, NOT AI, and a model of its own) are joined in one Martlet
network by a simulated desktop's real pairing client and network sync. The
desktop makes *Ana* (`openai`), *Ben* (`openai` and `equal-priority`) and a
`voice` key and syncs them to both hosts (verifiers only). A plain HTTPS
client with `Authorization: Bearer` then checks, at the front door: `GET
/v1/models` lists both hosts' models, each with the hosts that run it; the desktop's
signed `ReadOpenAiModelsAsync` reads each host's own models; a chat for the
front door's model runs there with `num_ctx` and `keep_alive` `-1` in the
native request; a streamed chat for the other host's model is sent on to it
(`X-Martlet-Host`) and ends with `data: [DONE]`; no key (401
`authentication_error`), a `voice` key (403 `permission_error`), an unknown
model (404 `model_not_found`) and `/martlet/v1/status` with an `openai` key
(403) are refused; the owner's reply stops Ana's streamed request
(`owner_needs_it`) and runs; while the owner's reply holds the front door's
model, `martlet@any` runs at once on the other host's free model (its
`model` names it), and two of Ana's requests for the front door's model wait
in the queue with the production limits (her stream gets an SSE comment after
5 seconds) and run once the reply ends (`X-Martlet-Queued`); and revoking
Ana's key refuses her on both hosts while Ben's request still goes through.
Before the owner-first step it also checks the [network model
names](../OPENAI_ENDPOINT.md#network-model-names): `GET /v1/models` lists
`martlet@any` (both models) and `martlet@fast` (the front door's `1b` model)
with the models behind each (the `"martlet_models"` field) and no `martlet@smart`; a chat for
`martlet@fast` runs on the front door's model and its answer names it;
`martlet@smart` gets 404 `model_not_found` and `martlet@fast` on
`/v1/embeddings` gets 400 `invalid_request_error`. Loopback only; nothing is
written to disk or the credential vault. It does not cover a real model, a
real reverse proxy with a certificate, or a second PC.

`api_selftest` (no arguments) rehearses API keys for software outside the
network end to end with the production code: two real gateways
(`lab-api-1`, `lab-api-2`: Kestrel, pinned TLS, a throwaway certificate) on
`127.0.0.1`, each with the real Ollama relay route over a fixture Ollama
(canned text, NOT AI), a simulated desktop using its paired client
(`HostApiKeys.cs`), and a plain HTTPS client that pins the host key and sends
`Authorization: Bearer`. It runs `src\Martlet.NodeLinkCheck` (mode `api`,
`ApiRehearsal.cs`) and returns `{exitCode, report}` like `network_selftest`.
Its steps: the desktop pairs with both hosts and creates a read+voice and a
manage key, and both hosts keep verifiers and no secret; no key
(`auth.missing`), a malformed and a wrong key (`key.invalid`) are refused; the
read+voice key reads version (naming the key), status and capabilities, the
documented `curl -k --pinnedpubkey` request (System32's curl.exe) answers and a
wrong pin is refused (exit 90), and the key chats through the native route (and
still gets its reply when the model's Gemma 4 speculative-decoding draft fails
to load: the relay saves `draft_num_predict 0` on the model with `/api/create`
and retries); it gets `key.scope` for commands, voices,
network, api-keys, posting the plan and posting logs; the manage key sends
`host.status` and follows it but can't chat or read status; the same key works
on the second host and after it restarts from its saved copy; the desktop sees
when keys were last used; revoking a key cuts its streaming reply and every
host refuses it after sync; a stale copy can't bring it back; an expired key
gets `key.expired`. Nothing leaves loopback and nothing is written to disk or
Windows Credential Manager; it does not cover the desktop window,
`api-keys.json` on a Linux host, a real model or a real LAN.

`gpu_priority_selftest` (no arguments) rehearses GPU priority (live turn first,
[Gateway README](../../src/Martlet.Gateway/README.md#gpu-priority-live-turn-first))
end to end with the production code: real gateways on `127.0.0.1` (Kestrel,
pinned TLS) with Thinking's Ollama relay (lane `live`) and the Deep thinking
role's (lane `pool`) placed on graphics cards the way `martlet-host` places them
(`gpus.json`), each over its own fixture Ollama server on loopback (canned text,
NOT AI; no GPU is used), a simulated desktop with the desktop's paired client and
its hold client (`HostLiveGpuHold`, the desktop's `ILiveGpuHold`). It runs
`src\Martlet.NodeLinkCheck` (mode `gpu-priority`, `GpuPriorityRehearsal.cs`) and
returns `{exitCode, report}`. Its steps: the host's GPU map (both routes on one
card, the warning to pin each Ollama server to its own GPU); a live reply stops a
running think on the same card at once (`job.preempted`, the fixture sees the
Ollama request aborted); while a reply runs, a new think there is turned away
(`job.busy`, detail `live`) and never reaches Ollama; the hold client keeps the
card (a think is turned away, renewing keeps one hold, release frees it); a 1 s
hold ends on its own; a hold stops a running think and replies never wait for
it; a think on its own card runs beside replies and holds; and an older host
without holds never breaks the live turn (the client notes it once). The
`report.priority` field is the host's `GET /martlet/v1/priority`: each route's
lane and cards, each card's hold state (`held`, `live`, `pool`, `holds`),
`whole_host_held`, the holds, the `preempted` and `refused` counts, the last
preemptions and refusals (with what held the card) and the warnings. It does not
cover a real GPU, a real Ollama stopping mid-token, a host with two or more
cards or when the desktop's live turn holds a card.
```powershell
.\scripts\Invoke-MartletMcp.ps1 -Build -Calls '[{"name":"gpu_priority_selftest"}]'
```

`gpu_priority_status` (`dataDirectory`) reads GPU priority on every host paired in
a desktop data directory (`hosts.json`), through each host's own gateway with the
desktop's pairing (pinned TLS; the secret from Windows Credential Manager only
signs the request and is never returned): the host's `GET /martlet/v1/priority`
report as above, or, for a host older than GPU priority, `gpuPriority: false` with
the advice to update it, or `reachable: false` with the problem. With no
`hosts.json` it contacts nothing. Read-only: it takes no hold and starts no work.
`Invoke-MartletMcp.ps1` gives it the disposable data directory unless one is
named.

`household_sharing` reads what each account of a household shares
([ACCOUNTS](../ACCOUNTS.md#sharing)) from a data directory (optional absolute
`dataDirectory`). It reads that PC's copy of the household settings
(`shared-settings.json`, the `sharing.<account>` entries) and the account in use
(`accounts\session.json`). It returns `state` (`none` or `loaded`),
`currentAccount`, `entries`, `unreadable`, `householdCharacters` (the characters
other people share, as the account in use sees them), `ownShared`, `ownJoined`
and `newFactsAboutMe`. Per account it returns `account` (32 hex digits),
`current`, `copy`, `together`, `joined`, `newFactsAboutMe`, `bytes`, `updatedBy`
and `changedAt`. Each of its `characters` has `key`, `mode`, `space` (the
`character-<id>` memory space when shared together), `lorebooks`,
`lorebookEntries`, `look` (`keep`, `builtin` or `shared`) and `voice`. Each of
its `joinedCharacters` has `account`, `key`, `space` and `stillShared`. It never
returns a name, a personality's text or a lorebook entry. Read-only; it contacts
nothing.

`nearby_status` reads whether this PC lets Martlet on the owner's other
computers [find it](../ARCHITECTURE.md#finding-your-other-computers) (optional
absolute `dataDirectory`, default the current user's): `share` is
`on (default)` when `nearby.txt` is missing, `on`, or `off` once the owner
unticked **Let my other computers find this PC**; `port` (9444); and `hosts`
from `hosts.json` (`state` `none`, `loaded` or `unreadable`; when loaded the
number `paired` and the `shareable` ones with `hostId` and `reach`
(`ThisPcDocker`, `SshDocker` or `SshNative`)). This PC's own host service set
up from the host dashboard is found by the desktop from Docker, not here
(`host_service_status` reads it). `find` says how *Add a computer* looks:
`listensSeconds` (15; each computer is listed as soon as it answers),
`queriesSentAtSeconds` (0, 0.5, 1.5, 3.5 and 7.5) and `hostsOfferedAtMost` (64,
all the hosts a computer can remember). While it looks, the `NearbyStatus` text
ends with *still looking for more...*. It
never returns addresses, SSH targets or keys and contacts nothing.

`node_link_check` runs [commands between computers](../CLUSTER.md#commands-between-your-computers)
end to end on this PC's loopback and returns `{exitCode, report: {passed,
steps: [{name, ok, detail}]}}`: the real gateway (Kestrel, pinned TLS with a
throwaway fixture certificate, pairing, signed requests, the command mailbox
and its storage), the desktop's real client and agent loop with a fixture
runner (FIXTURE: it installs nothing), and two fixture devices. Steps check
that only known commands and arguments are accepted, anonymous requests are
refused, only the agent's local token takes and reports commands, output and
outcomes reach the sender, secrets never appear in lists, commands or the saved
copy, the shared Home Assistant connection (including token sharing, revision
wins, tombstones, invalid bodies, restart storage and no token in gateway logs),
cancel works (waiting and running), commands run side by side
(`runs-side-by-side`: the agent's `TakeAsync` starts a slow describe and two
installs of different roles at once, each on its own connection, and the
agent info says `parallel`), a second change to one role waits for the first
and the sender's `HostCommandList.WaitingText` says so (`same-role-waits`)
while a status runs beside them (`others-run-beside`), an update waits for
what runs and holds what was sent after it (`update-waits-for-running`), the
waiting change runs once the first ends (`same-role-runs-next`), the
`host.exposure` command reaches the agent with exactly its martlet-host options
while an address with a shell character or an extra argument is refused
(`exposure-command`), the update
then runs alone and the held command after it (`update-runs-alone-then-the-rest`),
an agent from before commands ran side by side (no `running` list) still gets
one at a time (`serial-agent-one-at-a-time`), an update that continues later
(FIXTURE: "Martlet is in use here") stays first and holds the queue until it
finishes and the waiting command runs right after it, commands survive a
restart with a new token and the queue is bounded. It runs `src\Martlet.NodeLinkCheck` (built with
`Martlet.Mcp`) as its own process, because the gateway needs the ASP.NET Core
runtime; it takes no arguments and contacts nothing outside loopback. The same
program's `live <pairing-code> <container>` mode checks a disposable Linux
gateway container built from this checkout (not the real host service).

`host_engine_check` (no arguments) checks that a host runs
[changes side by side](../../deploy/host/README.md#changes-side-by-side) with this
checkout's real `deploy\host\martlet-host` and its modules (`deploy\host\lib\*.sh`): it starts one disposable
`ubuntu:24.04` container (`--network none`, `--pull never`, removed afterwards,
the engine in native mode against a fixture setup under `/tmp`; Martlet's own
host containers and volumes are never touched) and returns `{exitCode, report:
{passed, total, image, engine, steps: [{name, ok, detail}]}}`. Steps: `flock`
is present; a change (a `network-reset` waiting for a typed yes, like a console
left open) holds its locks (`engine.lock` 0600), records itself in
`engine.holders/` (scope `gateway`, running) and in `engine.holder`; `roles`
still runs; `status` says `Busy now: ...`; an automatic `update` (no terminal,
no `--yes`) stops at once with exit 75 and `MARTLET-BUSY ...`; a `--yes update`
with `MARTLET_LOCK_WAIT=3` waits, says what it waits for and gives up with 75;
a role change runs alongside it without waiting (`role-change-runs-alongside`);
a remove that already stopped its role and must still publish that through the
gateway keeps waiting past `MARTLET_LOCK_WAIT` instead of claiming nothing was
changed with `MARTLET-BUSY`, then unpublishes it
(`changed-host-never-claims-nothing-changed`);
`machine`, which restarts the gateway, waits for its lock and continues once the
holder is killed (SIGKILL), and so does an `update` without a terminal or
`--yes` that sets `MARTLET_LOCK_WAIT=60` (*Update hosts now*): it waits instead
of stopping and then runs; the next automatic run is not blocked (no stale
lock); `logs/engine.log` records the waits. With a fake `docker` CLI
(`/tmp/nativebin`, nothing real runs) two adds of different roles run side by
side, both stopped at their terms question (`role-changes-run-side-by-side`);
a remove of the same role waits for its add (`same-role-waits`) and an add of
another engine of the same `exclusive=` group waits too (`same-group-waits`);
an automatic `update` stops with `MARTLET-BUSY` naming all three adds; a
waiting `--yes update` holds back a role change asked for after it
(`later-change-waits-for-update`); once the adds are killed every waiting
change continues (`waiting-changes-continue`); and no records are left in
`engine.holders/` (`ended-changes-leave-no-records`). With a fake `dotnet` and
`systemctl`, `exposure` without options asks the gateway once and restarts
nothing (`exposure-prints`), with options and no `--yes` or terminal it stops
with *Nothing changed.* (`exposure-asks-first`), and `--yes exposure --outside
... --treat-all-as-outside yes` passes exactly those options to the gateway's
`owner-exposure` and restarts it (`exposure-saves-and-restarts`). The desktop's reader
(`HostEngineBusy.Read`) reads the engine's real busy line. It then checks the
Docker method's launcher and engine against a fake `docker` CLI (state in
`/tmp/fake`): an automatic `setup` while an `add` engine session runs in the
network holder's namespace stops with exit 75 and `MARTLET-BUSY installing
chatterbox (...)` without replacing `martlet-host-net`; a `--yes setup` waits
for it, then removes and recreates the holder and runs its engine; an engine
left in a replaced holder's namespace stops at once (`... was replaced while
this ran ... Nothing was changed`) while one in the current namespace
continues; and the desktop's reader reads that busy line. After a restart of
the Docker host, Docker may start the holder before its home address exists;
that start fails and Docker never tries again, so the gateway and roles stay
down. So `status` with a holder that failed to start starts it before its
engine joins the network (`command-starts-stopped-holder`); `update` adds the
keeper `martlet-host-keeper` (`--restart unless-stopped --network none`,
command `keep`) once and not again on the next update (`update-adds-keeper`);
and `keep` starts a holder that isn't running, with Docker Engine's start error
or without one (as Docker Desktop leaves it), and leaves a running holder alone
(`keeper-starts-stopped-holder`). Finally a native
`--yes add` of a fixture role against a fake `docker` whose `compose up` fails
with exit 17 (as a role image build does when a download times out) stops with
exit 1 and `Stopped: Building or starting fixture-build failed ... run
'martlet-host add fixture-build' again` (`build-failure-says-run-again`, also in
`logs/engine.log`). A fixture role with two variants (`FX_ENGINE` `alpha`, with
a GPU option, a model suggested by GPU memory and its own prepare step, or
`beta`, with its own models and prepare step), like the `stt` role's whisper and
Parakeet engines, then checks that `describe` lists each variant's choices,
suggestion and GPU option with their condition (`role.choice_when`,
`role.suggested_when`, `role.gpu_when`; `variant-describe`); that an add of
`beta` keeps its own model and prepare step and ignores a GPU answer
(`variant-own-choices`); that an install from before the role had variants
(`profile_legacy`) moves to `beta` by preparing it, creating it, and only then
stopping `alpha` (`variant-switch-after-prepare`); that the same switch run again
after it stopped at `up -d` (as a failed build or download would, with `.env`
already naming `beta`) still stops `alpha` before `beta` starts
(`variant-retry-stops-the-other`); and that `alpha` refuses
`beta`'s model, then runs on the CPU with its suggested model on this GPU-less
fixture (`variant-gpu-and-suggestion`). With a fake 8192 MiB NVIDIA card (fake
`nvidia-smi` and `nvidia-ctk`), a fixture role whose two models need at least
4000 and 26000 MiB (`min_vram=<MiB>|<why>` in each model's section, as the
`motion` role's HY-Motion models do) is listed by `describe` with
`role.min_vram_when=` lines, and with `role.unavailable=` once its smallest
model needs more than the card has (`min-vram-describe`); an add of the 26000
MiB model stops with *needs an NVIDIA graphics card with at least 26000 MiB*
before its terms or any compose call, while the 4000 MiB model goes on to its
terms and `up -d` (`min-vram-refuses-before-terms`). With `uname -m` answering `aarch64`
(a shim), an ARM64 host refuses an add of a role marked `requires=x86_64` (the
NVIDIA CUDA roles) with *built only for 64-bit Intel or AMD (x86_64)* and
`describe` prints `role.unavailable=` (`arm64-refuses-x86-only-role`), and a role
with an ARM64 build (`arm64=<overlay>`, like the `stt` role's whisper.cpp arm64
image) gets that overlay as `compose.arch.yaml`, runs on the CPU even when the
GPU is asked for, and `describe` offers no GPU option
(`arm64-overlay-runs-on-cpu`). The real `voice-recognition` role's files
(`deploy/host/roles/voice-recognition`, copied into the container) are read the
same way: on an x86_64 host with the fake 8192 MiB card `describe` offers
`role.accelerator=gpu cpu` and its one model
(`wespeaker-en-voxceleb-resnet34-lm`), and on the `aarch64` shim it offers no
GPU option and an add gets `compose.arch.yaml`, no `compose.gpu.yaml`,
`MARTLET_ACCELERATOR=cpu` and the model in `.env` (`voice-recognition-role`).
Last, `warm` on its own fixture host (fake
`docker` and `systemctl`) starts the stopped gateway (`systemctl --user start`),
runs `compose up -d --no-build` for every installed role, then the warm step of
`fixture-warm` with its saved choice filled in (`warm-it m2`; its `post_start`
step is not run again), reports `fixture-cold`, whose port never answers, as
*Not ready* and exits 1 (`warm-starts-and-warms-roles`); with `fixture-warm`'s
role lock held by another change it leaves that role alone (*Another change is
working on fixture-warm now*; `warm-leaves-held-role`). The real
`deep-thinking-app` role's files (copied into the container) are read with a
fake `curl` that answers as LM Studio on 127.0.0.1:1234: `describe` offers
`APP_PORT`, `APP_PATH` and the open `APP_MODEL` choice and no GPU option
(`app-role-describe`); an add for `qwen3-32b` runs no `docker compose` step,
writes the role record with `api=openai` and `path=/v1`, and the gateway
configuration names it (`app-role-relays-to-the-app`); an add for a model the
app doesn't list stops and keeps the record (`app-role-checks-the-app`); and
remove takes only the record away (`app-role-remove`). Without Docker or the
image it returns `exitCode` 2 and `notRun` (it never pulls). It does not cover
a real Docker daemon or a real host.

`host_supply_check` (optional `cacheDirectory`; default
`%TEMP%\Martlet\host-supply-check`) checks how Martlet sets up a native Linux
host [without internet access](../../deploy/host/README.md#computers-without-internet),
with the production `HostSupplier` and `HostCheckout` and this checkout's real
engine. It archives this checkout like GitHub's source archive and starts one
disposable `ubuntu:24.04` container (`--pull never`) on an internal Docker
network (a private LAN address, no route out) with its home folder on a
disposable ext4 volume; container, volume and network are removed afterwards.
This PC downloads the .NET SDK and the gateway's NuGet packages for real (about
240 MB the first time, cached in `cacheDirectory`); the container gets them only
from this PC, through `docker exec` instead of SSH. It returns `{exitCode,
report: {passed, total, image, cache, steps: [{name, ok, detail}]},
notCovered}`. Steps: `checkout-archived`; `needs-read` (the engine's .NET SDK
matches `global.json`, the packages from the gateway's lock files);
`host-is-offline` (`HostCheckout.InternetProbe` says `internet=no`);
`online-setup-stops-plainly` (the online command, with no git, sudo or
internet, stops with `Stopped: git is not installed here` and never runs a
missing engine); `supplied` (the files arrive intact, `~/Martlet` is unpacked
and the source archive removed); `setup-without-internet` (`martlet-host --yes
setup` with `MARTLET_SUPPLY` builds the gateway, creates its identity and starts
it healthy); `again-sends-nothing` (a second pass sends nothing and removes the
installed SDK's archive); and `update-without-internet` (`update` rebuilds the
gateway offline). FIXTURE `systemctl` (runs the gateway unit's `ExecStart`) and
`ip` (the container's address) stand in for systemd and iproute2. Not covered:
Martlet's SSH runner itself, and roles or Docker hosts without internet access
(Martlet stops with an explanation). Without Docker or the image it returns
`exitCode` 2 and `notRun` (it never pulls).

`proxy_manager_check` (no arguments) checks Share › Show setup › *Set it up in
Nginx Proxy Manager* with the production `ProxyManager` against a FIXTURE Nginx
Proxy Manager API in memory (it contacts nothing). It returns `{exitCode,
report: {passed, total, steps: [{name, ok, detail}]}, notCovered}`. Steps:
`finds-nginx-proxy-manager`; `wrong-password-refused`;
`plans-to-change-the-site-that-is-there` (the site that has the name, not a
second one, with its certificate); `changes-it-and-keeps-its-certificate`;
`set-up-already-changes-nothing`; `adds-a-site-with-a-certificate-that-covers-it`
(a wildcard certificate); `asks-lets-encrypt-when-no-certificate-covers-it` (only
an expired one covers it); and `password-only-in-sign-in`. In the desktop,
`OpenAiProxyManagerSetup` needs `--allow-ui-effects`; `OpenAiProxyManagerStatus`
and `ProxyManagerQuestion` are readable.

`host_docker_shell_check` (no arguments) checks the shell Martlet runs on a
[Docker host over SSH](../../deploy/host/README.md#docker-any-docker-host-including-windows)
when the computer is shaped like a NAS (TerraMaster TOS, Synology DSM, QNAP):
Docker is only on the login shell's PATH and there is no git. It joins the
production `HostCheckout.Docker` and `HostCheckout.DockerImage` text as the
desktop's Docker method does and runs it with `sh -c` and an SSH command's
plain PATH in one disposable `ubuntu:24.04` container (`--network none`,
`--pull never`, removed afterwards). It returns `{exitCode, report: {passed,
total, image, command, steps: [{name, ok, detail}]}, notCovered}`. Steps:
`plain-shell-has-no-docker`; `docker-found-on-login-path`
(`HostCheckout.DockerPath` finds Docker through `/etc/profile.d`);
`builds-from-source-archive` (without git, the release tag's archive is
missing, so main's source archive is unpacked into a temporary folder and
built, then the engine runs); `source-folder-removed`;
`image-there-builds-nothing`; `download-fails-stops` (nothing is built and
no engine runs); `plain-linux-is-no-nas` (the production `HostCheckout.NasProbe`
finds no NAS in `ubuntu:24.04`); and `nas-found` (it names TerraMaster,
Synology, QNAP, Unraid and TrueNAS by their markers `/etc/tos`,
`/etc.defaults/VERSION`, `/etc/config/uLinux.conf`, `/etc/unraid-version` and
`midclt`, also from an SSH command's plain shell; the desktop then sets up that
NAS only in Docker). FIXTURE `docker` and `curl` stand in for Docker and GitHub.
Not covered: Martlet's SSH runner, a real NAS and a real image build. Without
Docker or the image it returns `exitCode` 2 and `notRun` (it never pulls).

`host_update_check` (no arguments) rehearses how one Martlet keeps its own host
service updates from colliding, with the desktop's production
`HostUpdateTracker` and `HostEngineBusy` reader, and returns `{exitCode, report:
{passed, total, steps: [{name, ok, detail}]}}`. The timeline is the one seen on
a host PC right after Martlet updated itself: *Update this PC's host service*
(a run window) claims this PC's host service (`keys`,
`run-window-claims-host`); the automatic pass then leaves that host to the run,
both as its local pairing and as this PC's own host service, with no second
engine run and no "busy" note, while another host still updates
(`automatic-pass-leaves-host-to-run-window`); overlapping routes (a run window
and a command from another computer) end separately
(`overlapping-routes-end-separately`); the engine's real busy line for another
update is named as *Another update of that host was already running ...* while
an install keeps *Waiting to update ... busy* (`busy-note-names-another-update`);
a host found current stops waiting and its stale note becomes *Updated to
Martlet 0.22.0 (seen at ...)* (once), while other hosts keep theirs
(`current-host-replaces-stale-note`, `retry-takes-waiting`); and *Update
hosts now* waits up to 30 minutes for another change, inside an unattended
run's 45-minute limit (`asked-update-waits`). Then, with the production
`OwnHostFollower`, this PC's own host service follows the app's version after
Martlet restarted into its update: an older running one is updated
(`own-host-follows-app-update`); with Docker not answering or the host service
stopped it is read again on the next minute's check
(`own-host-waits-until-it-runs`); another route updating it, this PC's own
pending update and a reply or speech being heard go first
(`own-host-gives-way`); a busy one is not read again for three minutes, then
updated (`own-host-busy-retries`); and once updated it isn't read again, while
a failed update isn't retried automatically for that version but the next
version is (`own-host-settles`). It contacts nothing and touches no
Docker, host or data directory; the engine side of waiting is
`host_engine_check`'s `asked-update-waits-then-runs`.

`app_update_check` (no arguments) rehearses how Martlet installs its own update,
with the desktop's production update helper (`AppUpdateHelper`: the same
`install-update.cmd` script and the same hidden start Martlet uses) in a
disposable temp folder, and returns `{exitCode, report: {passed, total, steps:
[{name, ok, detail}], notCovered}}`. A windowless process that exits after about
two seconds stands in for Martlet; `Martlet.NodeLinkCheck` (built with
`Martlet.Mcp`) stands in for the installer and for the restarted Martlet
(FIXTURE: with `MARTLET_UPDATE_CHECK_RECORD` set it records its arguments,
whether its console window shows, which processes share its console, its
priority and its working folder, writes one line to the `/LOG` file, for
`/STAGE=prepare` puts a text file where the staged Desktop app goes, and exits with
`MARTLET_UPDATE_CHECK_EXIT`). Three
installs run side by side: one another computer asked for
(`asked-by-another-computer`), an automatic one from the notification area whose
installer fails with exit 5 (`automatic-from-tray-fails`) and one you confirmed
(`confirmed`). For each, the steps check that the installer starts only after
Martlet's process exited (`waits-for-martlet`, with how long after: about a
second, since the helper looks again at once for its first rounds and then waits
one second for what Martlet started to let go of its files); its switches
(`installer-switches`: `/VERYSILENT` with no window at all for the unattended
two, `/SILENT` with only the progress window when confirmed, always
`/SUPPRESSMSGBOXES /NORESTART /SP- /TASKS=` and `/LOG=...\install.log`); that it
runs inside the helper's console, which shows no window (`helper-hidden`); that
Martlet starts again after it with `--data-directory`, plus `--after-update`
(minimized, no focus) when unattended and `--tray` when it was in the
notification area, without a console window of its own (`restarts`); that
`last-install.txt` holds the exit code and version (`records-result`); and that
`update.log` has every step (`logs-steps`). After the failed install the
installer log's tail, which Martlet copies into its log, is checked too
(`installer-log-for-failure`). It installs nothing, starts no real Martlet and
contacts nothing; the real Inno Setup installer is not run (`notCovered`). What
Martlet does with these files when it starts again shows in `logs_tail`
(`Update helper: ...` lines, then *Martlet updated to ...* or a WARN *The update
to ... didn't finish* followed by the installer log's last lines), and
`ui_snapshot`'s `windowStates` shows the restarted window `minimized` and not
`foreground` (launch the desktop with `-DesktopArguments '--after-update'`).
Steps `resume: ...` check the note Martlet leaves itself as it closes to install
(`updates\resume.txt`, `AppUpdateResume`), so the restarted Martlet shows the
character and starts listening and watching again: it is read once
(`picks-up-character-and-listening-once`), carries only what was on
(`only-what-was-on`, `watching-alone`), still reads a note from a Martlet that
didn't save watching yet (`reads-note-without-watching`), leaves nothing when nothing was on
(`nothing-on-leaves-no-note`) and is ignored after 15 minutes, when you started
Martlet yourself rather than the update (`stale-note-ignored`).

Four more scenarios check an update installed beside the running Martlet
(`AppUpdateStaging`), in a disposable program folder that holds an old Desktop
file and an `uninstall` folder. `<scenario>: prepares-beside-martlet` runs the
prepare step as the desktop does while it keeps working: the stand-in installer
gets `/VERYSILENT ... /STAGE=prepare /DIR=<program folder>.next
/LOG=...\stage.log`, runs at `Idle` priority with no console window, and leaves
the running version untouched. Then the helper runs with `staged: true`. In
`staged-switch` it renames the folders, moves the `uninstall` folder over and
removes the staging and previous folders (`switches-folders`); it runs only the
installer's quick `/STAGE=finish /VERYSILENT` step (`runs-only-finish-step`); the
helper and that step work in the updates folder, not in the program folder that
Martlet usually works in (a process working there stops the rename), and Martlet
starts again in its own folder (`works-outside-program-folder`); and
Martlet starts again within 5 seconds of exiting, with `last-install.txt` `0`
and an `update.log` line *Switched to Martlet ...* (`back-in-seconds`, with the
measured seconds). In `staged-folder-in-use` a file in the program folder is
held open, as a Doctor window or a tool server would. The helper tries the
rename for about ten seconds, logs *Martlet's program folder is still in use,
so it could not be switched. Running the whole installer instead.*, runs the
whole installer on the unchanged program folder, removes the staging folder and
starts Martlet again (`falls-back-to-whole-install`).
`staged-program-working-inside` does the same with a program that Martlet
started and that still runs after Martlet exits (as the character renderer's
WebView2 processes, Ollama or a browser can), working in the program folder's
Desktop folder: it also falls back to the whole installer. This is why an
installed Martlet moves its own working folder to your user profile folder when
it starts (it logs *Martlet works in ..., outside its program folder, ...*) and
starts the character renderer in the temp folder: the programs it starts work
there too. `staged-program-working-outside` starts that program in the folder
`AppUpdateStaging.OutsideProgramFolder` gives, and the switch succeeds
(`switches-folders`, `back-in-seconds`). Both steps also check that the program
still ran when the helper finished.

**Updates installed beside Martlet.** An installed Martlet (its Desktop folder
is beside the installer's `uninstall` folder) installs a downloaded update into
`<program folder>.next` while it keeps running, as a background task *Install
Martlet x.y.z*: the installer's `/STAGE=prepare` step, with no window and at
idle priority. An automatic one starts only while Martlet isn't replying or
hearing you. It logs *Installed Martlet x.y.z beside this one in n s.* When
Martlet then closes for the update, the helper switches folders and runs
`/STAGE=finish`, so Martlet is back in about a second instead of after the whole
install. It logs *Restarting into Martlet x.y.z, installed beside this one, ...*.
If that install fails, the update installs the classic way. With automatic
installs off, Install first asks *Install Martlet x.y.z (... MB) now?* (Martlet installs it in the
background), then asks `RestartIntoUpdateQuestion`, *Restart Martlet now to start
using x.y.z?*; `ConfirmationYes` (*Restart now*)
restarts and `ConfirmationNo` (*Later*) leaves `AppUpdateStatus` reading
*Martlet x.y.z is installed, so restart Martlet to use it (a few seconds),
or it switches over when Martlet exits.* `InstallAppUpdate` reads *Install x.y.z
now*, then *Restart into x.y.z*; clicking it needs `--allow-ui-effects`. A
development build has no program folder and installs the classic way. To
exercise this with the `MARTLET_SIMULATE_APP_UPDATE` fixture below, also set
`MARTLET_SIMULATE_INSTALL_ROOT` to a disposable folder with `Desktop` and
`uninstall` folders (FIXTURE): its install beside Martlet writes a text file as
the staged Desktop app after two seconds, and the restart renames that folder
for real. The finish step then fails at once (exit 216, the stand-in is not a
program), so the restarted Martlet logs *The update to 9.9.9 didn't finish*.

**Automatic installs in the desktop.** With *Install updates automatically*
(`AutomaticUpdateInstall`), a downloaded update installs at once, even with
Martlet's window in front, other Martlet windows open, the character showing
or always listening on. It waits only for a reply, speech being heard or
transcribed, a modal question, or work exiting would cut short; then
`AppUpdateStatus` reads *Martlet x.y.z is downloaded and installs as soon as
Martlet isn't busy. Waiting: <what>.* and the next one-minute tick tries again.
Installing logs *Installing Martlet x.y.z automatically, with no installer
window. Martlet closes and restarts into it[, showing the character, listening
and watching again].* (only what was on), and the restarted Martlet logs *Martlet restarted after its
update and is showing the character[, listening and watching] again, as before the
update.* To exercise this without GitHub or a real install, set
`MARTLET_SIMULATE_APP_UPDATE` to a version newer than the build (for example
`9.9.9`) before launching the desktop (FIXTURE): checks find that version
without contacting GitHub, its download is a small text file, and installing it
runs the real update helper, whose stand-in installer fails at once (Windows
can't run it), so nothing is installed; the helper records the failure and
starts Martlet again (`--after-update`, same data directory), which reports
*The update to 9.9.9 didn't finish* and doesn't install it automatically again
that session. Turning on `AutomaticUpdateInstall` (`ui_toggle`) saves
`updates.json`, so it needs `--allow-ui-effects`. Every download is a
background task, *Download Martlet x.y.z*: out of sight when Martlet downloads
by itself, in its run window when you chose Install. `TaskState-<id>` reads its
percentage while it runs and *Done at ... Martlet x.y.z is downloaded and ready
to install.* at the end, and its Cancel task stops the download (`AppUpdateStatus`
*The download of Martlet x.y.z was canceled.*). With the fixture the download
ends at once; to keep Martlet from installing it while you look, launch with
`MARTLET_SIMULATE_BACKGROUND_TASK` too: `AppUpdateStatus` then reads *... Waiting:
simulated background task (in Background tasks).*

**This PC's own host service after an update.** On a PC that runs a host
service (a host PC, or one paired with its host service on Docker Desktop),
Settings › App updates shows `OwnHostUpdateStatus` (returned; hidden on other
PCs): *This PC's host service runs Martlet x.y.z, like this app...*, *Updating
this PC's host service from Martlet a.b.c to x.y.z in the background; it stops
answering for a moment...*, *...is busy (<what>), so nothing was changed and
updating it ... waits until <time>.*, *Updated this PC's host service from
Martlet a.b.c to x.y.z at <time>...*, stopped, not running, or why the update
stopped. Martlet starts it right after its own startup (after an update, once its
window is back) and looks again every minute, whatever `AutomaticHostUpdate`
says; `logs_tail` shows *Updating this PC's host service from Martlet a.b.c to
x.y.z in the background.* and *Updated ... (n s).*, and `host-runs` the run
(*Keep this PC's host service current*). That run is a background task out of
sight: `TaskTitle-<id>` *Keep this PC's host service current*, `TaskState-<id>`
*Running for 1 s. Updating this PC's host service from Martlet a.b.c to Martlet
x.y.z...*, then *Done at ... This PC's host service runs Martlet x.y.z.* A try
that finds the host busy changes nothing and leaves Background tasks. Its
Cancel task stops it, and `OwnHostUpdateStatus` then reads *You canceled
updating this PC's host service to Martlet x.y.z, so it waits until you choose
Update hosts (above).*: Martlet doesn't start it again by itself for that version. To exercise it without touching a real
host service, set `MARTLET_SIMULATE_OWN_HOST` to an older version (for example
`0.37.0`, or `0.37.0,busy` to have the first try find the host busy) and
`DOCKER_HOST` to an unused named pipe before launching the desktop, on a
disposable data directory holding `device-role.txt` with `Host` (FIXTURE): this
PC then reads as running a host service of that version, and its update takes 20
seconds, contacts nothing, changes nothing and logs `FIXTURE` lines in
`host-runs`; a busy try is repeated about a minute later. With
`-DesktopArguments '--after-update'` the window stays minimized (`ui_snapshot`
`windowStates`) while `ui_click` navigation keeps working during the update.

`pc_scope` reads the [PC scope](../ACCOUNTS.md#pc-scope) that the desktop with
the data folder uses (optional absolute `dataDirectory`; the script gives a
disposable one). It is read-only and returns no paths, user names or SIDs:
`folder` (`machine-wide` for the default data folder, `data-folder` for any
other, or `override`), `where` (`%ProgramData%\Martlet`, *the data folder* or
`MARTLET_PC_DIRECTORY`), `sharedByWindowsUsers`, `folderExists`,
`everyWindowsUserCanChange` (whether `BUILTIN\Users` may change the folder and
its files; null when the folder is missing or not shared), `role` (`companion`,
`host`, or null when nobody chose yet), `roleReadFrom` (`pc-folder`,
`data-folder`, or this Windows user's earlier choice that moves to the PC
folder when the desktop starts), `chosenByThisWindowsUser`,
`hostServiceRoles`, `hostServiceRolesReadFrom` and `hostServiceSeenBy`
(`this-windows-user`, `another-windows-user` or `unknown`: whose Docker Desktop
Martlet last read the host service in). Setting `MARTLET_PC_DIRECTORY` to an
absolute folder before you start the desktop and the MCP server (the
`Invoke-MartletMcp.ps1` child processes inherit it) gives several disposable
data folders one PC folder, as two Windows users of one PC have. Example:
put `Host` in `device-role.txt` of one data folder, then start the desktop
there: `DeviceRoleWhere` reads *... Martlet moved this Windows user's earlier
choice there.* A desktop on a second, fresh data folder then reads `Host PC` in
`DeviceRoleSummary` and shows no welcome tour. `UseAsCompanion` there (with
`--allow-ui-effects`) makes the first desktop, still running, read `Companion
PC` within 30 seconds; its log says *Another Windows user of this PC made it a
companion PC, so it is one here too.* `role_lab ask host` on a third data folder
writes the PC folder too.

`virtualization_status` reports whether Windows is ready for Docker Desktop's
WSL 2 engine, from the same read-only checks the desktop runs before it starts
Docker Desktop (optional absolute `dataDirectory`, default the current user's):
`ready`, `blocked`, `firmwareOff`, `needsWindowsChanges`, `problems` and
`recovery` (plain words), `probeIssues` (which checks could not be read),
`firmware`, `hypervisor`, `virtualMachinePlatform` and
`windowsSubsystemForLinux` (`Enabled`, `Disabled`, `Absent` or `Unknown`),
`wsl` (version, `none` or null), `wslStatus` (`Available`, `Unavailable`,
`RestartRequired`, `Failed` or `Unknown`), `wslStatusExitCode`,
`hostComputeService` (`vmcompute`) and `hostNetworkService` (`hns`), each
`Running`, `Stopped`, `Disabled`, `Absent` or `Unknown`, `restartPending`
(Windows component servicing or Windows Update needs a restart, or null when
unreadable), `restartRequired`, `virtualMachine`, `summary`,
`dockerDesktop {installed, running, engine, failedStartCheck,
windowsCanFixFailedStartCheck, failedStartCheckNeedsWindowsChanges}` (`engine` is what
`docker desktop status` reports, for example `running`, `starting` or
`stopped`, or null when Docker Desktop doesn't answer within 15 seconds;
`failedStartCheck` is the Windows check Docker Desktop's engine last failed in
its current session, in Docker's words from its warning and error log, for
example `Virtual Machine Platform not enabled` or `No virtualization available`
(its window then says *Virtualization support not detected*), or null;
`windowsCanFixFailedStartCheck` is true when that check names something
Martlet's Windows setup installs (WSL missing or too old, for example
`checking WSL version: wsl is not installed`, or Virtual Machine Platform not
enabled), and `failedStartCheckNeedsWindowsChanges` when, in addition, the
Windows checks could not read that fact themselves (they timed out or were
unavailable): Docker Desktop's word then counts, `needsWindowsChanges` is true
and `problems` quotes it, so a slow Windows probe never hides missing WSL; a
run window then sets up Windows (one administrator prompt) instead of
restarting Docker Desktop; a
run window restarts Docker Desktop once when it is open but its engine stays
`stopped` at two checks in a row or its start check failed while Windows is
ready, and always after Martlet changed Windows for
it) and `continueSetup {pending, kind, task,
created, startsAtSignIn}`: the setup Martlet continues after a Windows restart
(`continue-setup.json` in the data directory, and whether the per-user `RunOnce`
entry that starts Martlet at the next sign-in exists). It reads CIM facts,
Windows services and pending-restart registry markers and runs `wsl --version`
and `wsl --status` in a hidden Windows PowerShell, plus `docker desktop status`
and `docker desktop logs --boot 0 --priority 1`. It changes
nothing, starts no Linux VM and returns no paths or distribution names.
`Available` means WSL's status command answered without a recognized WSL 2
problem, not that a VM or GPU workload was tested. WSL 2 unavailability is
recognized even when that command exits **0**; a WSL 1-only warning is not a
WSL 2 blocker. A service that is merely stopped is not missing or disabled:
Windows can start it on demand. An unrelated pending Windows update alone
does not block otherwise-ready WSL. When features report enabled but their
runtime is unavailable and Windows has a restart pending, `restartRequired`
is true and `needsWindowsChanges` false: restart Windows rather than
reinstalling features or repeatedly restarting Docker Desktop.

`host_service_status` reads this PC's own Martlet host service on Docker
Desktop (the one the host dashboard sets up) with the same production code as
the dashboard (`LocalHostService` in `Martlet.Core`): `stage` (`DockerMissing`,
`DockerNotRunning`, `NotSetUp`, `Stopped` or `Running`), `ready` (running,
answering and still at one of this PC's addresses), `version` (the
`martlet-host:x.y.z` image), `hostId`, `published`, `publishedEverywhere`
(true once the owner set outside addresses in *Outside access*: the network
holder then publishes the gateway on every address of this PC, so an overlay
network such as Tailscale reaches it; the home address comes from the holder's
`martlet.home` label), `addressOnThisPc` (false
once the network gave this PC another address than the one set up),
`answering` (a TCP connect to the published port), `roles` (the installed role
records, for example `["audio2face","f5","stt"]`; null until the gateway runs),
`network` (`unbound`, `bound`, `removed` or `unreadable`), `desktops` (`{id,
name}` of the active desktops in the host's Martlet network, that is the
computers paired with it, including this PC when it is one), `sharedGpu` (the
warning Companion › Voice and Devices show when the host's voice engine shares
this PC's graphics card with its other roles, or null; see
[Chatterbox](../CHATTERBOX_VOICE.md#sharing-the-graphics-card)) and `problem`
(Docker's first error line). It runs `docker container inspect` on
`martlet-host-gateway` and `martlet-host-net` and one `docker exec` that lists
the role records and prints `host_id` and `network.json`; it never reads the
agent token, keys, secrets or pairings, returns no addresses and changes
nothing. Setting `DOCKER_HOST` to a missing named pipe gives `DockerNotRunning`.

`work_sharing_status` shows Devices › **Sharing work** (optional absolute
`dataDirectory`, default the current user's; optional `deviceId`, default this
PC's): `file` (`loaded` or `none`), `shared` (the `work-sharing` shared
setting's canonical JSON), `isDefault`, `device`, `ownHost` (the pairing saved
as this PC's own host service), `paired`, `planHosts`, and for each of
`speaking`, `thinking`, `listening` and `deep-thinking`: `role` (the host role
that does it), `shares` and `sharedByDefault` (Speaking and Listening yes,
Thinking no), the `order` and `never` chosen, `planned` (the computer the job
uses now, from `settings.json`), `runs` (paired computers the shared plan says
run that role) and `tries` (the production `WorkSharing.Order` for that device;
null for Deep thinking, whose places come from Companion › Deep thinking), and
`kept` (computers kept for some companion PCs, with `usableHere`). It also
shows the pool lists: `poolsFile` and `poolsLocalFile` (`loaded` or `none`) and
`pools`, one entry for each area that has a list, with `area`, `title`, `page`,
`shared` (pools.json) or not (pools-local.json), `required`, `whenEmpty`,
`off`, `fallback`, `members` (`key`, `kind`, `name`, `on`, `onlyFor`,
`settings`, `usableHere` and, for a cloud member, `agreed`) and, for Speaking,
Listening and Thinking, `tries` (the production `PoolRouting.Hosts` for that
device; null without a route on a computer). Host and device IDs, provider and
model names only; never a key.

`external_endpoints_status` shows Devices › **Cloud providers and servers**
(optional absolute `dataDirectory`, default the current user's) with the
production `ExternalEndpoints`: `settings` and `mcpJson` (`none`, `loaded`,
`unreadable` or `invalid`), `summary` (the card's status line) and `endpoints`,
one for each cloud provider or server on your network that a saved choice sends
work to: the Thinking, Listening and Speaking routes and *If Thinking fails* in
`settings.json`, external Thinking pool members in `thinking-pool.json` (read
without migrating or saving it), address and cloud members of every pool list
in `pools.json` and `pools-local.json`, and HTTP MCP servers in `mcp.json`.
Each has `id`, `name`, `kind` (`cloud` or `server`), `address` (scheme, host and
port only; null for a cloud provider whose address Martlet doesn't know),
`text` (the card's line) and `uses` (`purpose`, `page`, `model` or the MCP
server's name, `on`). This PC (loopback) and paired computers are left out.
Never a key; read-only.

`work_sharing_check` rehearses the production planner and queue (`WorkQueue`)
on the four-computer example (machines 1 and 3 companions with Chatterbox,
machine 2 a companion with Deep thinking and no voice, machine 4 lip-sync and
pictures) with simulated computers that run one request at a time and turn
another away at once as a host's gateway does (`job.busy`), **NOT real hosts
or models**. Each step reports `passed` and its detail: each companion's order
with its own computer first; Thinking left unshared; a busy voice passed over
at once (`WaitedMs` near 0); machine 2 waiting while both voices are busy and
taken by whichever frees first (machine 3, `WaitedMs` about 250); four segments
at once spread two and two; a computer kept for one companion PC or unticked
for Speaking left out; an unanswering computer skipped; Deep thinking leaving
out a kept computer; and the shared setting's round trip. Then the pools:
Sharing work's choices moved into the Speaking list (`PoolMigration`); the
list's order on machines 2 and 3; a member turned off; Thinking's own model
first; an empty list off for Speaking and voice loudness for lip-sync; a
rate-limited cloud member (HTTP 429, `PoolRefusals`) passed over by its member
key; a cloud member in the list taking a segment only when the computer before
it is busy (a free first computer speaks at once and the cloud member is never
asked); and the `pools` shared setting's round trip. `ok` is true when every step
passed. On the desktop, `ui_snapshot` reads `WorkSharingStatus` (how many of
this PC's requests another computer took since Martlet started), each area's
`WorkSharingPool-<area>` line ("Speaking: In order: This PC, m3-host.") with
its `WorkSharingOpen-<area>` button (opens the area's page; a safe click), the
Thinking pool's `WorkSharingJob-deep-thinking`, `WorkSharingPlace-deep-thinking-<host>`
and `WorkSharingUse-deep-thinking-<host>`, and `WorkSharingHost-<host>` and
`WorkSharingKeep-<host>` (the Thinking pool's keep choice). Those choices save
`work-sharing.json`, so they need `--allow-ui-effects`.

The pool list control (Companion › Voice and Listening under *Where it runs*,
Companion › Thinking under *When the Thinking model is busy*, and each pooled
area's page) reads through `ui_snapshot` with `idPrefix` `Pool-<area>`:
`Pool-<area>-Summary` ("One member is on. Requests go to it.", "Off: nothing
in the list is on.", or for a required area "Nothing in the list is on.
Instead: Voice loudness on this PC."),
`Pool-<area>-Member-<i>` ("1: This PC: Each companion PC itself."; the page shows *1. This PC* and the line under it),
`Pool-<area>-On-<i>` (ticked or not), `Pool-<area>-Up-<i>`, `-Down-<i>`,
`-Remove-<i>`, `-Settings-<i>`, `-OnlyFor-<i>` and the Add buttons
(`Pool-<area>-AddThisPc`, `-AddHost-<host>`, `-AddGpu-<host>-<card>`,
`-AddAddress` with the `-Address` box). Settings (a member's settings under
its row), `Pool-<area>-Show-<key>` and `Pool-<area>-AddCloud` (a place's own
card under the list) are safe clicks. On, Up, Down, Remove, Add and the
kept-for choice save `pools.json` (or `pools-local.json`) and share it, so they
need `--allow-ui-effects`. On Voice and Listening the list's intro shows one
sentence with `Help-Pool-<area>-Intro` (the whole intro, with a link to the
wiki's *The order of the list*).

`network_recommendation_check` runs the production network recommender
(`NetworkRecommender`, Home's
[recommended setup for all your computers](../RECOMMENDED_SETUPS.md#recommended-setup-for-all-your-computers))
on built-in fixture networks, **NOT real computers**. It takes no arguments
and reads nothing. Each step names its rule (1 to 12) and reports `passed` and
its detail: the change list (kind, computer, benefit, summary and why), the
target roles, jobs, pools, Thinking pool and notes. The steps are: two
companion PCs and two hosts with nothing set up (companion PCs run no host
roles; Thinking gets Gemma 4 E2B on a card of its own; one more voice for the
second companion PC; no Thinking pool change; a note says the context window
planned for Thinking's card, 32,768 tokens there, and that a reply still sends
no more than before, `ctx`); a host with
two NVIDIA cards (Thinking and Deep thinking pinned to cards of their own, and
the reason says two language models on one card share its tensor cores); a
Windows host whose voice shares its card (the voice moves to a card of its
own, and the reason names the time to first audio); a crowded network (no card over its capacity); Deep thinking beside the
voice (it moves to the card no live job uses); a companion PC that runs
Singing and a Listening pool place while both hosts are gone for 155 minutes
(needed jobs first: with no provider key, Thinking runs on its own card and the
voice joins it, lip-sync follows the voice's loudness because a companion PC's
card is only for Thinking and the voice, and Singing goes first as Required
because it is optional; with a saved free key on an 8 GB card, Thinking uses
the free hosted model and the voice gets the card); the parts the owner turned
off (step 13: every part in priority order, and Singing, Deep thinking and
advanced lip-sync off with their roles removed); every way to extend Martlet
(step 13: Vision, Reading, Hearing and Smart home have their own lines with
Off on their Companion page; Vision on Thinking's own model, Hearing's model
of its own on a companion PC's card with a note, Reading off on this PC so its
`ocr` role goes, but not with a second companion PC, and Home Assistant stays
while Smart home is off); People and Motion (step 13: People names the Voice
recognition role on the first computer of its list and that role always
stays, also with People off; Motion keeps its HY-Motion role on the card with
room, its role goes from the card Thinking fills and from every computer when
the owner turns Motion off); People in the Recommended setup (step 13: with
People on and no host running the Voice recognition role, Reconfigure installs
it on a host's graphics card, the GPU route with the CUDA build, else on a
host's processor, the CPU route, and puts that host first in the Voice
recognition list; once applied nothing changes); heavy roles on a companion PC
(they move to the host, Improvement); the setup order (step 12: job by job,
Thinking first, each job make before break); Thinking on a companion PC's card with
only a processor host (it stays: no added latency); hosted Thinking that the
owner chose (it stays, unless everything is kept local); a host left out of
the Thinking pool; the voice host not answering just now, for 4 and for 25
minutes (Speaking moves as Required each time, and nothing changes on that
host); the applied recommendation (no changes and the
same fingerprint in any order); and two `kept` steps: two equal hosts where one
kept Thinking's model from a role it turned off (Thinking goes there, the change
downloads nothing and says *already downloaded*), and a part turned off (the
review says *Turn off* and that its downloads stay). Four `voice` steps check the fallback when no
computer has room for the owner's voice engine: Chatterbox Nano on a 4 GB card;
Chatterbox Nano on the processor (about 8 threads) with no card; the hosted
voice with a saved key when no computer can run an engine; otherwise a note
that says how to set up the host service. A `preferences` step checks the
owner's recommendation preferences: on a companion PC alone with a 12 GB card,
Balanced thinks with Gemma 4 E4B beside the voice and Quick replies with Gemma
4 E2B; with no card and a saved free key, online services only as a backup
thinks on the processor and Yes with NVIDIA Build; a host card share of 50%
plans with 12 GB of a 24 GB host card. `ok` is true when every step passed.

`node_presence_status` shows when your other computers go away or come back
([CLUSTER](../CLUSTER.md#when-a-computer-goes-away-or-comes-back); optional
absolute `dataDirectory`, default the current user's): `awayMinutes` (Settings
› Your other computers, `node-presence.txt`, 10 by default) and
`awayMinutesSaved`, the rules (`missingAfterSeconds` 30, `backAfterSeconds`
30, `backShownForMinutes` 10), `report` (`loaded`, or `none` until the desktop
writes `node-presence.json`, which it does when it starts and when a
computer's state changes), `updatedAt`, `companion`, `ownHost` (left out of the
notices), each paired computer in `hosts` (`state` `Answering`,
`NotAnswering`, `Missing`, `Away`, `Returning` or `Back`, `since`, `backAt` and
`awayForMinutes` as of now) and the `notices` Home shows (`id`, `level`,
`title`, `detail`). Host IDs, computer names and times only.

`node_presence_check` rehearses the production rules (`PresenceWatch`,
`NodePresenceNotices`, `NodePresenceSettings`, `NodePresenceReport`) on
scripted timelines: a check every 15 seconds with the desktop's presence rule
and a 5-second tick, **NOT real hosts**. Each step reports `passed` and the
events with the second they are dated and the second they were noticed: one
missed check says nothing; 30 seconds of silence goes missing once (noticed at
second 45 for a host silent from second 15), with a notice that names the
failover move, the job that waits, the pools that go on and the one with no
other computer; still missing after 10 minutes stays away once; 30 seconds of
answers comes back once, and the back notice clears after 10 minutes or when
dismissed; back before the away time never stays away; a flapping computer is
missing, away and back once each; the away time follows the per-PC choice
(parsing 1 to 240 minutes); the report's round trip; and an unpaired computer
is forgotten. `ok` is true when every step passed. On the desktop, `ui_snapshot`
reads Home's `HealthIssue-presence-missing-<hostId>` ("Warning: Working with
less: gpu-box isn't answering. It hasn't answered for 2 minutes. ...") and
`HealthIssue-presence-back-<hostId>`, the Devices map's `Node-host:<hostId>`
("gpu-box, 192.0.2.10. Not answering for 2 min. ..."), and Settings'
`PresenceAwayMinutes` and `PresenceAwayStatus`. Choosing another time with
`ui_select` saves `node-presence.txt`, so it needs `--allow-ui-effects`. The
back notice's Dismiss (`HealthOpen-presence-back-<hostId>-dismiss`) is passive.

`host_connections_selftest` (no arguments) rehearses how the desktop connects
to a paired host and reports its status (`src\Martlet.NodeLinkCheck`, mode
`host-connections`, `HostConnectionRehearsal.cs`; returns `{exitCode, report}`).
One real gateway (`lab-connections`; Kestrel, pinned TLS, signed requests) runs
on 127.0.0.1 behind a loopback TCP forwarder that the check stops and starts at
the same address. A simulated desktop checks the host as the 15-second sync does
(a new `HostConnection` per check: routes, then the plan copy) and
logs through the desktop's status tracker (`HostAnswers`). Its steps: twelve
checks share one kept TCP and TLS connection (`HostRoutes` connections dialed
and connections the forwarder accepted are both 0 after the first); the host
stops and one missed check logs nothing, the second logs `Host lab-connections
stopped answering: ...` once, later misses log nothing, and the route status
says the home address didn't answer (refused); the host comes back and the next
check logs `Host lab-connections answers again.` once over one new connection;
a host that misses every other check logs nothing; and the host is paired
again (a new credential, the old secret deleted as the desktop deletes it): a
job saved with the old pairing reaches the host through the desktop's choice of
pairing (`HostPairingChoice`, this PC's current pairing with the same host ID),
and the step says whether the host still accepts the old credential. The
report's `log` holds the lines. Loopback only; writes nothing. Windows running
out of ports (`NoBufferSpaceAvailable`) is not simulated; unit tests check its
wording.

**Sharing characters with the household** ([ACCOUNTS](../ACCOUNTS.md#sharing)) is on the
same page. Each of your profiles has a choice, `CharacterProfileSharing-<key>`
("Private", "Share a copy with the household" or "Share together with the
household"), and a state line, `CharacterProfileShared-<key>`. The state line
reads "Private: only you see and use it.", "Shared as a copy: ..." or "Shared
together: ...". A character someone else shares with you together reads
"Shared with you together: ...". Its `CharacterProfileEdit-<key>` is disabled,
and `CharacterProfileLeave-<key>` replaces Remove and asks first. The
**Household characters** card lists the characters other people share (its
*?* is `Help-HouseholdCharacters`).
`HouseholdCharactersStatus` reads "2 characters shared by 1 person in your
household." or "Nobody in your household shares a character yet.". Each row has
`HouseholdCharacterState-<key>` ("Shared as a copy.", "Shared together: talk to it
to add it to your profiles.", and why a part can't switch here). Each row also
has a button: `HouseholdCharacterCopy-<key>` (*Use a copy*: a copy with new IDs
and its own memories, then switched to) or `HouseholdCharacterJoin-<key>` (*Talk to
it*: the character with the same IDs, then switched to; *Use* once it is in your
profiles). These state lines are counts and fixed text, never a name. The
choice, Use a copy, Talk to it and Leave need `--allow-ui-effects`. The
desktop log records *Sharing: character <key> is now together.*, *Sharing: used
a copy of a household character (<key>) as <key>.* and *Sharing: this account
talks to a character shared together (<key>).*. `household_sharing` and the
`sharing` field of `character_profiles` read the same state headlessly.
