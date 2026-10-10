# H03 authenticated gateway contracts

**Standalone internal library and controlled HTTPS contracts, not a deployed
LAN service or completed H03 acceptance.** `Martlet.Gateway` implements the
security boundary and bounded worker-backed inference routes. It does not join
`Martlet.slnx`, Desktop, Core settings, Doctor, packaging, host artifacts, Compose, systemd, firewall
or installer graphs.

The project performs no startup work by construction. A caller must supply one
exact private/loopback origin, an existing server certificate/private key, a
host identity derived from that certificate, workers, an audit sink and an
explicit listener factory. It never generates or installs a production host
key/certificate, changes a trust store, opens a firewall, publishes a Docker
port, runs an administrative command, downloads a model or activates inference.
The CLI still supplies no workers and truthfully advertises no inference.

The separate [Linux host executable candidate](../Martlet.Gateway.Host.Linux/README.md)
now supplies explicit private-IP binding and locally approved unattended restart
over the durable owner. Existing Windows CLI/default factories stay loopback-only;
native Linux, two-host LAN and container qualification remain NOT RUN.

`GET /health/live` remains exactly `{"status":"live"}`. The same TLS listener
also exposes the nonsecret, credential-free `GET /health/ready` contract:
`{"schema_version":1,"scope":"listener-auth-admission","listener":"listening","auth_admission":"open","model_readiness":"not-probed"}`.
It returns 200 for currently open authority admission, or 503 with
`auth_admission:"closed"` for known stopping/closure or clock/storage failure.
It does not list identities/devices, issue credentials, admit nonces, checkpoint,
call workers or prove model/future-storage readiness. The observation is a
separate internal read-only capability; request credential access remains
authenticate-only. New TLS handshakes can themselves fail after authority closure.
Authenticated role-scoped `/martlet/v1/status` is unchanged. No admin route or
extra port is introduced; see the executable guide for its exact bounded pinned
health command and evidence limits.

The dedicated tests use generated fixture certificates and controlled synthetic
workers only (**NOT AI**). They do not contact Ollama, F5, a LAN host or a public service.
The runtime fixture certificate is loaded as a user-scoped key handle because
Windows Schannel cannot serve an ephemeral key; it is never added to a
certificate store and is disposed with the in-process listener.

## Reuse lineage and authority boundary

**Current policy: permanent paired-device trust, protocol 2.0.** This revision
deliberately replaces the inherited timed-device contract. Devices do not
expire, unpair on reboot or lose pairing after recoverable interruption.
Invitations, signed-request freshness/replay, rotation overlap and action
budgets remain independently bounded. Protocol-1 pairing is refused; source
clients must explicitly adopt the required lifetime discriminator, not default
missing/null expiry to permanent. Existing route paths, IDs, roles, pins and
HMAC canonical bytes remain stable. See the durable owner's
[version and recovery contract](../Martlet.Gateway.Persistence/README.md).

This is the standalone H03 source slice from
`ec6db3979a1e2b8164176d455e4017475b9865ca`, reused by an attributed cherry-pick.
It preserves the transport/authentication protocol and host identity expected
by the earlier Gateway consumers. A focused follow-up adds a 128-registration
ceiling, inactive-state reclamation and shared observed-UTC rollback closure;
identity encoding, credential bytes, roles, HMAC canonicalization and successful
wire documents are unchanged. The inference layer is a focused reuse of
`d2a5db82fe559dc332151c87c1f6dd82c6102a1b`, resolved from the frozen
`1715d194c03e9b1399c93c4f496f382e7076afc5` lineage. Only its inference contracts,
HTTP/JSON/registry/perception output and tests were reused, with additive signing
and capability changes. Old security/pairing/server snapshots were not restored.
Current F5, Perception and Providers APIs are referenced without changing them.
Memory.Service, Host.Doctor and Host.Setup are not imported.
Core installation planning, settings, Desktop and HostArtifacts v2/catalog
remain independent and unchanged.

### Schema 5 client foundation

The client-only portion of `37181f8d2261c8d97d71b9ca297ff3bc278b7074`
is adapted to the current protocol-2 authority, not restored as an old
pairing/security/server snapshot. `GatewayPairingClient` requires the exact
pinned HTTPS origin and explicit pairing action; its response must contain
protocol major 2 and an explicit `paired` lifetime. Timed-v1, missing/null
lifetime and retiring credentials cannot become permanent client credentials.
`ScopedGatewayCredential.Restore` reconstructs only a secret-bearing client
bound to origin, host/SPKI, device and voice role. It is not a public
server-authority import or registration API.

`GatewayAuthenticatedClient` offers explicit bounded capability and Ollama/F5
requests only. It performs no startup work, key discovery, automatic re-pair,
probe, retry, model download or provider instantiation. Capability conversion
is passive, not proof that native runtime, reference rights or model inventory
is eligible. Outer gateway protocol 2 is separate from the F5 inner protocol 1.
Each inference action still requires its caller's fresh exact data/cost/resource
permission; pairing alone grants no action permission. Signed POST bytes,
bounded strict JSON/NDJSON, original cancellation/deadlines and clean EOF before
terminal publication are retained. Disposing the client clears its signer
material without deleting server device trust.

The shipped Desktop currently remains Fixture/OpenAI-only. The mandatory
second integration layer owns actual self-host UI/dispatch and expanded
transitive payload/notices/RID locks. This foundation does not qualify an AI
engine, Ubuntu deployment, host installation or native model execution.

The newer [Gateway.Trust foundation](../Martlet.Gateway.Trust/README.md) remains
isolated and **uncomposed**. These are alternative implementation lineages, not
two production authorities or interchangeable credentials. This reused Gateway
is the transport/authentication lineage for subsequent earlier-consumer reuse;
neither library currently owns a deployed host. The permanent protocol-2
Gateway.Persistence owner is the sole composed durable authority; no Trust
bridge, timed-device store, expiry sentinel or secondary approval surface is added.

| Boundary | Reused `Martlet.Gateway` | Isolated `Martlet.Gateway.Trust` |
| --- | --- | --- |
| Host/device identity | Bounded identifier strings; certificate-derived `sha256:` plus lowercase SPKI hex | Nonempty UUIDs; supplied uppercase SPKI hex without prefix; no TLS verification |
| Authorization | `voice`, `perception`, `memory`; per-request scoped HMAC, timestamp and nonce | Independent status/transcription/generation/synthesis/perception/memory-read/memory-write flags; point-in-time secret authorization, no signed-request replay contract |
| Secret ownership | Base64url strings exposed explicitly by `Reveal`; SHA-256 verifier is an HMAC signing key | Disposable 32-byte buffers with explicit copy/import; SHA-256 verifier for equality checks |
| Pairing and rotation | Eight local windows, five failed proofs/window; direct local rotation with up to ten-minute overlap | Sixteen pending approvals, authority-wide redemption limit; one-use locally approved renewal with two-minute overlap |
| State/time | At most 128 non-expiring paired/retiring records, bounded per-credential nonces; shared observed-UTC access closure | Volatile bounded live-device state; UTC and monotonic device expiry with rollback closure |

There is no conversion, fallback, shared authority, secret cast, scope widening
or credential interchange between these namespaces. Even though both use
SHA-256 over random device secrets, persisting a Gateway HMAC verifier requires
**signing-key protection**, not treating it as a harmless password hash.

The public volatile constructor has no durable import/export or restart recovery;
use the durable owner when pairing must survive process lifetime. It does not
inherit Trust's device-expiry policy or interchangeable secret/scope types.
The optional [canonical durable owner](../Martlet.Gateway.Persistence/README.md)
now supplies a Windows current-user DPAPI/private-NTFS backend and composes this
same server privately. It preserves HMAC signing keys **and live replay history**
across clean restarts, commits admission/rotation/revocation before success, and
recovers validated prepared transactions without resetting devices after a
crash or reboot. It never imports Trust records or restores a stale backup.
Its protocol-distinct schema, automatic same-key renewal and native
limitations are documented there.

The existing public constructor remains the explicit volatile path, not a
fallback when durable open fails. Production composition must keep
`GatewayServer` and its local management objects private to trusted host code,
not expose them through remote DI. Internal HTTP dependencies now receive only
pairing-exchange and signed-request authentication capabilities. The durable
owner is default-No and loopback-only. Its optional trailing `inferenceWorkers`
argument preserves existing positional calls, is not enumerated under No, and is
bounded/validated before durable creation under Enable. Local CLI approval
exists separately; service/backend qualification and deployed-host recovery
remain separate gates.

## Standalone local workflow

Use the exact SDK **10.0.401** selected by `global.json`. Point `$sdk` at an
existing owner-supplied SDK and keep build/test state in a private local
directory. These developer commands do not install, bind or launch a persistent
gateway.

```powershell
$artifacts = 'C:\YOUR-PRIVATE-DEVELOPMENT-DIRECTORY\gateway-artifacts'
$env:CI = 'true'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
& $sdk restore tests\Martlet.Gateway.Tests\Martlet.Gateway.Tests.csproj --locked-mode --artifacts-path $artifacts
& $sdk build tests\Martlet.Gateway.Tests\Martlet.Gateway.Tests.csproj -c Release --no-restore --artifacts-path $artifacts
& $sdk test tests\Martlet.Gateway.Tests\Martlet.Gateway.Tests.csproj -c Release --no-build --no-restore --artifacts-path $artifacts
```

No root-solution edit or separate ASP.NET package is required. The gateway uses
the shared `Microsoft.AspNetCore.App` framework in the pinned SDK/runtime; the
test project uses the repository's central xUnit/Test SDK pins and committed
locks.

## TLS origin and host identity

`GatewayOrigin` accepts only canonical `https://IP:port` text:

- IPv4 loopback, RFC 1918 `10/8`, `172.16/12` or `192.168/16`;
- IPv6 loopback or unique-local `fc00::/7`;
- one explicit decimal port from 1 through 65535;
- no HTTP, wildcard/unspecified/public address, DNS alias, userinfo, path,
  query, fragment, trailing slash, IPv4-mapped IPv6 or alternate spelling.

This intentionally excludes discovery names and link-local scope syntax from
the foundation. A changed address never supplies trust; the independently
compared host ID and SHA-256 SubjectPublicKeyInfo fingerprint do.

`GatewayTlsBinding` accepts only a caller-supplied, currently valid,
non-CA RSA/ECDSA server certificate with a private key, digital-signature use
and server-authentication EKU. One IP subject alternative name must cover the
selected binding address. Its SPKI must exactly match the supplied
`GatewayHostIdentity`. Renewal with the same pinned key remains possible;
a changed key fails closed and requires a separately verified re-pairing or
future authenticated transition policy.

`KestrelGatewayListenerFactory` binds that one address only, serves HTTP/1.1
over TLS 1.2/1.3, suppresses the server header and defaults request bodies to
8 KiB. Only recognized inference routes raise the per-request limit to their
advertised bound (at most 5,700,000 bytes); cancellation is limited to 2 KiB.
It also caps concurrent connections at 64, request headers at 32 /
16 KiB, header receipt at five seconds and keep-alive at 30 seconds. There is
no HTTP listener or redirect endpoint. The listener is injectable so tests and
future host composition do not need to alter OS state.

`PinnedGatewayClient`:

- pins the expected host SPKI and checks certificate validity/name;
- permits only an otherwise untrusted/partial private chain, while rejecting
  every other chain problem;
- disables certificate downloads/revocation-network probes, proxies, cookies,
  ambient credentials, decompression and automatic redirects;
- sends only to the exact selected HTTPS IP/port;
- uses a ten-second deadline through response headers and a five-second connect
  deadline; response-body reading/cancellation remains the caller's responsibility;
- returns bounded connection/deadline failures without a raw URI/exception;
- disposes and rejects every 3xx response without following `Location`.

There is no accept-any-certificate mode and no global CA installation.

## Explicit pairing and device lifecycle

Only trusted local host code can call `GatewayPairingService.OpenWindow`.
There is no network route that opens or approves an owner pairing window. The
one exception to "pairing starts on the host" is the owner's
[Martlet network](#martlet-network-member-pairing): a desktop the host's
roster lists as an active member may pair by itself with its network key. The
local caller freezes one device ID, display name and least-privilege role set
(`voice`, `perception` or `memory`). The resulting out-of-band card contains:

| Field | Bound |
| --- | --- |
| Host identity | Host ID plus exact `sha256:<64 lowercase hex>` SPKI |
| Destination | Exact approved private/loopback HTTPS origin |
| Pairing ID | Random 16-byte canonical base64url value |
| Pairing token | Random 32-byte canonical base64url secret |
| Lifetime | At most five minutes |
| Open windows / failed proofs | At most 8 / at most 5 per window |

The host retains only the pairing-token SHA-256 verifier. The client sends the
token in the bounded JSON body of `POST /martlet/v1/pair`, never in a URL. The
proof must repeat the out-of-band host ID, pin and approved device ID. A valid
proof consumes the window atomically; expired, failed-limit and previously used
windows cannot be revived.

### Short typed codes

`GatewayPairingService.OpenCodeWindow` (local host code only, like
`OpenWindow`) opens a window that a person redeems by typing instead of pasting
a card: the host shows its address and an 8-character code such as `K7QM-4XPA`
(32 symbols without `I`, `O`, `0` or `1`: 40 bits), one use, at most two such
windows open. A code window has **no deadline**: it stays open until a desktop
redeems it, five wrong proofs close it or the host withdraws it (the owner
cancels, or the pairing run ends and the service closes pairing);
`GatewayPairingService.IsOpen` tells whether it still works. The owner approves only the roles; the
desktop that proves the code names itself (device ID and display name) when it
redeems it. `GatewayPairingCode` defines the exchange, and desktops implement
the same derivation:

1. The desktop connects to the typed address without a pin (`GET /health/live`)
   and notes the SPKI fingerprint that answered; later connections in the same
   attempt must present that key.
2. It derives `K = PBKDF2-SHA256(code, "martlet-pair-code-v1\n" + fingerprint +
   "\n" + nonce, 100,000)` with a fresh 16-byte nonce and sends
   `POST /martlet/v1/pair/code` with `device_id`, `display_name`,
   `client_nonce` and `proof = HMAC(K, "martlet-pair-code-v1 client\n" +
   device + "\n" + name + "\n" + nonce)`.
3. The host derives `K` from each open code and its **own** fingerprint. A match
   issues the credential and adds `host_proof = HMAC(K, "martlet-pair-code-v1
   host\n" + host ID + "\n" + device + "\n" + credential ID + "\n" + secret +
   "\n" + nonce)` to the usual pairing response. A miss counts against every
   open code window (each closes after five).
4. The desktop pins the fingerprint only when `host_proof` verifies.

A key swapped in on the network changes `K`, so the real host refuses the proof
and the impostor cannot answer without the code. Guessing online stays bounded
by the five wrong proofs. One observed attempt leaves an offline search of 2^40
codes at 100,000 PBKDF2 iterations each; because the code has no deadline, that
search is bounded by the owner instead: the desktop that was answered by an
impostor reports that the computer could not prove the code, and withdrawing
that code on the host (and showing a new one) ends the search.

### Martlet network member pairing

`GatewayNetworkStore` (`GatewayNetwork.cs`) keeps the owner's
[Martlet network](../../docs/NETWORK.md) roster this host accepted
(`Martlet.Core.Network.NetworkRoster`, saved through
`GatewayServer.AttachNetworkStorage`):

- **Binding.** A host in no network is bound by the first paired device that
  posts a roster in which that device is an active member desktop and this host
  is listed with its own SPKI fingerprint. The owner already approved that
  device's pairing locally, so its network is the owner's. Afterwards only
  rosters of the same network are merged; another network is `network.other`
  until `martlet-host network-reset` (or until the host is removed from its
  network).
- **Merging.** Entries are ECDSA P-256 signatures by member desktops. The host
  accepts an incoming entry only when its signer is an active desktop in the
  roster it already accepted (removals first, so a desktop removed in the same
  copy vouches for nothing else). Per computer the newest entry wins; a removed
  desktop key never becomes active again.
- **Revocation.** When a desktop is removed (or its key changes) the host
  revokes every credential of that device ID. When this host is removed it
  revokes every network desktop and keeps the roster, so desktops learn of the
  removal; a member adding it again binds it again.
- **Member pairing.** `POST /martlet/v1/pair/member` carries the network ID,
  device ID, display name, a millisecond timestamp (within two minutes), a
  16-byte nonce (refused when seen again) and a signature over
  `NetworkPairing.ProofBytes` (this host's ID and SPKI, network, device, name,
  timestamp, nonce) by the device's roster key. The desktop pins this host's
  SPKI from the roster. A match revokes that device's older credentials here
  and issues one `voice` credential in the usual pairing response (no
  `host_proof`). At most 30 attempts a minute.
- **Joining.** A paired device that is not a member posts its display name and
  public key to `/martlet/v1/network/join`; the host keeps up to 8 requests for
  an hour (one per device ID, which comes from the caller's credential) with a
  six-digit check number. Member desktops see them in `GET /martlet/v1/network`
  and approve by signing the device into the roster, or turn one down with
  `/martlet/v1/network/deny`.
- **Who uses this host.** `GET /martlet/v1/network` also lists every computer
  with a live pairing here and when it last made a signed request (kept in
  memory, so unknown until it calls again after a restart). Any paired device
  sees the list, so every computer shows who is connected, whether or not the
  others are members yet.

Failures: `network.unbound` (409), `network.other` (409), `network.denied`
(403: not an active member, or a removed key asking to rejoin) and
`network.invalid` (400: unsigned or malformed roster, or a binding roster that
does not list this host). The in-process rehearsal is MCP `network_selftest`.

Pairing returns a random 16-byte credential ID and one 32-byte device secret.
The clear secret is returned once and is excluded from all registration,
audit, status, error and `ToString` surfaces. The host retains a SHA-256
verifier used as the HMAC key; the raw secret is not retained. Protocol 2 returns
required `lifetime: {"kind":"paired"}` with no expiry. There is no timed-device
constructor option, date sentinel or silent missing-field fallback.

Local host management code can list non-secret registrations, revoke one
credential/device, or rotate a credential. Rotation issues a fresh ID/secret
and shortens the old credential to an explicit overlap of at most ten minutes.
Revocation clears its nonce cache. A revoked or rotated-out
credential never falls back to another credential and cannot be reconstructed
from configuration.

The store retains at most 128 registrations, including live rotation overlap.
Issuance, listing and authentication sweep retired/revoked records, zero their
verifiers and clear their nonces; revocation also zeros the verifier immediately.
Later requests for swept IDs return `auth.invalid`, never restored authority.
Listing reports currently active registrations, not a durable audit history.
Capacity exhaustion returns `auth.capacity` without evicting active credentials
or live replay history. A refused rotation leaves the original unchanged; a
valid pairing proof refused for capacity remains usable until its original
window expires or is otherwise consumed/closed.

Pairing and credential operations share a serialized observed-UTC high-water
mark. A step back of at most 30 seconds (`MaximumClockStepBack`; Windows' NTP
sync routinely steps the clock back about a second and a WSL2/Docker VM follows
its host) holds the mark: observed time pauses until the clock passes it again,
so it never moves backwards and replay and expiry stay monotonic. A larger
backwards movement permanently closes their shared authority,
clears retained credential verifiers/nonces, and rejects every existing pairing
card and subsequent approval with `auth.clock_invalid`. Correcting the clock
does not reopen the same in-memory instance. With the durable owner, correct
the clock and reopen the same protected store: pairing data was not erased. The
store reopens when the clock is at most the same 30 seconds behind its saved
mark and resumes from that mark; the Linux host's `serve` exits (code 4,
`authority.closed`) once its authority closes so Docker or systemd restarts it
and reopens the same state. Host certificates are valid from 30 seconds before
they are made, so held time still accepts them.
The standalone volatile constructor has no persistence guarantee.
Clock sampling occurs inside the store lock, not before admission.

### Sign-in enrollment

`GatewaySignInService` (`GatewaySignIn.cs`, routes in `GatewaySignInHttp.cs`)
lets a computer away from home pair by signing in
([NETWORK](../../docs/NETWORK.md#joining-from-outside-home-by-signing-in)).
`GET /martlet/v1/signin` lists the ways to sign in (`id`, `kind`, `name`, `redirect_port` when fixed;
nothing secret). `POST /martlet/v1/signin/begin` (`provider`, and for a browser
provider a PKCE S256 `code_challenge` and a loopback `redirect_uri`
`http://127.0.0.1:<port>/`) returns a one-use attempt (`attempt_id`, `state`,
`nonce`, `expires_at` ten minutes on, and a browser provider's `authorize_url`).
`POST /martlet/v1/signin/complete` (`attempt_id`, `device_id`, `display_name`,
`proof`; for the owner account `{user, password, code}` where `code` is a
current TOTP code or a recovery code; for an OpenID Connect provider
`{query, code_verifier}`, the loopback callback's query and the PKCE verifier,
with which the host exchanges the code itself: `GatewaySignInOidc.cs` checks
the ID token's signature against the issuer's keys and its `iss`, `aud`/`azp`,
`exp`, `iat` and `nonce`; discovery and keys are cached for an hour; Discord and
Steam take the same `{query, code_verifier}`: the host exchanges Discord's code
with HTTP basic client authentication and reads `/users/@me`, and checks a
Steam OpenID 2.0 assertion against the attempt's `return_to` and with Steam's
`check_authentication`, `GatewaySignInDiscordSteam.cs`) answers
`201` like pairing (`credential_id`, `credential_secret`, `roles` `["voice"]`,
`lifetime` `paired`) plus `signed_in` (`provider`, `subject`, `label`) and
`access` (`member`, or `friend` for an identity allowed as a friend: a
[friend's credential](#friend-access)). A sign-in never takes another
computer's device ID: `signin.device_taken` (409) when the ID belongs to an
active member desktop of the host's network (it pairs by its network key and
never signs in) or has a live credential that this identity's earlier sign-in
didn't issue (a pairing made another way, or another identity's computer);
otherwise it replaces that earlier sign-in's credential. Failures: `signin.unavailable` (404),
`signin.invalid` (401), `signin.not_allowed` (403), `signin.expired` (400),
`signin.device_taken` (409), `signin.friends_full` (409), `signin.provider` (502); each sign-in outcome is recorded with the request
guard under route class `signin` and the claimed or verified account as
subject, and `TryAdmit` adds the per-account lockout. The member-only
`GET`/`POST /martlet/v1/signin/settings` (signed; `signin.denied` for a
non-member while the host is bound) read and change the owner account
(`owner`: `user`, `password` of 12+ characters, `totp_secret` Base32, `code`
proving the app took it; returns `recovery_codes` once), `recovery-codes`,
`remove-owner`, `allow`/`disallow` (`provider`, `subject`, `label`; `allow`
also takes `access`: `member`, the default, for the owner's own computers, or
`friend`; allowing an identity again replaces its entry),
`provider` (`provider_config`: `id`, `kind` `oidc`|`discord`|`steam`, `name`,
`issuer`, `client_id`, `client_secret`, `scopes`, `redirect_port` for a
provider that only takes registered redirects; an omitted secret keeps the
saved one) and `remove-provider`; the answer never contains a secret
(`has_client_secret` only), lists `access` for each `allowed` identity and
`enrolled` computer, and also carries `usable` and `blocked_reason`
(`GatewaySignInSettings.BlockedReason`, `GatewayServer.SignInBlockedReason`:
null when an owner account or a provider with an allowed identity (a friend
counts) exists,
otherwise `signin.not_set_up` or `signin.no_allowed_identity`) and
`removed_from_network` (computers whose sign-in was removed and that member
desktops still have to remove from the roster). Storage is `IGatewaySignInStorage`
(`GatewayServer.AttachSignInStorage`, `DurableGatewayHost.AttachSignIn`); with
none attached nobody can sign in. Enrollments whose identity is no longer
allowed, or no longer with the access it signed in with, are dropped and their
credentials revoked whenever the settings are read (a friend's exactly the
credential its sign-in issued, the owner's computers every credential of the
device). A join request from a computer enrolled as one of the owner's carries
`sign_in` in the network document (a friend's never). The host remembers the network key an enrolled device
asks to join with (`/network/join`); when its sign-in is removed the network
document lists it for member desktops (`sign_in_removals`: `device_id`, `key`,
`provider`, `subject`, `label`, `at`; never a friend's computer, which never
joined), which remove it from the roster, and the
host forgets the record once the roster shows it removed.

## Signed request and replay contract

Every authenticated request uses:

```text
Authorization: Martlet-HMAC <credential-id>.<base64url-HMAC-SHA256>
X-Martlet-Nonce: <24 random bytes, canonical base64url>
X-Martlet-Timestamp: <canonical Unix seconds>
X-Martlet-Role: voice | perception | memory
```

The signature covers an ASCII canonical record containing the protocol label,
host ID, credential ID, exact method, raw path/query target, requested role,
timestamp, nonce and SHA-256 body digest. Metadata routes remain bodyless.
`GatewayRequestSigner.Sign(request, role, body)` signs the exact POST bytes;
callers must send those bytes unchanged. Bounded body reading precedes hashing,
authentication precedes JSON parsing/worker dispatch, and durable nonce commit
precedes principal publication. A fresh nonce is not permission to replay a batch.

The server first verifies the credential and signature, then requires a
timestamp within two minutes, the requested role in the credential scope and a
previously unseen nonce. It retains nonces for four minutes with a hard maximum
of 1,024 per credential. Saturation fails closed rather than evicting a still
valid replay record and returns `auth.rate` with a four-minute drain remedy.
Method, path, role or timestamp changes invalidate the signature; replaying
the same request returns `auth.replay`.

### API keys for software outside the network

Software that is not one of the owner's paired computers sends a network API
key as `Authorization: Bearer martlet_<id>.<secret>` instead of a signed
request ([API guide](../../docs/API.md)). `GatewayApiKeyStore`
(`GatewayApiKeys.cs`) keeps the network's `Martlet.Core.Access.ApiKeyList`
(saved through `GatewayServer.AttachApiKeyStorage`; names, scopes, expiry and
SHA-256 verifiers, never a secret) and checks the secret in constant time.
Endpoints opt in per scope; every other endpoint answers a bearer request with
`key.scope` (403) before reading anything else, as does a key without the
scope. Unknown or wrong keys are `key.invalid`, revoked `key.revoked` and
expired `key.expired` (401). A key's principal (`DeviceId` `api-key-<id>`,
credential ID = key ID, role from the route, `X-Martlet-Role` or `voice`) is
vouched for by the key store while an inference job runs, so revoking or
expiring the key stops it like revoking a device credential. Keys carry no
nonce: TLS with the pinned host key protects them in transit.

| Scope | Admits |
| --- | --- |
| any key | `GET version` (with `api_key`: ID, name, scopes, expiry), `GET capabilities` |
| `read` | `GET status`, `GET machine`, `GET cluster`, `GET logs`, `GET commands[/{id}]` |
| `voice` / `perception` | that role's inference routes and `POST inference/cancel` |
| `manage` | `POST commands`, `POST commands/{id}/cancel`, `GET commands[/{id}]` |
| never | pairing, `network*`, `voices`, `POST cluster`, `POST logs`, `commands/agent`, `commands/{id}/report`, `api-keys`, `settings`, `memories`, `creations` |

### Friend access

A credential issued to an identity the owner allowed as a friend
([sharing a host with friends](../../docs/NETWORK.md#sharing-a-host-with-friends))
has `GatewayAccess.Friend`. It is stored with the credential
(`StoredGatewayCredential.Access`, written only for a friend's, so a store
without friends is unchanged) and fixed for the credential's life. A rotation
keeps it, but sign-in recognizes only the credential it issued, so a friend
signs in again after one. Deny by default: `GatewayRequestAuthenticator` refuses a friend's
signed request with `access.friend` (403) unless the route admits friends
(`GatewayApiAccess.Friends`), and with `auth.revoked` (401) unless sign-in still
records that credential for an identity allowed as a friend
(`GatewaySignInService.FriendAllowed`; `signin.json` is read again at most every
5 seconds, so a friend removed with `martlet-host` loses access within that time;
never without sign-in).

| Friends may use | Everything else answers `access.friend` before anything is read |
| --- | --- |
| `GET version` (with `access` `friend`), `GET capabilities` (only the routes friends may use), `GET status`, the inference routes of their role whose every operation serves only the request that asks (`GatewayInferenceRouteRegistry.FriendsMayUse`: chat, speaking, lip-sync, transcription, reading, perception), `POST inference/cancel` (their own requests) | the picture and song routes (they keep the whole host's queues, histories, results and models), pairing, `network*`, `machine`, `cluster`, `voices`, `speaking-voices`, `character-models`, `creations`, `home-assistant`, `settings`, `memories`, `api-keys`, `commands`, `logs`, `priority`, `security/audit`, `signin/settings` |

A friend keeps at most three computers on a host
(`GatewaySignInService.MaximumFriendDevices`; a sign-in on another revokes their
oldest) and all friends together at most 32 (`MaximumFriendCredentials`; then
`signin.friends_full`, 409), so friends never fill the credential table. A
friend's credential that sign-in no longer records (`signin.json` replaced) is
revoked on the next friend's sign-in, and when more than 64 sign-ins are
recorded, friends' oldest records go first and their credentials are revoked
with them.

`access.friend` refusals don't count toward lockout. A friend's speaking
requests never reach the owner's shared speaking voices: a voice named only by
its SHA-256 answers `reference.missing` (the friend sends its own recording) and
nothing a friend sends is kept. The network document marks a friend's paired
computer (`devices[].access` `friend`), and a friend never gets a join
attestation. The owner's requests come first ([owner first](#gpu-priority-live-turn-first)).

## Implemented HTTPS surface

| Operation | Authentication | Bounded result |
| --- | --- | --- |
| `GET /health/live` | None | Exact `{"status":"live"}`; no host ID, version, worker or system data |
| `POST /martlet/v1/pair` | Locally opened one-use proof | One scoped credential; 8 KiB strict JSON with required fields, duplicate/unknown rejection |
| `POST /martlet/v1/pair/code` | Proof of a locally opened short code ([short typed codes](#short-typed-codes)) | One scoped credential plus `host_proof`; same 8 KiB strict JSON rules |
| `POST /martlet/v1/pair/member` | Signature by an active member desktop's network key ([member pairing](#martlet-network-member-pairing)) | One `voice` credential (older credentials of that device revoked); same 8 KiB strict JSON rules |
| `GET /martlet/v1/network` | Signed scoped device request, any role | Host ID, `martlet_version` (the Martlet release this host runs, so every computer that syncs the network learns of an update here; desktops older than it ignore it), `state` (`unbound`, `bound`, `removed`), the accepted `roster` (or null), `devices` (the computers paired with this host, at most 16, most recently active first: `device_id`, `display_name`, `paired_at` and `last_seen`, when it last made a signed request since the gateway started, absent before that, and `access` `friend` for a [friend's computer](#friend-access)) and, for an active member desktop, pending `joins` (`device_id`, `display_name`, `key`, `check_number`, `requested_at`). Nonsecret; desktops older than `devices` ignore it |
| `POST /martlet/v1/network` | Signed device body, any role | Strict schema-1 roster JSON, at most 40 KiB, accepted entry by entry (binding an unbound host); returns the same document as GET and saves `network.json` when a storage is attached |
| `POST /martlet/v1/network/join` | Signed device body, any role | `display_name` and `key` (ECDSA P-256 SPKI); returns `state` (`pending` or `member`), `network_id` and `check_number` |
| `POST /martlet/v1/network/deny` | Signed body of an active member desktop | `device_id`; returns `denied` |
| `GET /martlet/v1/version` | Signed scoped device request (a friend's too), or any API key | Protocol `2.0`, gateway `0.2.0`, host ID, `martlet_version`, authorized role and explicit `credential_lifetime` (`paired` or retiring old key with deadline); for an API key also `api_key` (`id`, `name`, `scopes`, `expires_at`); for a friend's device `access` `friend` |
| `GET /martlet/v1/capabilities` | Signed scoped device request | Registry `martlet.gateway.inference-routes` `1.0`, at most 8 fixed routes and 16 status workers, filtered by role. Each route also says where it runs and its lane ([GPU priority](#gpu-priority-live-turn-first)): `gpus` (string array of GPU UUIDs, CUDA indexes or `cpu`; empty means unknown, which counts as the whole host) and `lane` (`pool` for Deep thinking's route, `live` for every other). Hosts older than GPU priority send neither |
| `GET /martlet/v1/status` | Signed scoped device request | Two-second cooperative cancellation for status reads for only that role |
| `GET /martlet/v1/machine` | Signed scoped device request, any role | Host ID, the gateway's Martlet release `martlet_version` (so desktops can offer to update older hosts), plus the host-reported `machine` (method `docker`/`native`/`app`, optional `platform`, `os_version`, `architecture` and `features` ([platform fields](../../docs/PLATFORMS.md#machine-report-platform-fields)), OS, kernel, CPU, threads, memory, container runtime, `nvidia_containers`, driver `cuda` version, at most 16 GPUs with vendor/memory/driver and, for NVIDIA, power limit/default and persistence mode) or no `machine` when none was collected. Informational and unauthenticated by the host itself; grants no authority |
| `GET /martlet/v1/cluster` | Signed scoped device request, any role | Host ID and this host's copy of the shared [cluster plan](../../docs/CLUSTER.md) (`plan`: who does each job, per-job failover, hosts and their roles). Nonsecret; the host never acts on it |
| `POST /martlet/v1/cluster` | Signed device body, any role | Strict schema-1 plan JSON, at most 16 KiB, merged per job and per host (newest stamp wins) into the host's copy, which is saved to `cluster.json` when a storage is attached (`GatewayServer.AttachClusterStorage`); returns the merged `plan`. Unknown fields, newer schemas and invalid names are `request.invalid` |
| `GET /martlet/v1/home-assistant` | Signed scoped device request, any role | Host ID and this host's shared Home Assistant connection, or `home_assistant:null` when none is stored. The value includes the HA access token and is returned only to paired devices over pinned TLS |
| `POST /martlet/v1/home-assistant` | Signed device body, any role | Strict JSON at most 16 KiB: `{revision,address,token,location_name,version}`. `address`/`token` are both set or both null (a tombstone); address is absolute HTTP(S) without userinfo/query/fragment. The host keeps the incoming value only when its revision wins, with equal revisions broken by writer device ID, stamps `updated_by`/`updated_at`, saves to `home-assistant.json` when attached (`GatewayServer.AttachHomeAssistantStorage`) and returns the stored value. Invalid fields are `request.invalid` |
| `GET /martlet/v1/settings` | Signed scoped device request, any role (never an API key) | Host ID, `digest` and this host's copy of the owner's [shared settings](../../docs/CLUSTER.md#one-martlet-on-every-computer) (`settings`: schema-1 `SharedSettings`, one last-writer-wins entry per setting and the `secrets` (cloud API keys) the entries use). Returned only to paired devices over pinned TLS; the host never uses it |
| `GET /martlet/v1/settings/digest` | Signed scoped device request, any role (never an API key) | Host ID and the SHA-256 `digest` of the copy, so desktops read the copy only when it changed |
| `POST /martlet/v1/settings` | Signed device body, any role (never an API key) | Strict schema-1 `SharedSettings`, at most 2 MiB, merged per setting (newest revision, then writer, then content) into the host's copy; secrets are pooled by SHA-256 and only those an entry uses are kept. Saved to `shared-settings.json` when attached (`GatewayServer.AttachSettingsStorage`); returns the same document as GET. Newer schemas, invalid names, stamps, values or secrets are `request.invalid` |
| `GET /martlet/v1/memories` | Signed scoped device request, any role (never an API key) | Host ID, `digest` and this host's copy of everything Martlet remembers ([one memory on every computer](../../docs/MEMORY.md#one-memory-on-every-computer); `memories`: schema-1 `SharedMemories`, one last-writer-wins entry per fact ID with the fact as Martlet.Memory writes it, or a tombstone). Returned only to paired devices over pinned TLS; the host never looks inside a fact |
| `GET /martlet/v1/memories/digest` | Signed scoped device request, any role (never an API key) | Host ID and the SHA-256 `digest` of the copy, so desktops read the copy only when it changed |
| `POST /martlet/v1/memories` | Signed device body, any role (never an API key) | Strict schema-1 `SharedMemories`, at most 12 MiB (1,024 facts, 4,096 tombstones, 64 KiB per fact), merged per fact (revision, then time, then forgotten, then writer, then content) into the host's copy. Saved to `memories.json` when attached (`GatewayServer.AttachMemoryStorage`); returns the same document as GET. Newer schemas and invalid IDs, stamps or facts are `request.invalid` |
| `GET /martlet/v1/memories/spaces/{space}` | Signed scoped device request of a member device (never a friend or an API key) | Host ID, `space`, `digest` and this host's copy of one [memory space](../../docs/ACCOUNTS.md#memory-spaces) (`memories`: the same schema-1 `SharedMemories` as `/memories`; an empty copy for a space the host never kept). `{space}` is exactly `household`, `account-<32 hex>` or `character-<32 hex>` (`Martlet.Core.Sync.MemorySpaceId`); anything else is `request.invalid`. The access hook (`GatewayMemorySpaces.Access`, default every member device) may refuse with `memories.space_denied` |
| `GET /martlet/v1/memories/spaces/{space}/digest` | Same as the space's GET | Host ID, `space` and the SHA-256 `digest` of the space's copy |
| `POST /martlet/v1/memories/spaces/{space}` | Signed device body of a member device that may read and write the space | The same body, limits and merge as `POST /memories`, into that space only; returns the same document as the space's GET. A merge with no fact makes no space; at most 64 spaces (a new one past that is `memories.spaces_full`). Saved as `memories-<space>.json` when attached (`GatewayServer.AttachMemorySpaceStorage`) |
| `POST /martlet/v1/memories/spaces/{space}/give` | Signed device body of a member device that may give to the space (`GatewayMemorySpaceUse.Give`; it need not read it) | Shares facts with another account ([sharing](../../docs/ACCOUNTS.md#sharing)): a schema-1 `SharedMemories` with 1-64 facts and no forgotten ones (else `request.invalid`). The host adds only the facts whose IDs the space doesn't have, never changes or brings back one it has, and answers `host_id`, `space` and `taken` (how many it added), never the space. A gift into a new space counts against the 64 spaces. Client: `Audio2FaceHostConnection.GiveMemoriesAsync` |
| `GET /martlet/v1/accounts` | Signed scoped device request, any role (never an API key or a friend) | Host ID, `digest`, `rejected` (0) and this host's copy of the household's [account directory](../../docs/ACCOUNTS.md#account-directory) (`accounts`: schema-1 `AccountDirectory`, one signed last-writer-wins entry per account; public facts only) |
| `GET /martlet/v1/accounts/digest` | Signed scoped device request, any role (never an API key or a friend) | Host ID and the SHA-256 `digest` of the copy, so desktops read the copy only when it changed |
| `POST /martlet/v1/accounts` | Signed device body, any role (never an API key or a friend) | Strict schema-1 `AccountDirectory`, at most 2 MiB (256 accounts). Each new entry passes `AccountDirectory.Refusal`: signed by an active member desktop of this host's roster with the key the roster lists, and keeping the account's creator; a host in no network takes none. Taken entries merge per account (removed, then revision, then writer, then content). Saved to `accounts.json` when attached (`GatewayServer.AttachAccountStorage`); returns the same document as GET with `rejected` set to the number of refused entries, and logs a warning when it is above 0. Newer schemas and invalid entries are `request.invalid` |
| `GET /martlet/v1/creations` | Signed scoped device request, any role (never an API key) | Host ID, this host's copy of [Martlet's creations](../../docs/CREATIONS.md) (`library`: schema-1 `CreationLibrary`, one last-writer-wins entry per creation or a tombstone) and `present` (the SHA-256 of every asset piece it holds). The owner's own data: returned only to paired devices over pinned TLS; the host performs nothing |
| `GET /martlet/v1/creations/digest` | Signed scoped device request, any role (never an API key) | Host ID, the SHA-256 `digest` of the copy, `present_digest` (of its sorted piece hashes) and `present_count`, so desktops read the copy only when either changed |
| `POST /martlet/v1/creations` | Signed device body, any role (never an API key) | Strict schema-1 `CreationLibrary`, at most 8 MiB (256 creations and 2 GiB of assets, 1,024 tombstones), merged per creation (revision, then writer, then content) into the host's copy; pieces no live creation uses are deleted. Saved to `creations.json` when attached (`GatewayServer.AttachCreationStorage`); returns the same document as GET. Newer schemas, unknown fields and invalid entries are `request.invalid` |
| `GET /martlet/v1/creations/chunks/<sha256>` | Signed scoped device request, any role (never an API key) | One piece (`chunk_sha256`, `data_base64`, at most 3 MiB), or `chunk.missing` |
| `POST /martlet/v1/creations/chunks/<sha256>` | Signed device body, any role (never an API key) | Exactly `{"data_base64": ...}` of a piece some live creation has, with that SHA-256 and length; anything else is `request.invalid`. Kept as `creation-chunk-<sha256>.bin` when attached (without storage, up to 256 MiB in memory); returns `present` |
| `GET /martlet/v1/logs?after=N[&limit=L]` | Signed scoped device request, any role | Host ID and a page of this host's [log](../../docs/DIAGNOSTICS.md#diagnostics-page-and-shared-logs), oldest first, after store position `N` (`0` for the oldest): its own activity and every computer's lines the owner's desktops share with it. Each entry is `{source, component, seq, at, level, message, relayed_by?}`; `next` is the position to continue from and `more` says whether more are waiting. `limit` 1-1000 (default 500); at most about 448 KiB per page |
| `GET /martlet/v1/logs?own_after=S[&limit=L]` | Signed scoped device request, any role | The same page shape with only this gateway's own lines (`source` = host ID, `component` `gateway`) whose `seq` is after `S`, so a desktop can read just them. `after` and `own_after` together, unknown or repeated parameters are `request.invalid` |
| `GET /martlet/v1/api-keys` | Signed scoped device request, any role (never an API key) | Host ID, this host's copy of the network's API keys (`keys`: schema-1 `ApiKeyList` with names, scopes, expiry, stamps and SHA-256 verifiers) and `used` (key ID → when it was last accepted here since the gateway started) |
| `GET /martlet/v1/security/audit` | Signed scoped device request, any role (never an API key) | Host ID, the exposure choices (`internet_reachable`, `outside_addresses` from this host's roster entry, `allow_pairing_outside_home`, `treat_all_as_outside`, `outside_access_blocked_reason` (sign-in's `signin.not_set_up` or `signin.no_allowed_identity`, absent when usable) and `outside_access_paused`), the guard's totals (`successes`, `failures`, `throttled`, `locked_out`) and its last 256 `events` (`{at, route_class, source, source_kind, outcome, code, subject?}`), oldest first ([guard](../../docs/NETWORK.md#reaching-your-network-from-outside-home)) |
| `POST /martlet/v1/api-keys` | Signed device body, any role (never an API key) | Strict schema-1 `ApiKeyList`, at most 32 KiB, merged per key (a revoked entry always wins, otherwise the newest stamp) into the host's copy, which is saved to `api-keys.json` when a storage is attached; returns the same document as GET |
| `POST /martlet/v1/logs` | Signed device body, any role | Strict schema-1 `LogBatch` (at most 384 KiB, 1,000 lines, 64 streams): lines from the sending desktop's own logs and every other computer's lines it holds (other desktops' and other hosts'). Per stream (`source`/`component`) only lines with a `seq` newer than the newest kept are stored, so redelivery is harmless; lines whose source is not the sender record `relayed_by`. Returns `accepted` and the `marks` (newest `seq`) of every stream the batch names. Saved to `logs.json` when a storage is attached (`GatewayServer.AttachLogStorage`) |
| `POST /martlet/v1/inference/ollama-chat` | Signed `voice` body plus action permission | Exact selected native-chat model/revision/artifacts; `input` plus optional `system` and `history` (`user`/`assistant`, at most 16) within one 16,384-byte text budget; bounded UTF-8 text events. `Martlet.Gateway.Ollama` relays it to the host's loopback Ollama (`ollama` host role) |
| `POST /martlet/v1/inference/deep-thinking-chat` | Signed `voice` body plus action permission | Route `martlet.gateway.deep-thinking-chat.v1`, the same native-chat contract, payload and bounds as `ollama-chat`, relayed by `OllamaRelayWorker.DeepThinking` to the `deep-thinking` host role's own loopback Ollama (a second server beside the conversation model's), so Deep thinking's background thinks never queue behind replies. The role's slots (`OLLAMA_NUM_PARALLEL`, 1 to 4) are the route's `maximum_concurrency`: the gateway admits that many thinks at once and answers one more with `job.busy`; every other route stays at one. It is the pool lane: on a graphics card that a live request or a hold keeps, a new think gets `job.busy` with `detail` `live` and a running think ends with `job.preempted` ([GPU priority](#gpu-priority-live-turn-first)) |
| `POST /martlet/v1/inference/f5-synthesis` | Signed `voice` body plus action permission | Exact F5/reference identity, WAV/transcript/chunk bounds and contiguous 24 kHz PCM. `Martlet.Gateway.F5` relays it to the host's loopback F5 service (`f5` host role; route `F5Relay`: pinned model weights, discard-only cancellation). Every voice engine in `SpeechEngines` (XTTS-v2, GPT-SoVITS) has its own route and path on this contract; the reference length must fit the engine (GPT-SoVITS: 3-10 s), and an optional `reference_language` (`en`/`ja`) carries the recording's language for GPT-SoVITS. For a voice the shared speaking-voice list says was made from several recordings, an engine that learns from several (`MultipleReferences`: XTTS-v2, GPT-SoVITS) passes when one of them fits its bounds and its loopback service also gets `reference.clips` (`start_sample`, `sample_count`, `transcript` per recording); other engines clone the joined recording |
| `POST /martlet/v1/inference/perception/ocr` | Signed `perception` body plus action permission | Selected P02 OCR identity and bounded selected-window frame |
| `POST /martlet/v1/inference/perception/vlm` | Signed `perception` body plus action permission | Selected P02 VLM identity, frame and bounded question |
| `POST /martlet/v1/inference/audio2face` | Signed `voice` body plus the host-enabled relay lease | At most 4 s of mono 16-bit generated-speech PCM (`sample_rate`, `pcm_base64`) relayed to the host's loopback Audio2Face service (the role's local open-source engine or NVIDIA's NIM); `face_frame` events carry ARKit blendshape JSON and chunk-relative `sample_offset` |
| `POST /martlet/v1/inference/transcription` | Signed `voice` body plus the host-enabled relay lease | Kind `Transcription`, contract `martlet.transcription-relay` `1.0`: one microphone utterance of at most 30 s of 16 kHz mono 16-bit PCM (`sample_rate` 16000, `pcm_base64`, at most 960,000 PCM bytes); `text_delta` events carry the final transcript (at most 16 KiB UTF-8), then `completed`. No text event means no speech was recognized. `Martlet.Gateway.Stt` relays it to the host's loopback speech-to-text server (`stt` host role: whisper.cpp, or Martlet's Parakeet service on the same `/inference` contract) and keeps the audio in memory only |
| `POST /martlet/v1/inference/cancel` | Signed owning credential/host/device/role | Local discard acknowledgment, bounded compute-cancellation report; no compute-stop claim |
| `GET /martlet/v1/priority` | Signed scoped device request, or an API key with `read` | [GPU priority](#gpu-priority-live-turn-first) now: `routes` (`route_id`, `name`, `lane`, `gpus`, `running`, and for a pool route `held`: a request there would be turned away now), `gpus` (each card or `cpu`: `id`, `held`, how many `live` and `pool` requests run on it and how many `holds` keep it), `whole_host_held`, `holds` (`holder` device, `gpus`, `until`), the `preempted` and `refused` counts since the gateway started, the last 16 `last_preemptions` and `last_refusals` (`at`, `route_id`, `gpus`, `by`: what held the card) and placement `warnings`. Device and route IDs only |
| `POST /martlet/v1/priority/hold` | Signed `voice` body (or a `voice` API key) | Exactly `{"routes":[route IDs],"ttl_ms":1..15000}` (1 to 16 distinct route IDs): keeps the graphics cards behind those routes free of pool work until now plus `ttl_ms` and stops pool work running on them at once. Returns `{"gpus":[...],"until":"<ISO 8601>"}`; empty `gpus` is the whole host (a route whose placement is unknown, or a route this host doesn't have). One hold per client credential, which a new call renews; at most 32 clients hold at once (one more gets `job.busy` with `detail` `holds`). Holds end on their own and never affect live requests |
| `POST /martlet/v1/priority/release` | Signed `voice` body (or a `voice` API key) | Exactly `{}`: ends this client's hold at once; returns `released` (false when it had none) |

Non-streaming responses are snake-case JSON and at most 64 KiB. Inference uses
bounded pull-driven NDJSON. Authenticated operations
require an exact route with no query. Unknown methods/routes do not redirect.
Unpaired clients cannot read version, capabilities, status, worker IDs, model
metadata or failure details. A [friend's device](#friend-access) is admitted
only by version, capabilities, status, the request-scoped inference routes of
its role and cancel; every other signed operation above answers it `access.friend`.

`IGatewayWorker` exposes only validated capability metadata and a bounded
status method. There is no worker URL, raw HTTP client, model-management API,
download API, arbitrary command or inference method. Synthetic tests use
`ollama-private` and `f5-private` identities through this interface; the raw
Ollama/F5 services remain outside the LAN gateway surface.
Worker adapters must honor the supplied cancellation token; this foundation
does not forcibly terminate an uncooperative status reader.

## Trusted inference composition and lifecycle

Fixed factories `GatewayInferenceRoute.OllamaChat`, `.F5Synthesis` and
`.Perception` freeze destination/worker/model/revision/artifact identity and
request/event/aggregate/deadline bounds. Clients cannot supply arbitrary paths,
raw worker addresses, provider alternatives, commands or model-management actions.
Mutable route replacement is refused both at admission and before effects.
Source adapter names and declared artifact hashes are **not runtime attestation**.

`IOllamaGatewayInferenceWorker`, `IF5GatewayInferenceWorker` and
`IPerceptionGatewayInferenceWorker` are trusted host-composition interfaces,
not remotely registrable handlers. Each must implement `AcquirePermissionAsync`
and return a distinct request/host/device/credential/role-bound
`GatewayInferencePermissionLease`. This is a deliberate addition to the source
interface. The implementation must consume the actual per-role provider/action
permit, verify readiness and the exact selected worker/model/artifact identity,
and hold all reference/capture permission ownership until disposal. Its
`Validate` must recheck current permission and its `Revoked` token must signal
revocation; a no-op/forever-valid production lease is not an implementation.
Adapters report typed `GatewayInferenceWorkerException` failures, including
`PermissionDenied` and `IdentityMismatch`; worker exception messages are never
published.
Acquisition/cancellation/iterator methods must return promptly without blocking
the caller; asynchronous validation belongs in the returned operation.

Trusted adapters must use reviewed private transport (the existing literal
loopback restrictions for C# Ollama remain unchanged; remote worker transport
requires independently authenticated/pinned identity). No unauthenticated raw
LAN, IP/username ownership inference, alternate cloud/provider/model fallback
or promotion of client-supplied frame/reference metadata into permission is
allowed. This module does **not** supply those production adapters or claim that
an interface declaration proves an actual runtime. Controlled leases in tests
are explicitly synthetic, not runtime qualification.

The authenticating store mints an internal authority capability. Publicly
constructed `GatewayPrincipal` records have no authority. Current credential
state is checked under that same store lock at admission and immediately before
dispatch/publication. A bounded periodic check also retires waiting operations
after revocation/rotation; publication never waits for that poll to validate
authority. Permanent pairing does not make a principal valid forever.
Already-issued transport writes cannot be retracted; publication linearizes
when the authorized bounded write is initiated.

Each role/worker owns at most one job, with no pending queue. In-process admitted
request IDs are remembered until their deadlines with a 1,024-entry cap;
duplicate IDs return `job.replay`, including newly signed duplicates. This is
not a durable inference ledger: durable exact-signed-request replay is the
credential nonce store, and action-permit consumption belongs to the trusted
provider owner.

Only one valid started event, contiguous identity/correlation/sequence, and one
valid terminal are accepted. Terminal output is held until EOF and iterator
cleanup are proven. F5 preserves chunk/frame/sample order and validates
reference WAV/transcript/revision hashes. P02 enforces selected worker image
limits, matching evidence/model/frame/source provenance, and freshness again
at dispatch and publication. Perception route job epochs are explicitly
`1..2147483646`, matching the worker's inner H03b boundary; generic P02 positive
long epochs and P01 capture epochs are distinct contracts, never truncated.
Clients must require both a valid terminal and clean EOF. Permission-lease
disposal remains owned through final publication; if it fails or stalls after a
terminal write, the server records `stream.cleanup`, quarantines the route and
aborts the apparent-success stream rather than delivering a successful EOF.

Cancellation/disconnect/deadline stops local publication and attempts one
bounded worker cancellation. A receipt does not release admission or clear
reference bytes while preparation, pending `MoveNext`, worker cancellation,
iterator disposal or permission disposal still owns them. Unproven cleanup
quarantines the route, retains that ownership and prevents replacement work.
Listener shutdown drains owned retirement before the durable host can mark a
clean checkpoint. A worker that never retires deliberately keeps the host dirty
and unavailable rather than pretending cleanup succeeded.

### Earlier self-host voice client migration

The later Settings5 reuse must require Gateway protocol **2.0** and mandatory
`lifetime` / `credential_lifetime` discriminators, retain paired credentials
across reboot/connectivity failures, and handle only explicit old-key retirement
as timed. Do not reintroduce `credentialLifetime`, `CredentialExpiresAt`,
automatic re-pairing or protocol-1 fallback. Keep the existing `/martlet/v1/`
paths and `martlet-request-v1` HMAC label; they are not auth-version negotiation.
All outer inference/cancel/error envelopes are protocol 2. Inner F5 and
Perception worker identities and registry version remain **1.0**, separately.
The pinned client still rejects redirects and cross-origin sends, and uses
response-headers completion; callers own bounded stream reading/cancellation.

## GPU priority (live turn first)

Windows has no priority between programs on one graphics card, and NVIDIA MPS is
Linux-only, so the gateway keeps priority itself, where the cards are. A live
reply's time to first audio always comes before background work: a Chatterbox
token takes about 11 ms alone and about 30 ms while another program uses the
same RTX 4070 ([sharing the graphics card](../../docs/CHATTERBOX_VOICE.md#sharing-the-graphics-card)).

- **Placement.** Each route says where its worker runs (`GatewayInferenceRoute.Gpus`,
  set once with `PlaceOn` before registration and advertised as `gpus`): NVIDIA
  GPU UUIDs (`GPU-...`, `MIG-...`) where known, else CUDA indexes (`0` to `63`),
  or `cpu` alone; at most eight. The Linux host takes it from `gpus.json` beside
  `host.json`, which `martlet-host` writes from the card each role was pinned to
  (`CUDA_VISIBLE_DEVICES`) or `cpu` for a role added to run on the processor.
  `gpus.json` is not part of the approved configuration: moving a role to another
  card restarts the gateway without a new approval, and an invalid file is
  ignored with a `gpus.invalid` note. The `ocr` role (Reading) always runs on the
  processor. Empty means unknown and counts as the whole host.
- **Lanes.** `lane` is `pool` for Deep thinking's route (the Thinking pool's
  background work) and `live` for every other route: replies, voices,
  listening, lip-sync, singing, pictures and reading.
- **Held cards.** A card is held while a live request runs on it and while a
  client's hold keeps it (`POST /martlet/v1/priority/hold`, 1 ms to 15 s,
  renewed by calling again, ended by `POST /martlet/v1/priority/release` or on
  its own). Two placements share a card when they name the same device or one
  of them is unknown.
- **Refusal.** A new pool request on a held card is turned away at once with
  `job.busy` and `detail` `live` (HTTP 429). It never reaches the worker. A
  pool request on a free card, and every live request, is admitted as before.
- **Preemption.** When a live request is admitted or a hold starts, each pool
  request running on a card it shares is canceled at once and ends with
  `job.preempted`: a `failed` stream event when the stream had started, else
  HTTP 409. The Ollama relay aborts its loopback request, and Ollama/llama.cpp
  stop within one token or one 512-token prompt batch. Holds never touch live
  requests.
- **Owner first.** A [friend's](#friend-access) request ranks below all of the
  owner's work, whatever its route's lane: any request of the owner (live or
  pool) or a hold stops a friend's request on a card it shares
  (`job.preempted`), and while the owner's work or a hold keeps one of its cards
  a friend's request is turned away with `job.busy` and `detail` `owner`. When
  every slot of the worker an owner's request needs runs a friend's request, that
  request stops and the owner's waits for its slot (at most 3 seconds and its
  deadline, `GatewayInferenceRouteRegistry.SlotWait`); meanwhile the slot isn't
  given to another friend. A friend's request never holds a card: `priority`
  counts it with the `pool` work. So sharing a host never adds time to the
  owner's own replies, beyond stopping a friend's request on the same worker.
- **Status.** `GET /martlet/v1/priority` shows the GPU map, each card's hold
  state, the holds, the last preemptions and refusals and placement warnings.
  At start the gateway logs the GPU map, and a warning for each pool route that
  shares a card with live routes, with the advice to pin each Ollama server to
  its own GPU (`CUDA_VISIBLE_DEVICES`). Preemptions and refusals go into the
  host log (the same kind within a minute is counted, not repeated).

The desktop's `HostLiveGpuHold` (in `Martlet.Avatar.Audio2Face.Remote`, the
`ILiveGpuHold` of `Martlet.Core.Cluster`) makes the hold and release calls. A
host older than GPU priority answers `request.invalid`; the client then notes
it once and leaves that host alone for ten minutes, and the live turn goes on.
`gpu_priority_selftest` rehearses all of it on loopback ([MCP](../../docs/MCP.md)).

## Errors and audit boundary

Every application error contains only protocol version, stable code, authored
summary, exact remedy, an optional one-word `detail` (`live` on a pool-lane
`job.busy` refused for a live turn, `owner` on a friend's `job.busy` refused
for the owner's work, `holds` when the host already keeps 32
holds) and a random trace ID. It never includes a supplied
token, credential, signature, URL, request body, worker exception or stack.
The required `IGatewayAuditSink` receives only trace ID, stable code and HTTP
status; implementations must be thread-safe and nonthrowing. Framework request
logging is disabled by the supplied Kestrel factory. The gateway keeps its own
bounded activity log instead (`GatewayLogStore`, served at `/martlet/v1/logs`):
start and stop, each paired device, each finished model request (route, device
and duration) and each refused or failed request (route and device or method
and path, stable code, HTTP status, summary and trace ID; the same code for the
same route or method within a minute is counted, not repeated). It never records a
request body, token, credential, signature, query value or worker exception.
At most 6,000 lines and about 1.8 MB are kept (oldest dropped first); the log is
saved at most every 30 seconds and when the listener stops.

Stable remedies are code-owned in `GatewayFailures` and contract-tested. Main
operator actions are:

| Codes | Exact action boundary |
| --- | --- |
| `binding.*`, `host.*` | Select one safe address/certificate; stop and compare a changed pin out of band |
| `pairing.*` | Open a new locally approved window; never reuse/repair an old token |
| `auth.missing`, `auth.invalid` | Use the credential for the verified paired host or revoke/re-pair if lost |
| `auth.expired`, `auth.revoked` | Use the explicit rotation replacement or respect revocation; a paired device itself does not expire |
| `auth.clock`, `auth.replay` | Correct clocks and create a fresh nonce/signature; never replay |
| `auth.clock_invalid` | Correct the host clock and reopen the existing durable authority; pairing records are preserved |
| `auth.closed`, `auth.storage` | Inspect local access/recovery status, then reopen the same protected store; no reset-all-devices or stale backup restoration |
| `auth.capacity` | Locally revoke unused credentials or wait for old-key rotation overlap to end; permanent paired registrations are never evicted |
| `auth.rate` | Reduce authenticated polling and wait for the four-minute replay window to drain |
| `auth.throttled` | Too many failures or requests from this address (or for this account); wait for `Retry-After` ([guard](../../docs/NETWORK.md#reaching-your-network-from-outside-home)) |
| `pair.outside_home` | Pair on the home network, sign in, or let the owner allow pairing from outside home |
| `outside.paused` | The host has outside addresses (or allows typed codes outside) but sign-in has no usable method: connect from home, set up sign-in, or remove the addresses ([sign-in comes first](../../docs/NETWORK.md#reaching-your-network-from-outside-home)) |
| `request.timeout` | Send the whole pairing or sign-in body at once (10 seconds) |
| `auth.role` | Use a separately approved least-privilege role |
| `access.friend` | The host is shared with this device for its engines only; use version, capabilities, status and the inference routes ([friend access](#friend-access)) |
| `signin.device_taken` | Sign in from the computer's own ID; a member desktop pairs by its network key, and a pairing made another way is removed by the owner first |
| `signin.friends_full` | The host keeps as many friends' computers as it allows (32); the owner stops sharing with someone first |
| `memories.space_denied` | The memory-space access hook refused this device for that space (403); sign in as the space's owner or have the memories shared ([memory spaces](../../docs/ACCOUNTS.md#memory-spaces)) |
| `memories.spaces_full` | The host keeps as many memory spaces as it allows (64); a new space is refused (409), the kept spaces still take changes |
| `action.denied` | Obtain the exact provider/action permission; permanent pairing is not approval |
| `job.replay` | Use a new explicitly permitted action and request ID; a new nonce alone cannot duplicate a batch |
| `job.busy` | The route's worker runs as many jobs as it can; with `detail` `live`, a live turn holds the graphics card of this pool-lane request: run it on another computer or after the live turn; with `detail` `owner`, the host's owner is using it: a friend runs it elsewhere or later |
| `job.preempted` | A live turn (or, for a friend's job, the owner's work) took the graphics card and stopped this job: run it again on another computer or later |
| `worker.*` | Keep only the named private worker unavailable and repair its local adapter/status |
| `gateway.redirect_rejected` | Use the exact paired origin; never follow another destination |
| `gateway.connection_failed`, `gateway.deadline` | Verify readiness/address/pin or the private path; never bypass TLS validation or retry indefinitely |
| `gateway.internal` | Use only the trace ID with local redacted diagnostics; no automatic retry/fallback |

## Evidence and remaining gates

The dedicated suite covers exact origin rejection, injected listeners, actual
loopback TLS handshakes, good/bad SPKI pins, missing private keys, redirect and
cross-origin refusal, minimal unauthenticated liveness, strict pairing JSON,
one-use/expiry/bad-pin pairing, scoped signed requests, timestamp/path/role
binding, replay/rate saturation, pairing window/attempt bounds, permanent
pairing, rotation overlap, revocation, bounded worker inventory/status,
thread-safe audit correlation and redacted failures.
Fresh protocol-2 cases additionally exercise action denial/readiness, revocation
and rotation during preparation/streaming, duplicate batches/permission reuse,
delayed retirement with retained reference bytes, selected identities, and
actual DPAPI-backed Kestrel signed-body replay across clean/unclean restart,
same-key renewal and interrupted nonce commits.

This is in-process evidence on one Windows development machine. It is **not**
AC-13 or G3 LAN/host qualification. The following remain NOT RUN:

- Ubuntu 24.04 non-root service/container composition, secret-file permissions,
  restart/upgrade/backup behavior and supported TLS/key custody;
- an actual selected IPv4/IPv6 LAN interface, port collision and OS socket
  ownership under real host policy;
- UFW/nftables/router/Docker published-port behavior checked from an
  unauthorized physical LAN source, including denial and no UPnP/forwarding;
- Windows Credential Manager persistence/restart/renewal UI and safe transfer
  of the pairing card;
- real certificate enrollment/renewal, clock skew, host-key transition and
  lost-device recovery;
- DNS/address change, VLAN/Wi-Fi loss, two physical hosts, host-2 isolation and
  hostile/replayed traffic across a real network;
- raw Ollama/F5 worker isolation in separate processes/private networks,
  readiness under load and any inference, model, GPU, rights or latency result.
- production implementations of the trusted inference/permission interfaces,
  actual Ubuntu/LAN/two-host acceptance and factory provisioning.

H01/H05 own host observation/lifecycle and firewall guidance; H02/H04 own the
actual Ollama/F5 runtime contracts; H06 owns witnessed one/two-host acceptance.
