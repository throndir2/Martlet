# V04a: reusable typed text-to-voice runtime

This portable `net10.0` library joins the **production** Responses LLM adapter,
Core temporal validator, incremental speech segmenter, raw-PCM TTS adapter and
`PcmPlaybackSink`. It is not the app's labeled offline fixture experience, a
provider readiness probe, an audio decoder or an alternate provider parser.
It references Core, Providers and Audio; no packages are added to production.

**V04b now wires this runtime into the explicit Desktop API surface and its
publish graph.** See [app composition and gates](../../docs/CONVERSATION.md).
Doctor remains fixture/read-only, not a live command. The app connects
explicit typed/PTT action policy, microphone/STT and the OS-vault bridge. Learned
VAD is V01b. V04/G2 real mic-to-voice acceptance has not passed.

## Composition and public boundary

`ConversationRuntime.Create(credentials, devices?, playbackOptions?, clock?)`
uses only the existing safe named provider factories. It owns the adapters
and, when supplied, a sink built around `IPlaybackDeviceFactory`. The portable
assembly has no Windows audio implementation dependency: a future Windows
caller supplies the real Windows factory. Neither construction, status,
unused disposal nor request validation resolves credentials, sends HTTP,
enumerates endpoints or opens a device. No model, voice, endpoint, consent,
fixture fallback or retry is silently selected.

The caller deliberately chooses:

- A bounded typed input, optional explicit personality/history, supported
  `TextModelSelection`, `TextGenerationLimits` and `ConversationLimits`.
- Either **no `SpeechOutput` (text-only)**, or a supported speech selection,
  explicit output selection and per-segment `SpeechSynthesisLimits`.
- An `IConversationAuthorizationSource` **for this turn**, separate from the
  injected `IProviderCredentialSource`. There is no default implementation
  returning consent, pretend vault, stored key or automatic budget approval.

Example boundary (variables are supplied by the future authorized caller,
not environment variables or embedded credentials):

```csharp
await using var runtime = ConversationRuntime.Create(
    credentialSource, selectedDeviceFactory, playbackOptions);

var request = new ConversationRequest(
    new BoundedTextInput(typedText, explicitPersonality, explicitHistory),
    selectedTextModel, textLimits, turnLimits, selectedSpeechOutput);

var turn = runtime.Start(request, perTurnAuthorizationSource, callerToken);
// Read bounded Events, or poll Snapshot and Content. They do not drive the worker.
var outcome = await turn.Completion;
// If !outcome.OwnershipReleased, work is still outstanding; do not start again.
// OwnershipRelease means actual worker/callback exit, not merely a timeout.
```

`Start` is the explicit new-turn operation; it does not silently stop or queue
behind another turn. To replace, await the old captured turn's `StopAsync`,
then inspect/await `OwnershipRelease` and check `Quarantined` before `Start`.
`Retry(previous, newRequest, newAuthorizationSource, acknowledgeEarlierSpeech)`
creates a **new turn ID, attempt IDs and epoch** with `RetryOf` correlation.
It requires acknowledgment if the previous turn `MayHavePlayed`. It never
reuses a consumed authorization or performs automatic replay.

The runtime cannot authenticate a human or prevent hostile code in its own
process from issuing permission. The UI/action boundary must authenticate the
user's disclosure, potential-charge and AI-generated-voice decisions. A
credential source is not evidence of that permission.

## Exact action permissions and reservations

The text callback receives `TextAuthorizationAction`: the actual immutable
input/history, chosen model, original IDs/epoch, limits, deadline and budget
demand. Return `AuthorizedTextOperation` only after this exact action is
approved and its budget reserved. It contains the provider's existing
one-use `TextDisclosureAuthorization` and an exact `BudgetReservation`.

The speech callback is invoked only for a segment actually selected for
synthesis, with a **fresh request ID** and the actual `BoundedSpeechInput`
instance. `SpeechAuthorizationAction` includes segment ordinal, model,
voice, format, limits and reservation demand. Return a fresh
`SpeechDisclosureAuthorization` bound to that exact input instance and
selection. Do not authorize unknown future text/voices, reuse the LLM
permission for TTS, or authorize from a blanket persisted toggle.

`OperationBudget` binds IDs, epoch, role, exactly one request, input bytes,
LLM input reservation/output tokens or TTS output samples. A reservation
means the caller reserved these maxima in its own budget. Unknown/unavailable
reservation or permission is represented by null and stops before secret
lookup. Wrong-scope and expired reservations also stop locally. The provider
authorization expiry must be no later than both the reservation expiry and
the action deadline. The runtime retains the action's original UTC/timestamp
pair across the authorization callback and restricts that same monotonic
window to both expiry bounds. It never grants a new lifetime merely because
the callback returned after a wall-clock rollback. Dispatch receives only the
remaining permission/reservation/stage/turn budget as its deadline; the
already-scoped authorization, IDs and limits are not rewritten. The existing
provider guard then enforces that deadline during credential lookup,
serialization, send and output. Runtime-observed permission expiry is
`AuthorizationExpired`, distinct from a general turn/stage deadline.

Both adapters still validate their exact origin/model/role/limits/IDs,
one-use consumption, disclosure and potential-charge flags. TTS additionally
validates voice/format/input identity and AI-generated-voice disclosure.
Providers own credential lifetimes and origin binding; runtime settings,
snapshots and logs never contain keys. No live account/budget metadata is
queried here. A canceled/failed request may still have cost money.

These reservations are **not a currency or invoice cap**. Unknown monetary
rates, upstream usage and account pricing stay unknown (`EstimatedCost`
is null). The caller must explain maximum segment/request counts, requested
output limits and cost uncertainty before approving. Across turns, budget
policy belongs to that caller; fresh explicit turns are not free requests.

## Streaming, state and text ownership

One background turn worker owns one LLM pull enumerator and one speech
consumer. `Start` returns without running user callbacks on the calling
thread. The LLM stream passes through `ProviderSequenceValidator` before
any UI text or speech staging. Final snapshots are not appended or spoken a
second time. Refusal text is available **only** as `Content.Refusal`, separate
from `Content.Text`, and ends in `Refused`, not `Completed`.

Normal states are `Idle`, `Authorizing`, `Generating`, `Synthesizing`,
`Playing`, `Completed`, `Refused`, `Canceled`, `Failed` and `Partial`.
Overlapping activities share a visible current state; `Playback` supplies
separate device state/accounting. `Playing` is observed from the actual sink,
not inferred from a completed TTS HTTP download. `TextComplete` is independent
of the turn state: fully generated text can remain visible in a partial
voice turn. Failed/incomplete LLM streams retain already displayed partial
text but discard uncommitted speech. An output/provider failure cancels the
rest of this turn; no hidden continuation or text/voice fallback request.
Text-only is the deliberate alternative for the next explicit action.

`ConversationTurn.Content` is a content-bearing polling surface, excluded
from ordinary JSON metadata and safe `ToString`. Text events also explicitly
carry content and must not be logged. Snapshots correlate session/turn/text
attempt and active speech attempt IDs, segment counters, both epochs,
role provenance, safe failure enums and playback accounting. They contain
no text, secrets, PCM, endpoint names/URLs or native exception messages.

`Events` is a drop-oldest ring (default/hard maximum 128; configurable 4-128)
with sequence and cumulative `DroppedEvents`. An abandoned/slow output
subscriber cannot block generation, grow an unbounded queue or retain provider
enumerator ownership. Poll `Snapshot`/`Content` after loss and use the reliable
`Completion` task; do not infer completion from channel exhaustion. A stalled
cleanup may leave the event channel open for a later `Released` event.
Nothing is persisted, exported or logged automatically. Handles retain
bounded in-memory text for their caller; this is not secure memory erasure.

Original provider provenance is preserved: production factories emit `Live`
attempt evidence, in-process fixtures emit `Fixture`. Neither envelope alone
certifies a working real account, measured audibility or an acceptance gate.

## Conservative spoken subset

The segmenter is an English-oriented, deterministic state machine, not a
general Markdown parser. It accepts plain prose and Unicode scalars, with
ASCII `.`, `!`, `?` followed by whitespace as stable sentence boundaries;
newline and a successfully completed final fragment are boundaries too.
Punctuation is not a boundary until its following character arrives.
CRLF and arbitrary delta splits are handled. A failed stream never flushes
its unfinished fragment.

Unsupported content is **suppressed for speech, never hidden from the UI**:

| Content | Conservative handling |
| --- | --- |
| Backtick/tilde fenced blocks | Stateful across lines/deltas, including unclosed blocks; no fenced content spoken |
| Inline code, emphasis, headings, links/images, HTML, JSON/braces, tables | Marker-bearing remainder of a line suppressed |
| URLs, paths, email, dotted tokens/domains | `:`, slash, backslash, `@` or a dot between letters/digits suppress speech |
| Lists/quotes/indented code | Leading `-`, `+`, `>`, numeric-dot marker, tab or four spaces suppressed |
| Other rejected syntax | `* _ # [ ] { } < > | = ^ &` suppress the remainder of that line |

Already committed plain sentences cannot be retracted when a later sentence
introduces markup. A clean newline can resume prose except while inside a
fence. Abbreviations, decimals, structured lists and multiline Markdown
constructs are not interpreted semantically. Some otherwise readable prose
is deliberately suppressed. This is a speech-format filter, not a content
moderator, general URL detector, translator or instruction-execution policy.
No hidden reasoning/tool JSON is admitted by the provider adapter in the
first place; this filter independently rejects ordinary output formatting.

One unfinished sentence is buffered within the existing whole-text limit
(default/hard 16,384 UTF-16 characters). At a stable boundary it is split
deterministically into the configured TTS byte bound, at most **1536 UTF-8
bytes** each, never inside a surrogate pair/scalar. A configured speech input
bound below four bytes is rejected. Splits may cut a long word; there is no
claimed universal linguistic sentence segmentation/prosody.

There are at most **two pending speech segments**, one active segment and
one bounded unfinished sentence/current provider delta. The producer waits
for queue capacity **before advancing segmentation**; a full queue stops
further LLM pulls. Existing provider bounded network/parser read-ahead is
unchanged. Ordinary sentences reach TTS before the LLM semantic terminal;
the entire answer is not routinely buffered before speech starts.

## Aggregate bounds and audio runs

`ConversationLimits` defaults / hard ceilings:

| Limit | Default | Hard maximum |
| --- | --- | --- |
| Whole turn | 90 s | 90 s |
| Local Stop observation/cleanup wait | 2 s | 2 s |
| Committed speech actions | 8 | 32 |
| Reserved speech input bytes | 12,288 | 16,384 |
| Reserved speech output samples (24 kHz/channel) | 2,160,000 | 2,160,000 (90 s) |
| Pending speech segments | 2, fixed | 2 |

Each committed action reserves its **full configured** per-segment output
maximum against the aggregate, not just the short body a fixture happened
to return. Unused reservations are not silently recycled. For example, eight
segments with a 10-second per-segment maximum fit an 80-second aggregate;
the provider's default 90-second segment maximum leaves room for only one
segment in a 90-second turn budget. Callers must deliberately choose useful
per-segment limits rather than mistake `MaxSpeechSegments` for a guaranteed
number of requests. `CommittedSegments` counts selected authorization
attempts, including a denied action; it is not a count of billed requests.

There is exactly **one TTS request and one playback run active at a time**.
Each segment has its own sink run. This avoids claiming gapless synthesis or
keeping an unsealed run alive across slow authorization/HTTP between segments.
The run opens only after the first authorized PCM frame arrives. A text-only,
refused, completely suppressed or pre-audio-failed turn opens no device.

The immutable **turn epoch** identifies provider attempts and invalidation.
A separate sink-scoped, strictly increasing **playback epoch** identifies
each segment run across turns. The mapping changes only the epoch after
checking the original provider IDs/turn epoch. Provider PCM frame sequence
and sample offsets (zero-based per segment) are preserved exactly.
TTS Started/terminal control sequence 0/1 is never used as PCM sequencing.
`CompleteInput` receives the actual provider final sample count, including
the last short frame once, not a zero-length sentinel.

Sink admission is paced using its actual accepted-minus-device-consumed
sample count and queued frame count before `Submit`. The runtime owns that
sink exclusively, so a single producer cannot race a competing writer.
Backpressure keeps at most one additional current provider PCM frame;
the sink still enforces its own hard bounds and duplicate/order checks.
Its native scratch, prebuffer, capacity, underrun and cleanup rules remain
unchanged. See [Audio](../../docs/AUDIO.md) and
[provider contracts](../Martlet.Providers/README.md).

Voice `Start` additionally rejects incompatible prebuffer configurations
passively, before authorization, credential lookup, transport or devices:
both the queued-frame limit and sample capacity must fit the selected
adapter's prebuffer rounded up to full 20 ms (480-sample) frames. For example,
150 ms prebuffer requires at least eight queued frames and 160 ms sample
capacity. One queued frame works with prebuffer at most 20 ms, not the
default 150 ms. Limits are never silently raised or prebuffer lowered.
Text-only turns do not require this unused voice compatibility condition.

TTS clean body completion means **HTTP body completed**, not semantic
generation completeness, device drain or audibility. Conversation completion
with voice additionally requires every segment's sink completion and cleanup.
`AcceptedSamples`, `SubmittedSamples`, `DeviceConsumedSamples`,
`DeviceDrainObserved`, `DeviceReleased` and `MayHavePlayed` retain the sink's
qualified meanings. `AudibleSamples` stays null. Device/engine consumption
is not proof that a human heard sound. Endpoint loss never switches outputs
or automatically replays a previous segment.

## Cancellation, deadlines and outstanding work

Stop/caller cancellation invalidates the **current turn epoch first**,
clears unfinished/staged speech, invalidates the captured playback run and
requests provider cancellation through `CancelAsync`. It never invokes
sink-wide Stop on a potentially newer generation. Visible canceled/partial
state is immediate; `Completion` separately observes bounded cleanup.
The original caller token is retained and checked directly at admission,
before/after every provider pull, after authorization and by supervision;
another caller callback cannot conceal its cancellation flag until that
callback returns. The caller still owns its unrelated external callbacks.
The original caller token is also passed unchanged to `Stream`, and the
runtime Stop token is passed unchanged to its enumerator. The approved
provider-boundary refinement checks these original sources (and shutdown)
inside the adapter, rather than depending on linked-token callback delivery
during credentials, serialization, send and read acceptance.

The worker is the sole owner of `MoveNextAsync`/enumerator disposal. Stop
never disposes an enumerator concurrently with an outstanding pull. It requests
abort and lets that owner dispose safely when the pull returns. Native device
release remains on the sink's own worker. Late results encounter cancellation
and epoch checks before content/frame admission.

Whole-turn and LLM/TTS stage windows start once, use original monotonic
budgets plus absolute deadlines, and include authorization time. Dispatch
clamps the provider deadline to the **remaining** original budgets, including
wall-clock rollback. Admission checks run synchronously before/after awaits;
timer delivery alone never licenses late work. Existing adapter first-byte,
idle, credential/serialization and absolute authorization-expiry guards are
preserved, not reimplemented. Backpressure consumes these budgets.

Noncooperative callbacks, credential sources, HTTP bodies or native devices
cannot be forcibly made safe by a portable library. The independent
supervisor can finish `Completion` after the bounded wait with
`OwnershipReleased=false`. `OwnershipRelease` remains pending until the
worker and cancellation callbacks actually exit. A blocked device or failed
native cleanup may also be `Quarantined`; even late release cannot erase
uncertain failed teardown into success. No next turn starts while ownership
remains or quarantine is set.

`DisposeAsync` requests the same bounded Stop. If dependencies remain stuck,
deferred owned-adapter/sink disposal is retained until actual ownership
release; it does not free buffers/credentials underneath outstanding work.
The task can therefore outlive the bounded disposal call. Process termination
is outside this local cleanup contract. Upstream request abort does not prove
compute cancellation, deletion or avoided charges.

## Offline evidence and remaining gates

`Martlet.Conversation.Tests` drives **real** LLM/TTS serializers, parsers,
authorization guards, Core validator, production runtime/segmenter and sink
through in-process counting HTTP handlers and worker-owned device fixtures.
Shared authored test helpers are linked from the unchanged provider/audio
test sources rather than copying implementations. Approved shared changes
are the test-only friend `Martlet.Conversation.Tests`, direct original-token
guards in the LLM/TTS adapters and internal request window, focused provider
cancellation regressions and provider-local documentation. The existing STT
adapter benefits from its original caller token reaching that shared window;
its source is unchanged. No authorization binding, parser, endpoint, TLS or
redirect policy is changed. The shipping Conversation assembly receives no
provider internals or arbitrary handler/endpoint access.

Cases include streaming before LLM completion, refusal, incomplete/malformed/
duplicate events, full-text preservation, formatting/fence/token boundaries,
UTF-8 caps, aggregate reservations, no-send authorization/budget denial,
expiry during credentials with deferred timers/UTC rollback, two-segment
read-ahead bounds, slow sinks, final short frames, mapped stale/duplicate PCM,
endpoint loss, quarantine, every cancellation stage, noncooperative callbacks
and credentials, explicit retry, old Stop racing a new turn, abandoned output
subscribers and metadata canaries. Only fixture provenance is exercised.

Independent review of the first head identified callback-order cancellation,
pre-authorizer short-permission lifetime and playback-prebuffer compatibility
defects. Before fixes, 12 of 17 new conversation review cases failed (five
valid prebuffer configurations passed), two deeper credential-to-send
cancellation cases failed, and 15 direct provider cancellation cases failed
across STT/LLM/TTS and caller/enumerator sources. The original failing tests
are retained. Additional controls cover unexpired permission remainders,
supervised cancellation while authorization remains blocked and text-only
operation with unused incompatible voice settings. Pre-fix TRX evidence is
kept in the implementing session's C: artifacts, not as generated repo files.

Direct-project workflow (no solution/packaging registration):

```powershell
dotnet restore tests\Martlet.Conversation.Tests --locked-mode --artifacts-path C:\your-session\conversation-artifacts
dotnet build tests\Martlet.Conversation.Tests --no-restore -c Release --artifacts-path C:\your-session\conversation-artifacts
dotnet test tests\Martlet.Conversation.Tests --no-build -c Release --artifacts-path C:\your-session\conversation-artifacts
```

Local evidence uses the supplied read-only SDK 10.0.401, process-local
CLI home/SDK path and CI=true, with this session's unique C: artifacts to
avoid the independently established pooled-drive VSTest native startup issue.
Central test pins/SDK/runner/cache and existing locks are unchanged. The
dedicated read-only workflow uses the existing exact pinned checkout and
setup-dotnet actions and these direct-project commands.

**Not run / not claimed:** live provider HTTP, keys or vault writes, real
account/model/voice eligibility, actual billing/retention, physical audio or
microphone capture, measured first-audio/Stop/acoustic tail, device churn,
Windows app installation/publish integration, full V04 mic-to-voice or G2.
No automatic capture/VAD, system audio, keyboard/game injection, personality
tools, learned models, downloads, service/driver/security changes or release
operations are introduced.
