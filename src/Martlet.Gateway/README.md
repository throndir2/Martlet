# H03 gateway security foundation

**Standalone internal library and controlled HTTPS contracts, not a deployed
LAN service or completed H03 acceptance.** `Martlet.Gateway` implements the
security boundary needed before a future host gateway can expose worker-backed
operations. It does not join `Martlet.slnx`, Desktop, Core settings, Doctor,
packaging, host artifacts, provider authorization, Compose, systemd, firewall
or installer graphs.

The project performs no startup work by construction. A caller must supply one
exact private/loopback origin, an existing server certificate/private key, a
host identity derived from that certificate, workers, an audit sink and an
explicit listener factory. It never generates or installs a production host
key/certificate, changes a trust store, opens a firewall, publishes a Docker
port, runs an administrative command, downloads a model or calls inference.

The dedicated tests use generated fixture certificates and synthetic metadata
workers only. They do not contact Ollama, F5, a LAN host or a public service.
The runtime fixture certificate is loaded as a user-scoped key handle because
Windows Schannel cannot serve an ephemeral key; it is never added to a
certificate store and is disposed with the in-process listener.

## Reuse lineage and authority boundary

This is the standalone H03 source slice from
`ec6db3979a1e2b8164176d455e4017475b9865ca`, reused by an attributed cherry-pick.
It preserves the transport/authentication protocol and host identity expected
by the earlier Gateway consumers. It does **not** import the later
`d2a5db8` inference/routes slice or the final `1715d19` source-branch snapshot,
their F5/Perception/Memory.Service dependencies, or Host.Doctor/Host.Setup.
Core installation planning, settings, Desktop and HostArtifacts v2/catalog
remain independent and unchanged.

The newer [Gateway.Trust foundation](../Martlet.Gateway.Trust/README.md) remains
isolated and **uncomposed**. These are alternative implementation lineages, not
two production authorities or interchangeable credentials. This reused Gateway
is the transport/authentication lineage for subsequent earlier-consumer reuse;
neither library currently owns a deployed host. Protected Gateway.Persistence
PR #37 remains **held** for a deliberate consolidation/adaptation decision.
This import neither adopts its storage format nor changes that hold.

| Boundary | Reused `Martlet.Gateway` | Isolated `Martlet.Gateway.Trust` |
| --- | --- | --- |
| Host/device identity | Bounded identifier strings; certificate-derived `sha256:` plus lowercase SPKI hex | Nonempty UUIDs; supplied uppercase SPKI hex without prefix; no TLS verification |
| Authorization | `voice`, `perception`, `memory`; per-request scoped HMAC, timestamp and nonce | Independent status/transcription/generation/synthesis/perception/memory-read/memory-write flags; point-in-time secret authorization, no signed-request replay contract |
| Secret ownership | Base64url strings exposed explicitly by `Reveal`; SHA-256 verifier is an HMAC signing key | Disposable 32-byte buffers with explicit copy/import; SHA-256 verifier for equality checks |
| Pairing and rotation | Eight local windows, five failed proofs/window; direct local rotation with up to ten-minute overlap | Sixteen pending approvals, authority-wide redemption limit; one-use locally approved renewal with two-minute overlap |
| State/time | Volatile credential records and bounded per-credential nonce cache; UTC expiry | Volatile bounded live-device state; UTC and monotonic expiry with rollback closure |

There is no conversion, fallback, shared authority, secret cast, scope widening
or credential interchange between these namespaces. Even though both use
SHA-256 over random device secrets, persisting a Gateway HMAC verifier requires
**signing-key protection**, not treating it as a harmless password hash.

Before choosing or adapting protected persistence, reconcile the exact identity,
wire/version, role/scope, credential-ID, renewal and revocation contracts.
The reused store has no durable import/export, restart recovery, total
registration ceiling or expired-registration sweep; revoked/expired records
remain in memory. It does not inherit Trust's monotonic rollback closure,
disposable secrets, device ceiling or approval/handler capability split.
Production composition must keep `GatewayServer` and its local management
objects private to trusted host code, not expose them through remote DI.
Persisting credentials alone would also lose replay history on restart:
an explicit fail-closed restart/re-pair or reviewed replay-restoration policy
is required. Local approval UI, protected host keys, atomic durable
rotation/revocation and native-host recovery remain unimplemented gates.

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
over TLS 1.2/1.3, suppresses the server header and limits the request body to
8 KiB. It also caps concurrent connections at 64, request headers at 32 /
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
verifier used as the HMAC key; the raw secret is not retained. Default and
maximum credential lifetime is 90 days.

Local host management code can list non-secret registrations, revoke one
credential/device, or rotate a credential. Rotation issues a fresh ID/secret
and shortens the old credential to an explicit overlap of at most ten minutes.
Revocation clears its nonce cache. An expired, revoked or rotated-out
credential never falls back to another credential and cannot be reconstructed
from configuration.

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
timestamp, nonce and SHA-256 body digest. The implemented authenticated
metadata routes are bodyless; inference/request-body signing remains future
contract work.

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
| `GET /martlet/v1/version` | Signed scoped device request | Protocol `1.0`, gateway `0.1.0`, host ID, authorized role and credential expiry |
| `GET /martlet/v1/capabilities` | Signed scoped device request | At most 16 configured worker capability records for only that role |
| `GET /martlet/v1/status` | Signed scoped device request | Two-second cooperative cancellation for status reads for only that role |

Responses are snake-case JSON and at most 64 KiB. Authenticated operations
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
| `auth.expired`, `auth.revoked` | Explicitly renew/re-pair; never resurrect the old credential |
| `auth.clock`, `auth.replay` | Correct clocks and create a fresh nonce/signature; never replay |
| `auth.rate` | Reduce authenticated polling and wait for the four-minute replay window to drain |
| `auth.role` | Use a separately approved least-privilege role |
| `worker.*` | Keep only the named private worker unavailable and repair its local adapter/status |
| `gateway.redirect_rejected` | Use the exact paired origin; never follow another destination |
| `gateway.connection_failed`, `gateway.deadline` | Verify readiness/address/pin or the private path; never bypass TLS validation or retry indefinitely |
| `gateway.internal` | Use only the trace ID with local redacted diagnostics; no automatic retry/fallback |

## Evidence and remaining gates

The dedicated suite covers exact origin rejection, injected listeners, actual
loopback TLS handshakes, good/bad SPKI pins, missing private keys, redirect and
cross-origin refusal, minimal unauthenticated liveness, strict pairing JSON,
one-use/expiry/bad-pin pairing, scoped signed requests, timestamp/path/role
binding, replay/rate saturation, pairing window/attempt bounds, credential
expiry, rotation overlap, revocation, bounded worker inventory/status,
thread-safe audit correlation and redacted failures.

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

H01/H05 own host observation/lifecycle and firewall guidance; H02/H04 own the
actual Ollama/F5 runtime contracts; H06 owns witnessed one/two-host acceptance.
