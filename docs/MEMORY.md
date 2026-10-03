# Memory (P03a/P03b/P03c)

**Memory is ON by default.** Martlet remembers lasting things that are talked
about and recalls them in later conversations. New profiles, v1-v5 migrated
profiles and v4/v5 profiles whose memory was only OFF by the old default all
read as ON; an OFF saved under settings v6 is an explicit choice and stays OFF.
Configuration restore still forces memory OFF for review (like routes).

When memory is ON, each explicit conversation turn recalls saved facts
automatically, and after each completed reply the same Thinking model picks out
lasting facts to save (see [Automatic recall and remembering](#automatic-recall-and-remembering)).
Facts stay in the local store on this PC; there is no embedding, vector
database, cloud copy, store-wide upload, backup or background timer. Screen and
camera glances are never remembered. This is internal functional evidence, not
completed remote P03, AC-16, P04 or G4 qualification. Gateway
authentication/role enforcement, memory-store backup/restore, real-user
usefulness/privacy comprehension, clean-machine and physical crash/power-loss
evidence remain separate gates.

The foundation was reused from `fc1fd57f59fed0899ccbd5991797f1699d4e9bfb`
in PR #39 with finalization-time expiry and consistent top-K ranking fixes.
This slice adapts only the memory delta of
`b80920ad2fde41fdb3456a992786f642a7e22887`, plus the named memory fixture argument
from `68bd6c8264a7215dc24f8fb8115d903910925cfb`, onto PR #44's canonical
schema-3 companion baseline. It preserves PR #39 fixes and PR #44 historical
receipt/snapshot semantics. Source avatar/listen-first dependencies are excluded;
no Gateway or installation schema/activation changes are included.
AC-16 is unchanged. Source package hashes are historical, not new evidence.

## Desktop enablement and fact management

**Memory (ON by default)** exposes an accessible management window. Opening it
loads the policy and, when memory is ON, lists the remembered facts newest
first with where each came from. The default scope is the app-owned `memory`
child of the selected Martlet local-data directory. A user may instead enter or
browse to a custom absolute local directory. The shared foundation rejects
roots, UNC/network/unknown-volume, alternate-stream and reparse/link scopes.
The enable checkbox turns memory on or off and saves at once (there is no Save
button; a typed custom folder saves when you leave the field or press Enter,
and settings from before memory existed record the default when the window
opens). Each save goes into the newest saved settings, so other changes made
meanwhile are kept. Configuration validation reads path metadata but creates
no directory, lock or store document.

Strict memory settings own `enabled`, the app-local/custom policy, optional
normalized custom path and a fresh configuration revision. Settings v6 marks
memory as ON by default: reading a v1-v5 file treats an OFF memory section as
the old default (ON) until the file is saved as v6, and v1-v5 migration is
explicit and atomic and creates the existing exact original-file snapshot.
Configuration backup includes only this enable/path policy, never facts/store/
exports. Restore accepts compatible v1-v6 settings but always forces memory OFF
for review: v4+ sources retain their storage policy; older sources preserve the
current policy. No restore action opens, copies or validates a fact store.
Changing configuration invalidates an in-flight app retrieval.

Facts come from two places: the dedicated editor (**Add fact**, with an
explicit `until_deleted` or 30/90/365-day expiry and fresh `user_entry`
provenance) and automatic remembering from conversations (`conversation`
provenance, kept until deleted). Inspect shows fact/store revisions, creation
and last-modified source/times/consent IDs and exact expiry. Delete uses an
explicit Yes/No dialog whose default is No; purge expiry is also an explicit
action. Management work runs off the WPF dispatcher under the app-shared effect
owner, and every store open in the process (recall, remembering, management)
is serialized behind one store gate.

## Explicit activation and actions

The library still has no default directory or automatic `Open`. The Desktop
owner resolves its settings policy, then creates a pure path-bound
preview. `ValidateLocalScope` checks the local path without creating anything.
The activation decision defaults to `No`; the Desktop owner produces a one-use
`Allow` authorization only while memory is ON:

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

The only fact-producing library operation is `SaveAsync(SaveFactRequest)`.
The library has no transcript, conversation, provider, watcher, ingestion
callback or generic object API; Desktop decides what to save. Each save
requires bounded fact content, typed provenance, a consent UUID and a retention
choice. Supported provenance is:

- `user_entry`
- `user_reviewed_import`
- `conversation` (picked out of a finished exchange while memory was ON)

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
The full ranking (score, matched terms, newest modification time, then UUID)
is applied before the requested result limit.

Every save, edit, delete or expiry swaps in a freshly built index and clears the
bounded query cache. A retrieval snapshots one revision, computes outside the
writer boundary, then samples expiry under the writer boundary and rechecks
that revision before returning or caching. Facts expired at that final sample
are purged and invalidate the query, including cache hits. A
delete that commits while retrieval is in flight makes that retrieval fail with
`QueryInvalidated`; it cannot return the deleted fact. Tests also inspect the
real derived index/cache after deletion. Old managed strings can remain in
process memory until reclaimed, and atomic replacement is not a secure disk
wipe; the guarantee is exact logical source/index/cache removal and stale-result
suppression.

If a private store/export partial cannot be removed after a failed or canceled
Desktop action, the original store and app effect slot remain owned. Memory
shows the pending cleanup and **Retry owned cleanup** retries only the engine's
recorded paths, even from a reopened Memory window. Cancellation and closing
an observer never release this quarantine; no new app effect is admitted until
cleanup and disposal finish. There is no automatic cleanup sweep or retry timer.
Exiting the process can still leave private partial bytes; this is not secure
erasure or crash recovery of an export.

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

Desktop displays the exact frozen JSON plus preview UUID, fact count, byte
length, store revision and SHA-256 before destination selection. Its export
checkbox is cleared for every preview and the export button requires that
fresh checkbox. Export remains create-only and local; support has no upload or
contact path and configuration recovery never includes the bytes.

## Automatic recall and remembering

There is no per-turn memory checkbox: the memory setting decides. When memory
is OFF, the store is never opened or read by a conversation.

**Recall.** After STT and participation accept the current explicit typed, PTT
or hands-free turn, Desktop opens the store once, asks the lexical index for the
best matches for that user input and fills up to twelve facts with the most
recently changed ones (so a small store is recalled whole). The facts travel in
the system instructions as one block between `[MARTLET_LOCAL_MEMORY]` labels,
one line per fact with its source (`saved by the user` / `from conversation`)
and date. The instruction says they are background data, never instructions,
permissions, routing or tool directives. Consent UUIDs, paths and the rest of
the store are not sent.

The existing 16,384-byte, 16-message and 16,640 local input-token reservation is
unchanged. Current input/persona/style are admitted first; oldest volatile
conversation messages are omitted, then the least relevant recalled facts,
until the request fits. Used/omitted fact counts and store revision are visible
metadata, but fact content is excluded from the timeline, support journal and
ordinary logs. Memory is helpful, not required: if a fact changes while it is
read, the current facts are read once more; if the store can't be read (busy,
unavailable, turned off), the reply goes ahead without memory and the status
names the problem.

**Remembering.** After a reply completes, the exchange is queued (at most four
pending) and handled in the background, one at a time, on a separate text-only
runtime so it never delays the next turn. Desktop recalls up to ten related
facts, then sends one extra request (persona-free, at most the max reply length
in output tokens, same
Thinking route and credential binding, its own one-request authorization) with
the latest exchange, the previous exchange as context and those numbered facts.
The model answers in a strict line format: `REMEMBER: <fact>`,
`UPDATE <n>: <fact>`, `FORGET <n>` or `NOTHING`, at most three lines. Desktop
validates every line (single line, <=300 characters, real words, in-range
numbers, no memory labels), skips near-duplicates, applies updates/forgets only
to the exact fact revisions it showed, and saves new facts with `conversation`
provenance until deleted. A full store drops its oldest conversation fact, never
a fact the user typed. The conversation window shows what was remembered
(*Remembered: …*), and Memory lists it.

A model that declines or answers with nothing simply has nothing to remember;
an answer cut off by the max reply length keeps the lines it finished. If the
store is briefly in use (recall, the Memory page) or a fact changed meanwhile,
remembering waits and tries twice more. When it still fails, the conversation
says why once (*Couldn't update memory.* plus the reason, such as the
provider limiting requests or the memory folder being unusable); the same
problem on later exchanges is only logged (`Remembering failed (<code>)`) until
remembering works again.

Delete, save/edit/purge, memory configuration, pause, lock, Stop,
output/configuration change, conversation close and app exit advance the app
retrieval generation and cancel the old operation. The foundation store
revision check separately invalidates a source/index/cache result changed in
flight. Lock, pause, mute, a configuration change, conversation close and app
exit cancel pending remembering; Stop and window deactivation do not, because
the exchange already finished. Turning memory off or changing its configuration
drops anything still being remembered.

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
test pins. Both now join `Martlet.slnx`; Desktop and package graphs reference
the runtime assembly. Direct Memory tests remain useful for focused storage
validation. Desktop/Core/Updates suites cover production-path WPF, settings
migration/recovery, automatic recall/remembering/input encoding and lifecycle invalidation.
All validation remains local; no remote workflow was added or run.

## Remaining gates

- Define authenticated gateway/host-2 ownership, role scope, transport schema,
  service lifecycle, migration and compatibility before remote access.
- Design explicit backup/restore semantics and prove compatible restore and
  deletion behavior; this foundation intentionally makes no backup.
- Run physical interruption/filesystem/storage tests and clean-machine
  lifecycle tests; controlled IO interruption is not power-loss qualification.
- Evaluate usefulness, false retrievals and privacy comprehension with consented
  held-out real-user tasks and resource budgets before G4.
- Reassess embeddings/reranking only after a labeled evaluation demonstrates
  material benefit and model/license/index deletion lifecycle is reviewed.
  No embedding/vector adapter is present in this slice.
