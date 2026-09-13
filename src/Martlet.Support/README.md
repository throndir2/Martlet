# Local support engine (V06a)

Portable `net10.0` engine, not an uploader, collector, repair tool or completed
V06 release gate. [V06b Desktop integration](../../docs/TROUBLESHOOTING.md)
now registers it in the solution, Desktop and packaging, with explicit local
recording and frozen preview/export; nothing starts collection on launch.
Doctor help is read-only and CLI export is not implemented. The sole engine project
reference is the existing Diagnostics project (and its existing transitive
Core/Sessions/Fixtures/Audio references). No new runtime package, database,
network client, credential-store adapter, device adapter, or source callback
is added. Refer to the shared [installation/support design](../../docs/INSTALLATION_SUPPORT.md),
[privacy boundaries](../../docs/ARCHITECTURE.md), and [delivery ledger](../../docs/DELIVERY.md).

## Integration API

The Desktop integration owns when to start a journal, which absolute local
directory belongs to the selected profile, what range to select, and the
visible preview/destination/explicit confirmation. A saved setup choice or
permission to run a probe is **not** support-export consent.

```csharp
// These are explicitly supplied objects and paths, not discovered defaults.
using var journal = DiagnosticJournal.Start(selectedJournalDirectory);
journal.Append(DiagnosticEvent.FromProbe(report.Probes[0], observedUtc));
var selection = journal.Select(new LogRange(fromUtc, throughUtc));
var summary = SettingsSummary.FromStatus(loadState, profileKind, setupStatus);
using var snapshot = SupportSnapshot.Freeze(
    summary, report, BuildMetadata.FromExecutingAssemblies(), selection);

// Present every Files entry (name, byte size, digest, source), SelectedRange,
// FrozenAtUtc, OmissionSummary, and the chosen local destination.
// Preview returns a COPY of the exact bytes included for this ZIP entry.
byte[] safePreview = snapshot.Preview("doctor.json");

// Call ONLY in response to deliberate confirmation of that preview/destination.
ExportConsent consent = snapshot.Approve(snapshot.Id, snapshot.Digest, selectedZipPath);
ExportReceipt receipt = snapshot.Export(consent, selectedZipPath);
```

For the deliberate report-only choice, `JournalSelection.Empty(range)` validates
the same null/UTC/order/count/byte bounds and returns an immutable empty selection
without IO. It never substitutes for a failed journal read. The general
selection constructor remains internal.

`FromProbe` deliberately refuses unsupported probes/catalog IDs or a stale pass;
it does not turn arbitrary diagnostic text into safe log data. Historical
stale reports can still be included by `Freeze`: the projection preserves
`recorded_outcome`, freshness, provenance, and **the shared `DoctorReport.ExitCode`**.
It adds no readiness computation. A historical observation of Passed with
Stale freshness is not a current pass; render the report's shared incomplete
exit and labels, never just its recorded outcome. Log freshness similarly
describes the observation at recording time, not a readiness claim at export.

For a packaged build, pass the actual already-read packaging `manifest.json`
bytes to `BuildMetadata.FromPayloadManifest`. It understands the current
schema-1 internal unsigned payload manifest, validates its exact field set,
and projects versions, target alias, commit, dirty flag and inventory count.
It does **not** open inventory paths or verify the installed payload. No current
C# build-manifest contract exists to reuse; the parser follows the existing
packaging schema without changing it. `FromExecutingAssemblies` reports the
executing shared Diagnostics assembly and process runtime version; SDK,
commit and inventory are unavailable, not guessed from a pin. Neither source
claims publisher-signature, install, hardware, or provider qualification.

All IO APIs are synchronous. WPF consumers **must run them off the
dispatcher** and retain the journal/snapshot and outstanding task until the
actual call completes. A slow filesystem call can block past a deadline.
Cancellation is checked directly on the original token, along with monotonic
elapsed time, before and after IO and before finalization. It is not forced
termination of native IO. Never display Busy, cancellation, timeout, or an
outstanding task as a completed write/export.

## Exact accepted metadata

All support schemas start at version 1. `DiagnosticEvent`, `JournalOptions`,
the journal manifest/frame/record, and typed settings summaries disallow
unmapped JSON members. Core `ContractJson` provides duplicate-property and
Unicode checks, required-field checks, and exact case-sensitive snake-case
enum tokens (not numeric enum values). No dictionary, arbitrary object, raw
exception, content string, file picker, source delegate, or provider object
is accepted by the snapshot API.

| Source | Accepted fields | Omitted / rejected |
| --- | --- | --- |
| Event | UTC timestamp (years 2000-2100); optional finite monotonic duration 0-24h; declared severity/component/stage/role; exact authored finding/action pair; provenance/freshness; `None`/`Fixture`/`OpenAi` adapter alias; nonempty trace/turn UUIDs; count 0-1,000,000; queue depth 0-65,536 for audio/conversation/provider; declared state transition and suppression reason | Free text, model/voice/user IDs, paths, endpoints, provider bodies, unknown enums/codes/actions, inconsistent role/stage or fixture labels, unobserved/stale pass |
| Settings summary | Load state/profile kind; checkpoint; exactly typed STT/LLM/TTS route/destination/credential-reference booleans; bounded pending-removal count; audio selection/default-policy booleans | `AppSettings`, credentials/references, origins, model/voice IDs, device IDs/names, raw configuration, checkpoint timestamps or claimed current audio readiness |
| Doctor report | Version/time; shared exit code; settings state/profile kind; known probe IDs/stages/required flags; recorded outcome/provenance/freshness; observation and bounded timing/age; execution/still-running; stable catalog code/action and authored catalog guidance; normalized error enum and pseudonymized trace | All supplied Summary/Remedy/Error.Summary text; raw fixture text/refusal/trace; profile UUID; full Fixture/Setup objects; arbitrary probe/action IDs |
| Build manifest | Exact current top-level and inventory-entry schema; numeric versions; fixed internal unsigned channel and Windows x64 RID; hex commit; dirty flag; file count | Inventory paths/bytes/hashes are validated but not exported or opened; unknown members rejected; raw runtime/environment/configuration dumps never collected |
| Journal selection | Explicit inclusive receipt-UTC range and bounded records from this engine | Recursive directory sweep, application-data/home scan, external files, unrequested records |

The known probe IDs are the existing local registry IDs plus `fixture.session`:
`settings.load`, `provider.connection`, `audio.playback`, `application.version`,
`runtime.version`, `audio.input`, `pipeline.vad`, `pipeline.stt`, `pipeline.policy`,
`pipeline.llm`, `pipeline.tts`, `host.connection`. Adding a new caller/probe
requires a deliberate allowlist update; new diagnostic findings/remedies still
belong to the shared Diagnostics catalog, not a second support catalog.

An underlying probe `Error.ActionId` (for example Core's
`provider.stream.inspect`) is validated by the shared error contract but is
never exported or used as fallback guidance. It need not be a Diagnostics
catalog remedy. Exported probe actions and invocation actions still require
exact authored catalog entries. Actual shared fixture deadline/sequence failures
therefore remain exportable without exposing underlying action/error text.

Sensitive identities are preferably not accepted at all. Raw journal trace and
turn UUIDs remain useful for local correlation. Export replaces all accepted
journal/trace/turn UUIDs, including report error traces, with a coherent fresh
per-bundle HMAC-derived UUID map; the random key/map are discarded at freeze.
The same raw UUID maps to the same alias across files in that bundle, not across
bundles. This is pseudonymization, not anonymization. Times/counts/build versions
can still identify incidents; allowlisting and authored canaries are not a
universal redaction or legal privacy guarantee.

## Journal layout, limits, and recovery

`Start` is explicit and is the only directory-creation entry point. Constructing
options or metadata does no IO. A directory is one app/profile owner's scope;
there is no default application directory or automatic collection. Supply a
fully qualified non-root local path. UNC/device/network-drive paths, alternate
streams and existing reparse-point ancestors/entries are rejected. Unrelated
files in the selected directory are never read or removed.

The layout has a fixed `.martlet-support.v1.json` ownership manifest and empty
`.martlet-support.v1.lock`, a random manifest owner UUID, up to 32 exact slot
names `<owner-N>.<00-31>.open` / `.jsonl`, and one exact
`<owner-N>.control.pending` manifest staging name. Only this finite layout is
inspected. Unknown names are not swept. A collision at an unregistered
engine-layout slot, a nonempty lock, corrupt manifest, duplicate active/final
slot, or unsupported configuration is rejected. The reserved manifest/lock
names must be available to this engine.

The ownership manifest also carries a SHA-256 checksum over the deterministic
System.Text.Json encoding of its version/owner/options/next-sequence/slot
payload. Accidental changes to retention/sequence metadata fail before cleanup.
Like frame hashes, this is not protection against an intentional same-user
rewrite of both payload and checksum.

Each newline-terminated UTF-8 frame contains `schema_version`, `record` and
`sha256`. The record contains schema version, journal UUID, reserved sequence,
trusted receipt UTC, and the typed event. SHA-256 covers the canonical compact
record JSON including its LF. It detects accidental valid-JSON corruption, not
malicious rewriting or publisher authenticity. Records are selected in reserved
sequence order, regardless of event-time ordering.

| Limit | Default | Valid configuration / behavior |
| --- | --- | --- |
| Total owned storage | 50 MiB | 64 KiB-50 MiB; 32 KiB reserved for two bounded manifests/lock, remainder for actual segment bytes |
| Segment | 1 MiB | At least the record limit and at most total minus 32 KiB; at most 32 segments, so slot pressure may remove older evidence before size pressure |
| Encoded record including LF | 8 KiB | 2-8 KiB; public event JSON additionally capped at 4 KiB |
| Retention | 7 days | 1-7 days; maintenance on Start and Append, no timer or constructor loop |
| Operation timeout | 30 seconds | Greater than zero through 2 minutes; cooperative boundary deadline, not a native-IO kill guarantee |
| Selected logs | 2,048 records / 2 MiB encoded frames | Caller may lower bounds; overflow is an explicit error, not truncation |
| Frozen snapshot | 4 MiB total uncompressed bytes | Fixed five files; export adds at most 4 KiB of ZIP framing |

Rotation finalizes an active segment with a same-directory create-only rename.
Manifest updates use flush-to-disk staging plus atomic replacement. Sequence
reservation precedes append; the record is acknowledged only after its actual
write/flush/close and cancellation/deadline checks. Interrupted writes can leave
sequence gaps or even a complete unacknowledged record; retry does not imply
exactly-once delivery. Every acknowledged frame has its actual encoded byte
count in `AppendReceipt`.

Retention uses **segment creation receipt UTC from the injected clock**, never
caller event timestamps or filesystem mtime. Expired segments are removed as a
whole: later records in that segment can be removed sooner than seven days.
Size/slot rotation can also remove recent records. An idle/offline directory is
not cleaned until the next explicit maintenance action; this is not a claim of
deletion at an exact seven-day wall-clock boundary. A backward clock step
defers time-based cleanup; size limits remain. All candidate records are
validated before cleanup, so corrupt evidence fails closed instead of being
purged. Ordinary writes stay within the actual-byte bound, including reserved
control staging and partial writes. External corruption/oversize files are
preserved with an error, not advertised as within bounds.

Startup recovery accepts only an unterminated tail in an owned active `.open`
segment: validate the complete prefix, truncate that bounded tail, flush and
finalize. `Recovery` explicitly reports truncated bytes and abandoned reserved
slots. Missing reserved files after interrupted creation/removal are counted;
complete malformed/checksum-invalid frames and truncated **finalized** files
are preserved and rejected. A valid pending manifest with the same owner/options
can be discarded in favor of the committed manifest, because no data append
occurs before manifest finalization. A partial initial/control manifest is not
silently replaced with defaults.

Start holds an exclusive file lease; a second writer is rejected. Calls on a
writer are serialized with immediate `Busy`, no waiting queue and no silent
dropped records. `Select` on the live writer is supported. `ReadClosed` uses a
read-only shared lease, does no recovery/cleanup or writes, and rejects an
active writer or unfinalized state. No cross-process live tail reader is
promised. `Close`/`Dispose` finalize healthy segments and release ownership;
faulted writers preserve recovery evidence. Flush/close/finalization failures
are surfaced, not converted into a success result.

The scope must remain a trusted, user-owned local directory. These path checks
do not defend against a hostile same-user process swapping directory entries
between checks, malicious hard links, or filesystem/power-loss behavior beyond
ordinary local file flush/atomic-rename semantics. No ACL/protection changes,
distributed locking, recursive repair, automatic reset, or secret-store access
is attempted. Preserve a corrupt scope and choose a new explicit scope instead
of deleting arbitrary files to make diagnostics appear healthy.

## Frozen preview, consent, and export ownership

`Freeze` copies only the allowlisted projections and selected records into
private byte arrays. Caller-owned report collections must remain stable during
the synchronous freeze; callers may change/release sources afterwards.
There is no lazy source callback, path reopening, `ToString` serialization,
log reread, or config reread during preview/export. The only filenames are:

| File | Frozen content |
| --- | --- |
| `build.json` | Actual assembly/supplied-manifest projection with source label |
| `settings.json` | Typed configuration-only summary |
| `doctor.json` | Shared report projection and authored remedies |
| `events.json` | Selected, correlated, pseudonymized metadata records |
| `manifest.json` | Snapshot ID/time, requested and actual receipt range, source/omission explanation, first four files' byte sizes and SHA-256 digests |

`Files` lists all five files, including the manifest's digest. The manifest
does not recursively hash itself. `Digest` binds the ordered filename,
uncompressed byte length and exact contents of all five files. Integrity is
not a publisher signature or certification. ZIP entries use fixed names,
ordering, timestamp and no compression; no dynamic archive entry or filesystem
expansion is possible. Preview covers the exact entry bytes, not an opaque
archive container supplied by a third party.

`Approve` requires matching snapshot ID/digest and a normalized absolute `.zip`
destination whose parent already exists. The destination is intentionally not
included in preview file data, manifest or approval `ToString`. The integrator
must display it separately before the user confirms. Export rechecks this exact
destination and consumes the approval once an attempt begins. Wrong/replayed
approval is refused. A new explicit confirmation is needed after a failed
attempt; creating a snapshot or running a probe cannot infer consent.

Export writes a newly created random `.martlet-support-<uuid>.partial` in that
destination directory, flushes and closes it, then performs a **create-only**
atomic rename. Existing/racing outputs are preserved. Cancellation before the
rename prevents finalization; cancellation arriving inside the synchronous
rename cannot undo an already committed export. A completed rename returns a
success receipt; a pending native call remains pending, not magically canceled.
No archive bytes are sent anywhere.

A failed export cleans only its own exact partial after closing it. If close
or deletion fails, the snapshot retains the stream/path and reports
`HasPendingCleanup`. Further exports are blocked with `CleanupPending`.
Keep that object alive; use `RetryCleanup` (or retry `Dispose`) after the
filesystem permits cleanup. A failed close is not assumed to have released the
handle. Never remove an open file as repair. `Dispose` zeroes private frozen
byte arrays after owned cleanup succeeds. Preview byte-array copies belong to
the caller; clear them when no longer needed. Managed immutable strings, shared
report objects, and already-exported files cannot be guaranteed erased.
User-created exports and abandoned partials are outside journal retention;
there is no automatic broad cleanup of the output directory.

Failures use `SupportException.Failure` and fixed actionable messages with no
inner exception or raw filesystem/provider messages. Invalid input is refused,
not filtered into an apparently successful result. A faulted journal must be
closed and explicitly reopened; do not hide the error or automatically retry
with a different profile/destination. Display the safe failure/message, not
unrelated exceptions or raw source objects.

## Focused production-path coverage

`tests\Martlet.Support.Tests` uses the real journal/snapshot/export algorithms
and a narrow internal filesystem seam that delegates to actual FileStream IO.
Every test scope is a unique child of its artifact output directory. Authored
canaries are not user conversation data. The end-to-end harness runs the shared
Doctor executor over an explicitly created malformed-settings fixture, maps its
known failure to an event, persists/selects it, previews every frozen file,
requires destination-bound consent, and reads back the actual local ZIP.
Other cases cover rotation/actual bytes, retention timestamps, recovery and
corruption/checksums, lock/backpressure, source mutation, canaries, direct-token
cancellation with blocked callbacks, deadline overruns, IO/close/cleanup faults,
existing targets, schema rejection, provenance and disposal. Counting probe
callbacks demonstrate no new probe/device/provider execution during export.

The new projects use existing central pins and their own committed locks.
The projects are registered in the solution; the focused engine suite also runs directly:

```powershell
$env:CI = 'true'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
dotnet restore tests\Martlet.Support.Tests --locked-mode --artifacts-path $artifacts
dotnet build tests\Martlet.Support.Tests --no-restore -c Release --artifacts-path $artifacts
dotnet test tests\Martlet.Support.Tests --no-build -c Release --artifacts-path $artifacts --results-directory "$artifacts\results"
```

Use the repository-pinned SDK, an isolated CLI home, and a single unique local
artifact directory. Run these direct-project commands locally without keys,
uploads or application startup under the
[repository policy](../../README.md#local-only-validation-policy); the former
hosted support workflow is removed, not replaced.
Historical local/hosted fixture evidence is not physical audio, a reproduced real
provider issue, a clean consumer installation, a signed release, or a shipped support UI.
