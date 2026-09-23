# V07b-c: verified local activation pointer and readiness transaction

`LocalActivationEngine` is a private-filesystem activation coordinator layered
after signed staging, selection and any required V07a rollback configuration
restore. It owns a **private activation transaction record** named `active.json`.
There is no Desktop registration or launcher consumer in this slice, and the
coordinator never executes a candidate. Staged and selection receipts remain
non-runnable. `ActivationStatus.PointerPublished` means that the coordinator
reverified the exact transition, received its internal inert test result and
atomically published the matching terminal record. It does not mean a candidate
is ready: `ActivationReceipt.Readiness` is `MissingReadiness` and `IsRunnable`
is false, including after reopening or recovering a test-authored record.
The full pending/terminal/recovery state machine is retained; no public callback,
constructor or caller-provided bool can upgrade its proof into launch authority.

This record does not prove Authenticode, publisher authorization, installer behavior,
process termination, migration quality, native startup, clean Windows behavior
or physical crash durability.

## Roots and explicit initialization

The caller supplies an existing ordinary local private activation directory and
an existing `LocalSelectionEngine`. The activation root must not overlap the
selection root, staging root, settings directory or bootstrap installation.
Construction creates nothing and does not inspect a package.

`Initialize(expectedSelectionRevision)` is a create-only format-1 binding to the
actual selection/staging/settings roots, profile and unqualified bootstrap
facts. It publishes revision 0 with no active version, so its receipt is not
runnable. An existing pointer, `initial.json`, transaction evidence, the old
name `activation.json`, or an unsupported record blocks initialization. There
is no automatic adoption, relocation or format migration.

```text
private-activation-root\
  .martlet-activation.lock
  active.json
  transaction-<32-hex-UUID>\
    before.json
    journal-v1.json
    pending.json                  consumed by pending pointer publication
    pending-publication.json
    readiness-intent.json
    readiness-result.json
    active.json                   consumed by terminal pointer publication
    active-publication.json
    recovered-0.json              consumed by explicit recovery
    recovered-0.json.publication
```

## Prepare, approve and activate

1. `PrepareActivation(expectedActivationRevision, expectedSelectionRevision)`
   owns both private roots, current settings and one staging owner. It validates
   both retained activation entries and the selected current/previous entries
   under the current approved signing policy. The signed inventory must contain
   the exact `Desktop/Martlet.Desktop.exe`; the plan binds its signed length and
   SHA-256 without opening it for execution.
2. The actual current settings profile, revision and schema are pinned under
   Core's current-configuration read/writer scope. The supplied stager's existing
   installed-facts provider must still match the original installation version,
   RID, directory and image revision; this is not an installation scan or a new
   authority callback. Settings facts come from the real store, not stale
   settings fields in the installed-facts provider. The
   selected reader bounds must include that schema. A pending selection or
   `RestoreRequired` selection is refused.
3. The transition relationship is exact. First activation reviews only the
   selected current version; a selected previous that was never activated is
   not adopted as verified rollback evidence. A forward transition requires
   selected previous = active current and a strictly newer selected current. A rollback requires selected
   current = the one active previous and selected previous = active current.
   No arbitrary stage, bootstrap or deeper history can be targeted.
4. Rollback additionally requires the selection history to report
   `Recorded` or `AcknowledgedVerifiedState` for the real V07a configuration
   restore. A caller bool, saved receipt or matching settings file cannot clear
   that gate.
5. After a default-No review, `ActivationPlan.Approve` binds the exact operation,
   activation revision, selection revision, case-preserved settings revision
   and plan digest. Approval and use are one attempt and engine/plan-bound.
6. `Activate` writes and flushes the immutable before/journal/pending evidence,
   then publishes the pending pointer with write-ahead intent. A pending pointer
   is always non-runnable, including when it still names the formerly active
   version.
7. The coordinator writes a bounded readiness intent and calls only its injected
   internal test probe. Production construction has no probe and fails with
   `ReadinessUnavailable` before creating a transaction. There is no command,
   process path, shell, executable callback or default candidate launch.
8. The probe receives the pinned stage/executable path and exact digests, can
   return only `Ready` or `NotReady`, and has a fixed local limit of at most
   30 seconds. Both monotonic elapsed time and UTC enforce the limit before
   invoking the probe and through final publication, including time spent on IO.
   A blocked synchronous probe is not abandoned when its limit expires:
   ownership remains held until the actual work returns. The result
   document is a closed deterministic passed/failed/timed-out shape with no
   caller-controlled success JSON or diagnostic text.
9. Only a passed result with the expected digest can proceed. The coordinator
   rereads the selected control/history, current settings, signed envelope,
   archive, staged receipt, every extracted file, readiness files and the
   replacement name while all original pins remain owned. The final source and
   authoritative target names/handles are checked again after provider
   revalidation, with deadline/cancellation checked after those last IO reads.
   It writes terminal
   publication intent, validates the proposed history and atomically overwrites
   `active.json`. That rename is the pointer-publication linearization point. Cancellation
   after it does not revoke the recorded transition.
   A later IO/cancellation callback failure reports `PublishedOutcomeUncertain`,
   never a cancelled or uncommitted transition; inspect the actual retained
   history before taking another action.

The terminal record retains only the newly active version and the formerly
active version. A forward transition therefore keeps N and N-1; a rollback
swaps those same verified entries. No version directory, configuration snapshot,
transaction, original or unknown child is deleted.

## Interruption and recovery

`Recover(optionalTransactionId)` never resumes readiness and never publishes the
candidate from a remembered passed result. With a valid pending pointer and no
terminal publication intent, it re-verifies current trust, selected history,
settings and retained stages, then atomically records the previous activation
state at the transaction's reserved terminal revision. The recovered old current's
earlier internal result and current package proof are revalidated, but it still
has `MissingReadiness`, not launch permission. Recovery is idempotent and never
replaces current settings with the old target's recorded settings revision.

Before pending intent, a partial transaction is an inert orphan; an explicit ID
only acknowledges the unchanged authoritative pointer. Partial readiness intent,
result or terminal scratch is also non-authoritative and can be recovered
without deleting it. Unknown children remain untouched.

Once `active-publication.json` exists without the exact terminal pointer, the
outcome is `PublicationAmbiguous`. The same is true for a partial/malformed
pointer publication, a control-only rewind, substituted final scratch or
conflicting recovery publication. Inspect and recovery stay read-only and
preserve every byte for manual reconciliation; they do not guess whether rename
returned, remove intent or reuse its revision.

Only `journal-v1.json` is recognized. `journal.json`, another `journal-*.json`,
format 0/future control, the legacy `activation.json` name and missing required
publication/readiness evidence fail closed. There is no cleanup API or recursive
delete. Up to eight create-only recovery scratch names bound repeated local IO
failures.

## Ownership, bounds and threat model

Lock order is activation root/history, selection root/history/control, staging
root/snapshot pins, then Core's settings current-read/writer scope.
One activation operation per engine and persistent root locks serialize
cooperating instances. Through readiness and final publication the coordinator
retains:

- the authoritative activation and selection control bytes;
- current settings bytes and writer-lock scope;
- selected and retained signed envelopes, archives, receipts and every staged
  file;
- prior activation/readiness history plus the new before/journal/intent/result;
- exact replacement sources and publication receipts.

On Windows, write-denying pins prevent ordinary writes, deletes and replacement.
Names are reopened and compared immediately before rename for filesystems with
weaker sharing. Source pins use bounded streaming reads. Synchronous filesystem
calls and an injected probe can block; cancellation is checked around each
boundary but cannot interrupt an OS call or a probe that violates its contract.
The whole operation must be offloaded and awaited without abandoning ownership.

Activation control is at most 64 KiB, journal 256 KiB, readiness documents
32 KiB, 128 immediate root children, 32 transaction directories, 32 ancestry
links and eight recovery scratches per transaction. Paths retain the existing
240-character local bound. Existing signed archive/file/count limits remain
unchanged.

The private root is the activation-history authenticity boundary. Canonical JSON
and hashes detect ordinary corruption and control-only rewind; they are not an
HMAC, hardware monotonic counter or sandbox against same-user/admin/root history
replacement, hardlinks, mount races or deletion of all evidence. File flushes
do not portably fsync directory metadata. Controlled interruption tests do not
establish power-loss durability.

## What remains

This port reuses `93ae3825b22bd258fd9be48f08dab2829d1309ce` while retaining
main's historical schema semantics and fixing its readiness/status, live-facts,
history ownership and final-publication gaps. Settings schemas 1-4 remain
byte-exact on activation: activation does not migrate settings or reset enabled
schema-4 memory/privacy policy. Separately consented rollback restore still
uses Core's existing schema-4 policy (including disabling memory for review).
Old restore receipts retain their recorded schema, not today's writer schema.
No Gateway or device-pairing state is read, expired, reset or converted.
Imported configuration cannot convert timed prototype authentication into
permanent pairing or establish device ownership.

The production readiness dependency is intentionally absent. The internal test
probe exercises the exact filesystem transaction with inert payload bytes; it
does not run `Martlet.Desktop.exe`, scripts, installers or migrations. A future
reviewed host must supply a non-arbitrary readiness implementation, exclusive
process/launcher ownership, process stop/start policy, actual migration/startup
checks and UI offload without weakening these proofs.

The later `833923e` launcher adapter is deliberately not imported. Its future
port must keep the image/settings/current-trust rechecks, exact source and
history pins, proof deadline through the final rename, original cancellation
token, one-use consent and post-publication uncertainty distinctions. It must
bind non-fabricatable real readiness to the exact process/image/settings and
retain all leases through actual process/callback retirement. Neither the inert
probe's deterministic result hash nor an old `active.json` may authorize launch
or be automatically upgraded into that future proof.

Still deferred / **NOT RUN**: publisher authorization and key lifecycle,
Authenticode and installer signing, protected signing access, actual executable
startup/readiness, N-1/N process rollback, installer/repair/uninstall execution,
physical disk-full/power-loss tests, clean standard-user Windows qualification,
novice lifecycle evidence, release publication and support readiness. No signing
key, publisher claim, release status or OS evidence is invented by this layer.
