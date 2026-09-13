# V07b-b: private candidate selection, not executable activation

`LocalSelectionEngine` implements real private-file transactions, not an updater
or a functional upgrade claim. It does not execute a payload, replace running
files, stop processes, alter the installed layout, restore settings, access the
vault, download anything, modify Windows registration or delete history.
`selection.json` is an **approved candidate selection**, never a launcher pointer.
`SelectionReceipt.IsRunnable` is always false; there is no readiness bool, setter,
public receipt constructor or method that changes it to true.

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
    journal.json
    pending.json                consumed by pending atomic replacement
    selected.json               consumed by selected atomic replacement
    recovered-0.json            recovery scratch; consumed on replacement
```

`Initialize(unqualifiedBootstrap)` is explicit create-only adoption of
**unqualified owner-provided facts**, not authentication of an unsigned program.
It reads the real settings schema/revision through `SettingsStore.LoadAsync`,
pins that file, and persists format 1, generation 0, exact bootstrap facts,
control-root/settings-path/profile identity and empty current/previous selections.
An existing record or initialization/transaction evidence blocks reinitialization.
An empty signing policy may retain a bootstrap, but cannot select any package.
Neither unsigned receipts nor candidate-supplied keys establish trust.

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
   approved snapshot, stage envelope, archive, receipt and every extracted file.
   It creates a unique transaction directory, flushes/readbacks the exact
   original record, configuration copy and journal, then atomically replaces
   `selection.json` with a pending record at generation +1. The original
   selection remains current while pending.
6. Before the final replacement, it rereads control/journal, actual settings,
   exact approved source and retained snapshot bytes, and reruns signer policy,
   signature, manifest, full archive and extracted-file verification. It checks
   the original cancellation token directly. Successful `File.Replace` installs
   the selected record at generation +2. The result is `Committed` and
   `AwaitingReadiness`, **not activated**. Cancellation racing after that
   linearization point does not report a cancelled selection.

All journal paths are control-root-relative generated names; stage paths are
bounded direct children of the supplied stage root. No external unsigned JSON
path is followed. The authoritative record must agree with its retained
pending/final/recovered journal and original ancestry, not merely parse as JSON.

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
requires readiness. A later shared effect owner must obtain a fresh
`PreviewConfigurationRestoreAsync` for the exact retained snapshot, explicitly
approve it and use `RestoreConfigurationAsync`. Existing V07a guards revoke
stale acknowledgments/checkpoints and imported credential bindings, retaining
current cleanup ownership. Selection never calls restore or any vault API, and
does not clear the pending-restore requirement on a caller's assertion.

## Recovery and bounds

`Recover(optionalTransactionId)` never resumes candidate selection using lost
in-memory consent. Valid pending state restores the original **selection** at
generation +2 using flushed create-only recovery scratch and atomic replacement.
It does not restore settings. Repeated recovery returns `Unchanged` at the same
generation. After a completed final replacement, recovery recognizes the selected
record and leaves it selected but unready. Corrupt/newer control or authoritative
journal/original evidence is an error, never fallback success.

Before pending replacement, an interruption can leave an inert incomplete
transaction. An explicit transaction ID lets recovery acknowledge the unchanged
authoritative selection without interpreting, promoting or deleting the orphan.
Unknown children and all partial files remain intact. A failed initialization
that left `initial.json` requires explicit manual preservation/reconciliation;
this API refuses to overwrite it or silently adopt a guessed bootstrap.

Records are canonical bounded JSON: 32 KiB control/original, 128 KiB journal,
128 KiB V07a snapshot (Core's bound). At most 32 transaction directories, 128
immediate control-root children, 32 ancestry links and 8 recovery scratch attempts
per transaction are allowed. Full paths are at most 240 characters, control roots
170, stage names 100. Existing staging archive/inventory/handle bounds still apply.
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
entire private history. Package authenticity is always independently reverified
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

Files are flushed with `Flush(true)` and finalization uses same-volume
`File.Replace`/`File.Move`. Directory-entry persistence under power loss is not
portably fsynced or physically qualified. Tests inject controlled interruptions
around actual operations; they do not establish physical-crash durability.
Preserve unknown crash leftovers for explicit recovery.

The small **internal** `CandidateVerifier.ReadPackage` separates package proof
from initial strictly-newer installed-fact comparison. Only the internal
transaction-owned pinned inspection uses it for a receipt already bound to the
private history. Public `Stage` and `InspectStaged` keep their original newer-
version, schema, current-fact, exact-receipt and trust rejection behavior.

Still NOT RUN / not implemented here: executable activation coordinator and
launcher, Desktop UI/shared effect integration, actual readiness/migration,
executed N-1/N rollback, uninstall, physical disk-full/power-loss/clean-Windows VM
qualification, real publisher authorization/signing/revocation and release.
No shipping trust key is invented. Existing unsigned packaging is not a trusted
update. No remote validation or publication is part of this slice.
