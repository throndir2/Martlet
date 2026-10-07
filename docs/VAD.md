# Bounded post-capture voice activity library (V01b)

**Experimental standalone library; normal production native initialization is
BLOCKED.** `Martlet.VoiceActivity` implements managed PCM window adaptation,
deterministic endpoint ranges and an owned post-capture analysis pipeline with
an internal pinned CPU classifier. It is not referenced by Desktop, Doctor,
Audio, the root solution or the Windows payload. Existing typed/PTT capture,
audio bytes, consent, devices and provider behavior are unchanged.

The public `CpuVoiceActivityAnalyzer` constructor and `Availability` are inert.
Normal public `Start` throws the typed `NativePrivacyUnqualified` failure
**before** consuming authorization, copying/borrowing PCM, reading a model,
scheduling a worker or initializing ORT. No empty result, no-speech response,
fixture classifier or automatic fallback replaces that error. There is no
public native-factory injection, eligibility setter, qualification grant,
environment/configuration bypass or `allowUnqualified` switch.

## Composition and evidence boundary

The exact Windows runtime's ordinary telemetry/privacy boundary is not qualified.
Library code does not read or set `ORT_RUNNING_UNIT_TESTS` as permission.
Only named internal friend test/qualification composition can bind the real
backend. Internal visibility is an engineering/composition boundary, not a
security sandbox against arbitrary code/reflection in the same process.

Ordinary tests are unfiltered **managed/fake-native tests only**. They contain
no discoverable native Fact/Theory, skipped native test or environment/category
opt-in. `QualificationComposition` is non-discovered wiring, not an executable
test or a permission issuer. Any future execution of this NEW classifier needs
separate exact host/source/binary review and new execution authorization.

An earlier, separate material probe established the selected model identity and
schema under an explicitly reviewed, synthetic, internal-runtime test condition.
Its single authorization was consumed. Its scores, timing and working set are
not native validation of this library, speech evidence, ordinary-production
performance, incremental private memory or a hard allocator-cap measurement.
That probe is not replayed, embedded or shipped here.

## Public managed algorithms

`PcmVadWindowAdapter` consumes explicitly supplied mono 16 kHz signed PCM16
little-endian bytes. It does not open a device, parse WAV, resample, classify
speech or send anything. Arbitrary byte fragments retain one possible trailing
byte and one partial 512-sample window. PCM is converted by division by 32768.
Offsets are integer source samples, independent of packet sizes or wall clocks.
Final zero padding exists only inside the 512-sample model window: valid source
counts and endpoint ranges never include it. A partial PCM16 sample is an error.

`SpeechEndpointer` consumes explicit finite, bounded `VoiceActivityWindowScore`
values with contiguous offsets. Supplied score schedules are not a learned
classifier or calibrated confidence. Default policy:

| Boundary | Rule |
| --- | --- |
| Onset | Score >=0.50 for 4,096 consecutive actual source samples; lower scores reset an unconfirmed candidate |
| Active hold | Scores >=0.35 keep an already confirmed segment active |
| Silence endpoint | Score <0.35 for 9,600 actual source samples; an intervening >=0.35 score resets pending silence |
| Endpoint position | First low window's source start, with the later decision sample reported separately |
| Pre-roll range | Up to 8,000 already-owned samples before onset, clamped to zero and previous range end; never pre-consent recording |
| EOF | Close confirmed speech at actual input end; do not promote an unconfirmed candidate |
| Exact input cap | Distinct `MaximumInput` endpoint reason; oversized input is rejected, not silently trimmed |

Ranges are half-open. Suggested pre-roll-inclusive ranges are separate from
speech onset/end. Windowed estimates are not sample-by-sample acoustic truth;
600 ms of samples is not a measured wall-clock endpoint latency.

## Borrowed input, permission and ownership

The caller obtains `CapturedUtterance` through the existing capture engine's
one-time, original-token/clock-checked `TakeUtterance` transfer. The caller keeps
ownership. The internal analysis composition uses `CopyPcmTo` once under that
object's existing serialized copy/dispose boundary. It never disposes or changes
the caller's original PCM, trims it, or suppresses a PTT action.

Disposal before copying is `InputUnavailable`, not silence. Concurrent disposal
serializes with the copy. Subsequent disposal or mutation of separate external
frame copies cannot change the private analyzed samples. To revoke permission
after a copy, use the original caller token or authorization revocation; disposal
of one copy cannot remotely revoke another.

The one-use `LocalVoiceActivityAuthorization` binds the exact source object,
original IDs/epoch, profile/configuration revision, whole `[0, SampleCount)`
range, options and original local-processing lifetime. It asserts the caller
obtained local data permission; it cannot prove human consent or grant native
eligibility. It does not renew capture, authorize upload/send, create a new live
turn, or permit continuous/name listening. Accepted epochs must increase for
an analyzer; there is no retry/backlog/replay/rearm behavior.

One dedicated non-UI worker owns copying, model loading, inference and disposal.
The factory/session are fresh per analysis; recurrent state and context are not
shared between utterances. There is one active owner and no pending queue.
Direct original-token, explicit-revocation, UTC and original monotonic checks
apply at admission, copies, loading, native returns and result transfer. Timer
or linked-callback delivery is never the sole permission authority.

`Completion` is a bounded observation; cancellation/deadline can be reported
while resources remain pending. `OwnershipRelease` separately confirms actual
worker, owned cancellation, timer and backend disposal retirement. `TakeResult`
requires successful release and still-valid original permission and transfers
its immutable metadata once. Historical completion is not fresh permission.
The current snapshot instead reflects effective cancellation, expiry or cleanup
failure; it never pairs a successful activity/no-activity outcome with a current
failure after result invalidation.
Old operation controls cannot affect the next operation.

Cancel/dispose do not free native buffers or a busy slot merely because a wait
expired. Failed cleanup/cancellation quarantines the analyzer; no replacement
factory is a recovery strategy. Owned audio/tensor scratch can remain bounded
while a native call/cleanup is blocked or quarantined. Buffers are cleared only
after safe actual retirement; immediate erasure of ORT/OS-internal copies is
not promised. A future app must retain its existing shared effect slot until
this ownership boundary, not create a second scheduler.

## Model, tensor and dependency identity

The small embedded `model-manifest.json` contains metadata only; **no weights
or runtime binaries are committed**. The external file is explicitly selected,
bounded and hash-verified before native model loading, never downloaded by the
library.

| Item | Pin |
| --- | --- |
| Model | Silero VAD v6.2.1, `silero_vad_16k_op15.onnx` |
| Revision | `7e30209a3e901f9842f81b225f3e93d8199902b1` |
| Model length | 1,289,603 bytes |
| Model SHA-256 | `7ed98ddbad84ccac4cd0aeb3099049280713df825c610a8ed34543318f1b2c49` |
| Runtime | CPU `Microsoft.ML.OnnxRuntime` and Managed, both 1.30.0 |
| Runtime source | `f2c39fe2f838cf35ce7da92824f5a5e3ee6e88a7` |
| Resolved inference subset | ORT CPU1.30.0, Managed1.30.0, transitive Tensors9.0.0 |

The model SHA-256 was measured from an official immutable artifact and its
Git-framed SHA-1 correlated with upstream metadata. It is not an independently
published SHA-256 or publisher attestation. The SDK prunes Managed's Memory
edge on net10; the earlier four-package probe included an explicit Memory
reference. No artificial Memory dependency or pruning-policy change is made.

Declared schema: Float `input[-1,-1]` (`batch,sequence`), Int64 scalar `sr[]`,
Float `state[2,-1,128]`, Float `output[-1,1]`, Float `stateN[-1,-1,-1]`.
The source symbols are pinned too. Operational input is `[1,576]` (64 context
plus 512 new samples), state `[2,1,128]`, scalar rate 16000. Dynamic output
metadata does **not** relax actual Float `[1,1]` score / `[2,1,128]` state checks,
finite values or score range. Rank-one sample rate is not an accepted fallback.

Runtime build/buildTransitive/analyzer/content hooks are excluded. The approved
package path property selects only the two win-x64 CPU DLL assets. Application
and normal/RID publish locks remain unchanged. Runtime MIT does not license all
embedded components uniformly; complete upstream notices and the separate
Silero rights boundary are retained in the library's `THIRD-PARTY-NOTICES.txt`
and `Notices` directory. Martlet still has no project license grant.

## Bounds and failure handling

Hard maximums: 480,000 input samples / 960,000 private PCM bytes, 938 model
windows, 128 segments, one active worker/session and one pending cooperative
cancellation request. Append/input admission validates bounds before copying.
Options may lower the input/segment caps. Processing budget is positive and
at most 10 seconds, local permission at most 30 seconds, and disposal
observation wait at most two seconds. These are admission/observation policies,
not hard native abort or physical latency guarantees.

Errors are fixed authored codes/summaries/action IDs. Missing consent, wrong
binding, disposed input, malformed/truncated PCM, invalid scores, model
size/hash/schema, platform/runtime failures, deadlines and cleanup failures
are explicit; none becomes successful no-activity. No native exception text,
path, raw PCM, scores, transcript or provider data is written to logs/export.
Result scores are uncalibrated local data, not STT confidence or recognized
speech. No-activity does not silently discard the caller's intent/audio.

## Managed-only developer checks

Use the existing pinned SDK, unchanged audit policy and locked restores, with
one owned C: artifacts/cache/temp/results location on the pooled-drive host.
The standalone test project is intentionally not added to the root solution:

```powershell
dotnet restore tests\Martlet.VoiceActivity.Tests --locked-mode --artifacts-path $artifacts
dotnet test tests\Martlet.VoiceActivity.Tests --no-restore -c Release --artifacts-path $artifacts --results-directory $results
```

Run it **without a native-exclusion filter**: native execution is not
discoverable. Managed tests use actual capture/normalization/transfer with
authored in-memory capture devices, the real managed window/endpointer/owner,
and explicitly fake inference seams for shape/error/clock/cancellation races.
Core and both Audio test targets remain required boundary regressions.

**Remaining gates:** native execution of this new classifier is PENDING;
normal production native privacy/eligibility is BLOCKED; live capture stopping,
Desktop integration and installation qualification are unwired/unqualified.
Real speech/noise/language/device accuracy, normal performance, incremental
private memory, signing, novice use and V01/AC-05/G2 are not passed by these
managed checks or the earlier material probe.

## Always listening's end-of-turn judge (production, separate)

This library stays blocked. Always listening in the desktop uses its own
energy detector (`EnergyVoiceActivityDetector` in Martlet.Audio) and, on top of
it, an end-of-turn judge (`EndOfTurnGate` in Martlet.Conversation). With
Companion › Listening › *Judge when I finish talking* on (the default), the
detector itself waits for the longer pause for unfinished speech
(`EndOfTurnGate.DetectorEndSilence`, at least 1.6 s). After 260 ms of silence
the gate asks Smart Turn v3.2 (`SmartTurnEngine` in Martlet.Sherpa, the
bundled `turn-detection\smart-turn-v3.2-cpu.onnx`) and ends the speech early
with `EnergyVoiceActivityDetector.EndSpeech()` when it hears a finished turn.
At the plain pause (Reply after) the gate ends it unless the judge said the
turn is unfinished. Off, missing or failed, the detector runs with the plain
pause exactly as before. Smart Turn runs through the ONNX Runtime that ships
with sherpa-onnx (1.28.2), not this library's 1.30 package. See
[Voice latency](VOICE_LATENCY.md#the-end-of-turn-judge) and
[Conversation](CONVERSATION.md#hands-free-voice-activity-and-voice-id).
