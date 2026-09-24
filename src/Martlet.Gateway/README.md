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
There is no network route that opens or approves pairing. The local caller
freezes one device ID, display name and least-privilege role set
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
mark. Any observed backwards movement permanently closes their shared authority,
clears retained credential verifiers/nonces, and rejects every existing pairing
card and subsequent approval with `auth.clock_invalid`. Correcting the clock
does not reopen the same in-memory instance. With the durable owner, correct
the clock and reopen the same protected store: pairing data was not erased.
The standalone volatile constructor has no persistence guarantee.
Clock sampling occurs inside the store lock, not before admission.

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

## Implemented HTTPS surface

| Operation | Authentication | Bounded result |
| --- | --- | --- |
| `GET /health/live` | None | Exact `{"status":"live"}`; no host ID, version, worker or system data |
| `POST /martlet/v1/pair` | Locally opened one-use proof | One scoped credential; 8 KiB strict JSON with required fields, duplicate/unknown rejection |
| `GET /martlet/v1/version` | Signed scoped device request | Protocol `2.0`, gateway `0.2.0`, host ID, authorized role and explicit `credential_lifetime` (`paired` or retiring old key with deadline) |
| `GET /martlet/v1/capabilities` | Signed scoped device request | Registry `martlet.gateway.inference-routes` `1.0`, at most 8 fixed routes and 16 status workers, filtered by role |
| `GET /martlet/v1/status` | Signed scoped device request | Two-second cooperative cancellation for status reads for only that role |
| `POST /martlet/v1/inference/ollama-chat` | Signed `voice` body plus action permission | Exact selected native-chat model/revision/artifacts; bounded UTF-8 text events |
| `POST /martlet/v1/inference/f5-synthesis` | Signed `voice` body plus action permission | Exact F5/reference identity, WAV/transcript/chunk bounds and contiguous 24 kHz PCM |
| `POST /martlet/v1/inference/perception/ocr` | Signed `perception` body plus action permission | Selected P02 OCR identity and bounded selected-window frame |
| `POST /martlet/v1/inference/perception/vlm` | Signed `perception` body plus action permission | Selected P02 VLM identity, frame and bounded question |
| `POST /martlet/v1/inference/cancel` | Signed owning credential/host/device/role | Local discard acknowledgment, bounded compute-cancellation report; no compute-stop claim |

Non-streaming responses are snake-case JSON and at most 64 KiB. Inference uses
bounded pull-driven NDJSON. Authenticated operations
require an exact route with no query. Unknown methods/routes do not redirect.
Unpaired clients cannot read version, capabilities, status, worker IDs, model
metadata or failure details.

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

## Errors and audit boundary

Every application error contains only protocol version, stable code, authored
summary, exact remedy and a random trace ID. It never includes a supplied
token, credential, signature, URL, request body, worker exception or stack.
The required `IGatewayAuditSink` receives only trace ID, stable code and HTTP
status; implementations must be thread-safe and nonthrowing. Framework request
logging is disabled by the supplied Kestrel factory; a future host owns bounded
local listener diagnostics separately.

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
| `auth.role` | Use a separately approved least-privilege role |
| `action.denied` | Obtain the exact provider/action permission; permanent pairing is not approval |
| `job.replay` | Use a new explicitly permitted action and request ID; a new nonce alone cannot duplicate a batch |
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
