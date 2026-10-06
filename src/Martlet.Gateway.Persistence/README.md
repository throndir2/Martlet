# Durable paired-device gateway

**Internal library, default No; not an installed or qualified host.** Existing
factories retain Windows current-user DPAPI. An explicitly selected
`LinuxServicePermissions` backend is an **unqualified Linux candidate**, not
DPAPI-equivalent encryption. This composes the canonical `Martlet.Gateway` authority and its existing
Kestrel/TLS stack. The explicit [Linux executable/binding candidate](../Martlet.Gateway.Host.Linux/README.md)
adds named private-address factories and bounded same-key SAN rebinding; existing
factories remain loopback-only. Optional typed inference workers can now be supplied through
the existing trusted owner; defaults and the CLI still supply none. No implicit LAN
listener, model activation, service installation, firewall or AppSettings/Desktop/companion
integration is added. See [Gateway inference composition](../Martlet.Gateway/README.md)
for mandatory per-action permissions, retained retirement ownership and protocol-2
client migration.

**macOS:** the same `LinuxServicePermissions` backend runs on macOS through
`MacFileSystem` (selected by `PosixFileSystem.Create()`), with the same
contract on local read-write APFS: single-name `openat` below held directories
with `O_NOFOLLOW_ANY` and a same-volume check, `fstat` identities, ACLs that
grant access refused (deny-only entries allowed), `flock`, `F_FULLFSYNC`,
`renameatx_np(RENAME_EXCL)` and `kern.bootsessionuuid` as the boot identity.
arm64 Linux is admitted too (its `O_DIRECTORY`/`O_NOFOLLOW` values differ and
are translated). Neither has run natively here: macOS NOT RUN; arm64 Linux
reached `openat2` under QEMU emulation, which QEMU doesn't implement.

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
owner without touching storage, DPAPI, keys or sockets. Existing factories require
explicit Enable and a canonical loopback origin to open the selected authority. Existing
signatures still select Windows DPAPI; Linux requires the explicit backend overload
described below. Loading state and
starting its listener are separate actions. Host-control objects, certificate
keys and mutable authority are never exposed to request handlers or DI.

| Local operation | Effect |
| --- | --- |
| `CreateNew` | New absent private path and host key with exact selected host ID; never overwrites/adopts existing state |
| `OpenExisting` | Loads committed state and performs validated predecessor-bound redo if needed, keeping the same pairings |
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

The original [local approval executable](../Martlet.Gateway.Host/README.md) remains a
Windows-only console application. A separate [Linux host candidate](../Martlet.Gateway.Host.Linux/README.md)
now composes explicit TTY approval/disclosure and approved foreground service
restart; its native execution remains NOT RUN. Graphical setup, service
installation and container qualification remain separate work. API capability possession
is not proof of human consent. No installer, packaging, service or hosting
preset is enabled here.

## Protected state and crash-safe commit

### Explicit private binding

The additive `GatewayHostBinding.Loopback` / `ExactPrivateAddress` descriptor
and named `CreateNewForBinding`, `OpenExistingForBinding`,
`OpenForLocalAdministration`, `RebindForLocalHost` factories are local owner
capabilities. Named factories preserve source compatibility for existing
default-No calls with null arguments. No returns an inert owner without
dereferencing binding or selecting a backend. Enable uses the supplied backend;
existing factory signatures still refuse private origins.

The private form binds one exact canonical RFC1918/ULA literal origin, never
wildcards/discovery/public addresses. Certificate loading accepts the legacy
two-loopback SAN form or those two SANs plus exactly one private address.
Normal open requires the selected SAN; renewal retains the existing SAN set
and key/SPKI. A deliberate `RebindForLocalHost` checks the expected identity
before replacing the optional private SAN and commits the same-key certificate
with current credentials/revocations/replay state. It neither resets pairings
nor accumulates addresses. There is still one listener, not one per SAN.
The old strict reader does not accept the new private-SAN form: do not downgrade
to an older binary while that form is stored. A deliberate same-key rebind to
loopback removes the added SAN; no automatic compatibility migration is made.
The Linux CLI binds this capability to TTY approval and separate receipt
invalidation/reapproval; configuration alone cannot change service scope.

The HMAC SHA-256 verifier is itself a **signing key**. Treat it like a credential,
not a harmless password hash. **On the Windows backend**, current-user DPAPI protects all signing keys,
PKCS#12 identity, role/ID records, revision/generation/predecessor hashes,
clock observations and live nonce history. Raw issued secrets and pending
pairing tokens are not persisted. Temporary clear key/checkpoint buffers are
zeroed; existing managed wire secret strings cannot be reliably erased.

Windows storage requires an app-owned canonical fixed local NTFS path, current-user
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
nonce windows: a step back of at most 30 seconds (routine NTP/VM time sync)
holds the saved mark until the clock passes it, including when the store
reopens; a larger one closes the authority. Correct the clock and reopen the
**same store**; no re-pair is required. Certificate time limits and five-minute
invitation budgets are independent of device trust.

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

### Explicit Linux service-permissions candidate

The new overloads put `GatewayStorageBackend storageBackend` immediately after
`GatewayOrigin origin`, before workers/audit. They preserve all existing public
signatures as Windows-DPAPI defaults. No automatically selected Linux fallback,
environment selector or stored approval flag exists. For example, a future
trusted local owner can explicitly choose:

```csharp
await using var host = DurableGatewayHost.OpenExisting(
    selectedPath, loopbackOrigin, GatewayStorageBackend.LinuxServicePermissions,
    workers, audit, LocalGatewayDecision.Enable);
```

Opening and starting remain separate. `CreateNew` still requires an absent
selected leaf; it never adopts an existing empty directory. `OpenExisting`
never creates missing state. `RenewCertificateForLocalHost` has the same
explicit backend overload and retains the host key/pin. Routine updates reopen
the same backend/path; changing custody is not an update/migration mechanism.

**LinuxServicePermissions is plaintext at rest**, isolated by native filesystem
permissions under a stable non-root service UID. Signing keys and the PKCS#12
private key are in the same canonical state document, not in another authority
or a companion key file. The separate `MRTLUP02` envelope contains version,
length, payload and SHA-256 checksum over the header/payload. That checksum
detects accidental damage, **not authenticity against a writer of the state**.
There is no encryption, vault claim, key-beside-ciphertext scheme or DPAPI parity.
The unchanged strict inner v2 document and Gateway protocol 2 are shared.
Opposite-backend envelopes report `StorageBackendMismatch`; known timed formats
report `MigrationRequired`. Original state is preserved, not translated.

This does not protect against same-UID code, root/privileged administrators,
compromised OS, offline disk/backup reads, memory inspection, privileged namespace
changes or complete-state rollback. Use separately operator-managed disk
encryption when offline confidentiality is needed; the library does not
configure/detect it. A desktop Secret Service can require login/unlock.
systemd encrypted credentials require separate key provisioning and lifecycle
integration; neither is silently assumed available at unattended boot.

The initial native candidate is **Linux x86_64 with glibc, persistent local
ext4**, targeting Ubuntu 24.04. Missing native APIs/metadata or unsupported
filesystems fail explicitly. ARM64, musl, tmpfs, overlay, NFS/SMB/FUSE,
container user/mount mappings and arbitrary Docker volumes are not supported
by this candidate. ext4 identification includes the mount record, not just
the filesystem magic shared with ext2/ext3. No native acceptance has run here.

| Native boundary | Policy |
| --- | --- |
| Identity | Current real/effective non-root UID and unchanged real/effective GID; no supplied administrator identity or environment-derived UID. |
| Parents | Canonical absolute path; existing ancestors root/current-UID owned, not group/other writable, no ACLs/symlinks. Selected existing parent is current-UID-owned 0700. Shared sticky temp directories do not qualify. |
| Creation | Only the exact absent leaf, mode 0700; files created exclusively with 0600. No ancestor creation, permission repair, chown, umask changes or adoption. A restrictive umask can cause explicit refusal. |
| Native references | Held directory FDs; openat2 no-symlink/no-magic-link resolution, beneath-relative access and no mount crossing below the selected parent. statx masks, inode/device/mount/type/UID/modes and named/held identities are rechecked. |
| Files | Five-name allowlist; regular files, mode 0600, UID match, one hardlink, no ACL and same device/mount. Symlinks/FIFOs/devices/unknown entries are refused. |
| Ownership | Stable CLOEXEC owner.lock inode with nonblocking exclusive cooperative flock; never removed on close. It does not constrain hostile same-UID writers. |
| Timing | Kernel procfs boot ID, canonical monotonic/UTC checks; changed boot does not expire paired credentials. No trusted offline clock is claimed. |

The shared `AuthorityStore` owns the same transition and predecessor-bound redo
algorithm for both platforms; Windows NTFS/DPAPI mechanics remain in their
adapters. Linux uses same-directory `renameat2`: preparation is no-replace,
promotion replaces only the verified current predecessor. It flushes the staged
file and directory, then the **directory after preparation**, then committed
file and **directory after promotion** before returning authority/results.
Creation, running-marker changes and unprepared-stage removal also flush
directory metadata. No `File.Exists` error becomes implicit absence in the
shared engine. No path-based reopen/delete/rename is used by the Linux store.

On error, live admission closes and prepared bytes remain available for redo.
Corrupt committed/prepared state, unknown entries and foreign successors are
preserved and block access. Reserved, correctly owned unprepared staging may
be removed only after committed authority validates and no pending transaction
exists; a decodable foreign-host/generation stage is preserved instead.
Interrupted initialization before preparation may leave no recoverable identity,
but cannot have issued a device credential. There is no implicit new identity.

Raw Linux envelopes contain secrets, unlike Windows ciphertext: shared
read/encode/recovery buffers and owned native transfer copies are zeroed on
success/failure. Checkpoint/certificate ownership remains explicit. Managed
JSON/certificate-internal allocations and wire strings cannot be promised
perfectly erasable. Errors are bounded failure categories, not raw native
messages, key data or serialized state.

### Evidence separation

The portable test project runs production Linux directory/envelope/shared-store
code through a stateful syscall model; it checks flags, permissions, identity,
short transfers, lock ownership, exact file/directory flush ordering and crashes
at preparation/promotion boundaries. Actual canonical pinned loopback TLS and
inference admission use generated synthetic identities with that modeled disk.
These results are **not native Linux filesystem or service evidence**.

`tests/Martlet.Gateway.Persistence.Linux.Tests` is a separately compiled native
target. On a separately authorized Linux host it requires
`MARTLET_LINUX_TEST_PARENT` to name an existing current-UID-owned private ext4
parent and `DOTNET_ROOT` to select the test host. It creates only uniquely named
child paths and generated keys; it does not create service accounts or repair
existing modes. Unsupported OS/missing authorization prerequisites fail with
**NOT RUN**, not a passing platform skip. Tests cover native owned handles/modes,
cooperative subprocess lock, interruption, proc boot identity, symlink refusal,
and actual canonical loopback pairing/replay/renewal/revocation.
**This target was compiled, not executed, on Windows.**

Actual Ubuntu UID/ACL/mount/fsync behavior, boot without login, OS reboot and
power loss, service installation, container volumes/namespaces, Linux approval
console/UI, private-worker transport and LAN/two-host deployment remain
**NOT RUN / unqualified**. No Linux setup preset becomes eligible from a
portable pass, compile, saved setup report or this backend's existence.

### Windows provenance and unchanged holds

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

No real OS reboot, arbitrary hardware power loss, native Linux backend execution, service
identity, LAN/firewall/two-host, engines/models or installed update is qualified
by those tests. Windows passes are not Linux support. H01/#21 and deployment
holds remain unchanged. No remote CI is enabled or dispatched.

DPAPI/ACLs do not protect against same-user code, administrators, compromised
OS or rollback of the entire profile. Local revision chains prevent this
implementation from selecting stale backups; they are not a hardware
anti-rollback counter or external trusted clock.
