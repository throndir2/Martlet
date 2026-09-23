# Audio2Face standalone adapter

This is Martlet's first rich speech-to-face backend: a real protobuf/gRPC client
for an **already provisioned NVIDIA Audio2Face-3D NIM v2 service** on an explicitly
configured numeric loopback address. It is not a native Audio2Face SDK bridge.
It does not install, launch, discover, health-probe, download, authenticate to, or
silently replace a service. Construction and `InspectPrerequisites()` are passive.
Normal Desktop/Conversation/renderer wiring is a later integration step; this
module does not change application defaults or activate itself.

## Runtime prerequisites and evidence

The operator must independently provision and verify the NIM release, its NVIDIA
GPU/driver/container requirements, model artifacts, and applicable product/model
terms before authorizing a request. The MIT license on the separate
[Audio2Face-3D-SDK](https://github.com/NVIDIA/Audio2Face-3D-SDK) repository does
**not** license NIM or its weights. No weights, proprietary runtimes, credentials,
or license acceptance are included here. The NIM overview also documents usage
restrictions; this client neither exposes nor uses inferred emotion metadata.

`InspectPrerequisites()` explicitly reports `RuntimeVerified = false`. Its
configuration result means only that the local options passed validation, not
that a port is open, a model is loaded, a license is granted, or inference works.
The local tests use a Kestrel HTTP/2 fixture implementing the generated upstream
service. They establish protocol behavior, not NVIDIA GPU execution, animation
quality, latency, renderer appearance, or end-to-end application integration.

## Live generated-speech ingress

The live path must not delay, pause or fail voice to wait for animation. Use
`GeneratedSpeechStream`, not a buffered full clip, for the generated PCM tee:

```csharp
using var input = new GeneratedSpeechStream(
    ids, playbackEpoch, pcmFormat, firstSequence, originalSampleOffset,
    maxSamples, queueCapacity: 16);
using var permission = new Audio2FaceAuthorization(
    input, options, DateTimeOffset.UtcNow.AddSeconds(90),
    allowGeneratedSpeechAnalysis: true);
// Run this consumer independently of the voice producer.
await foreach (var frame in adapter.AnimateAsync(input, permission, cancellationToken))
{
    // Queue validated frames against the actual playback sample clock.
}
```

The caller obtains permission before starting this consumer; the example is
not itself a consent flow. `TrySubmit(PcmFrame)` on the voice producer never
waits for queue capacity. It returns `SpeechIngressResult.Accepted` or the
explicit `Closed`, `InvalidFrame`, `LimitExceeded`, or `QueueFull` result.
Invalid/discontinuous input, exceeded bounds and capacity exhaustion fail the
animation segment, clear pending ingress and cancel its RPC. They never drop
audio covertly to make the animation appear successful. The voice producer
must report the animation failure, stop submitting for that segment, and
continue its normal playback independently. There is no engine fallback.

`CompleteInput(finalSampleCount)` requires a nonempty stream and the exact
accepted sample count, relative to the original start offset. It returns
`Completed` on success or an explicit rejection; closing without that count
cannot succeed. No subsequent PCM is accepted. `Dispose()` cancels an unfinished
stream, and is idempotent. `Failure` exposes a typed animation failure when
ingress rejected data. `SampleCount` is the accepted original-rate sample count.
The bounded channel may be prefilled before enumeration to avoid a startup
race, but no network is touched until the one-shot authorization is consumed.

The immutable stream identity binds original correlation IDs, playback epoch,
mono PCM format, first sequence, original start offset, maximum samples and
queue capacity. Maximum samples cannot exceed 90 seconds at the original rate;
capacity is 1..128 frames, default 16, with a separate 10,000-input-frame ceiling.
Use the *mapped playback epoch*, not a different provider epoch. Authorization
binds the exact stream instance and endpoint/options; it cannot authorize a
different stream with equal-looking fields. The stream can be activated once.
This authorizes bounded future generated audio, not a hash of unknown future
samples and never microphone capture.

The actual gRPC input writer and output reader run concurrently. Frames can
arrive while ingress is still open. Output time codes may refer only to samples
already accepted, never to the future maximum allowance, and after completion
remain bounded by the explicit final count. A service EOF while input remains
open is a protocol failure. Input/output/transport failures propagate to the
independent animation consumer; callers must invalidate its pending frames and
display the failure without coupling it to voice.

`InspectPrerequisites()` is not runtime readiness. An application can remain
"armed, awaiting generated speech" until the first valid frame from an explicitly
authorized action demonstrates operation-local service response. Advertise only
that frame's actual blendshape keys as observed capabilities, not all possible
52 schema channels; this backend never emits semantic channels.

## Buffered clip activation (comparison/offline path)

1. Construct `Audio2FaceOptions` with an exact address, for example
   `new Uri("http://127.0.0.1:52000")`. This example is not a discovered/default
   destination. Only HTTP numeric loopback endpoints without path, query,
   fragment, or user information are accepted. DNS names, LAN, cloud, redirects,
   proxy routing, ambient HTTP credentials and cookies are not used.
2. Construct `GeneratedSpeechClip` from **clean synthesized TTS** `PcmFrame`s.
   It copies a contiguous mono signed-16 little-endian segment (maximum 90 seconds
   and 10,000 frames), with one correlation identity, epoch and format.
   The first sequence and original sample offset may be nonzero.
   This type is an ownership boundary, not an acoustic microphone detector:
   the owner must never feed captured microphone audio into it.
3. After obtaining permission for this exact clip/destination/options/audio
   analysis action, construct `Audio2FaceAuthorization` with
   `allowGeneratedSpeechAnalysis: true` and a UTC expiration within 120 seconds.
   A different clip instance, endpoint or limit set cannot reuse the grant.
   A grant is one-shot, revocable, and checked against both wall and monotonic
   elapsed time. It is consumed at first enumeration, not enumerable creation.
4. Enumerate `Audio2FaceAdapter.AnimateAsync(clip, authorization, token)`.
   Dispose the enumerator and authorization when finished. Exceptions terminate
   the operation; there is no fallback backend or success-shaped error result.

The bounded clip is prepared before transmission and is useful for repeatable
comparison or offline use. Do not gate normal voice playback on this method;
use the bounded streaming ingress above for the live tee. The upstream RPC is
bidirectional streaming: input uploads and output reads run concurrently.
`Conversation` remains the sole audio playback owner. Echoed service audio is
counted against response limits and discarded, never replayed or returned.

## Exact protocol and clock

The generated client calls
`/nvidia_ace.services.a2f_controller.v1.A2FControllerService/ProcessAudioStream`.
It sends `controller.v1.AudioStreamHeader`, one or more `AudioWithEmotion`
messages containing only PCM, then the explicit `EndOfAudio` marker and request
half-close. The header requests `enable_clamping_bs_weight = true`; this is an
upstream documented option, not local clipping of invalid coefficients.

Supported original sample rates are 16,000, 24,000, 44,100 and 48,000 Hz. The
[official NIM v2 sample documentation](https://docs.nvidia.com/ace/audio2face-3d-microservice/latest/text/interacting/sample-app.html)
documents internal downsampling to the optimal 16 kHz. The client therefore
transmits the original sample rate and PCM without a second resampler. Returned
relative seconds are converted by flooring `time_code * originalSampleRate`,
then adding the clip's original `SampleOffset`. Service wall-clock epochs are
not used as playback clocks. Output retains original IDs/epoch/rate and has its
own increasing sequence. Times must be finite, strictly increasing, within the
clip's duration (or accepted streaming sample count), and map to strictly
increasing original sample offsets.

Output is shared `Martlet.Avatars.AvatarFrame` v1.0, `SourceId` =
`nvidia-audio2face`. Header-indexed PascalCase ARKit names are mapped exactly to
the shared 52 camelCase names; only names actually supplied are emitted.
Semantic channels are empty; no emotional-state classifications are exported.
Every emitted weight must be finite and in `[0,1]`; invalid values are rejected,
not clamped. Duplicate/unknown names and cardinality mismatches fail.

Explicitly excluded upstream channels unsupported by the shared face v1 schema:
`HeadRoll`, `HeadPitch`, `HeadYaw`, `TongueTipUp`, `TongueTipDown`,
`TongueTipLeft`, `TongueTipRight`, `TongueRollUp`, `TongueRollDown`,
`TongueRollLeft`, `TongueRollRight`, `TongueUp`, `TongueDown`, `TongueLeft`,
`TongueRight`, `TongueIn`, `TongueStretch`, `TongueWide`, `TongueNarrow`.
They are not renamed to unrelated face channels. Their values must still be
finite. `TongueOut` is part of the shared 52 and is retained. Joint/camera tracks,
emotion metadata and echoed audio are deliberately not output. **Facial
animation is not full-body gesture generation.**

The header must precede data, at least one face frame is required, and successful
completion requires a final upstream SUCCESS status followed by successful gRPC
EOF. Intermediate statuses are supported as documented. ERROR and WARNING fail
explicitly; event markers do not imply success. No upstream free-text error,
audio, metadata or credential is logged or placed in adapter exception messages.
Previously yielded frames are partial if enumeration later fails; the caller
must invalidate pending frames on cancellation/epoch changes and must not treat
partial output as completed.

## Bounds and cleanup

Hard ceilings: 90-second request, 20-second default read/write idle timeout,
1 MiB per received protobuf message, 64 MiB aggregate decoded protobuf bytes,
20,000 messages, 10,800 output frames, and 128 header names. Options may reduce
frame/byte/deadline ceilings. Response reads advance with consumer demand rather
than feeding an unbounded queue. One received message may be decoded ahead;
HTTP/2 also has bounded transport buffering. The request deadline remains active
while a consumer is paused, and no queued frame is yielded after cancellation.
The RPC and producer are canceled on early disposal, failure or cancellation,
with a two-second producer cleanup bound.

Cancellation and revocation surface cancellation exceptions; protocol,
upstream, transport, limit and deadline failures are explicit
`Audio2FaceException` categories, including `InvalidInput` and `Backpressure`
for live ingress failures. Callers must always dispose async enumerators; abandoned enumerators cannot
provide deterministic managed cleanup.

## Local validation

With the repository-pinned .NET 10 SDK and shared `Martlet.Avatars` project:

```powershell
dotnet test tests\Martlet.Avatar.Audio2Face.Tests\Martlet.Avatar.Audio2Face.Tests.csproj
```

Tests use loopback fixtures only. They do not contact an installed NIM service,
use a GPU, install containers, fetch models, or run hosted CI.

## Sources and redistribution

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and `Protos`.
The exact NVIDIA `.proto` files are pinned, not reconstructed from a JSON
example. NVIDIA documents that NIM v2 continues using `nvidia_ace` 1.2.
Shared Martlet frame v1.0 and upstream protocol versions are separate contracts.
