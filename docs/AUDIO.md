# F03b bounded PCM playback

This is an opt-in playback library, not a voice app or a passed audio hardware
gate. F03c now connects Desktop/Windows Doctor fixture sessions to this same
sink through a separate per-action synthetic-tone permission boundary.
See [the fixture experience](DIAGNOSTICS.md#offline-fixture-experience-f03c).
[V02b Audio setup](SETUP.md#explicit-local-audio-setup-v02b) additionally uses
this same sink/tone for explicit selected-output local tests and human listening
confirmation. The reviewed playback lifecycle is unchanged. The later
[V04b explicit API conversation](CONVERSATION.md) now routes authorized
TTS segments through this sink. Learned VAD, loopback and avatars remain unwired.
Remote D02 protocols remain partial. Loading either assembly, constructing
`PcmPlaybackSink` / `WasapiDeviceFactory`, or disposing an unused sink does
not enumerate, activate or play an endpoint.

## Projects and dependency direction

`Martlet.Audio` targets `net10.0` (portable lifecycle/pump, no NAudio dependency)
and `net10.0-windows` (the same lifecycle plus `Martlet.Audio.Windows`).
Both reference the existing portable `Martlet.Core`. A Windows consumer must
select the Windows TFM to access `WasapiDeviceFactory`; the portable target
does not substitute a fake output. `IPlaybackDeviceFactory` is mandatory.
Only focused tests supply a controlled device.

The Windows target uses **NAudio.Wasapi 3.1.0**, official release `v3.1.0`,
package repository commit `0aaef29d04bec9567bdf2f669036fabecc33a2e2`.
The lock resolves NAudio.Core **3.1.0** and System.Numerics.Tensors **9.0.0**.
Only NAudio.Wasapi is a new central direct pin. Existing foundation test pins
are unchanged; project lockfiles retain direct/transitive content hashes.
The NAudio umbrella, capture APIs, ASIO, WinMM, codecs, VAD and native third-party
decoder packages are not selected. See [audio dependency notices](AUDIO_NOTICES.md).

## API and integration

```csharp
using Martlet.Audio;
using Martlet.Audio.Windows;
using Martlet.Core.Audio;
using Martlet.Core.Contracts;

await using var sink = new PcmPlaybackSink(new WasapiDeviceFactory());
var request = new PlaybackRequest(
    ids, epoch, format,
    new OutputSelection(OutputPolicy.FixedEndpoint, selectedEndpointId),
    DateTimeOffset.UtcNow.AddSeconds(60));
var run = sink.Start(request, cancellationToken);
var device = await run.Ready; // null if the attempt ended before binding
// Feed existing validated Core PcmFrame objects; no WAV/header/compressed bytes.
var acceptance = run.Submit(frame);
// After all frames, the producer must provide the exact per-channel sample count.
run.CompleteInput(finalSampleCount);
var terminal = await run.Completion;
```

`ids`, `epoch`, `format`, `selectedEndpointId`, `frame` and `finalSampleCount`
above come from the owning orchestration layer; this snippet is not an
automatic speaker smoke. `Start` schedules a worker and returns immediately.
`Ready`, `Completion`, `StopAsync`, `ReplaceAsync` and `DisposeAsync` must be
awaited, not synchronously waited on a UI thread.

| Surface | Meaning |
| --- | --- |
| `PcmPlaybackSink.Start(request, token)` | Explicitly activate one output and reserve a fresh epoch. Reject an already-active or unreleased worker. |
| `PlaybackRun.Submit(PcmFrame)` | Nonblocking admission: accepted, exact duplicate discarded, or stale discarded. Invalid ordering/format or overflow throws a sanitized Core `ContractException` and terminates the attempt. |
| `PlaybackRun.CompleteInput(finalSampleCount)` | Seal input; count must equal accepted samples. Returns true for a valid seal/repeated identical completion, false for an already invalidated/terminal attempt. No zero-length PCM sentinel. |
| `PlaybackRun.Ready` | Bound native mix format and client buffer metadata, or null if binding never completed. Binding is not audibility. |
| `PlaybackRun.Completion` / `.Snapshot` | Terminal outcome or current state with separate sample counters and safe error/action. Completion is reliable even if the event ring overflows. |
| `PlaybackRun.Events` | Bounded `ChannelReader<PlaybackEvent>`, sequence, monotonic elapsed time, timestamp, IDs/epoch, state/count snapshot, optional device-format metadata. No audio bytes, endpoint names/IDs, text or arbitrary native exceptions. |
| `sink.StopAsync()` / `run.StopAsync()` | Immediately invalidate admissions and clear the queue, then asynchronously request and await this instance's device teardown. |
| `sink.ReplaceAsync(newRequest, token)` | Stop and release the old attempt before starting the new epoch. Competing starts/replacements are rejected, never silently queued. |
| `PlaybackRun.DeviceRelease` | Await actual worker exit/teardown; returns a normalized error if native work failed. Inspect `Snapshot.DeviceReleased` too: failed native disposal quarantines this sink. This task may remain pending for a hung driver. |
| `sink.DisposeAsync()` | Invalidate future starts and perform the same bounded local Stop; idempotent. |

## Device and format policy

`FixedEndpoint` requires an endpoint ID. `DefaultAtStart` requires no ID and is
a deliberate choice to resolve the current Windows **console-role render**
default once at each explicit Start. It is **not automatic default following**.
Both modes bind an actual `MMDevice`; neither uses NAudio's automatic-routing
virtual endpoint. Mid-stream default changes do not move the stream. Endpoint
loss/invalidation stops the attempt, reports an error and requires an explicit
new epoch/selection; no headset-to-room-speaker failover or automatic replay.
The V02b Desktop selection adapter explicitly enumerates capture/render choices
without activating audio clients; it does not replace this adapter.
This minimal playback adapter polls selected-device state and observes WASAPI
operation errors. It does not change system defaults, endpoint volume, mute,
permissions, drivers, services or another audio instance.

Core is the sole input PCM contract: signed PCM16 little-endian, 16/24/44.1/48
kHz, mono/stereo, aligned nonempty frames at most 100 ms. The pump forwards
those bytes unchanged to a client initialized with precisely that format.
WASAPI shared-mode `AutoConvertPcm | SrcDefaultQuality` explicitly supplies the
Windows channel matrix/sample-rate converter to the actual engine mix format.
There is no float-as-PCM reinterpretation, guessed rate, software decoder or
silent exclusive-mode fallback. Initialization errors are explicit.
The mix format is surfaced as metadata, not a competing Core PCM type.
Accepted mix bounds are 8-192 kHz, 1-8 channels, PCM16/24/32 or float32.
Unsafe/unknown mix formats and excessive driver buffers fail closed.

The adapter requests a 50 ms shared buffer, rejects a reported client capacity
over 200 ms, and uses a maximum 100 ms scratch read. Each client has a fresh
session GUID, no cross-process grouping, and nonpersistent session settings.
All COM activation, padding queries, buffer leases, Start/Stop/Reset and release
stay on one long-running non-UI MTA worker. The loop polls at up to 10 ms;
there are no COM callbacks, user handlers on the audio thread, thread joins on
the UI, callback-driven reopen, or unbounded `BufferedWaveProvider`.

## Sequencing, limits and stopping

Frames start at sequence **0**, sample offset **0** per attempt. Offsets count
samples per channel, not interleaved values or bytes. Every accepted frame
must match all correlation IDs, the active epoch and unchanged `PcmFormat`,
then advance both sequence and sample offset contiguously.

An exact replay of a retained sequence/offset/length/SHA-256 fingerprint is
discarded, including after those samples leave the device buffer. Conflicting
duplicates, gaps, overlap, future epochs or foreign IDs fail the attempt.
Fingerprint history is bounded to the latest 256 accepted frames; older
sequences are rejected, never accepted/replayed. Older-epoch frames, stopped
run handles and terminal run handles return `StaleDiscarded`. Epochs are
strictly increasing across all starts on one sink, including normal completion,
Stop and replacement. An exhausted Core epoch range needs a new owning
session, not wraparound.

| Policy | Bound and behavior |
| --- | --- |
| Admitted, not device-consumed audio | Default/hard maximum 5 seconds: `AcceptedSamples - DeviceConsumedSamples`. This includes queued, partially read, in-flight write and endpoint-buffer samples. Overflow fails with `PayloadTooLarge`; producers never block awaiting queue space. |
| Frame objects | At most 256 queued frames, independently of byte/sample capacity. The pump retains at most one bounded scratch read and at most one partially read queue head. |
| Memory | At most the admitted PCM bound plus a 100 ms scratch buffer and 256 small hash fingerprints. Caller-owned frame copies outside the sink are the caller's responsibility. |
| Total attempt | At most 90 seconds of source samples and 4,320,000 samples/channel, whichever is smaller; required deadline at most 90 seconds from Start. |
| Initial prebuffer | 150 ms by default. A valid completed short stream flushes a smaller prebuffer; a valid empty completion never starts the device. |
| First audio | At most 20 seconds from Start, including open/prebuffer. |
| Underrun | An empty source with zero endpoint padding after Start emits `Underrun`; resume only with contiguous current-epoch samples, within 1 second. No inserted source PCM silence or replay. The OS can output silence during starvation. Recovery expiry is `StreamTruncated`, not completion. |
| Local stop/cleanup | Queue invalidation is immediate; supervisor cancellation is observed independently of blocked native calls. Native teardown is awaited for at most 2 seconds by default. This is **not a measured 250 ms physical Stop guarantee**. |
| Stuck/failing native teardown | Terminal `AudioPlaybackFailed`, `DeviceReleased=false`; the worker alone retains ownership until it exits. No cross-thread COM disposal or new Start is allowed while unreleased. A late native return cannot start playback or accept more source data. Already in-flight driver work cannot be retracted honestly. |
| Event retention | 128 metadata events, drop oldest, cumulative `DroppedEvents`, monotonically increasing event sequence. Consumers detect loss and obtain a fresh Snapshot/Completion. Terminal task is not dropped. |

`PlaybackOptions` can lower these bounds; capacity must be at least 100 ms,
prebuffer must fit it, and all timeout values must be positive. Timeout tests
inject `TimeProvider` rather than treating scheduler timing as hardware evidence.
Cancellation means local admission invalidation, local queue discard and a
request to stop/reset this client. It makes no upstream request-abort or compute
cancellation claim.

## Timing and error truthfulness

`AcceptedSamples` is admission; `ReadSamples` is removed from the source queue;
`SubmittedSamples` is a successful endpoint-buffer commit;
`DeviceConsumedSamples` is committed samples minus WASAPI client padding.
Partial writes preserve the exact remainder without double-counting.
Every unit is source-format samples per channel, even with Windows resampling.

Device-consumed is an endpoint/engine progress observation, **not proof that a
human heard those samples**: hardware/Bluetooth/acoustic tails and mute/volume
remain outside this accounting. `AudibleSamples` is always null.
`Completed` requires sealed input, exact final count, all samples device-consumed,
observed zero padding and successful teardown. `DeviceDrainObserved` is kept
separate from terminal success. Cancellation/loss after any read is conservatively
`MayHavePlayed`, even when an interrupted write never returned a commit count.
A timed-out terminal snapshot stays frozen; late driver work cannot inflate it
into success. Avatar integration may use device progress only with these
qualifications and must reject stale epochs; queued text/audio is not lip sync.

Errors use the existing `MartletError`/`ContractException` shape and `Stage.Playback`.
The approved additive Core members are `AudioDeviceUnavailable`, `AudioDeviceLost`,
`AudioFormatUnsupported`, `AudioPlaybackFailed`, `DeadlineExceeded`; existing
`InvalidContract`, `PayloadTooLarge`, `StreamTruncated` retain their meanings.
`PlaybackErrors.Create` provides fixed user-safe summaries and `audio.*` actions.
Retry is never automatic or marked safe after partial playback. Unknown native
failures are normalized at the device task boundary rather than silently ignored
or logged with raw messages.

## Safe smoke helper and remaining gates

The Windows-only `SpeakerSmoke.RunAsync()` defaults to
`PlaybackRequested=false`, no result, and **no device access**. It is not wired
to launch or diagnostics. Its `SyntheticTone` generator is shared with the
explicit fixture action, rather than duplicated. With separate physical-audio
permission, a developer may call:

```csharp
var smoke = await SpeakerSmoke.RunAsync(
    new OutputSelection(OutputPolicy.FixedEndpoint, approvedEndpointId),
    allowPhysicalPlayback: true, cancellationToken);
```

This opt-in helper generates a bounded 200 ms, low-amplitude faded 440 Hz tone
in memory, with a five-second attempt deadline. It returns real sink accounting,
not a fake backend pass or an audibility verdict. It does not enumerate output
choices or change any system setting. Inspect the endpoint, volume and audience
before explicitly permitting playback. No physical playback was authorized
or performed for this implementation.

Tests exercise the production sink/pump through the injectable device boundary,
including all eight Core rate/channel combinations, byte identity, partial writes,
capacity/frame bounds, duplicate/conflicting/out-of-order input, replacement,
blocked open/write, cancellation/disposal, underrun/deadlines, late worker return,
device loss, cleanup failures, bounded events and canonical Core error JSON.
The Windows target also builds the real adapter and tests construction/default
smoke without device access, including single-worker/MTA ownership assertions.
These are headless tests, not device qualification.

Existing Foundation commands include both audio TFMs through `Martlet.slnx`;
no workflow or gate was weakened:

```powershell
dotnet restore Martlet.slnx --locked-mode
dotnet build Martlet.slnx --no-restore -c Release
dotnet test Martlet.slnx --no-build -c Release
.\scripts\Smoke-Doctor.ps1
```

Portable-only: `dotnet test tests\Martlet.Audio.Tests -f net10.0 -c Release`.
Windows adapter plus headless cases:
`dotnet test tests\Martlet.Audio.Tests -f net10.0-windows -c Release`.

Local developer evidence (2026-09-12, SDK 10.0.401): locked full-solution restore
and Release build passed with no warnings/errors; 51 audio cases passed on
each TFM, plus 74 Core and 20 Doctor cases (196 total); the existing Doctor
smoke passed. CI-mode build/test artifacts were placed in a session-local
`C:` directory using `--artifacts-path` after an independently reproduced
testhost startup issue with byte-identical `D:` outputs. No test/SDK pins,
runner, workflow or safeguard were changed for that host-specific issue.
The focused audio cases also passed again after worker-thread assertions.

**Not run / not passed:** actual audibility, physical 250 ms Stop, device
unplug/default-change/Bluetooth/format-churn matrix, clean Windows installation,
denied-egress qualification, acoustic-tail measurement and F03c fixture-to-device
integration. F03b's actual-speaker acceptance and G1/G2 remain pending witness.

Sources: [NAudio 3.1.0 release](https://github.com/naudio/NAudio/releases/tag/v3.1.0),
[package metadata](https://api.nuget.org/v3-flatcontainer/naudio.wasapi/3.1.0/naudio.wasapi.nuspec),
[AudioClient API at pinned tag](https://github.com/naudio/NAudio/blob/v3.1.0/src/NAudio.Wasapi/CoreAudioApi/AudioClient.cs),
[Microsoft shared-mode conversion flags](https://learn.microsoft.com/en-us/windows/win32/coreaudio/audclnt-streamflags-xxx-constants).
