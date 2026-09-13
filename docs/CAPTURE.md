# V01a explicit microphone capture

This is a **library foundation**, not full V01, a live conversation feature,
validated VAD, microphone qualification, or a passed G2 gate. Desktop, Doctor,
settings, provider routes, credential storage and packaging are unchanged.
Existing [playback](AUDIO.md) remains the single playback implementation.
The portable and Windows targets of `Martlet.Audio` are reused, with the
existing locked **NAudio.Wasapi 3.1.0** dependency and no new package.

Loading assemblies, constructing `MicrophoneCapture`, either Windows factory,
or `InputDeviceMonitor`, and disposing unused instances do **not** enumerate,
activate, capture, play, or change a device. There is no launch/profile-load/
reconnect hook, background arming, system loopback, process/Discord/game
capture, diarization, beamforming, or AEC.

## Explicit request and ownership

The owning UI must obtain actual human consent and construct a new
`CaptureRequest` plus matching `CaptureAuthorization(..., true)` for each
deliberate PTT press. This assertion is not a Windows permission grant and
cannot prove that a human consented. Do not persist or restore it as consent.
The request binds all Core `CorrelationIds`, the current session, a strictly
increasing epoch, intended input, maximum duration and authorization expiry.
Mismatch, missing authorization, mute/pause/lock, expired requests and reused
epochs are rejected before scheduling native work.

Authorization checks read the **original caller token** rather than trusting
cancellation callback delivery (a newer LIFO callback can block it). They also
recheck **absolute UTC expiry** and the **original monotonic authorization
lifetime** (`ExpiresAt - initialUtcNow`), stored separately from maximum capture
duration. Neither acquisition nor delayed transfer can outlive that original
consent budget after a UTC rollback. Revocation is checked before native
open/start, admission, frame copies, successful seal/completion and transfer
of unclaimed PCM. No wait on another caller-token callback is needed to reject
revoked data.

The following is an integration sketch, **not an automatically executed smoke**:

```csharp
await using var microphone = new MicrophoneCapture(
    sessionId, new Martlet.Audio.Windows.WasapiCaptureDeviceFactory());
var request = new CaptureRequest(ids, epoch, selectedInput,
    TimeSpan.FromSeconds(25), DateTimeOffset.UtcNow.AddSeconds(30));
// Only in response to the user's separately consented press:
var run = microphone.Press(request, new CaptureAuthorization(request, true));
await run.Ready;
// On release:
var terminal = await run.ReleaseAsync();
using var utterance = run.TakeUtterance();
// Only a completed utterance is available. Its destination needs separate consent.
// No STT/network/provider call is performed by this library.
```

`MicrophoneCapture` is bound to one nonempty session ID. Calls are thread-safe;
competing presses do not queue or replace one another. A new press requires
both previous completion and successful actual device release. New epochs
invalidate old run handles by ownership: old reads and controls cannot affect
the next run. A failed or hung native lifetime quarantines its factory/run;
constructing another factory is not a recovery strategy.

| API | Contract |
| --- | --- |
| `Press(request, authorization, token)` | Return a run immediately; one dedicated non-UI worker checks authorization again before native work. There is no automatic-arm API. |
| `run.Ready` | Source format after native Start returns, or null when no binding completed. Not proof of microphone privacy permission, received samples, or useful speech. |
| `run.ReleaseAsync()` | Seal exactly once, stop this capture and await bounded terminal reporting. Repeated release returns the same completion task/count. |
| `run.Completion`, `run.Snapshot` | Metadata-only state, end reason, source/canonical sample counts, retained canonical bytes, dropped event count and sanitized error. No audio payload/endpoint identity/native message. |
| `run.TakeUtterance()` | After completion, transfer the single completed `CapturedUtterance` to the caller, once. No audio for no-frames/canceled/failed attempts or when original-token cancellation/absolute expiry is observed at transfer. |
| `CapturedUtterance.CopyPcmTo(destination)` | Copy private owned PCM; no mutable array or array-backed memory is exposed. Caller owns and must clear its copy. |
| `CapturedUtterance.GetFrame(index)` | Copy a Core `PcmFrame`, 320 samples/20 ms at mono 16 kHz, with original IDs/epoch and canonical sequence/sample offset. Final frame may be shorter. |
| `CapturedUtterance.Dispose()` | Zero its private storage and reject subsequent reads. PCM is immutable until explicit disposal. |
| `run.TryCopyMonoFrame(index, destination)` | Optional synchronous pull of one fully available 640-byte canonical frame, only while active. Caller supplies the buffer; no separate audio queue or callback. On unavailability it clears the destination. Deadline expiry can cancel the read. |
| `StopAsync`, `SetMutedAsync`, `SetPausedAsync`, `SetSessionLockedAsync`, `DisposeAsync` | Immediately invalidate admission, discard unclaimed audio and zero local owned working buffers. All return bounded asynchronous reporting; unmute/unpause/unlock never restart. Future UI/OS integration must deliver these signals. |
| `run.DeviceRelease` | Actual native-worker **and run-owned device-token cancellation-handler** completion, with `Released` and a safe error. Unrelated caller-token callbacks are not awaited. May remain pending for an uncooperative driver. Do not equate a terminal report with released resources. |

An unclaimed completed utterance remains a single bounded in-memory payload.
Stop, mute, pause, lock, disposal or a subsequent press discard it. There is
no retry list/offline backlog. The completed task and event ring retain only
metadata. `RetainedPcmBytes` describes owned payload bytes at that observation;
the current Snapshot updates this count after take/discard, while the terminal
outcome and sample accounting stay frozen. Ownership transferred to a caller,
including frame copies, cannot be remotely revoked; the coordinator must
discard those on epoch change/cancellation and dispose them promptly.

Cancellation or expiry observed **after** a valid terminal completion but
**before** `TakeUtterance` clears the unclaimed payload and returns null.
Historical Completion/event metadata is not rewritten; Snapshot then reports
zero retained PCM, with no new post-terminal callback/event. Completion alone
does not guarantee that the utterance will still be authorized at transfer.

## Format normalization and sample time

The Windows adapter explicitly resolves an actual **capture** `MMDevice`,
initializes WASAPI **shared mode** with that device's mix format, and requests
a 50 ms buffer. It rejects a reported buffer over 100 ms, non-capture endpoints,
unknown layouts/encodings, rate or alignment changes, and invalid sample
accounting. There is no exclusive fallback, float-as-PCM reinterpretation,
automatic-routing virtual device, playback adapter change, or hidden system
converter.

Supported source subset: **16 / 24 / 32 / 44.1 / 48 / 96 kHz**, **1-8
interleaved channels**, signed **PCM16LE** or **float32LE**, including equivalent
WAVEFORMATEXTENSIBLE forms whose valid bits equal their container size.
PCM24, integer PCM32, 192 kHz, compressed/unknown formats and reduced-valid-bit
containers fail explicitly. This is not a claim that every microphone supports
the subset; hardware qualification remains pending.

`CaptureNormalizer` is the production portable conversion path, not a test
substitute. It averages the actual input channels with equal weights; finite
float input is clipped to [-1, 1]. NaN/infinity fails rather than manufacturing
silence. The selected mono input provides real peak/RMS meters, not VAD or
confidence. Multichannel averaging is not microphone-array beamforming.

Output is the existing Core `PcmFormat`: **mono 16,000 Hz signed PCM16
little-endian**, rounded to nearest-even and saturated. At 16 kHz there is no
resampling filter, preserving mono PCM16 byte identity. Other supported rates
use a bounded 64-tap Blackman-windowed sinc filter, cutoff
`0.45 * 16000 / sourceRate` cycles/source sample, normalized per rational phase.
This supplies anti-alias filtering rather than dropping samples or nearest-
neighbor decimation. Physical microphone/acoustic quality has not been qualified.

Arbitrary packet splits, including splits inside float values/interleaved
samples, retain at most one source-frame remainder. Rational integer phase
accounting is continuous across packets: canonical sample `n` maps to source
position `n * sourceRate / 16000`, relative to this run's first received source
sample. The filter waits for 32 source samples of lookahead; it extends the
first/last sample at the boundaries and flushes only on a valid seal.
Final output count is `floor(sourceSamples * 16000 / sourceRate)`, further
bounded by the explicit canonical-byte limit. There are no synthetic trailing
samples beyond that count. A partial final source frame is `StreamTruncated`.
Source count records processed source samples and can include up to one bounded
packet beyond a smaller canonical-byte cap; it is not the submitted STT count.

Native device positions must be contiguous after the first packet. Subsequent
WASAPI discontinuities, timestamp errors, impossible counts or unknown flags
fail and discard the utterance. An initial WASAPI discontinuity marker alone
is permitted at stream establishment; subsequent markers are loss. Native
`Silent` packets are explicitly zero-valued **reported source frames** with real
sample counts. They are distinct from no packets, no canonical frames,
capture failure, and unimplemented speech/no-speech detection.

## Bounds, deadlines and privacy

| Resource | Enforced policy |
| --- | --- |
| Utterance | At most 30 seconds and 2 MiB canonical PCM. At 16 kHz PCM16, the duration normally limits this to **960,000 bytes / 480,000 samples**. Options and requests may lower limits, not raise them. |
| Byte/duration limits | Seal at the exact allowed canonical count and report `ByteLimit`/`DurationLimit`; do not silently continue recording. Minimum request duration is 20 ms; byte cap can be as low as one aligned PCM16 sample. |
| Authorization deadline | Capture duration and the original monotonic consent lifetime are separate limits, both including opening time. The abort timer uses the earlier one. The shared authorization check rejects either absolute UTC expiry or elapsed time reaching the original consent lifetime, including after seal and before transfer. Timer/cancellation callback delivery is not the sole authority. |
| Audio memory | One pre-sized canonical buffer, one source remainder, 128 mono history samples, fixed coefficient tables and a 3,264-byte canonical scratch. Worker and Windows adapter each have one source-format scratch of at most 100 ms (up to 307,200 bytes each in the supported subset). No unbounded native-to-managed audio channel. |
| Admission | Packet bounds are checked before conversion/copy/queueing; output is capped before copying into owned storage. Oversized or discontinuous packets fail, not drop-oldest audio. |
| Capture events | 128 metadata-only entries, drop oldest, monotonic sequence/elapsed time and cumulative `DroppedEvents`. Meters occur only after source samples arrive; no animation/synthetic listening state. |
| Local stop | Immediately invalidates local ownership and clears retained buffers. Default reporting/cleanup wait is at most 2 seconds, configurable lower. Not a measured 250 ms physical-stop guarantee. |
| Noncooperative native work | Remains owned by its original worker; no cross-thread COM disposal, premature release claim or new capture on that factory. A late return cannot admit audio or Start after observed invalidation. |

Already in-flight native calls cannot be retracted. A blocked native read may
temporarily own up to its bounded scratch while the terminal report is already
failed; that scratch is zeroed when the owner returns, not unsafely overwritten
from another thread. Native driver/OS buffers are not claimed to be immediately
zeroed by managed cancellation. No capture events are emitted after the terminal
event; actual late cleanup is observed through `DeviceRelease`, not resurrection
of an earlier result.

Expiry of either authorization clock is a hard `DeadlineExceeded` failure with
`CaptureEndReason.AuthorizationExpired`, not a successful duration-limit seal.
It wins if consent expiry and the duration limit coincide. Callers wanting a normal
duration-limit utterance should choose a shorter capture duration within the
authorization window (as in the sketch), leaving time for cleanup/transfer.
Limits have not been increased: authorization still expires within 30 seconds.

No temporary audio, audio files, transcript, provider request, endpoint logging,
recording retention setting, telemetry upload or diagnostic bundle is produced.
This does not establish a process-wide privacy guarantee for future consumers
or Windows itself. Never put explicit device lists, requests or PCM in logs.

## Discovery, selection and Windows notifications

`InputDeviceMonitor` takes an `IInputDeviceDiscoveryFactory`; on Windows use
`WasapiInputDeviceDiscoveryFactory`. `StartAsync(new InputDiscoveryRequest(true))`
explicitly requests discovery plus a scoped notification subscription.
`RefreshAsync()` explicitly enumerates again. Construction is inert.
Only one refresh can be pending; it is not an unbounded UI work queue.

The returned `InputDeviceList` contains at most 128 active capture endpoints
with opaque ID, UI-only friendly name and console-role default status. Names
are bounded to 256 characters, IDs to 1,024. Listing a microphone is **not**
capture authorization or proof of Windows privacy access. Errors include a
non-null safe error; an empty list on failure must not appear as success.

The 32-entry `InputDeviceMonitor.Events` ring contains only coalesced
add/remove/state/default/property flags, increasing generation/sequence and
drop counts. IDs/names never enter this event stream. Events mean the list
may be stale; only an explicit refresh enumerates. Generic add/remove/state/
property callbacks can also invalidate the list for unrelated render devices,
because those Windows notifications do not supply data flow. Default events
are restricted to capture/console role. This conservative refresh hint grants
no device permission. Stop/dispose closes the ring and invalidates generations;
late callbacks cannot publish or reopen. `StopAsync()` reports bounded release
status; `DeviceRelease` records actual native cleanup.

Discovery/refresh uses a dedicated worker, so synchronous native calls cannot
block the UI thread. A caller awaiting an uncooperative discovery can call
`StopAsync`; its pending list request becomes an explicit failure immediately,
while cleanup remains accurately pending. The monitor is one-shot after Stop
or failure, not a reconnection loop.

| Input policy | Device-change behavior |
| --- | --- |
| `FixedEndpoint` | Always the requested actual endpoint. Loss/state/profile/property change stops and discards. Unrelated default changes do not silently switch it. Reconnection does not rearm. |
| `FollowDefaultOnNextPress` | Explicitly resolve the Windows console-role capture default once per authorized press. A mid-utterance default change stops/discards with `AudioDeviceChanged`. Only a new press/epoch can bind the new default. No automatic reopen/follow within an utterance. |

Selected property changes are conservatively treated as format/device churn,
even if the changed property later turns out not to affect PCM. Review selection
and press again; a false-safe continuing stream is worse than a visible stop.
The native adapter consumes sticky selected-device failures again after
Stop/Reset, so release before the next Read cannot turn a recorded default/
property/state/loss event into a completed utterance. Stop/Reset and disposal
are still attempted before reporting the failure.
Neither policy changes default devices, permissions, privacy, endpoint volume/
mute, drivers, services, another capture instance or playback.

Activation, endpoint/format queries, capture buffer leases, Stop/Reset and
COM release remain on one long-running MTA worker. Pinned NAudio notifications
use `CreateNotificationClient(false)`; OS callbacks only set atomic flags, never
call user handlers/audio APIs or synchronously unregister. NAudio 3.1.0's
notification client swallows unregister exceptions, so this adapter deactivates
handlers and releases the owning enumerator **before** disposing that wrapper.
The enumerator's observed COM-release boundary retains notification roots on
failure. Failed resources remain referenced by the quarantined factory. No
thread switching or claims of successful unregister are used to hide failures.

## Error contracts and future integration

With the shared owner's approval, this pre-release additive extension appends
`Stage.Capture` after `Provider` and `AudioCaptureFailed`, `AudioAccessDenied`,
`AudioDeviceBusy`, `AudioDeviceChanged` after the existing `DeadlineExceeded`.
Existing numeric ordinals are preserved. Canonical Core JSON/validation is
covered in the Audio-owned tests; existing shared schemas/helpers are unchanged.

`CaptureErrors` creates Core `MartletError` with `Stage.Capture`, fixed summaries/
action IDs and no automatic retry. Other reused codes include `NotConfigured`
(authorization mismatch), `AudioDeviceUnavailable`, `AudioDeviceLost`,
`AudioFormatUnsupported`, `StreamTruncated`, `PayloadTooLarge` and
`DeadlineExceeded`. Native access denied/missing/busy/unsupported/lost failures
are distinguished where Windows supplies a known HRESULT. Arbitrary exception
messages/HRESULT text are not returned. No failure becomes successful silence.
New event objects are in-process Audio contracts, not a frozen remote protocol.

`SetOwnOutputActiveAsync(true)` cancels capture and gates presses.
`ArmingDecision(false)` returns `OwnOutputSuppressed` during own output;
`ArmingDecision(true)` returns `PlaybackStopRequired`. The future coordinator
must stop **its own** playback, await actual release and any qualified acoustic
tail, then signal inactive before a new manual press. This library never stops
global/sibling audio, and headphone/output state is not consent.

**V01b** must select and qualify actual CPU learned VAD/endpointer and model
windowing. Peak/RMS is not VAD. It can pull bounded 20 ms canonical frames
through `TryCopyMonoFrame`, then read the sealed tail through `GetFrame`,
without moving native-buffer ownership or adding a second device stack.
No continuous capture/preroll is implemented or authorized in this slice.
V01b must preserve cancellation/epoch rules for any copies it owns.

**V02** owns human consent, device-selection UI, visible mic state, privacy
remedies, mute/pause/lock signal delivery and destination approval.
**V04** owns playback coordination/tails, STT request conversion, turn epochs,
frame consumption, provider cancellation and disposal of transferred audio.
There is no live UI/CLI conversation or provider integration in V01a.

## Evidence and real-device qualification

Deterministic tests use the production `MicrophoneCapture`/`CaptureRun`/
`CaptureNormalizer` and injectable capture/discovery boundaries. They exercise
zero-device construction/invalid authorization, IDs and epochs, odd fragments,
known PCM/float/multichannel conversion, independent reference resampling,
anti-alias response, exact duration/byte limits, delayed timer delivery,
blocked native open/read/cleanup/cancellation handlers, concurrent callers,
privacy controls, immutable ownership/zeroing, failure normalization and bounded
metadata. Windows tests compile the real NAudio APIs and invoke production
managed notification handlers/format parsing without opening COM devices.

No test or smoke opens a physical microphone, enumerates a physical endpoint,
plays audio, or changes devices/privacy. Compiling WPF/WASAPI and passing fake-
device tests is not hardware evidence. No native audio smoke helper is installed.

Use the existing locked CI workflow and SDK; e.g. with `CI=true` and an isolated
artifact directory consistently supplied to all commands:

```powershell
dotnet restore Martlet.slnx --locked-mode --artifacts-path C:\OwnedSession\ci-artifacts
dotnet build Martlet.slnx --no-restore -c Release --artifacts-path C:\OwnedSession\ci-artifacts
dotnet test tests\Martlet.Audio.Tests --no-restore -c Release --artifacts-path C:\OwnedSession\ci-artifacts --filter 'FullyQualifiedName~Capture|FullyQualifiedName~InputDevice'
dotnet test Martlet.slnx --no-build -c Release --artifacts-path C:\OwnedSession\ci-artifacts
.\scripts\Smoke-Doctor.ps1 -ExecutablePath C:\OwnedSession\ci-artifacts\bin\Martlet.Doctor\release_net10.0\Martlet.Doctor.dll
```

Local implementation evidence (SDK 10.0.401, CI mode, isolated session-owned
`C:` outputs): 102 portable and 118 Windows new capture/device tests, all 102
existing playback tests, and the full solution's 712 tests passed. The
separately restored/built existing provider HTTP lane passed 191 offline tests.
Both portable and Windows Doctor smoke paths passed with physical audio OFF.
No package/SDK pin, workflow, runner or global build setting was changed.

Independent-review regressions were added before production fixes: 11 portable
and 15 Windows failures reproduced delayed caller-token callback authorization,
independent UTC expiry, and sticky device notifications bypassed by release.
The initial empty-PCM UTC rollback controls already passed. A second regression
pass reproduced five further failures per TFM with admitted PCM, a 30-second
capture maximum, one-second consent and UTC rollback: admission, release, native
cleanup, timer expiry and a direct delayed take all bypassed the original
consent lifetime. Separating that lifetime now rejects and zeroes those payloads
without rewriting earlier valid Completion history; nonempty shorter-duration
captures still complete when their longer original consent remains valid.
The final review suite has 21 portable and 25 Windows cases, including live
frame-copy revocation and coincident expiry.
Native-stop cases execute the actual Windows wrapper's Stop/Dispose and managed
notification handlers with COM handles unset and controlled PCM; they are not
physical capture or native-driver qualification. Existing Playback files were
not changed by these fixes.

All physical gates below are **not run / not passed**. They require separately
authorized human/device qualification, not an autonomous agent enabling hardware.

| Qualification | Required witness |
| --- | --- |
| Actual selected USB/internal/headset mic | Correct endpoint, PCM format/rate/channels, sample continuity, level meter and intelligible authorized utterance; no unintended endpoint. |
| 44.1/48 kHz PCM16/float32 and multichannel | Native shared mix acceptance, conversion quality and no callback packet loss under realistic load. |
| Access denial / no device / exclusive contention | Known Windows HRESULT/remedy, no green microphone status from enumeration. Do not change host privacy settings just to simulate these here. |
| USB unplug/replug/default changes | Fixed endpoint never switches; default policy stops visibly and requires a new authorized press; no spontaneous capture on reconnect. |
| Bluetooth HFP/A2DP/profile churn | Format/state/property notification delivery, explicit unsupported-format result where outside subset, correct release behavior. |
| Pause/mute/lock/sleep/resume/exit | Real UI/OS signals connected, bounded physical stop and accurate stuck-driver reporting; no background restart. |
| Noncooperative Windows notifications/driver | Enumerator release and callback lifetime under OS churn, quarantine and no use-after-release; current tests are managed simulations only. |
| Acoustic feedback / latency | Real PTT stop latency and acoustic-tail measurements, headset/no-room-speaker surprises; no AEC/250 ms guarantee inferred from tests. |
| End-to-end speech / VAD / G2 | Separate learned VAD qualification, destination-consented live STT/LLM/TTS and V04 orchestration. None is part of V01a's hardware evidence. |

Sources for the compiled pinned adapter:
[NAudio capture client](https://github.com/naudio/NAudio/blob/v3.1.0/src/NAudio.Wasapi/CoreAudioApi/AudioCaptureClient.cs),
[endpoint enumerator](https://github.com/naudio/NAudio/blob/v3.1.0/src/NAudio.Wasapi/CoreAudioApi/MMDeviceEnumerator.cs),
[notification lifetime](https://github.com/naudio/NAudio/blob/v3.1.0/src/NAudio.Wasapi/CoreAudioApi/MMDeviceNotificationClient.cs),
[extensible format](https://github.com/naudio/NAudio/blob/v3.1.0/src/NAudio.Core/Wave/WaveFormats/WaveFormatExtensible.cs).
