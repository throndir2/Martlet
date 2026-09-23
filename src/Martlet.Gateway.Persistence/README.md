# Canonical HMAC gateway durability

**Windows-only internal library composition, OFF by default; not an installed
host or a qualified hosting preset.** This project references the canonical
`Martlet.Gateway` and composes its existing server, Kestrel listener and pinned
client protocol. It does not compose `Martlet.Gateway.Trust`, introduce another
TLS stack, install anything, expose a LAN interface or invoke an engine.

## Lineage and scope

Windows DPAPI, private NTFS directory/ACL/handle checks, atomic file replacement,
fencing and certificate mechanics are adapted from held PR #37's exact final
source `18daa4acfa90d7c7dff52861c93800a2f63b0823` (those source bytes are unchanged
from introducing commit `b5073a332ffbe0c73d7ab2064f89a3339730124f`).
The code is adapted here, not merged or cherry-picked with that PR's
incompatible Trust authority. Its UUIDs, scope flags, uppercase pins,
bearer-verifier checkpoints and `MARTLET1` format are **not accepted**.

Canonical string IDs, `sha256:` lowercase SPKI pins, 16-byte credential IDs,
32-byte issued secrets, `voice`/`perception`/`memory` roles, request HMAC bytes
and successful JSON documents remain unchanged. The 128-registration cap,
inactive reclamation, shared observed-UTC closure, eight pairing windows,
five failed proofs, five-minute pairing limit, 90-day credential limit and
ten-minute maximum rotation overlap remain.

The separate explicit **local approval executable/UI is a dependent future
PR**, not supplied by this library. It must obtain deliberate local approval
for the exact host, device, roles, recovery and key actions, and keep the
owner out of handler DI. A library capability or enum is not proof that a
human authenticated or consented. No root-solution, Desktop, settings,
companion schema, installer, updates, host-artifact or deployment wiring is
added here.

## Explicit local ownership

Every factory defaults `LocalGatewayDecision` to `No`. No returns an inert
disabled owner without validating/opening the path, accessing DPAPI, generating
keys or starting a listener. `Enable` is an explicit call by trusted local
composition. Only canonical loopback origins are accepted by this owner; the
existing standalone Gateway origin contract is unchanged.

| Local action | Result |
| --- | --- |
| `CreateNew` | New previously absent private directory and P-256 host key; exact supplied string host ID |
| `OpenExisting` | Opens only valid clean canonical state within the same Windows OS boot; never creates, repairs, converts or restores |
| `StartAsync` | Separately starts the existing TLS listener after identity, time, SAN and certificate checks |
| `OpenPairing` | Freezes the exact locally approved device/display name/roles in a one-use volatile window |
| `Rotate` / `RevokeCredential` / `RevokeDevice` | Commits the canonical transition before returning success |
| `ListRegistrations` | Detached non-secret current registrations; not a durable audit history |
| `CloseCleanlyAsync` | Blocks new admissions, stops/drains listener, commits final state, removes fence last |
| `DisposeAsync` | Stops/disposes without marking clean; next open requires recovery |
| `ResetDevicesForLocalRecovery` | Explicitly drops every device, retains only a validated host key, advances generation; re-pair |
| `RenewCertificateForLocalHost` | Stopped clean-host same-key renewal; retains pin, credentials and live replay history |
| `ReplaceIdentityForLocalRecovery` | Explicit new host ID/key/pin with all devices removed; out-of-band re-pair required |

The owner exposes no server, mutable credential store, certificate/key,
protector, public checkpoint importer or raw signing-key collection.
Internal request capabilities only exchange an already-approved pairing proof
or authenticate a signed request. They cannot open approvals, list/revoke
devices, rotate or recover identity.

Only one store owner exists at a time. Owner controls and start/stop are
serialized; canonical authority operations serialize local and HTTP mutations
under the same authority gate. Gateway authentication remains point-in-time
admission, not a provider/action permit or a running-work lease. Revocation
blocks later admission; it does not retroactively cancel an already admitted
operation. Future inference, perception and memory consent remain separate.

## Signing-key custody and storage format

The SHA-256 verifier in this HMAC protocol is itself a **request signing key**.
It is protected like a credential, never considered a safe password hash.
The server does not persist the original device secret or pairing token.

Storage requires a canonical absolute path on a fixed local NTFS volume with
an existing parent. No UNC, ADS, reparse traversal or linked state files.
The new app-owned directory has a protected DACL, current-user ownership and
only current-user/SYSTEM access. A pinned directory handle, hardlink/file-type
checks and exclusive `owner.lock` enforce the owned-directory contract.
Broadened permissions and unexpected entries are rejected, not repaired.

Only `owner.lock`, `authority.bin`, `running` and transactional `pending.bin`
are allowed. Writes use create-new pending files, write-through/flush,
same-volume move/atomic replacement without a backup, and a committed-file
flush. Live revision/hash checks reject replaced committed bytes.

The new 16-byte envelope has eight ASCII bytes `MRTLHM01`, little-endian Int32
version 1, and exact Int32 ciphertext length. Total size is at most 16 MiB.
Current-user DPAPI protects the entire strict UTF-8 document, which contains:

- Schema 1, exact `martlet-request-v1` and `hmac-sha256-key-sha256` identifiers,
  host ID, generation, positive checked revision, Windows boot identifier,
  observed UTC and boot-scoped monotonic timestamp/frequency.
- Protected PKCS#12 identity; key possession, certificate profile and actual
  canonical SPKI are checked. No plaintext private-key file or adjacent password.
- At most 128 credentials with IDs, names, exact roles, signing keys, original
  issue/expiry times, remaining lifetime budget and optional rotation linkage.
- At most 1,024 live nonce/expiry entries per credential.

Unknown/duplicate fields, foreign formats, invalid IDs/roles/times/budgets,
duplicate records, over-limit collections and invalid lengths are rejected.
There is no Trust conversion, snapshot import, backup selection or key
regeneration on malformed state. The 16 MiB ceiling is tested using a real
DPAPI-protected 128-by-1,024 checkpoint, not a reduced-capacity proxy.

One complete protected checkpoint is written per authoritative transition.
There is no batching, write-behind window, live nonce eviction or unchecked
journal. This prioritizes reviewable safety over throughput: full-capacity
native serialization/protection is exercised, but production throughput is
not qualified. DPAPI and disk flush are synchronous and not forcibly cancelable;
no hard per-write wall-clock guarantee is claimed.

Temporary clear checkpoint/key buffers are zeroed on disposal/error. Existing
client `GatewaySecret.Reveal()` and managed secret-string wire APIs remain
compatible; managed strings cannot be reliably erased. No real vault, provider
credential or existing production key is involved in the tests.

## Commit, replay and time

Issuance, rotation (new credential and old overlap together), revocation and
**every nonce admission** commit before a secret, successful result or
principal is returned. A failure with uncertain outcome closes the entire live
authority and leaves its fence; there is no permissive in-memory rollback.
HTTP failures use fixed redacted codes, never storage paths or key material.

Replay history survives every clean restart. Keep the existing two-minute
request skew and nonce retention of accepted UTC plus four minutes plus one
tick. Never shorten nonce retention using monotonic elapsed time: a stalled
UTC clock can leave the signed timestamp admissible. Saturation returns
`auth.rate`, including after restart, rather than evicting history.

Credentials, overlap and volatile pairing windows are additionally bounded by
monotonic elapsed time in the durable path. Checkpoints save the smaller
remaining UTC/monotonic budget and its boot-scoped monotonic observation.
Windows QPC (`TimeProvider.System`) is comparable across processes within one
OS boot. The protected Windows boot GUID is obtained through read-only
`NtQuerySystemInformation(SystemBootEnvironmentInformation)`; unavailable boot
identity is an unsupported backend, not permission to assume clock continuity.
Reopening within the same boot subtracts the larger of elapsed UTC and QPC
time and anchors the remaining budget without extending original expiry.
Observed UTC/monotonic rollback, timestamp-frequency change or invalid time
closes authority permanently. Backwards startup UTC is refused.

**A changed OS boot requires explicit all-device reset and re-pairing.** The
protected host key may be retained by that deliberate recovery. This backend
does not silently switch to UTC-only lifetime accounting across a reboot,
where a trustworthy elapsed-time bound is unavailable. Normal clean process
restarts within the same boot retain credentials and replay history.

After durable I/O, credential expiry, request timestamp window and cancellation
are checked again before returning a principal; pairing checks its window
again before returning a secret. A committed-but-lost or canceled issuance
cannot retransmit its secret or reuse the consumed proof. Explicit local
revoke/new approval is the remedy. Cancellation after a revocation commit
cannot undo it.

Clean shutdown grants at most 30 seconds to listener drain. Timeout or uncertain
stop leaves dirty authority and retains ownership until the listener actually
stops; disposal can be retried. The final checkpoint passes clock/cancellation
checks before fence removal. There is no artificial lifetime reserve or claimed
hard deadline on synchronous final filesystem I/O: boot-scoped elapsed-time
accounting charges **all** final write/fence-removal time on reopening, including
a stall after the last check and repeated short-overlap restarts with frozen
UTC. Repeated restarts cannot refund that elapsed lifetime. Nonce UTC horizons
are never shortened by monotonic lifetime accounting.

All pending pairing windows are discarded on restart. A capacity refusal
retains a valid proof in the current process; it does not persist the window.
The clean checkpoint retains nonces even if the original HTTP response was lost.

## Fail-closed recovery and limits

A surviving running fence, pending file, changed OS boot, corrupt identity,
unknown record or uncertain mutation never opens as normal authority. Explicit device reset
validates the committed protected identity, advances generation and commits
empty credentials; it may delete only the exact verified pending file and
never promotes its contents. If identity cannot be validated, recovery fails:
deliberately create a new identity at a new owned path rather than overwriting
unknown files. Key replacement always invalidates old devices and changes pin.

Same-key renewal is explicit, not automatic. The certificate covers loopback
IP SANs and is valid for 90 days. Expired certificates can be loaded for local
maintenance, but cannot start TLS. Windows Schannel uses a user-scoped key
handle loaded from the protected identity; no certificate/trust store is
modified, and the owned handle is disposed after listener shutdown.

DPAPI/ACLs do not defend against same-user code, administrators, a compromised
OS or restoration of an entire older clean profile. A complete rollback while
no owner is running cannot be reliably distinguished using these local files.
There is no public restoration path, but no hardware anti-rollback or trusted
offline clock guarantee. Real process interruption is tested; universal
power-loss durability is not inferred.

## Local validation and follow-up gates

Use the pinned existing SDK 10.0.401 with `DOTNET_ROOT` set to its directory
in the same process, unique C: artifacts/temp state, locked restore and Release:

```powershell
& $sdk restore tests\Martlet.Gateway.Persistence.Tests --locked-mode --artifacts-path $artifacts
& $sdk test tests\Martlet.Gateway.Persistence.Tests -c Release --no-restore --artifacts-path $artifacts
& $sdk restore tests\Martlet.Gateway.Tests --locked-mode --artifacts-path $artifacts
& $sdk test tests\Martlet.Gateway.Tests -c Release --no-restore --artifacts-path $artifacts
```

The persistence suite requires actual Windows/NTFS and uses newly generated
disposable keys only. It covers DPAPI/ACL/links, strict format/capacity,
clean/unclean restart, revocation/rotation/replay, clocks, cancellation,
post-I/O checks, native process interruption and real pinned-loopback TLS
with persisted key loading/renewal. The test executable is solely a bounded
interruption harness, not a shipped local host console.

POSIX protection and native Ubuntu service identity remain separately gated;
Windows results are not Linux evidence. Local approval UI/executable,
packaging/service boot, LAN/firewall/two-host behavior, real engines/models,
power loss and production performance remain unqualified. H01 / PR #21's
native hold and all unqualified hosting presets remain unchanged.

PR #37 remains held until this replacement is independently reviewed and
accepted; then the coordinator can mark it superseded without deleting its
preserved branch. Isolated Gateway.Trust remains uncomposed historical work,
not a second production authority.
