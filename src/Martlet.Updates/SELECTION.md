# V07b-b: private candidate selection, not executable activation

`LocalSelectionEngine` implements real private-file transactions, not an updater
or a functional upgrade claim. It does not execute a payload, replace running
files, stop processes, alter the installed layout, access the
vault, download anything, modify Windows registration or delete history.
`selection.json` is an **approved candidate selection**, never a launcher pointer.
`SelectionReceipt.IsRunnable` is always false; there is no readiness bool, setter,
public receipt constructor or method that changes it to true.
The separate rollback configuration-restoration operation described below is
the only settings effect. Ordinary selection/recovery never restores settings.

## Explicit ownership and initialization

The caller supplies an existing private control directory, an existing
`LocalStagingEngine` whose root is a direct child of that directory, and the
actual caller-owned `SettingsStore`. The settings data directory and bootstrap
installation must be outside and non-overlapping with the control directory.
Constructors do not create directories, scan applications, load keys or discover
settings. Explicit operations validate paths and read the supplied store.

```text
chosen-control-root\
  .martlet-selection.lock
  selection.json
  stages\                       existing root owned by LocalStagingEngine
    .martlet-staging.lock
    version-0.2.0.0\...          actual retained signed stage
  chosen.martlet-config          caller-created V07a snapshot source
  transaction-<32-hex-UUID>\
    before.json
    configuration.martlet-config
    journal-v2.json
    pending.json                consumed by pending atomic replacement
    selected.json               consumed by selected atomic replacement
    recovered-0.json            recovery scratch; consumed on replacement
    pending-publication.json    flushed intent before pending rename
    selected-publication.json   flushed intent before selected rename
    recovered-0.json.publication
```

`Initialize(unqualifiedBootstrap)` is explicit create-only adoption of
**unqualified owner-provided facts**, not authentication of an unsigned program.
It reads the real settings schema/revision through `SettingsStore.LoadAsync`,
pins that file, and persists format 2, generation 0, exact bootstrap facts,
control-root/settings-path/profile identity and empty current/previous selections.
An existing record or initialization/transaction evidence blocks reinitialization.
An empty signing policy may retain a bootstrap, but cannot select any package.
Neither unsigned receipts nor candidate-supplied keys establish trust.

**Format 2 is not an automatic upgrade of the earlier unpublished format 1
checkpoint.** A format-2 control transaction requires a version-2 journal named
`journal-v2.json`; the explicit restore fence below introduces version 3.
Format 1 control, legacy `journal.json` even in an orphan,
and missing required publication evidence fail closed. Preserve earlier local
checkpoints for explicit manual reconciliation; never interpret their missing
publication receipts as proof that no selection occurred.

## Prepare, approve, commit

1. Create a fresh snapshot explicitly with
   `SettingsStore.CreateConfigurationSnapshotAsync` into a selected direct child
   of the control root ending in `.martlet-config`. Do this before preparation,
   outside any selection operation. Snapshot creation is not performed implicitly.
2. `PrepareActivation(stagedDestination, currentRevision, freshSnapshotPath)`
   loads the authoritative control record and bounded journal ancestry, obtains
   actual current settings, validates the snapshot, and enters the staging
   engine's verifier-backed pinned operation. The stage must match the actual
   original installed facts and fresh settings facts, and be strictly newer than
   the currently selected version (or bootstrap when no version is selected).
   Current/previous retained selections are also reverified.
3. The returned `SelectionPlan` freezes transaction ID, kind, generation, exact
   source path/bytes, bootstrap origin, signer/version/archive/manifest/receipt,
   reader bounds, profile and opaque settings revision, snapshot identity and
   digest, retained-previous choice and planned effects. `Candidate`,
   `PreviousAfterSelection`, `Snapshot`, `PlannedEffects` and `PlanDigest` are the
   explicit local review surface. No file is selected by preparation.
4. An explicit default-No host review may call
   `plan.Approve(plan.TransactionId, plan.ExpectedRevision, plan.PlanDigest)`.
   Approval is engine/plan-bound and one attempt only, including failed,
   cancelled, wrong-plan and wrong-engine attempts. A later preparation attempt
   on the same engine invalidates older previews even if that preparation fails.
5. `CommitSelection(plan, approval)` reacquires control/stage ownership and
   read-only handles denying Windows writes/deletes for current settings,
   approved snapshot, and **all incoming/current/previous** stage envelopes,
   archives, receipts, extracted files and matching snapshots. One staging
   ownership scope covers the bounded batch (at most three stage requests);
   there is no recursive staging-lock acquisition. Retained file pins disable
   per-handle read buffering; streaming hash buffers remain bounded.
   It creates a unique transaction directory, flushes/readbacks the exact
   original record, configuration copy and journal, then atomically replaces
   `selection.json` with a pending record at generation +1. The original
   selection remains current while pending. Create-only, flushed publication
   intent precedes each pending/selected/recovered rename.
6. Before the final replacement, it rereads control/journal, actual settings,
   exact approved source and retained snapshot bytes, and reruns signer policy,
   signature, manifest, full archive and extracted-file verification. It checks
   the original cancellation token directly. Successful same-volume overwrite
   `File.Move` installs the selected record at generation +2. The result is `Committed` and
   `AwaitingReadiness`, **not activated**. Cancellation racing after that
   linearization point does not report a cancelled selection.

All journal paths are control-root-relative generated names; stage paths are
bounded direct children of the supplied stage root. No external unsigned JSON
path is followed. The authoritative record must agree with its retained
pending/final/recovered journal, original ancestry and publication evidence, not
merely parse as JSON. Every read checks bounded, known publication names across
all retained transaction directories, not just the chosen pointer's ancestry.
Replacing only `selection.json` with a valid older bootstrap, committed or pending
record is rejected when newer/conflicting publication evidence remains.

## Snapshot boundary and rollback

Core's new `ConfigurationSnapshot.Inspect(ReadOnlyMemory<byte>)` rejects length
before copying, takes one owned bounded copy, and uses the existing V07a parser.
It exposes only getter-only snapshot ID, profile ID, persisted schema, source
revision, full-file digest and manifest digest. It performs no IO and does not
expose settings bytes. Envelope consistency is not publisher authentication,
freshness, consent or readiness.

The selection engine separately checks the actual typed store's profile, schema
and **ordinal, case-preserved revision**, and retains a settings file pin through
commit. Fresh means byte/revision/profile/schema agreement with the actual store,
not acceptance of a public `ConfigurationRecoveryReceipt`, a caller's cached
facts, a timestamp, or a lowercased revision. Core snapshot digests/revisions stay
uppercase; signed package hashes stay lowercase. Those conventions are not mixed.

`PrepareRollback(currentRevision, freshSnapshotPath)` has **no candidate path
argument**. It can select only the recorded previous verified entry, never the
bootstrap or an arbitrary older package. It revalidates that entry's signature,
retained receipt digest, archive/files, exact compatible historical snapshot and
same profile, while also retaining a fresh backup of current settings.
The plan is explicitly a **pending configuration-restore plan**. Its candidate
identifies the exact historical snapshot relative path/digest and
`ConfigurationRestoreRequired`; its `Snapshot` describes the preserved current
configuration. The plan does not authorize a restore.

When leaving a selection, its last compatible configuration snapshot is retained.
A fresh snapshot becomes that previous entry's snapshot only when the signed
reader bounds include its schema; otherwise the prior compatible snapshot remains
visible in `PreviousAfterSelection`. No incompatible snapshot is silently paired
with an old reader. Rollback also refuses a reader that cannot consume schema 2,
because today's V07a restore emits v2 even for historical v1 envelopes.

A committed rollback selection remains `AwaitingConfigurationRestore` and then
requires readiness. Neither another activation nor another rollback selection
may bypass that requirement. Only the separate exact restore operation below, or the separately consented
acknowledgment of its recorded commit described later, can clear it; no caller
assertion, selection approval or public recovery receipt is accepted as proof.

## Explicit rollback configuration restoration

`PreviewRollbackConfigurationRestore(selectionRevision, sharedEffects)` takes
the **existing caller-owned `SetupOperationRunner`**, not a new runner or a busy
predicate. It returns an operation handle with `Completion` and
`RequestCancellation()`. The whole synchronous selection/staging operation is
offloaded by the runner; Core asynchronous work is awaited to actual completion
inside the retained pinned callback. Returning a Task from that callback without
awaiting it would release the pins early and is not used.

Preview loads authoritative control and only its recorded rollback snapshot,
reverifies current/previous package and snapshot evidence, and invokes the real
`SettingsStore.PreviewConfigurationRestoreAsync`. There is no source/candidate
path argument, injectable restorer, discovery or startup action. The immutable
`RollbackRestorePlan` displays exact selection generation/transaction, package
and snapshot identity, actual profile/revision/schema, Core's candidate JSON,
candidate digest and summary, expiry, and the effects of the private format-3
fence. It writes no settings, backup, transaction or control version.
The existing persistent lock files may be opened/created on explicit operations.
The preview contains personal configuration and opaque references; do not put it
in generic logs/support bundles.

After a host's distinct default-No review, explicitly call:

```csharp
var previewWork = engine.PreviewRollbackConfigurationRestore(revision, sharedEffects);
var plan = await previewWork.Completion;
// Only after explicit review of this exact plan; never unconditional in a host.
var approval = plan.Approve(plan.OperationId, plan.ExpectedSelectionRevision,
    plan.ExpectedSettingsRevision, plan.PlanDigest);
var restoreWork = engine.RestoreRollbackConfiguration(plan, approval, sharedEffects);
var result = await restoreWork.Completion;
```

Approval is engine/runner/plan/source/profile/revision/candidate-bound and one
attempt, including wrong binding, busy, failed and canceled attempts. Another
accepted rollback preview supersedes the old plan at admission, even if it is
canceled before the runner invokes its worker. A busy/refused preview does not
advance that in-memory generation or revoke a restore already running. The
existing engine gate is reserved before runner admission; accepted generation
is published before the worker proceeds or an older restore can reenter. That
reservation retires only after actual worker/cancellation completion, including
pre-start cancellation, without reacquiring the same gate inside the worker.
No configuration or control file is written merely by admission.
Its five-minute limit starts at the original
preview start and checks **both monotonic elapsed time and UTC**; awaiting IO,
approving, or a backward wall-clock change never renews it. This limit belongs
to the new rollback coordination approval; standalone V07a did not previously
have an approval-expiry feature.

Ownership order is the supplied shared effect runner, selection engine/root,
one bounded staging owner with all retained snapshots, then Core's actual
settings writer/source/current/original/result scope. The caller does not borrow
the settings lock, and standalone V07a does not recursively acquire it.
`SettingsStore.OpenConfigurationRestoreAsync` issues the narrow live scope:
one exact approved restore, not a generic writer capability. Core performs its
existing inert candidate transformation, fresh create-only byte-exact original
and actual replacement. Source and original remain pinned; only the checked old
settings target is released for rename. The resulting settings file is pinned
and verified against the exact candidate digest, same profile and emitted schema
2 through selection acknowledgment. Core validation callbacks only veto; they
cannot supply success or substitute bytes. Saved/public receipt objects are
never restoration authority.

The restore-only Core replacement uses exact write-denying, share-delete scratch
pins plus immediate named-byte checks and same-volume overwrite `File.Move`.
That preserves the same Windows source-ownership pattern as selection, without
changing generic settings save/v1 migration backup behavior. Core scope
retirement waits for actual IO. A failed temporary cleanup retains an exact
Core-issued capability in this engine: `RetryRollbackRestoreCleanup(sharedEffects)`
has no arbitrary path argument and never removes an original, snapshot, journal,
unknown child or key. Other operations on that engine refuse while cleanup is
pending.

`RollbackRestoreResult` distinguishes settings not attempted/not committed,
unproven replacement, verified settings commit, outstanding/ambiguous selection
publication and recorded acknowledgment. A late error after actual settings
replacement cannot be called "original unchanged." Actual verified result facts
and a retained original are observations, not reusable authorizations. A
later read failure preserves those already-observed facts for reporting but
still prevents acknowledgment; fresh verification is never bypassed. A
cancellation after settings commit but before acknowledgment leaves the selection
pending; cancellation after the final selection rename cannot deny that recorded
commit. Callback-retirement failure is separately exposed as `OwnershipFailed`.
The shared runner remains occupied through actual cancellation callbacks, not
just until a UI observer times out.

### Version-3 fence and interrupted restore

Read-only inspection/preview still accepts strict v2 control with unchanged v2
history. First restore approval explicitly authorizes v3, which is published
**before Core creates the pre-restore original or replaces settings**. Subsequent
transactions stay v3. Control keeps its known field shape; `journal-v3.json`
explicitly distinguishes selection from configuration restoration and binds the
exact source, profile/current revision, candidate digest and Core-generated
original filename. Existing `journal-v2.json` files are never rewritten or
reinterpreted. Unsupported/legacy formats remain refused.

The bounded restore transaction contains:

```text
transaction-<32-hex-UUID>\
  before.json
  journal-v3.json
  pending.json                 consumed by v3 generation+1 fence
  pending-publication.json
  restore-committed.json        written only from real live Core result evidence
  selected.json                consumed by generation+2 acknowledgment
  selected-publication.json
```

The final state changes only generation/transaction markers, the version fence
if necessary and current `RestoreRequired` from true to false. Packages,
previous selection and compatible historical snapshot identity are unchanged.
It requires valid commit evidence plus the existing exact publication/ancestry
checks. No package manifest/settings parser, trust authority or readiness bool
is introduced. The same 32 transactions/ancestry, 128 children, 8 recovery
scratches and existing byte/path limits apply; there is no automatic history
purge or increased capacity.

| Retained state | Explicit inspection/recovery |
| --- | --- |
| No publication intent | Preserve inert v3 scratch/orphan; no settings restore was entered. Fresh consent uses a new transaction. |
| Partial/full publication intent without its exact control pointer | `InvalidControl`, read-only manual reconciliation, as for v2 selection. |
| Valid v3 pending fence, no valid recorded settings commit | `OutcomeUnproven`; even matching candidate/original bytes cannot reconstruct lost consent or prove the operation's outcome. |
| Valid recorded settings commit, still-pending selection | `SettingsCommittedSelectionPending`; preserve actual settings and original, do not automatically acknowledge. The distinct fresh-consent acknowledgment below supports only exact unambiguous evidence and verified present settings. |
| Valid terminal pointer, commit record and publication/history | `Recorded`, idempotent Inspect/Recover, `AwaitingReadiness`, never runnable. |

`Recover` refuses an interrupted configuration restore with
`ConfigurationRestoreReconciliationRequired`: it never takes the selection-only
`RecoveredOriginal` path, reruns restore, reverses settings or silently finishes
acknowledgment. Partial/mismatched commit records remain invalid evidence.
Manual reconciliation of ambiguous checkpoints is still later-owner debt. The
new acknowledgment below does not change `Recover` into a repair command. All
old files remain intact.

Old v2 readers reject the v3 pointer at operational entrypoints. Control-only
rewind cannot hide the new transaction while publication history remains:
old readers also reject its missing v2 journal, and new readers require the
versioned restore evidence throughout bounded ancestry. This remains private
filesystem authority, not protection from deleting/rewriting the entire history.
Completed evidence describes a historical commit, not permanent agreement with
future settings changes. Any later activation must establish actual current
settings/policy/readiness again.

## Fresh-consent acknowledgment of a recorded restore

`PreviewRollbackConfigurationAcknowledgment(selectionRevision, sharedEffects)`
is read-only review of **settings already restored**, not another restore
preview. Its only eligible source is the exact authoritative format-3/4
`ConfigurationRestore` Pending state with its exact valid recorded commit,
pending publication, original and complete bounded ancestry. No terminal or
recovery publication may exist for that old restore. All current/previous
signed packages, receipts, snapshots, bootstrap origin and current immutable
publisher policy are reverified. The actual settings file must still be valid
same-profile schema 2 and byte-exactly match the recorded candidate revision.
Missing/malformed/changed evidence, wrong profile/newer schema, revoked policy
or any unmatched publication refuses read-only.

The immutable `RollbackAcknowledgmentPlan` displays a NEW operation ID, the
interrupted restore and original rollback IDs, old plan/journal/commit/pending
digests, exact original and source snapshot, current/previous selections,
actual current settings JSON/profile/schema/uppercase revision, policy/history
fingerprints, proposed generation, skipped old terminal generation, original
expiry and explicit durable effects. The JSON and paths are personal local
review information, not sanitized support/log data. Constructors/setters,
mutable settings arrays, public receipt ingestion and override booleans are
not provided.

After a distinct default-No host review:

```csharp
var work = engine.PreviewRollbackConfigurationAcknowledgment(revision, sharedEffects);
var plan = await work.Completion;
// Only after explicit review of THIS new acknowledgment, not old restore consent.
var approval = plan.Approve(plan.OperationId, plan.ExpectedSelectionRevision,
    plan.ExpectedSettingsRevision, plan.PlanDigest);
var acknowledgment = engine.AcknowledgeRollbackConfiguration(plan, approval, sharedEffects);
var result = await acknowledgment.Completion;
```

Issuance and use each burn one attempt, including wrong bindings, wrong
plan/engine/runner, busy, stale, failed or canceled use. The original five-minute
UTC **and** monotonic lifetime is not renewed by IO or wall-clock rollback.
Restore and acknowledgment previews share the existing accepted-admission
sequence: even an accepted pre-worker cancellation invalidates older plans;
a refused busy call cannot invalidate or release already-owned work.

The SAME supplied shared runner owns the whole offloaded operation. Selection
engine/root/history ownership precedes one retained staging owner, then the
actual store's `OpenCurrentConfigurationReadAsync` scope. That narrow Core
scope owns the existing settings writer lock and exact current/name read pins;
its immutable observation and `VerifyAsync` do not prove a historic commit.
Updates separately verifies the actual retained commit/original/history. The
scope has no Commit, arbitrary-path parameter or restore-evidence input.
Concurrent/use-after-retirement verification is refused; disposal awaits actual
in-flight IO. Existing persistent selection/staging/settings lock files may be
opened/created on explicit actions, but missing settings/directories are never
initialized. No real settings write, new original, snapshot creation, candidate
transformation, credential operation or second Core restore is performed.

The NEW transaction uses strict `journal-v4.json` kind
`ConfigurationAcknowledgment`, with exact Before = OLD Pending, null Pending
and Restore fields, an exact acknowledgment binding, and a new After. After
changes only format to 4, generation, NEW transaction ID, Pending to null and
current RestoreRequired to false. Old package/snapshot/previous facts stay
unchanged. If the old restore has Pending generation R+1 and reserved terminal
R+2, acknowledgment uses R+3, never the old terminal generation. Complete
intentless v4 acknowledgment drafts also reserve their recorded generations:
the new generation exceeds their bounded maximum. Overflow, duplicate
reservations or an unreadable v4 reservation journal refuses without guessing.
No new ledger or expanded bound is introduced.
Preview/admission reserve room for the additional immediate control-root child;
an already-full 128-child root refuses before a draft can make history unreadable.

```text
transaction-<NEW-32-hex-UUID>\
  before.json
  journal-v4.json
  selected.json                consumed by the single terminal rename
  selected-publication.json    flushed intent before that rename
```

One terminal rename installs both the format fence and acknowledgment because
there are **no settings/original effects requiring an earlier fence**. The
original restore's journal, original, marker and pending publication remain
byte-identical, with no manufactured old terminal receipt. Ancestry traverses
NEW After -> NEW Before / OLD Pending -> OLD Before. Strict history validation
still sees the old restore as historically unacknowledged. Later ordinary
selection/restore transactions stay v4 and retain their existing two-phase
rules; the same narrow acknowledgment supports a later proven v4 restore.
Existing v2/v3 records are not rewritten or reinterpreted.

Before final publication, actual settings, original/source, current-policy
stages and all bounded known history are checked again under retained pins.
After its own intent is written, a narrow private veto validates the exact
proposed After history while separately checking that the actual pointer
still equals Before. Ordinary readers never receive an ignore-intent mode.
The existing replacement helper releases only the checked old control target
required for Windows overwrite, retaining source/evidence/settings pins.

| New acknowledgment outcome | Meaning |
| --- | --- |
| No intent | Preserve inert draft; fresh consent uses a new ID and a provably fresh reservation. An unreadable reservation remains actionable refusal. |
| Partial/full intent without exact new pointer | `PublicationAmbiguous` / `InvalidControl`; preserve every byte for manual reconciliation. No retry, intent removal or lost-consent completion. |
| Exact terminal pointer and history | New acknowledgment `Recorded`, selection progress `AcknowledgedVerifiedState`, linked `AcknowledgedRestoreTransactionId`; `AwaitingReadiness`, never runnable. |
| Later error/cancellation/callback failure | Preserve already observed facts and any actually recorded acknowledgment; expose failures/`OwnershipFailed` separately. Never claim the old operation completed successfully or permanent settings readiness. |

`RollbackAcknowledgmentResult` distinguishes historical recorded-commit
verification from fresh present-state verification and selection publication.
It does not claim this operation committed settings. A later failed read
preserves earlier observations but cannot authorize publication. Actual
terminal rename wins later cancellation; shared admission stays owned through
real callback retirement, not merely UI observation timeout.

Restarted Inspect/Recover return Unchanged at the same new generation. Later
legitimate settings changes do not erase historical acknowledgment, but every
later effect still needs actual current verification. Old v3 readers can
observe the old pending state during intentless scratch, not complete it.
Once the recognized terminal publication filename exists they fail closed
even while the pointer remains v3; the terminal v4 pointer is unsupported too.
Control-only rewind cannot hide retained acknowledgment publication history.

No acknowledgment cleanup API exists. Old originals/snapshots/settings/history,
new interrupted drafts/intents and unknown files are preserved. This adds only
one supported known-commit reconciliation case, not corrupt-control salvage,
unproven restore adoption, automatic repair or an executable rollback workflow.

## Recovery and bounds

`Recover(optionalTransactionId)` never resumes candidate selection using lost
in-memory consent. Valid pending state restores the original **selection** at
generation +2 using flushed create-only recovery scratch and atomic replacement.
It holds the original current/previous stage and snapshot pins under one staging
owner through revalidation and the recovery rename. It does not restore settings.
Repeated recovery returns `Unchanged` at the same
generation. After a completed final replacement, recovery recognizes the selected
record and leaves it selected but unready. Corrupt/newer control or authoritative
journal/original evidence is an error, never fallback success.

Before **publication intent**, an interruption can leave an inert incomplete v2
transaction. Only a transaction with no publication receipts and no authoritative
history reference requiring one may be treated as unpublished. An explicit
transaction ID lets recovery acknowledge the unchanged authoritative selection
without interpreting, promoting or deleting that orphan.
Unknown children and all partial files remain intact. A failed initialization
that left `initial.json` requires explicit manual preservation/reconciliation;
this API refuses to overwrite it or silently adopt a guessed bootstrap.

A partial or complete publication receipt whose expected pointer has not been
installed is **ambiguous**, not a recoverable-original success. This includes
failure/cancellation between publication creation/flush and overwrite rename.
`Inspect`/`Recover` reject it read-only with `InvalidControl`, retaining exact
originals, scratch, receipts and journals for manual reconciliation. They do not
guess whether rename happened, complete lost consent, erase the receipt, reuse
the generation or automatically repair authority. The same rule rejects missing
receipts required by a history reference, a selected record substituted with a
same-generation recovered-original record, or conflicting terminal receipts.

Records are canonical bounded JSON: 32 KiB control/original, 128 KiB journal,
128 KiB V07a snapshot (Core's bound). At most 32 transaction directories, 128
immediate control-root children, 32 ancestry links and 8 recovery scratch attempts
per transaction are allowed. Full paths are at most 240 characters, control roots
166 (including room for publication filenames), stage names 100. Existing staging
archive/inventory bounds apply per pinned stage.
Capacity exhaustion is actionable and stops new work; there is no purge/archive
implementation, recursive cleanup or unlimited journal. Initialization scratch,
old versions, snapshots, failed originals and unknown data are never deleted.

## Threat model and remaining gates

Use an ordinary local private filesystem under the caller's ownership. The engine
serializes cooperating instances with its own persistent lock; the staging engine
owns its separate lock for the entire pinned callback. It never borrows another
owner's lock, removes a lock to defeat an operation, changes ACLs or scans global
trust. UNC/network, reparse/symlink, ambiguous/trailing path segments and path
escapes are refused. File sharing and final name/hash rechecks detect ordinary
noncooperating writes/replacement.

Private-root filesystem authority is the journal's authenticity boundary;
hashes/canonical JSON are not an HMAC. This is **not** a sandbox against malicious
same-user/admin/root mutation, hardlink or mount races, or an actor rewriting the
entire private history or deleting all publication evidence. The bounded retained
history check detects control-only rewind; it is not a hardware monotonic counter
or cryptographic anti-rollback store. Package authenticity is always independently reverified
against the caller's current approved policy. Root relocation/re-adoption,
key-policy lifecycle and external tamper-resistant installation authority need a
separate reviewed coordinator, not a new unsigned receipt or fabricated facts.

IO is synchronous and can block, including typed asynchronous Core reads awaited
by this synchronous boundary. Offload the **whole operation** from a UI
synchronization context and await actual completion. Cancellation is checked at
source and write/flush/finalization boundaries, not delivered only via callbacks.
Do not abandon an operation, dispose its sources or start another operation on a
UI timeout. No generic exception message or default plan/receipt `ToString`
includes configuration contents, credential references or private paths.

Replacement scratch is pinned with `FileShare.Read | FileShare.Delete` (no write
sharing), and its exact expected bytes are rechecked both through the retained
handle and the current filename immediately before rename. Publication evidence
stays separately pinned without delete sharing. On this Windows filesystem,
`File.Replace` conflicts with the write-denying source pin; same-volume
`File.Move(..., overwrite: true)` supports it. The old target is pinned during
publication IO and checked again, then released because Windows overwrite rename
cannot retire an open target. The **source** pins remain through rename.
Delete sharing necessarily permits source-name replacement: the final named-byte
check rejects substitution before that check, not a malicious same-user rename
race after it. This is not a hostile filesystem sandbox.

Files are flushed with `Flush(true)` and finalization uses same-volume
`File.Move`. Directory-entry persistence under power loss is not
portably fsynced or physically qualified. Tests inject controlled interruptions
around actual operations; they do not establish physical-crash durability.
Preserve unknown crash leftovers for explicit recovery.

The small **internal** `CandidateVerifier.ReadPackage` separates package proof
from initial strictly-newer installed-fact comparison. Only the internal
transaction-owned pinned inspection uses it for a receipt already bound to the
private history. `WithPinnedStages` batches at most three requests under one
staging lock and retains all their read handles until the selection/recovery
callback returns. Public `Stage` and `InspectStaged` keep their original newer-
version, schema, current-fact, exact-receipt and trust rejection behavior.

Still NOT RUN / not implemented here: executable activation coordinator and
launcher, Desktop UI registration/wiring to its existing shared effect owner,
manual ambiguous-restore reconciliation beyond the exact recorded-commit case, actual readiness/migration,
executed N-1/N rollback, uninstall, physical disk-full/power-loss/clean-Windows VM
qualification, real publisher authorization/signing/revocation and release.
No shipping trust key is invented. Existing unsigned packaging is not a trusted
update. No remote validation or publication is part of this slice.

The host must supply its actual `MainWindow.setupOperations`, actual store and
explicit private roots, exclude existing SupportController resources, render the
exact default-No review, invalidate stale presentation, and await actual
ownership retirement on close/timeout. Updates does not discover that runner or
add Desktop/setup/default-directory UI. This tested library capability is
**not a shipped user workflow**, binary rollback or overall V07b completion.
