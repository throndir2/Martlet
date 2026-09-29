# V05: deterministic participation policy

Portable `net10.0`, BCL-only, synchronous policy over **caller-supplied** input
and state. No Core schema/enum changes, provider reference, microphone,
playback implementation, credentials, network requests, model, persistence,
logging, timers, callbacks or background queue. This is not a bot framework,
LLM relevance prompt, acoustic wake-word detector, VAD, diarization or AEC.

**App-wired by V04b for explicit typed/PTT only.** Its
[Desktop composition](../../docs/CONVERSATION.md) registers this project and
connects caller-owned controls, consented capture/STT, runtime authorization,
cancellation and vault retrieval. This policy engine is unchanged; automatic
name/group listening is not enabled. Live-provider/device tests, real
group-audio qualification and G2 remain **NOT RUN**.

## Privacy and trust boundary

Default `PushToTalkOnly` permits a deliberate `PushToTalkControl` action,
a `HandsFreeListening` utterance (endpointed by voice activity inside a
listening session the user explicitly started and can stop at any time), or
`TypedControl` with `TrustedTypedAddress = true`. These source values are
issued **only by the caller's UI/control boundary**, never parsed from speech,
model output, personality, game content or another participant's text.
Saying "push to talk" is just ambient text. The library cannot authenticate a
human or defend against hostile code in its own process.

`ParticipationState` is an immutable snapshot of separately established
capture, transcription and selected text/speech destination consent signals.
It is **not** a provider disclosure authorization or monetary reservation.
Default signals are false. `AuthorizationRevision` is a positive, opaque
caller-owned revision whenever any consent is true. Increment it when any
selected destination, route, model or permission scope changes. The policy
rejects a decreasing revision; it cannot discover a route change that the
caller failed to report. It does not store destination names, URLs or keys.
Text-only input needs text destination consent, not capture/STT consent.
Speech output additionally requires its own destination consent.

Changing name, personality or participation mode **does not arm capture or
continuous cloud STT and does not create consent**. Name matching runs only
on already obtained final transcripts after separately authorized capture
and STT. **Cloud name-listening may disclose every VAD-qualified utterance
before name recognition. Suppressing a response cannot undo that disclosure
or cost.** Future setup must explain this explicitly and obtain group capture
permission. Personality affects response content/style, never this policy,
capture permission or network permissions.

## Public API and dispatch boundary

Create one long-lived `ParticipationPolicy(sessionId, configuration, state,
clock?)` per conversation session. Supply a nonempty caller-owned correlation
GUID. All mutable state is private and protected by one lock; public values
are immutable. Each policy call is linearizable. The injected `TimeProvider`
must be fast, nonblocking and monotonic. There are no asynchronous operations.

| API | Meaning |
| --- | --- |
| `CreateIntent(ParticipationInput)` | Issue a unique increasing ID, current epoch and original receipt time. No evaluation, capture, authorization, reservation or dispatch. Caller retains bounded content separately. |
| `Evaluate(intent)` | Return immutable `ParticipationDecision`: `Allow`, `Suppress` or `Wait`, authored `PolicyReason`/`PolicyAction`, correlation/epoch, authorization revision, gap, age and supplied/unknown confidence. Never spends budget or claims the active slot. |
| `TryCommit(decision)` | Atomically recheck current gates, original expiry, ownership and revisions. Only an original `Allow` decision can commit. Successful `DispatchCommit` claims the single active slot, spends applicable allowance and supplies a `DispatchLease`. |
| `Release(lease)` | Release only the exact currently active handle after actual runtime ownership ends; false means that handle is no longer active. An old handle cannot release newer work. No refunds. |
| `SetState(state)` | Any changed snapshot advances epoch; pause/mute/required-role revocation or changed active authorization scope reports `StopActiveTurn`. It does not stop anything or release ownership. An identical snapshot does not invalidate proposals. |
| `Reconfigure(configuration)` | Always advances epoch, invalidates old names/intents/proposals, retains active ownership, accepted-ID high-water mark, cooldown and rate history; requests stop if active. |
| `NoteSpeechActivity()` | Caller reports actual ongoing/new speech or utterance end. Resets gap to zero and advances epoch, invalidating old waiting intents. Does not interrupt active responses. |
| `Snapshot` | Content-free IDs, active ownership, recent/retained budget counts, remaining cooldown and current gap. Reading it spends nothing. |

Illustrative **policy-only** usage (values supplied by the future caller):

```csharp
var policy = new ParticipationPolicy(sessionId, configuration, consentState, clock);
// For speech: report real VAD/activity before creating the final transcript intent.
policy.NoteSpeechActivity();
var input = new ParticipationInput(
    InputSource.PushToTalkControl, new Transcript(finalText, confidence: suppliedConfidence));
var intent = policy.CreateIntent(input);
var decision = policy.Evaluate(intent);
var commit = policy.TryCommit(decision);
// Only commit.Accepted allows the caller to start this exact input now.
// The lease is NOT network permission and must never itself authorize a provider.
// After runtime completion AND actual OwnershipRelease:
if (commit.Lease is { } lease)
    policy.Release(lease);
```

For V04b: apply policy **before** `ConversationRuntime.Start` and therefore
before its authorization/credential/provider work. Serialize accepted commit
and immediate start with the UI's pause/stop/destination transitions; do not
save successful commits for later dispatch. Start the exact content/source
whose intent was evaluated, not substituted content. Keep a caller-owned
mapping from `SessionId/IntentId/Epoch` to the runtime's new turn/attempt IDs.
The two epoch domains are different. Obtain fresh exact per-turn provider
authorizations and budgets using the existing runtime contract. Recheck live
consent in those callbacks; handle concurrent stop/revocation through the
runtime's cancellation contract. A policy decision or lease is not a token
accepted by any provider API.

After start, `Release` requires actual `OwnershipRelease`, including after
bounded Stop timeout/quarantine; a stop request alone is insufficient.
If runtime start fails without taking ownership, release the policy lease.
Do not replace the policy object to clear cooldown/rate or stuck ownership.
This is session-local accounting, not a persisted or cross-session quota.

### No pending queue or automatic replacement

`Wait` is diagnostic/advisory, not scheduling. There are **zero pending
turns**. Gap waiting can be reevaluated within the original intent lifetime;
the host must not auto-retry cooldown/rate/busy suppression. A partial
transcript needs a newly created final intent, not mutation of an old input.

Busy manual input returns `RequestExplicitReplacement`. The caller must
obtain the deliberate replacement action, stop the captured Martlet turn,
await ownership release and create a **fresh** intent. An intent created
while busy remains unusable even after release. Ambient noise never requests
replacement. During Martlet playback, PTT returns `StopPlaybackThenRecapture`;
the caller stops that playback and obtains a fresh utterance, never captures
simultaneous echo or reuses the old transcript. Typed explicit input may ask
for deliberate replacement without microphone capture. No action controls
arbitrary OS players. Known self/loopback content is suppressed even for PTT.

## Deterministic reasons and precedence

Invalid API data is an explicit `PolicyValidationException` with an authored
`PolicyValidationCode`, never `Allow`, a silent no-op or a raw-content error.
Normal silence is not an exception/provider failure.

Evaluation precedence after input validation:

1. `Paused`, `Muted`, `ConsentMissing` dominate all phrases and modes.
2. `StaleEpoch`, `IntentExpired`, `AlreadyDispatched`, `SupersededIntent`
   reject invalidated/consumed work.
3. `SelfAudio` from known own/loopback origin, then `NoSpeech`,
   `LowConfidence`, `UncertainTranscript`, `FinalTranscriptRequired`.
4. `PolicyDisabled` for disabled mode, or nonmanual input in PTT-only mode.
5. Playback feedback protection, `Busy`, then `FreshIntentRequired`.
6. Manual input: `ExplicitPushToTalk`, `ExplicitHandsFree` or `ExplicitTypedAddress`.
7. Automatic input: `AudioOriginUnknown`, `ConfidenceUnknown`, then the
   name/group grammar, `NotAddressed` or zero-rate `PolicyDisabled`.
8. `InsufficientGap`, `Cooldown`, `RateLimit`, then `NameAddressed` or
   `GroupInvitation`.

An eligible proposal is `Allow` + `CommitBeforeDispatch`; it is **not**
accepted dispatch. `TryCommit` returns `DispatchAccepted` only once.
An old non-Allow proposal cannot become permission just because time passes
(`DecisionNotAllowed`); evaluate again. A dispatch/release between proposal
and commit invalidates the proposal (`StaleDecision`). State/name/activity
epoch changes invalidate the intent itself.

The symbolic reason/action names are the stable local diagnostic codes;
their enums serialize to strings. They are not additions or alternate wire
values for Core `TurnResult.Suppression`. Existing equivalent Core meanings
(`NoSpeech`, `NotAddressed`, `PolicyDisabled`, `Cooldown`, `Busy`, `SelfAudio`,
`ConsentMissing`) remain unchanged. V04b must deliberately map richer local
metadata without inserting unsupported values into that contract.

### Confidence and empty output

`Transcript` contains finality, `SpeechEvidence`, nullable supplied
confidence and explicit uncertainty. No confidence is invented, rescaled or
inferred from vendor log probabilities. Automatic participation requires
caller-supplied calibrated confidence at least the threshold. A provider
that cannot provide it leaves automatic participation unavailable
(`ConfidenceUnknown`), while deliberate PTT/typed input with unknown
confidence still works. Known low confidence or explicit uncertainty blocks
even PTT. This is not a claim of calibrated STT or speech identity.

Explicit no-speech evidence wins even if text looks plausible. Blank,
punctuation-only and exact case-insensitive markers `[no speech]`, `[silence]`,
`[music]`, `[blank_audio]`, `[inaudible]` and corresponding parenthesized
markers (except `blank_audio`) suppress. The policy cannot identify arbitrary
hallucinated prose without evidence; it does not blacklist common human
phrases such as "Thank you for watching."

## English addressing subset and limitations

Only declared `PolicyLanguage.English` is supported. Names and input use
Unicode NFKC followed by invariant uppercase and ordinal comparison,
independent of host culture. Canonically equivalent accents and fullwidth
Latin letters match; no transliteration, phonetic/similar-name matching or
confusable folding. Supported names are 1-32 UTF-16 code units before and
after normalization, letters/nonspacing marks and single ASCII spaces,
starting with a letter, at most three words. Aliases share the same bounds.

Direct address must start (after outer whitespace) with:

`[Hey | Hi | Hello | Okay ]NAME, REQUEST`

The greeting is optional. The exact name/alias must be followed by comma and
an ASCII space. The request begins with `can you`, `could you`, `would you`,
`will you`, `please`, `what`, `what's`, `how`, `why`, `where`, `when`, `who`,
`do you` or `are you`, followed by a space and some letter/digit payload.
This enforces word boundaries: `Supermartlet`, `Martlett`, possessives and
`Little Birdsong` do not match. Trailing vocatives, bare names, missing comma,
unsupported imperative wording and punctuation between greeting/name are
deliberate false negatives.

Automatic matching rejects quotes, inline/fenced code markers, bracketed or
parenthesized text, colon/semicolon, slashes, block-quote/markup delimiters,
internal newlines/control/format characters anywhere. Apostrophes are allowed
only between letters (contractions). It does not strip quotes and then match
their contents. This conservatively rejects even genuine questions that
contain quotations or parentheticals. Reported speech such as "Martlet said"
or "Martlet, she said, ..." does not fit the grammar. Whole reporting words
`said`, `says`, `asked` and `wrote` also suppress anywhere, catching common
postposed reports while deliberately rejecting genuine requests containing
those words. This is a small lexical exclusion, not a reported-speech parser.

Conversational mode is a separate explicit opt-in. Its only additional
heuristic is an unquoted plain utterance beginning **"Does anyone know "** or
**"Can anyone explain "**, with letter/digit payload and a final question
mark. This is a literal invitation to the group, not inferred relevance or
an LLM judgment. It still needs known external audio, confidence, gap,
cooldown and unsolicited-rate allowance. Other unaddressed game noise and
questions remain silent.

These heuristics cannot establish speaker intent, sarcasm, whether a human
with the same name was addressed, or reported speech with lost punctuation.
English punctuation and confidence availability can cause false negatives;
unpunctuated reports that accidentally fit the grammar can cause false
positives. The corpus establishes the **specified subset**, not real-world
false-positive/negative rates or a robust group-conversation claim.

## Configuration, time and bounded state

| Setting | Default | Valid range |
| --- | --- | --- |
| Mode | `PushToTalkOnly` | Defined modes only; `Disabled` also blocks manual turns |
| Companion name / aliases | `Martlet` / none | Name rules above; at most 8 aliases; no normalized duplicates |
| Addressed gap | 600 ms | 100 ms-3 s |
| Unsolicited gap | 1.2 s | 1.2-3 s |
| Automatic cooldown | 8 s | 1-60 s |
| Intent lifetime | 5 s | 1-5 s and strictly longer than both configured gaps |
| Unsolicited turns/minute | 2 when conversational mode is enabled | 0-2; effective default unsolicited allowance is zero in PTT-only mode |
| Minimum supplied confidence | 0.7 | Finite 0-1 |
| Transcript | No default text | Valid Unicode, at most 4096 UTF-16 code units; blank is `NoSpeech` |

Configuration defensively copies aliases and validates a complete immutable
snapshot. It has no personality content, editor, persistence or migration.
Alias enumeration stops with an error at item nine, including unbounded
enumerables. The normalized text working set is bounded by the input bound
and Unicode normalization expansion, with no regex backtracking.

All durations use the original `TimeProvider.GetTimestamp` origin and fixed
timestamp frequency, never UTC or `Task.Delay`. A reversing, overflowing or
frequency-changing monotonic source fails explicitly. Gap starts at zero
on session construction; caller activity events reset it. Missing activity
events are **not evidence of silence**: V04b must report actual VAD activity,
including ongoing speech, before using automatic mode.

Gap and cooldown open at `elapsed >= limit`; intent expiry is
`age >= lifetime`; a rate entry is recent only while `age < 60 s`.
Reevaluation never renews intent lifetime. Cooldown is measured since **any**
accepted dispatch and applies to automatic name/group turns. Manual turns
bypass gap/cooldown/rate but update cooldown. Only group-invitation dispatches
consume the unsolicited sliding-minute rate.

**Accounting boundary:** denied/uncommitted attempts spend nothing.
Successful commit represents accepted dispatch and is counted even if the
subsequent runtime authorization, provider or device fails; release is not
a refund. This conservative rule prevents rapid failing attempts from
silently reacquiring automatic allowance. The caller must commit immediately
before actual dispatch, not while displaying a preview.

The policy retains at most one content-free active lease, two unsolicited
timestamps, one last-accepted timestamp, one activity timestamp and scalar
ID/epoch/revision counters. Expired rate entries are excluded on reads and
evicted before the next unsolicited commit; no timer/large history store.
No input-ID dictionary is required: policy-issued increasing IDs and the
accepted-ID high-water mark reject older replay forever within the session.
IDs never reset at reconfiguration; exhaustion fails closed, never wraps.
Caller-held intents/decisions retain their bounded input in memory until
the caller releases them. The policy itself stores no transcript history.

## Authored evidence and requirement mapping

Tests use the actual public production policy, synthetic text and an injected
monotonic clock. No provider mocks are needed because this assembly has no
provider calls. No accounts, keys, audio devices, model actions or user-data
writes are involved. Test-runner/build outputs are ordinary artifacts, not
captured conversation data. Production dependency assertions, metadata
canaries and source review support this boundary; tests are not an OS
sandbox or proof against hostile code in the same process.

| Requirement / AC-09 slice | Authored production-path evidence |
| --- | --- |
| Direct-address positives/negatives | `AddressCorpusTests.NameCorpus` N01-N43: aliases, boundaries, case, normalization, contractions, quotes, nested punctuation, code, reported speech and documented unsupported positions |
| Concrete optional group heuristic | `GroupCorpus` G01-G10; named-only, zero-rate and default-PTT cases in `ConsentAndInputTests` |
| Explicit control vs ambient words | `Default_is_control_only_not_spoken_push_to_talk`; ambient trusted-flag validation |
| Privacy and feedback | `ConsentAndInputTests`: each consent role, pause/mute precedence, no-speech evidence, unknown/low confidence, uncertainty/finality, own playback/loopback/unknown origin |
| Nonconsuming polling, one active, replay/races | `DispatchLifecycleTests`: repeated evaluation, parallel distinct/same proposal commits, old-ID replay, stale decision, old release and busy fresh-intent requirements |
| Pending expiry and mute | No queue is implemented; busy-intent, activity/epoch invalidation, original-expiry, mute/unmute and active-revocation cases cover the chosen replacement-only contract |
| Gap/cooldown/rate and clocks | `TimeAndBudgetTests`: exact one-tick boundaries, configured/default rates, two-per-sliding-minute, UTC rollback/forward, broken monotonic source, bounded eviction |
| Immutable config, errors and privacy | `ValidationAndPrivacyTests`: defensive copy, unsupported/invalid bounds, malformed Unicode, enum/NaN/infinity/null cases, cross-session handles, exhaustion, safe JSON/ToString canaries and BCL-only dependencies |

The former dedicated hosted participation workflow is removed under the
[local-only repository policy](../../README.md#local-only-validation-policy).
Retain the SDK pin and direct-project locked restore, Release build and tests
below; no remote replacement or live calls are needed.

For the isolated Windows development run, use SDK 10.0.401 with `CI=true`,
telemetry opted out, certificate generation disabled and session-owned
`DOTNET_CLI_HOME`. Use the **same unique C: artifacts directory** on all
restore/build/test commands to avoid the observed D: pooled-drive VSTest
issue:

```powershell
dotnet restore tests\Martlet.Participation.Tests --locked-mode --artifacts-path "$artifacts"
dotnet build tests\Martlet.Participation.Tests --no-restore -c Release --artifacts-path "$artifacts"
dotnet test tests\Martlet.Participation.Tests --no-build -c Release --artifacts-path "$artifacts"
```

Both generated `packages.lock.json` files are committed. Production has no
packages; tests use only existing centrally pinned xUnit/Test SDK packages.
