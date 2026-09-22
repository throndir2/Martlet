# Consented local memory foundation (P03a/P03b)

**Implemented as an isolated library and test project; not wired into Desktop,
conversation, Doctor, a gateway, host 2, packaging or the root solution.** The
foundation provides an OFF-by-default local fact store and bounded lexical
retrieval. It performs no capture, transcript ingestion, provider call, upload,
embedding, model execution, vector-database access, backup or background timer.
Opening or using the application does not open this store.

This is internal P03a/P03b contract evidence, not completed P03, AC-16, P04 or
G4 qualification. App consent UX, gateway authentication/role enforcement,
host lifecycle/restore, real-user usefulness and physical crash/power-loss
evidence remain separate gates.

## Explicit activation and actions

There is no default directory or automatic `Open`. A caller first creates a
pure path-bound preview. Its decision defaults to `No`; only an explicit
`Allow` produces a one-use authorization:

```csharp
var activation = MemoryStoreActivationPreview.Create(selectedLocalDirectory);
// activation.Authorize() refuses: the default decision is No.
var approval = activation.Authorize(MemoryConsentDecision.Allow);

using var store = MemoryStore.Open(activation, approval);
```

Preview creation performs no filesystem IO. Authorized `Open` validates the
absolute non-root local path, requires a known local fixed/removable/RAM volume,
rejects UNC/network/unknown-volume/reparse/alternate-stream locations, creates
only that selected scope and acquires its exclusive owner lock. A second owner
is refused. An authorization is bound with ordinal equality to the exact preview
and normalized path and cannot be replayed, including by case-folding it.

The only fact-producing operation is explicit `SaveAsync(SaveFactRequest)`.
There is no transcript, conversation, provider, watcher, ingestion callback or
generic object API. Each save requires bounded fact content, typed provenance,
an explicit consent UUID and a retention choice. Supported provenance is:

- `user_entry`
- `user_reviewed_import`

`InspectAsync` returns exact current facts and provenance. `EditAsync` and
`DeleteAsync` require the fact UUID and expected fact revision; a stale or
missing fact fails rather than reporting a success-shaped result. Edits retain
creation provenance and record a fresh last-modified provenance. Every durable
mutation increments one authoritative store revision.

## Owned format and bounds

The selected directory has a finite owned layout:

| Name | Purpose |
| --- | --- |
| `.martlet-memory.v1.json` | Authoritative strict schema-1 fact document |
| `.martlet-memory.v1.lock` | Empty exclusive ownership lease |
| `.martlet-memory.v1.pending` | Exact same-directory atomic-write stage; never an authority |

Unrelated files are not opened, renamed or removed. There is no `.bak` file,
directory sweep or automatic backup. The document records a store UUID, monotonic
store revision, UTC update time and exact facts. JSON rejects duplicate
properties, unknown members, numeric/unknown enums, malformed UTF-8, missing
required fields, future schema versions, duplicate fact UUIDs and inconsistent
timestamps. A malformed or newer authoritative file is preserved and refused;
it is never reset to an empty store.

| Bound | Value |
| --- | --- |
| Facts | 512 |
| Fact content | 4,096 UTF-16 characters and 8,192 UTF-8 bytes |
| Authoritative/export JSON | 8 MiB |
| Query | 256 characters, 512 UTF-8 bytes and 24 distinct terms |
| Retrieval result | 1-20 facts |
| Derived query cache | 64 revision-bound queries |
| Expiring retention | At most 366 days when saved/edited |

Retention is an explicit `until_deleted` or `expires_at` choice; there is no
implicit permanent/default policy in a save request. The library has no idle
timer. Expired facts are unavailable and are physically removed from the
authoritative document, lexical index and cache on the next explicit store
operation or `PurgeExpiredAsync`. An unused/offline scope can therefore retain
expired bytes until it is explicitly opened and operated. Clock rollback and
physical media retention are not qualified.

## Atomicity, cancellation and interruption

Writes serialize behind the store owner. A mutation builds and validates its
complete next document and derived index, writes the fixed pending file with
write-through and disk flush, checks the original cancellation token, then
performs a same-directory atomic move/replace. Before that move, cancellation
or IO failure removes only the exact owned stage and preserves the previous
authority. Once the move commits, cancellation cannot turn the committed
mutation into a reported cancellation.

On explicit reopen, a leftover exact pending filename is discarded as
uncommitted; it is never promoted over the current authority. Controlled tests
cover cancellation before commit, a fault at the commit boundary, a leftover
partial stage, exclusive ownership and concurrent saves. This is ordinary
local-filesystem atomic-write evidence, not proof against physical power loss,
controller caches, hostile same-user races, filesystem failure or secure
erasure of old disk blocks.

`HasPendingCleanup` and `RetryCleanupAsync` retain ownership if an exact staging
file cannot be removed. No broad cleanup or unrelated-file deletion is used.
All library failures have fixed messages and retain no raw IO exception, path
or fact content.

## Lexical retrieval and exact invalidation

Retrieval uses only a deterministic in-memory lexical index rebuilt from the
authoritative facts. Unicode letter/digit tokens are lowercased; overlong token
runs are not indexed. Scores combine term frequency and inverse document
frequency. Results are bounded, deterministic and return the full fact plus its
creation/edit provenance and the exact store revision. There are no embeddings,
semantic summaries, external rerankers, vector stores or provider calls.

Every save, edit, delete or expiry swaps in a freshly built index and clears the
bounded query cache. A retrieval snapshots one revision, computes outside the
writer boundary, then rechecks that revision before returning or caching. A
delete that commits while retrieval is in flight makes that retrieval fail with
`QueryInvalidated`; it cannot return the deleted fact. Tests also inspect the
real derived index/cache after deletion. Old managed strings can remain in
process memory until reclaimed, and atomic replacement is not a secure disk
wipe; the guarantee is exact logical source/index/cache removal and stale-result
suppression.

The held-out synthetic fixture at
`tests\Martlet.Memory.Tests\Fixtures\retrieval-held-out.json` keeps its facts and
queries outside the retrieval implementation. It checks bounded top-result
behavior and provenance across server-region, preference, schedule, device and
care facts. It is deterministic contract evidence, not a real-user usefulness
score or justification for embeddings.

## Frozen export and default-No authorization

`CreateExportPreviewAsync` freezes one readable schema-1 JSON document containing
the exact current facts/provenance, export UUID/time and store revision. The
caller can copy and display those exact bytes, count, byte length and SHA-256.
The destination is selected separately and does not enter the exported JSON.

`MemoryExportPreview.Authorize(destination)` defaults to `No` and refuses.
Explicit `MemoryExportDecision.Export` creates a one-use authorization bound to
the preview UUID, store UUID/revision, digest and normalized new `.json`
destination. Export is create-only and uses a same-directory flushed partial
plus atomic rename. Reserved store filenames are never valid export destinations.
A changed store revision, deletion while the partial is being written, or
expiry sampled at finalization invalidates export before rename. Once an
authorized export rename commits before a later delete, that user-created file
is outside store ownership and immediate erasure, as are any user-created
copies. No upload or support contact exists.

The schema has no credential, endpoint, model, device or filesystem-path field.
Fact content itself is intentionally present in an explicit export and may
contain whatever the user chose to save; callers must preview it. The library
has no logger and exception/authorization `ToString()` output omits fact content
and destinations.

## Local validation

Use the exact SDK in `global.json`, committed project locks, an isolated CLI
home and a unique local `C:` artifact directory:

```powershell
$env:CI = 'true'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
dotnet restore tests\Martlet.Memory.Tests --locked-mode --artifacts-path $artifacts
dotnet build tests\Martlet.Memory.Tests --no-restore -c Release --artifacts-path $artifacts
dotnet test tests\Martlet.Memory.Tests --no-build -c Release --artifacts-path $artifacts `
  --results-directory "$artifacts\results"
```

The project uses only the runtime/BCL; the test project uses existing central
test pins. Neither project is in `Martlet.slnx`, Desktop/Core settings, package
payloads or a remote workflow. Validate it directly until an app integration
owner deliberately takes those shared surfaces.

## Remaining gates

- Design and qualify the Desktop UX for enabling a selected store, explicit
  save/edit/delete, expiry choices and exact default-No export confirmation.
- Define authenticated gateway/host-2 ownership, role scope, transport schema,
  service lifecycle, migration and compatibility before remote access.
- Integrate retrieval into a fresh per-turn authorization/budget without
  uploading the complete store or treating retrieved text as instructions.
- Design explicit backup/restore semantics and prove compatible restore and
  deletion behavior; this foundation intentionally makes no backup.
- Run physical interruption/filesystem/storage tests and clean-machine
  lifecycle tests; controlled IO interruption is not power-loss qualification.
- Evaluate usefulness, false retrievals and privacy comprehension with consented
  held-out real-user tasks and resource budgets before G4.
- Reassess embeddings/reranking only after a labeled evaluation demonstrates
  benefit and model/license/index deletion lifecycle is reviewed.
