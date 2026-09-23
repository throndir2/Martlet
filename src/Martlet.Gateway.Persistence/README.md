# Durable gateway authority (H03b1)

This is a **Windows-only protected storage foundation**, not an inference host
or TLS listener. It composes the existing H03a trust state machine with actual
current-user DPAPI, a private app-owned NTFS directory, a persisted ECDSA P-256
host key/certificate, and restart-safe device verifiers. It starts no process,
service, listener, discovery, provider or model. AppSettings, Desktop credentials,
Docker, firewall configuration and the H01 native qualification hold are unchanged.

## Local owner and lifetime

The trusted local host composition alone owns `DurableGatewayHost`. Give future
remote handlers only `Devices`, the existing H03a redemption/authorization
surface. It cannot approve, recover, renew certificates, replace host keys, list
or revoke devices. Public factory/maintenance methods are host-control
capabilities, **not evidence of an authenticated human**; H03b2 must provide
the reviewed local approval console before exposing production transport.

| Explicit owner operation | Meaning |
| --- | --- |
| `CreateNew(absoluteDirectory)` | Creates a previously absent app-owned directory and new host UUID/key. Requires an existing parent; never adopts or repairs an arbitrary existing directory. |
| `OpenExisting(absoluteDirectory)` | Reopens only valid clean state. Never regenerates keys, restores a backup, chooses a temporary file or silently drops devices. |
| `ApprovePairing` / `ApproveRotation` | H03a's exact local device/scope approval. Five-minute single-use tokens; no secret is persisted. |
| `RevokeDevice` | Durably removes current and overlap credentials before reporting success. |
| `CloseCleanly` | Saves final clock/lifetime budgets, removes the running fence last, closes authority and releases storage. The future transport owner must first stop/drain its work. |
| `Dispose` | Releases resources and closes authority **without** declaring clean shutdown. An ordinary `using` without `CloseCleanly` intentionally leaves recovery required. |
| `ResetDevicesForLocalRecovery` | Explicitly invalidates every device and changes authority generation while retaining a verifiable host key. Requires re-pairing, never resurrects credentials. |
| `RenewCertificateForLocalHost` | Stopped, clean-host maintenance; reissues the loopback certificate with the same key/pin and retains committed device lifetimes. |
| `ReplaceHostKeyForLocalRecovery` | Explicitly resets devices and generates a different key/pin; stable host UUID remains, old credentials cannot match the new binding. Requires new out-of-band pairing. |

All operations preserve H03a's limits/scopes/secret handle ownership. Durable
redemption and revocation are serialized under the existing authority lock.
The updated state is not externally observable and no new credential is returned
until the bounded synchronous local commit succeeds. A commit failure closes
the entire authority; it is not rolled back to a permissive in-memory state.
No arbitrary public callback, importer, verifier collection or protector is
accepted. Internal friend-assembly wiring is not a sandbox against code already
executing in the trusted host process.

Cancellation is checked before issuance/revocation commits. Once a durable
commit starts, it completes or leaves an unclean fence; cancellation cannot
undo a completed revocation. A lost successful redemption response requires
new local approval, not retry of the consumed ticket. Dispose caller-owned
`GatewaySecret` handles and clear explicitly copied bytes as in H03a.

## Protected format and storage contract

Only Windows, a local fixed NTFS volume and a canonical absolute directory are
supported. UNC/network paths, reparse traversal and linked state files are
rejected. A non-delete-shared directory handle pins the directory, validates
its resolved path, and an exclusive `owner.lock` serializes host instances.
The directory has a protected DACL and current-user owner with only current SID
and SYSTEM access. Child files inherit the restricted ACL at creation.
Ownership, ACLs, file types and hardlink counts are checked; unexpected files
or permissions cause refusal, not an automatic chmod/ACL repair.

The directory contains exactly `owner.lock`, `authority.bin`, and, while active,
`running`; `pending.bin` exists only during a transaction or an interruption.
An unknown entry blocks both normal open and automatic device-reset recovery.
No generic app settings backup or restore participates in this authority store.

`authority.bin` has a 256 KiB maximum. Its fixed 16-byte envelope is eight ASCII
bytes `MARTLET1`, little-endian Int32 protector/format version `1` (current-user
DPAPI), and Int32 ciphertext length. Extra/truncated bytes or unknown versions
are errors. The DPAPI-protected UTF-8 JSON has strict constructor-required fields,
no unknown/duplicate members, depth 16, a 16 KiB maximum PKCS#12 identity and
at most 128 devices. It contains:

- Format version, nonempty host UUID, authority generation and positive revision.
- Empty-password PKCS#12 protected by the outer DPAPI envelope, not a plaintext
  key file or a password stored beside it. The host pin is derived from the
  actual certificate key. Key/certificate possession, identity, P-256 curve,
  server-auth extensions, loopback SANs and self-issued chain are validated.
- A UTC observation and device ID/scopes, 32-byte SHA-256 credential verifiers,
  absolute expiries, remaining lifetime budgets and optional rotation overlap.
  No pairing token or reusable device credential is serialized.

The self-issued certificate covers `127.0.0.1` and `::1` and lasts 90 days.
There is no global trust-store installation or implicit renewal. This library
loads expired identity for **explicit local maintenance**, not proof that TLS is
usable; H03b2 must enforce validity/SAN/pin before sending any secret and extend
certificate address policy deliberately for LAN configuration. No TLS certificate
or listener is currently exposed by this library's public API.

## Atomic writes, restart and deliberate recovery

The running fence is written and flushed before authority is made usable.
Every mutation verifies the committed byte revision, writes a create-new
`pending.bin`, flushes it, atomically moves/replaces the same-volume committed
file and flushes that file. No backup file is used. The durable owner fails closed
on storage or protection errors, returning fixed sanitized failure categories.

A clean close commits the final checkpoint and removes the fence **last**.
A surviving fence, partial temporary file, interrupted revocation/rotation,
unknown revision or invalid committed record refuses normal restart. This first
backend intentionally prioritizes safety over unattended crash availability.
Process-termination tests cover the real file path; power-loss durability on all
hardware/filesystems is **not** inferred from those tests.

Explicit device-reset recovery validates the committed protected identity,
leaves/establishes the fence, clears all verifiers and commits a new generation.
It can remove the exact owned pending file only after verifying the existing
identity, never promote its contents. If identity is missing or unverifiable,
recovery fails; explicit creation at a new owned path is necessary. Existing
files are not destructively replaced by a guessed identity. A key-replacement
failure still leaves all old devices invalidated or startup fenced.

## Clock rules

Clean checkpoints save the smaller remaining UTC/monotonic allowance for each
credential and overlap, not raw process timestamps. Reopen rejects a UTC clock
earlier than the checkpoint, subtracts offline elapsed UTC from the saved budget,
and anchors a new monotonic lifetime without changing original expiry. Pending
approvals and admission counters are intentionally volatile; all tickets are
gone at restart, so restarting cannot replay an outstanding pairing token.

Observed in-process UTC/monotonic rollback, frequency changes and overflow still
permanently close the authority. Such an instance cannot write a clean checkpoint.
New approval/re-pairing never derives a verifier from the previous credential.

DPAPI/ACLs do **not** protect against code running as the same user, administrators,
a compromised OS or restoring an entire older profile. No hardware anti-rollback
or trusted offline clock is claimed; clock manipulation entirely between observed
samples or during downtime cannot be proved from local files alone. Device
credentials remain bearer credentials within H03a's lifetime, not signed-request
anti-replay tokens.

## Local evidence and remaining gates

Use the pinned SDK, a unique C: artifacts directory, and locked restore:

```powershell
dotnet restore tests\Martlet.Gateway.Persistence.Tests\Martlet.Gateway.Persistence.Tests.csproj --locked-mode --artifacts-path C:\owned\gateway-build
dotnet test tests\Martlet.Gateway.Persistence.Tests\Martlet.Gateway.Persistence.Tests.csproj --no-restore --artifacts-path C:\owned\gateway-build
dotnet restore tests\Martlet.Gateway.Trust.Tests\Martlet.Gateway.Trust.Tests.csproj --locked-mode --artifacts-path C:\owned\gateway-build
dotnet test tests\Martlet.Gateway.Trust.Tests\Martlet.Gateway.Trust.Tests.csproj --no-restore --artifacts-path C:\owned\gateway-build
```

The storage tests require Windows/NTFS and use only uniquely owned temporary
keys/directories, with actual DPAPI, ACL, hardlink/junction, replacement, process
termination and clean/unclean restart paths. They do not silently substitute
fixtures for native operations. Set `DOTNET_ROOT` to the selected SDK so the
bounded subprocess interruption harness uses the same host. The tests assembly's
entry point is only that harness, not a shipped gateway executable.

H03b2 is a separate dependent PR for the actual explicit host composition, local
approval and bounded pinned TLS client/server. No usable inference host, model
ready state or production Ollama issuer is provided here. H03b3 explicitly
retains the Ubuntu 24.04 service-identity/protected-storage goal; non-Windows
startup currently fails with `UnsupportedPlatform`. POSIX modes must not be
described as a vault. Ubuntu/service/container lifecycle, Windows native gateway
packaging/boot, real LAN/firewall/two-host behavior, power loss and actual engines
remain separate unrun qualification gates. No hosted CI is involved.
