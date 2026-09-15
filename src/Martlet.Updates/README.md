# V07b-a/b: local candidate verification, staging and selection

**A staging library, not an updater, installer, signed release or supported
upgrade.** No network, execution, activation, credential access,
model access, process termination, shortcuts, registry, services, old-version
deletion or release publication. Existing unsigned packaging remains unchanged.
The library is not registered in the application or shared solution.

The separate [private candidate-selection transaction](SELECTION.md) now joins
real staged-package verification to V07a snapshot inspection and durable local
selection/rollback planning. It **never changes an executable launcher pointer**:
every committed selection remains `AwaitingReadiness`, or additionally
`AwaitingConfigurationRestore`. The staging API described below remains unchanged.
The separately approved [rollback configuration coordination](SELECTION.md#explicit-rollback-configuration-restoration)
can now invoke actual V07a restore under the supplied existing shared effect
owner, with a distinct expiring one-use preview/approval and version-3 fence.
Only a verified actual settings commit can be recorded as `AwaitingReadiness`;
interrupted/unproven outcomes stay pending for manual reconciliation. Staging
and ordinary selection never implicitly restore settings. There is no Desktop
wiring, launcher, execution or shipped rollback workflow.

## Caller boundary

`LocalStagingEngine(existingPrivateStagingRoot, trust, readInstalledFacts, limits)`
requires an existing, caller-owned, local staging directory, separate from the
installed version and all mutable user data. It never creates the staging root,
changes permissions, scans the installation or discovers candidate files.

`UpdateTrustPolicy` accepts up to 16 explicitly approved canonical DER
SubjectPublicKeyInfo public keys. It copies their bytes and accepts only RSA-3072
or RSA-4096. Supply keys from an independently authorized owner policy, **never
from the candidate, an unsigned companion checksum or a user trust-store scan**.
Empty policy returns `TrustUnconfigured` before reading a candidate.
Recreate the engine/policy after an independently authorized key revocation or
rotation. No built-in key, trust-on-first-use, fallback or certificate purchase.

`InstalledVersionFacts` contains an exact four-part version, RID, installation
directory, installation-revision SHA-256, persisted settings schema and the
opaque settings revision concurrency token. The token is bounded to 1-128
characters without control characters, but is never parsed as a content hash,
case-folded, trimmed or otherwise normalized. In particular, the uppercase
revision returned by `SettingsStore.LoadAsync`/`SaveAsync` is preserved exactly
through facts, preview, approval and receipt. Equality remains ordinal and
case-sensitive, like the store's own concurrency check. This is separate from
the signed candidate's canonical lowercase hashes.
The host must obtain coherent facts through its existing
installation/settings authority and retain that authority's operation ownership
through staging; a callback must not return a stale cached revision. The callback
is sampled at preview/staging start and before commit. This library does not read
settings or validate an installed executable on the caller's behalf.

The only existing production dependency is Core, for
`AppSettings.CurrentSchemaVersion`. Supported known settings schemas are the
same v1/v2 accepted by `SettingsJson` and V07a recovery. The candidate's signed
`settingsMinimumReader`/`settingsMaximumReader` bounds must include the **actual
persisted current schema**, not just the latest schema supported by Core.
Unknown current schemas are refused. Only the existing payload RID `win-x64`
and a strictly newer canonical four-part version are supported in this slice;
equal-version repair, downgrade/rollback and other RIDs need separate policy.

## Explicit operation flow

1. `Preview(archivePath, envelopePath, destination, token)` verifies the real local
   bytes without writing files. Both sources must be absolute local paths outside
   the staging root. Destination must be an absent direct child of that root.
   The immutable plan exposes both source paths, archive/envelope/manifest
   SHA-256, version, signer ID, RID, files and hashes, reader bounds, expanded
   bytes, directory count, free-space budget and expected installed facts.
2. After an explicit default-No review, call
   `plan.Approve(plan.ArchiveSha256, plan.EnvelopeSha256, plan.Destination, plan.Installed)`.
   Approval is not implicit in preview. It is issued once and consumed by one
   attempt, including failed/cancelled attempts and use with the wrong plan.
   Another preview attempt on that engine supersedes the previous plan, even
   when the new preview fails. Neither plans nor approvals have public
   constructors or deserialization paths.
3. `Stage(plan, approval, token)` checks instance/generation, current facts,
   destination absence and disk space. It pins both sources using read-only
   handles denying Windows writes/deletes, compares exact approved bytes and
   reopens their paths to detect replacement on platforms with weaker sharing.
   It copies the exact signed archive into a new `.pending-<random UUID>` child,
   hashes that copy, and extracts only from that copy. It never extracts from an
   input that can change underneath a previously checked stream.
4. Files use `CreateNew`, bounded streaming hash/CRC verification, write-through
   IO and `Flush(true)`. All written payload and metadata bytes are read back.
   Before same-parent directory rename, the source handles/names and current
   facts are checked again. `Directory.Move` finalizes to the absent destination.
   There is no replacement of an existing version or activation.
5. `InspectStaged(destination, token)` produces a fresh receipt only after
   re-running current-policy signature verification, the full archive verifier,
   exact extracted-file hashes and the strict persisted receipt comparison.
   Missing/extra files or directories, changed current facts and corrupt receipts
   cannot become successful evidence. This operation never repairs or deletes
   the selected stage.

The retained stage is:

```text
selected-version\
  candidate.zip       exact approved archive bytes
  candidate.json      exact signed envelope bytes
  staged.json         deterministic staging evidence and later requirements
  payload\
    manifest.json     exact existing internal unsigned payload manifest
    SHA256SUMS.txt     exact existing payload checksums
    Desktop\...
    Doctor\...
    help\...
    notices\...
```

`StagedReceipt` is created only by the verifier-backed engine. It exposes immutable
file hashes, archive/manifest/receipt hashes, signer/version/RID, destination and
installed facts. Persisted JSON is **not** an authenticated activation ticket.
It contains no caller-controlled `verified` flag. `InspectStaged` verifies again
instead of trusting the JSON, its digest, or a previously cached receipt object.

## Signed format 1 / byte contract

The external envelope is bounded canonical UTF-8 JSON, without BOM, whitespace,
comments, unknown/duplicate members or trailing bytes, in this property order:

```json
{"manifest":"BASE64_OF_EXACT_MANIFEST_BYTES","signature":"BASE64_SIGNATURE"}
```

Base64 is standard padded RFC 4648, with no whitespace. `signature` is RSA-PSS
over the **exact decoded manifest bytes**, SHA-256 digest, MGF1 SHA-256,
32-byte salt, the standard .NET `RSASignaturePadding.Pss` contract. A signature is
exactly 384 or 512 bytes according to the pinned RSA key size. PKCS#1 v1.5,
hash-only, certificates, alternative algorithms and package-provided keys are
not accepted. `signerId` is lowercase hex SHA-256 of the approved canonical DER
SPKI; it selects an already approved key, never introduces one.

Manifest properties occur in this exact order. Values below are illustrative
placeholders, not a usable signed package:

```json
{"formatVersion":1,"minimumReaderFormat":1,"application":"Martlet.Update","algorithm":"RSA-PSS-SHA256","signerId":"LOWERCASE_SPKI_SHA256","applicationVersion":"0.2.0.0","rid":"win-x64","settingsMinimumReader":1,"settingsMaximumReader":2,"archiveBytes":12345,"archiveSha256":"LOWERCASE_ARCHIVE_SHA256","files":[{"path":"Desktop/example.dll","bytes":123,"sha256":"LOWERCASE_FILE_SHA256"}]}
```

Every field is required/non-null. Integers use minimal unsigned-looking decimal
JSON notation (no sign, decimal point, exponent or leading zero). Strings use
the JSON output of `System.Text.Json` with its default encoder, camel-case
property naming, no indentation and depth at most 16; canonical reserialization
must reproduce the signed bytes exactly. Accepted manifest field values are
restricted ASCII, so payload paths use literal `/`, not escaped separators.
The normative serialization and declarations are in `Wire.cs`.

The complete `files` array includes `manifest.json` and `SHA256SUMS.txt`, not the
external wrapper. Entries are strictly ordinally sorted by slash-form path.
Each entry uses exactly `path`, `bytes`, `sha256` in that order; SHA-256 strings
are lowercase, 64 characters. Archive digest/length cover every original ZIP
byte including headers and directory, before any decompression.

The existing `packaging\windows\Packaging.Common.ps1` internal payload contract
is cross-checked, not rewritten: schema 1, exact application version/RID,
channel `INTERNAL DEVELOPMENT ONLY - UNSIGNED`, bounded SDK/runtime versions,
40-character lowercase source commit, Boolean dirty flag and complete file
inventory. Its inventory excludes its own manifest/checksums, uses backslash
paths sorted ordinally, and is compared to the signed wrapper's inventory.
Checksum text must be exact lowercase hashes, two spaces, original backslash
path, LF, followed by the exact internal-manifest hash and LF. The internal
manifest may retain PowerShell formatting; its **exact bytes** are authenticated
by the wrapper inventory, not normalized or regenerated.

This verifies the signed declarations and bytes, not PE machine headers,
Authenticode, toolchain provenance assertions, SBOM completeness, licenses,
binary behavior or a functional installed layout. A signer could authorize a
nonfunctional payload; staging must never be advertised as readiness/release
qualification. The fixture executables are deliberately inert text.

## Restricted ZIP and bounds

Only single-disk classic ZIP, stored or deflate, DOS/Unix creator IDs and reader
versions 1.0/2.0 are accepted. UTF-8 flag, deflate speed hints and the standard
signed 16-byte data descriptor are allowed. Central/local names, flags, methods,
sizes, CRCs and descriptor values must agree. Local records must be contiguous
from offset zero up to the central directory: no prepended executable,
overlapping/aliased records, gaps or trailing data. CRC-32 and SHA-256 are checked
against actual decompressed bytes, including a read past the declared length.

ZIP64, encryption, multi-volume archives, unknown methods, comments, **all extra
fields**, directories-as-entries, symlink/reparse/device/FIFO metadata and special
Unix file types are refused. Rejecting all extras includes common ZIP hardlink
extensions; no package link metadata is applied. Only regular files are ever
created. Unix permissions and timestamps are not restored; no executable bit,
ACL, ownership, script or postinstall behavior is applied.

Names must be unambiguous ASCII using letters, digits, `_`, `-`, `.`, and `/`
between segments. No leading/trailing/doubled separator, traversal, backslash,
absolute/drive/UNC path, colon/ADS, spaces, trailing dot, Unicode normalization
ambiguity, Windows device name, file/directory prefix collision, duplicate or
case-collision (including directory components). Payload files are limited to
`Desktop`, `Doctor`, `help`, `notices` and the two root manifests. Mutable
settings, `.lock`, data, logs, models, profiles and recordings names are excluded.

| Resource | Default and absolute maximum |
| --- | --- |
| External JSON envelope / receipt | 3 MiB each |
| Decoded signed manifest / internal metadata file | 2 MiB each |
| Compressed archive | 512 MiB |
| Individual expanded file | 256 MiB |
| Total expanded bytes | 2 GiB |
| Files / derived directories | 8,192 each |
| Path / segment / depth | 240 / 100 ASCII characters / 16 segments |
| Per-entry expanded/compressed ratio | 200:1 |
| Streaming buffer | 64 KiB per active copy, never whole-archive allocation |
| Approved keys | 16, each at most 1,024 DER bytes |

`StagingLimits` can only lower archive/file/total/count/directory/ratio ceilings.
Central-directory count is checked before `ZipArchive` creates its table.
Metadata parsing, in-memory tables and persisted receipts have independent
hard bounds. Source length and actual reads are bounded, not just ZIP metadata.

Required additional free bytes = archive copy + all expanded bytes + envelope
bytes + 3 MiB receipt allowance + 16 MiB reserve +
64 KiB times (files + directories + 8). The source already exists outside staging.
The budget includes copy retention; it does not pretend to reserve space against
other disk users. Existing installed/data bytes and a later configuration
snapshot/rollback reserve are separate. Disk exhaustion during write/flush
still fails and retains owned cleanup if necessary.

## Operation and filesystem ownership

One operation per engine; competing calls return `Busy`. A persistent
`.martlet-staging.lock` under the supplied root serializes cooperating engines
during staging, receipt inspection and cleanup retry. It is never used as a
source of package trust or deleted to break another owner.

The trusted-local threat model requires a private root on an ordinary local
filesystem, no hostile same-user/admin process modifying the root or inputs,
no mount replacement/network/virtual filesystem behavior, and a caller
coordinating installation/settings changes. Reparse paths are checked through
their ancestors; UNC and Windows network drives are refused. This is not a
sandbox against an actor with the same filesystem authority. In particular,
portable path checks cannot defeat arbitrary adversarial mount/reparse/hardlink
insertion races. Cooperating owners must not mutate engine-owned pending or
finalized stages. A future activation coordinator must reverify and pin its
inputs under its own ownership through activation.

All IO is deliberately **synchronous**. Stream reads, `Flush(true)`, OS directory
rename and cleanup may block; cancellation cannot interrupt an OS call already
executing. The original token is checked directly between chunks and before
creation, writes, flush/finalization and after controlled callbacks; a blocked
cancellation callback does not hide a requested cancellation. No source is
destroyed/disposed from a cancellation callback. Final successful rename is the
linearization point: cancellation racing after it does not undo a complete stage.

The future UI must offload the whole operation, await it even when navigating
away, and retain the engine while cleanup is pending. `CleanupPending` carries
the original failure and an explicitly local `RetainedDirectory`; the engine
retains its exact remaining path ledger. `RetryCleanup()` takes **no arbitrary
path argument**, performs no recursion, and retires each ledger item only after
successful removal. Unknown children block parent deletion and remain intact.
New previews/stages are blocked until that cleanup completes. Source archives,
installed versions, sibling stages and existing destination sentinels are not
cleanup targets.

File contents are flushed, but directory metadata is not portably fsynced.
Abrupt process termination/power loss can leave an incomplete `.pending-*` or a
complete stage without a returned in-memory receipt. There is no startup scan
or automatic orphan deletion. After restart, preserve incomplete stages for
explicit local recovery; only a completed destination can be passed through
`InspectStaged`. Physical crash durability and OS interruption qualification
remain NOT RUN. No receipt means no claimed staged success.

## Failures and later gates

`StagingException.Failure` distinguishes unconfigured trust, untrusted signature,
invalid/corrupt input, unsafe entries, incompatible format/schema/RID/version,
capacity, disk, access, conflict, busy, cancellation and cleanup pending.
Messages contain safe remedies, never raw IO exception text or package/source
paths/keys. There is no logging sink. Preview paths and retained cleanup paths
are explicit local UI information and must not be sent to generic logs or
support bundles.

Before any later activation, the coordinator must require a separate **valid,
fresh pre-activation V07a configuration snapshot**, bound to current settings
revision/profile and verified via the recovery boundary. Merely constructing
the existing public `ConfigurationRecoveryReceipt` record is not evidence of
verification. This stage neither creates nor accepts a snapshot receipt, and
its `NextSteps` requirement cannot be cleared by caller JSON.

The selection engine supplies snapshot-verification integration and separate
selection approval, not activation approval. Still separate: exclusive app
ownership, migration/readiness checks, executed N-1/N rollback, uninstall/data
preservation, publisher revocation/freshness policy, Desktop UI/offload/retirement,
and actual environment qualification.
**Actual publisher authorization, release signing, protected signing access,
clean-Windows install/upgrade, rollback and release qualification: NOT RUN.**
No signing credentials, certificates or user security settings were changed.

## Local development

See `tests\Martlet.Updates.Tests\README.md` for isolated local commands and the
real ephemeral-key fixture matrix. No additional NuGet dependencies or tools
are needed; test packages use existing central pins and both new projects have
their own committed locks. Do not register a remote validation workflow for this
library. `CI=true` in local commands is an MSBuild determinism/locked-restore
setting, **not** permission to use a hosted runner.
