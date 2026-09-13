# F03a: deterministic provider text fixtures

**FIXTURE - NOT AI.** This library supplies authored synthetic
provider scripts and a deterministic runner through the production
`Martlet.Core.Streaming.ProviderSequenceValidator`. It performs no inference,
network access, credential/profile access, file writes, audio/device work, or
WPF/Doctor integration. It has no external package dependencies.

`FixtureCatalog.Create(name)` supplies 30 named scripts;
`FixtureRunner.Run(scenario, cancellationToken)` returns a bounded in-memory
`FixtureTrace`. This is a library API, not a new Doctor command or app mode:

```csharp
var scenario = FixtureCatalog.Create("backpressure");
var trace = FixtureRunner.Run(scenario);
byte[] json = ContractJson.Write(trace);
```

The runner serializes scenarios and each scripted event through the existing
Core `ContractJson` boundary, invokes the real validator, drains its real
bounded text queue, and round-trips the final trace through Core serialization.
`RawEvent` deliberately exercises malformed ingress; it is not another parser.
JSON uses the foundation's exact snake_case tokens, Unicode validation, depth,
duplicate-property, and 256 KiB document boundaries unchanged.

The coordinator-authorized shared change appends **`TurnOutcome.Refused`**
(`refused`) without renumbering existing values. Refusal is not a completed
answer, suppression, or failure and carries neither error nor suppression.
This is an explicit evolving pre-release contract addition; older readers
reject that unknown token. No remote protocol is frozen here.

## Production API and ownership

| API | Meaning |
| --- | --- |
| `TextStreamRequest` | Immutable session/turn/request IDs, attempt epoch, validated STT/LLM capabilities and route provenance. No request body, credentials, endpoints or readiness flag. |
| `ProviderSequenceValidator(request, limits, timeProvider, cancellationToken)` | One serialized owner, no background tasks/timers/transport. Call from a single orchestrator; concurrent access is not supported. |
| `AcceptJson(bytes, overflow)` / `Accept(event, overflow)` | Validate at production ingress, enforce correlation/order/bounds, return `SequenceUpdate`. Direct typed ingress also rejects malformed UTF-16 content/error summaries. |
| `TryReadText(out chunk)` | Remove one current validated chunk, retaining IDs/epoch/sequence. A completed event can leave queued text to drain. No audio operation is implied. |
| `Poll()` | Enforce cancellation and monotonic first-event/idle/total deadlines. The owner MUST schedule this while idle, including a stalled/backpressured transport. `Snapshot` is passive, not a watchdog. |
| `EndOfInput()` | Explicit EOF: any unterminated attempt fails with `StreamTruncated`. |
| `Stop()` | Idempotently advance the local epoch once, clear queued/refusal text, request cancellation, and cancel an active attempt. Existing terminal results remain immutable, including completion whose queued text is discarded. |
| `Suppress(reason)` | Local policy terminal before provider activity, not a fabricated provider event. An already terminal/canceled request retains its result. |
| `Transition(next, Retry/Replace, nextToken)` | Explicit bounded lifecycle transition; returns the closed previous snapshot. Never initiates a provider call or automatic retry/fallback. |
| `Snapshot` / `RefusalText` | Snapshot has correlation, terminal/result, issue, cancellation-request metadata and bounded counters. Refusal text is separate content, not a normal output chunk or diagnostic field. |
| `Dispose()` | Idempotent local stop and release of queued content/dedup/history; subsequent operations throw. Passive final metadata remains readable. |

Every accepted event must match **all** current session/turn/request/provider
IDs, request epoch and fixture/live provenance. An exact historical request
tuple from this validator's bounded attempt history is discarded as stale.
An unknown old epoch or wrong identifier is a failure, not presumed stale.
Events are structurally validated before stale classification. Closed attempts
discard later input without decoding it or changing the terminal result.

Initial epoch is nonnegative and strictly below `int.MaxValue`, leaving a slot
for Stop. Retry and replacement require `CurrentEpoch + 1` and a never-used
request ID in the same session. Retry retains the turn, requires failed/canceled
state and **zero accepted text**, and does not establish upstream idempotency or
spending permission. Replacement requires a never-used turn ID, closes the old
active attempt as canceled, and flushes its text. A prior Stop already consumed
one epoch; replacing after that requires the following epoch. At exhaustion,
invalid transitions throw `ContractException`, never reset or wrap identifiers.
The owner must deliberately end this session-lifetime validator at its attempt
budget; creating another instance is not evidence of remote retry safety.

### Event and turn state table

Each attempt starts with `started`, sequence **0**, then contiguous sequences
and exactly one terminal. `text_delta` means append-only stable user-visible
text. STT adapters must normalize partial revisions before this seam; it is not
a universal STT partial protocol. Started/error stages are not arbitrary UI
stages: the selected role determines `Transcription` or `Generation`, including
the required stage of a normalized provider error.

| Input | Allowed behavior / turn result |
| --- | --- |
| `started` | Exactly once, first. No direct terminal before start. |
| `text_delta` | Requires that role's positively supported delta/partial capability. Queues text once. |
| `completed` | Requires non-whitespace response text, either previous deltas OR one complete text payload, never a replay of deltas. `Completed`. |
| `refused` | LLM only; may follow partial text, clears undelivered chunks. `Refused`; separate `RefusalText`. |
| `no_speech` | STT only, before any text. `Suppressed(NoSpeech)`, not an empty successful answer. |
| `canceled` | Clears undelivered chunks. `Canceled`; does not acknowledge remote compute cancellation. |
| `failed` | Uses the adapter's validated, user-safe normalized error at the role stage. `Failed`; clears queued text. |
| Local suppression | Before provider starts, `Suppressed(reason)`, no error. |
| Local deadline, malformed input, EOF, sequence or resource fault | `Failed`, fixed sanitized local error summary and precise `SequenceIssue`; queued text cleared. |

Delivered chunks cannot be retracted: consumers must label existing text as
partial on refusal/failure/cancellation, and recheck the current epoch before
committing downstream work. This validator is **not a full voice turn
orchestrator** and cannot prove zero stale audible segments.

Exact duplicate accepted sequences are discarded by bounded SHA-256
fingerprints over the canonical validated event. Unknown optional JSON fields
do not affect identity. A changed payload for an accepted sequence fails
`ConflictingDuplicate`; a gap fails immediately, without reordering/replay.
Duplicates, stale input and backpressure retries consume ingress budget and
never refresh idle progress. After termination all input is closed-discarded.

### Bounds, clocks, and cancellation

| Resource | Default / hard ceiling |
| --- | --- |
| Ingress event offers per attempt (including duplicates/stale/retries) | 1024 / 4096; the first exceeding offer records limit + 1 and fails |
| Aggregate accepted text including refusal text, UTF-16 characters | 65,536 / 262,144; existing per-event maximum remains 16,384 |
| Pending text chunks | 2 / 128, also bounded by aggregate text |
| Attempts/history entries per validator | 32 / 64 |
| First event / idle / total deadline | 15 / 10 / 60 seconds; each configurable positive, at most 5 minutes |
| Fixture script | 1-128 ordered steps, absolute synthetic time 0-900,000 ms, at most 128 chunks per drain; 256 KiB serialized input |
| Trace | At most 129 observations, bounded delivery metadata and 256 KiB serialized output |

`Backpressure` leaves the event's sequence, text total and queue untouched.
Drain and reoffer the **same** event; never advance the producer. The ingress
budget still counts the offer and time still advances. `Fail` overflow mode
instead terminates with `QueueOverflow` when the producer cannot pause.
There is no hidden channel, unbounded task, or blocking wait.

Deadlines use `TimeProvider.GetTimestamp()`/elapsed time, not wall-clock dates.
At equality, the deadline expires before accepting an event. Precedence is
cancellation token, total deadline, first event, then idle. Only newly accepted
events refresh idle. A completed terminal disables provider deadlines, but
token cancellation/Stop still flush pending text.

The runner advances a synthetic `TimeProvider` from script timestamps; equal
timestamps preserve script order. It does not use sleeps, timers, task races
or random IDs. A canceled runner aborts remaining script actions and emits the
actual canceled terminal. Construction/usage errors are explicit
`ContractException`s, not success-shaped traces.

`CancellationRequested` is **an instruction for the owning adapter**, never
an acknowledgment or guarantee:
unknown/discard-only -> `LocalDiscardOnly`; request-abort -> `AbortRequest`;
cooperative support -> `CooperativeComputeCancel`. Capabilities remain visible
separately. The caller actually performs any supported upstream operation;
neither closing a socket nor this metadata proves compute stopped.

## Scenario catalog and error semantics

`FixtureCatalog.Names` is the executable catalog. The three stored scripts in
`tests\fixtures` are independent JSON inputs compared with their catalog traces.
Other cases are authored in `FixtureCatalog.cs`, not xUnit-only stand-ins.

| Cases | Expected distinction |
| --- | --- |
| `complete`, `streaming`, `stt-partial` | Complete text vs append-only normalized streaming |
| `refused`, `refused-after-partial`, `no-speech`, `suppressed`, `failed`, `canceled`, `empty` | Refusal, ordinary suppression, errors and empty-answer rejection stay distinct |
| `first-deadline`, `idle-deadline`, `total-deadline` | Precise local deadline issue; existing `ProviderFailed` error code pending separate shared error integration |
| `duplicate`, `conflicting-duplicate`, `out-of-order` | Discard exact duplicates, reject changed replay (`InvalidContract`), fail gaps (`StreamTruncated`) |
| `wrong-ids`, `wrong-epoch`, `wrong-provenance` | Reject correlation/provenance faults (`InvalidContract`) |
| `late-after-stop`, `late-after-replace`, `truncated` | Clear old queued text, discard closed/stale input, reject missing terminal (`StreamTruncated`) |
| `unsupported`, `unknown` | Both `ProviderCapability`, with separate unknown/unsupported issues; neither indicates readiness |
| `malformed`, `version` | Existing `InvalidContract` / `UnsupportedVersion` from production parser |
| `text-limit`, `event-limit`, `backpressure`, `overflow` | Aggregate/ingress/queue bounds (`PayloadTooLarge`), or successful bounded retry-after-drain |

Local order/correlation/empty failures map to existing `InvalidContract`;
EOF/gaps to `StreamTruncated`; bounds to `PayloadTooLarge`. No parallel enum
edit introduces an unmerged `DeadlineExceeded`. Local error summaries never
include input/provider bodies. Adapters remain responsible for sanitizing
their own `MartletError.Summary` and action IDs: contract validation is not a
redactor. Trace snapshots include such normalized error metadata but no
transcript, delta, refusal text, credentials, model output log, or readiness.
Fixture traces are exclusively fixture provenance even for deliberately
wrong-provenance raw-event fault injection.

## Reproduction and evidence scope

Use the repository-pinned .NET 10.0.401 SDK and central xUnit/TestSDK pins.
The new projects commit ordinary generated package locks. Solution registration
belongs to the audio/integration owner, so this slice's commands are direct:

```powershell
dotnet restore tests\Martlet.Fixtures.Tests --locked-mode
dotnet build tests\Martlet.Fixtures.Tests --no-restore -c Release
dotnet test tests\Martlet.Fixtures.Tests --no-build -c Release
```

The dedicated read-only, commit-pinned `fixtures.yml` runs those commands.
The existing foundation solution lane still covers the shared enum addition.
NuGet build/restore can use network; **fixture execution does not**.

Production-path coverage includes malformed UTF-8/UTF-16/ignored fields,
canonical enum tokens, exact deadline boundaries, wrong IDs/stages/epochs,
bounded attempts, disallowed retry, overflow, duplicate floods, cancellation,
disposal, explicit refusal serialization, and deterministic trace round-trips.

The interruption matrix contains **120 distinct schedules**:
5 partial-output cut points x 3 stop/replace/EOF boundaries x 2 consumer
schedules (eager versus one-chunk backpressure) x 4 declared cancellation
capabilities. Each injects duplicate and late events and asserts exact delivery,
discard, backpressure and cancellation metadata, not merely test completion.
These are combinations of production paths, not 120 independent protocol
implementations or real network disconnects.

| Acceptance | Covered here | Still required |
| --- | --- | --- |
| AC-01 (partial) | Text-event temporal/correlation/terminal/aggregate/deadline conformance, strict Core JSON and explicit capability outcomes | Role-specific request bodies, SSE, binary framing, remote version negotiation, real provider/gateway qualification |
| AC-03 (partial) | Deterministic offline synthetic provider scripts through actual Core validation/queue delivery; truthful fixture traces | F03c app/session/status wiring and F03b real PCM/playback path |
| AC-08 (partial) | 120 deterministic stop/replace/disconnect-like schedules, no stale/duplicate **text delivery**, bounded pressure and honest cancellation requests | Audible segment safety, real PCM/frame contiguity, Stop latency, OS/device/network behavior and actual upstream compute cancellation |

Baseline developer-host tests and this new suite do not establish denied-egress
OS isolation, clean consumer Windows installation, an AI conversation, real
audio, M1 completion, G1 or any release gate.

**Integration boundary:** audio owns PCM framing/contiguity, sink queues and
epoch/Stop enforcement at playback; F03c/V04 must wire cancellation and epoch
changes to both boundaries. Diagnostics can consume `SequenceSnapshot.Result`,
`Issue`, role-stage errors, counters and cancellation-request metadata, retaining
fixture provenance, without treating completion as provider readiness. Desktop
and Doctor remain offline foundation entry points until their owners integrate.
