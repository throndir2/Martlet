# Gateway trust foundation (H03a)

This is a working **in-memory authorization library**, not a production gateway.
It opens no listener, discovers no hosts, accesses no OS vault, and registers no
application/provider route. It has no dependency on Core installation planning,
Desktop settings, provider permits or host artifact schemas. It uses the .NET
`TimeProvider`/cancellation conventions and disposable, redacted secret ownership
used elsewhere in Martlet without reusing cloud-provider permission semantics.

The separately [reused Gateway transport/authentication lineage](../Martlet.Gateway/README.md#reuse-lineage-and-authority-boundary)
preserves the earlier consumers' pinned TLS and HMAC protocol. This Trust library
remains isolated and uncomposed, not a second production authority. Its UUIDs,
fingerprint encoding, granular scopes and secret/renewal contracts are not
interchangeable with that protocol. No bridge or persistence integration is
provided; protected Gateway.Persistence PR #37 remains held pending a deliberate
consolidation/adaptation decision. The H03b responsibilities below describe
this library's remaining gaps, not instructions to create another transport.

## Authority and approval boundary

The future trusted host composition constructs and exclusively retains
`GatewayHostAuthority`. Give request handlers **only** its `Devices` object,
whose public operations are redemption and point-in-time authorization. It has
no approval, rotation, enumeration or revocation API and no public constructor.
The owner must call `ApprovePairing` only after deliberate authenticated **local
host** approval of the exact device ID and scopes displayed to the operator.
There is no `approved` Boolean, settings flag, desktop administrator credential,
remote management token or generic administrative scope.

Possession of the host-control object is the local capability boundary, not
proof that this library authenticated a human. Code already executing inside
the trusted host process can construct a separate authority, just as it can
replace any library. H03b must implement and review the real local UI/CLI
authorization and keep this object out of transport dependency injection and
remote endpoints. This slice deliberately supplies no production composition.

The host identity is an exact nonempty UUID plus canonical uppercase hexadecimal
SHA-256 SPKI fingerprint. It is supplied by the trusted host, **not computed or
attested by this library**. Neither DNS name, IP address nor display label is an
identity. Matching these values in an API call is not TLS pin verification.
H03b must derive the identity from protected key material, authenticate the TLS
peer/pin before transmitting any secret, reject unsafe redirects, check
certificate expiry and keep host/key replacement an explicit re-pairing event.
There is no old-key-signed transition mechanism here.

## Implemented lifecycle and limits

| Item | Fixed policy |
| --- | --- |
| Pairing/renewal token | 32 CSPRNG bytes (256 bits), five-minute lifetime, one successful redemption |
| Identity binding | Exact host ID + fingerprint, approved device UUID and exact scope set |
| Scopes | Status, transcription, generation, synthesis, perception, memory read, memory write; none/unknown bits rejected; no automatic status or write grant |
| Device credential | Independent 32 CSPRNG bytes; 90-day expiry; host stores only SHA-256 verifier |
| Renewal | Explicit host `ApproveRotation`, one-use renewal token delivered out of band like pairing; same device and scopes, new 90-day credential |
| Overlap | Old credential valid for at most two minutes, never beyond original expiry; no further rotation approval while overlap is live |
| Devices | Maximum 128 live devices, including reserved slots for new-device approvals |
| Pending approvals | Maximum 16 across pairing and renewal, at most one per device |
| Redemption admission | At most 32 attempts per authority per fixed one-minute window, successes and invalid bindings/tickets included |
| Time | Both UTC expiry and monotonic elapsed-time bounds; reaching either bound expires authority; observed rollback of either clock or frequency change permanently closes the instance |

Host approval returns a `GatewayPairingOffer`. The locally approved operator
must transfer the secret and compare the fingerprint through the reviewed
out-of-band flow; possession of this token authorizes exactly its bound device
and roles. `Devices.Redeem` atomically checks the binding and verifier, consumes
the approval, and creates/rotates the credential. No pending approval means no
credential. Wrong bindings or proofs cannot consume a legitimate approval.
Expiry, cancellation of an approval, replay or revoked/expired devices reject
redemption. A still-pending renewal cannot revive an expired or revoked device.
Failed redemption does consume rate budget, which intentionally favors fail
closed over availability; H03b must add bounded ingress and appropriate
transport/source admission without trusting caller-provided IP identifiers.

`Devices.Authorize` requires an explicit nonempty subset of granted scopes on
every request. The returned immutable decision reports only the requested
scopes. It is an observation at the call's linearization point, **not** a durable
permit, running-work lease or provider authorization. Bearer credentials are
reusable until expiry/revocation; this library does not claim signed-request
anti-replay. H03b owns TLS, request IDs, route/session ownership, dispatch and
in-flight cancellation/revocation policy. No Ollama issuer is provided and the
existing literal-loopback boundary remains unchanged.

`RevokeDevice` removes both current/overlap verifiers and all pending approvals
for that device. `CancelApproval` removes only its approval. Re-pairing after
revocation generates unrelated bytes, even for the same device ID. Device lists
are detached read-only metadata snapshots, never credential collections.

All state access is serialized under one short lock; no asynchronous I/O or
user callbacks (other than the trusted clock) run as part of issuance. Concurrent
redemptions have one winner. Revocation and authorization are ordered by the
same lock. Expired entries are swept during operations, so idle state stays
bounded without a timer or background worker. Removal clears verifier buffers.
Clock rollback invalidates all state permanently; repairing the clock does
not resurrect it. Clock resolution is the injected provider's resolution;
backward changes occurring entirely between samples cannot be observed.
Out-of-range expiry arithmetic also closes the authority, and clock exceptions
propagate rather than falling back to a permissive time source.

Every operation accepts the original cancellation token and checks it inside
the lock and before its commit. Cancellation observed before commit does not
issue, consume, rotate or revoke credentials; expiry cleanup/rate accounting
may already have occurred. Cancellation racing after commit cannot undo an
issued or revoked credential. A caller missing an issuance response must
obtain fresh host approval, not retry a consumed token. This synchronous,
bounded library has no cancelable network or queued worker operation.

## Secrets and restart behavior

`GatewaySecret` has no public raw-value property. Explicit `CopyTo` and `Import`
use exactly 32 bytes for future bounded transport/vault integration; the caller
owns and must clear any copied buffer. It clones imported bytes and clears its
owned buffer on `Dispose`; disposing a client handle is not host revocation.
Never turn bytes into logging fields, URLs, query strings or configuration.
`ToString` redacts, System.Text.Json serialization/deserialization rejects
secrets, and all library exceptions contain only fixed sanitized categories.
The library logs nothing. Verification uses SHA-256 of high-entropy random
secrets and `CryptographicOperations.FixedTimeEquals`; no password hashing or
custom cryptography is substituted.

The authority holds **only volatile state**. Disposing it, losing its process
or constructing a new instance loses every device and pending approval even
when the same host identity is supplied. Backups of client credentials cannot
restore authority. There is no persistence/import/export hook or success-shaped
restart recovery. Re-pair locally. H03b must supply protected durable identity
and verifier lifecycle, safe atomic storage/rotation/revocation, restore policy,
actual pinned TLS listener/transport admission and production local approval.
Native vault/service, unauthorized-client firewall and two-host qualification
remain unrun H03b/H06 gates, not inferred from library tests.

## Local validation

Use pinned SDK 10.0.401; no hosted CI, inference or hardware is needed:

```powershell
dotnet restore tests\Martlet.Gateway.Trust.Tests\Martlet.Gateway.Trust.Tests.csproj --locked-mode
dotnet test tests\Martlet.Gateway.Trust.Tests\Martlet.Gateway.Trust.Tests.csproj --no-restore
```

Tests call the production library, including absent approval, exact binding,
every scope, one-use/concurrent redemption, both clocks, expiry, rotation,
revocation, restart, cancellation, secret ownership and resource ceilings.
