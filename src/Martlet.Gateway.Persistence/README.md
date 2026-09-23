# Durable paired-device gateway

**Windows-only internal library, default No; not an installed or qualified
host.** This composes the canonical `Martlet.Gateway` authority and its existing
Kestrel/TLS stack. Optional typed inference workers can now be supplied through
the existing trusted owner; defaults and the CLI still supply none. No LAN
listener, model activation, service, firewall or AppSettings/Desktop/companion
integration is added. See [Gateway inference composition](../Martlet.Gateway/README.md)
for mandatory per-action permissions, retained retirement ownership and protocol-2
client migration.

## Pairing is not connectivity

Device pairing is explicitly **non-expiring**. Ordinary process restarts, OS
reboots, updates, offline periods and recoverable interruptions do not unpair a
device. Access can be unavailable because of clock, storage, network or
certificate problems while the same pairing remains recorded.

Only deliberate revocation/unpairing removes the corresponding authority.
An explicitly rotated credential may have a short retirement overlap; the
replacement device pairing remains permanent. Actual loss of the protected
identity is different from a temporary access problem. There is no default
reset-all-devices recovery API.

Invitation expiry/one-use, scoped requests, request timestamp freshness and
bounded nonce replay history remain mandatory. Permanent pairing is not an
unlimited action permit, model/provider authorization, memory consent or trust
in the LAN/IP address.

## Explicit version and compatibility decision

Gateway protocol **2.0** requires a discriminated lifetime:

```json
{"lifetime":{"kind":"paired"}}
```

The paired variant has **no expiry field**. It is not a null/missing date,
`DateTimeOffset.MaxValue`, far-future sentinel or parser default. Version
responses carry `credential_lifetime`; when an explicitly rotated old key is
used within its overlap, that value is:

```json
{"credential_lifetime":{"kind":"retiring","expires_at":"2026-09-23T18:10:00Z"}}
```

`IssuedDeviceCredential.Lifetime`, `GatewayDeviceRegistration.Lifetime` and
`GatewayPrincipal.CredentialLifetime` use explicit sealed variants.
Timed-device constructor options are removed, so source consumers must adapt
deliberately rather than silently treating missing expiry as permanent.
Version-1 pairing requests fail `protocol.unsupported` without consuming the
invitation. Established routes retain their `/martlet/v1/` transport paths and
the exact `martlet-request-v1` HMAC canonicalization; response/request protocol
version is the authority-semantics negotiation. IDs, roles, pins and key bytes
are unchanged. Future client reuse must validate protocol 2 and the required
lifetime discriminator; no automatic v1 fallback is supplied.

Storage is a distinct **v2** format (`MRTLHM02`, envelope version 2, protected
protocol `martlet-paired-v2`, algorithm `hmac-sha256-key-sha256`). Old timed
`MRTLHM01` and prototype `MARTLET1` report **MigrationRequired** without changing
their files. Unknown versions and malformed/missing lifetime variants are also
refused, never interpreted as permanent. No bulk migration
turns expired, swept or revoked prototype/timed credentials into permanent
authority. Those development formats were not deployed by this work; a real
legacy migration would need a separately reviewed explicit current-authority
and revocation policy, not a guess from old snapshots.

### Deliberate earlier-format transition plan

Routine updates **within the supported permanent v2 format** reopen the same
identity and pairings. That guarantee does not describe an upgrade from the
superseded internal timed experiment. Current main's earlier canonical v1
constructor is volatile; it has no durable record to migrate. Earlier source
clients must be changed to require major 2, explicitly deserialize lifetime
variants and stop equating connectivity/clock failures with unpairing.

For any owner who actually retained timed experimental state: stop its sole
writer, preserve original files, and establish the exact latest committed
authority and all pending revocations using that format's reviewed rules.
Do not pick an older clean backup because the latest state is inconvenient.
A future dedicated migration must retain the verified host key/SPKI and only
offer live, explicitly selected current credentials/roles for permanent trust;
revoked, swept, expired and ambiguous records cannot be silently resurrected.
An explicit new local approval would be needed for authority that no longer
exists. Migration must be atomic and versioned, with a source digest/receipt,
no dual writers, and rollback that never restores revoked authority.
Prototype Trust IDs/scopes require a separate deliberate mapping decision,
not an automatic conversion into HMAC credentials.

**That migration tool is not implemented or claimed here.** MigrationRequired
is a data-preserving compatibility block, not an instruction to delete the
identity, reset all devices or silently claim a normal permanent-state update.

## Local ownership and certificate lifecycle

Every factory defaults to `LocalGatewayDecision.No`, which returns an inert
owner without touching storage, DPAPI, keys or sockets. Only an explicit Enable
and canonical loopback origin can open a Windows authority. Loading state and
starting its listener are separate actions. Host-control objects, certificate
keys and mutable authority are never exposed to request handlers or DI.

| Local operation | Effect |
| --- | --- |
| `CreateNew` | New absent private path and host key with exact selected host ID; never overwrites/adopts existing state |
| `OpenExisting` | Loads committed state and performs authenticated redo if needed, keeping the same pairings |
| `StartAsync` | Starts the existing TLS listener; fresh certificate/key/pin/SAN/time validation |
| `OpenPairing` | Explicit exact device/name/roles approval; volatile five-minute one-use invitation |
| `Rotate` | New permanent credential and old-key retirement (up to ten minutes), committed together |
| `RevokeCredential` / `RevokeDevice` | Explicit durable unpair/revoke; device revoke also closes its pending invitations |
| `ListRegistrations` | Detached non-secret current paired/retiring records, not a connectivity status |
| `CloseCleanlyAsync` | Stop new admissions, drain listener, save state, remove diagnostic running marker |
| `DisposeAsync` | Stop/dispose without marking clean; later open still recovers the existing pairings |
| `RenewCertificateForLocalHost` | Optional deliberate same-key renewal, in addition to automatic renewal |

Certificates are renewed automatically with the **same protected key/SPKI**
when opened or selected for a new TLS handshake within 30 days of expiry.
An expired certificate can be loaded for renewal, but is never served in place
of a valid one. Renewal commits the new certificate with the **current**
credential/revocation/replay state under the authority lock before use.
Failure blocks access and preserves recovery state; it does not unpair devices.
TLS validity, IP SAN, server EKU and pinned-key checks are not disabled.
Existing TLS connections are not forcibly renegotiated on certificate renewal.
Old certificate handles remain owned until listener shutdown so concurrent
handshakes cannot see a disposed key; one handle per actual renewal is retained.

No implicit new-key generation repairs a corrupt existing identity. Deliberately
creating a new identity at a **different** path produces a different pin and
requires verified pairing to that identity; it does not erase the old store.

The real authenticated local approval executable/UI remains a **separate
dependent PR**. API capability possession is not proof of human consent.
No installer, packaging, service or hosting preset is enabled here.

## Protected state and crash-safe commit

The HMAC SHA-256 verifier is itself a **signing key**. Treat it like a credential,
not a harmless password hash. Current-user DPAPI protects all signing keys,
PKCS#12 identity, role/ID records, revision/generation/predecessor hashes,
clock observations and live nonce history. Raw issued secrets and pending
pairing tokens are not persisted. Temporary clear key/checkpoint buffers are
zeroed; existing managed wire secret strings cannot be reliably erased.

Storage requires an app-owned canonical fixed local NTFS path, current-user
owner and protected DACL granting only current-user/SYSTEM. Directory pinning,
file-type/link/ACL checks and an exclusive owner lock reject UNC/ADS/reparse/
hardlink attacks and concurrent writers; permissions are never auto-repaired.

The exact allowlist is `owner.lock`, `authority.bin`, `running`, `staging.bin`
and `pending.bin`. The state format is strict, bounded to 16 MiB, 128 credential
registrations and 1,024 live nonces per registration; no live eviction.
Full escaped-field capacity is tested through actual DPAPI. Each mutation
writes a complete protected checkpoint, not an unchecked write-behind cache.

1. Serialize the canonical transition under its authority gate. The protected
   successor binds host ID, generation, revision + 1 and SHA-256 of the exact
   committed predecessor ciphertext.
2. Create `staging.bin`, write-through and flush. This is **unprepared** work;
   no result or dispatch may rely on it.
3. Atomically rename staging to `pending.bin` and flush it. This is the durable
   prepared transaction and is replayed even if its response was lost.
4. Atomically replace/move into `authority.bin`, without a backup, then flush
   the committed file. Only then return a credential, revocation result or
   authenticated principal.

On restart, the running marker alone is not a failure or reset instruction.
An authenticated complete pending successor is promoted only if its exact
predecessor hash, revision, generation, host and same SPKI match current state.
Initialization can recover a complete revision-1 pending identity when no
committed state exists. A stale/foreign pending transaction cannot replace a
newer committed revocation. No older backup is selected.

Unprepared staging (including an interrupted partial write) is never promoted.
After the committed identity is validated, that exact unused stage is removed
and existing pairings resume. An operation interrupted **before preparation**
was not acknowledged and may need deliberate retry. From preparation onward,
revocations and nonce admission are redone before serving access.

Malformed/corrupt **prepared** transactions, conflicting files, corrupt identity
or mismatched predecessors fail closed and preserve both committed and pending
bytes for diagnosis. Reopen after correcting access/clock problems retries
nondestructively. Arbitrary corrupted authority cannot be reconstructed safely
without verified original data: this library does not pretend deleting pending
records, resetting devices or restoring stale snapshots is recovery.

## Independent time safeguards

Permanent paired records have no clock-based lifetime and no boot-reset gate.
UTC high-water checks still prevent backward time from resurrecting pruned
nonce windows. Correct the clock and reopen the **same store**; no re-pair is
required. Certificate time limits and five-minute invitation budgets are
independent of device trust.

Retiring old keys retain UTC deadlines and bounded remaining overlap budgets.
Within the same native Windows boot, elapsed QPC and UTC are both charged;
after a changed boot only observed UTC can measure downtime. No trusted
offline clock is claimed. Pairing itself never uses either clock to expire.

Signed requests still require the exact canonical signature and role, a
timestamp within two minutes, and a new nonce. Nonces retain accepted UTC plus
four minutes plus one tick, **including across dirty restart and reboot**.
Monotonic time never shortens nonce retention when UTC stalls.
Saturation fails `auth.rate`, not eviction.

Credential/role/request time and cancellation are checked around durable
admission. Invitation expiry is rechecked after I/O. A lost/canceled issuance
response does not retransmit a secret or revive its invitation; local revoke
and a deliberately new approval remain available. Cancellation cannot undo a
prepared/committed revocation. Storage failure closes live access, not the
persisted device relationship.

Listener drain has a 30-second wait limit. Uncertain stop retains ownership
until the listener actually stops. DPAPI and disk flush are synchronous, not
forcibly cancelable; no hard wall-clock disk deadline is promised. Final fence
deletion cannot refund any device lifetime because paired devices have none;
remaining old-key overlap is charged on reopen.

## Provenance, validation and remaining gates

DPAPI/ACL/NTFS/atomic-file and certificate mechanics are adapted from held #37
final source `18daa4acfa90d7c7dff52861c93800a2f63b0823`, byte-identical mechanics
introduced by `b5073a332ffbe0c73d7ab2064f89a3339730124f`. Its Trust credentials,
scope flags, schema and reset-on-crash behavior are **not** imported.
Gateway.Trust remains uncomposed; #37 and preserved branches are untouched.

Use existing SDK 10.0.401, same-process `DOTNET_ROOT`, unique C: artifacts,
locked restore and Release tests directly for Gateway and Persistence. Native
tests use only newly generated disposable keys and private temp directories:
real DPAPI/ACL/links, actual interrupted subprocesses, exact replay, native
boot identification plus injected boot changes, pinned loopback TLS,
same-key automatic renewal and malformed/stale recovery cases.

No real OS reboot, arbitrary hardware power loss, Linux backend, service
identity, LAN/firewall/two-host, engines/models or installed update is qualified
by those tests. Windows passes are not Linux support. H01/#21 and deployment
holds remain unchanged. No remote CI is enabled or dispatched.

DPAPI/ACLs do not protect against same-user code, administrators, compromised
OS or rollback of the entire profile. Local revision chains prevent this
implementation from selecting stale backups; they are not a hardware
anti-rollback counter or external trusted clock.
