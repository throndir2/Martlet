# Avatar compatibility and composition contracts

`src/Martlet.Avatars` is a standalone, managed `net10.0` foundation. It depends
only on `Martlet.Core`. It does not change `AppSettings`, start a service, inspect
the filesystem, load models, install SDKs, perform inference, or attach a renderer.
Desktop integration must explicitly supply inspected model metadata, verified
source capabilities and actual audio-device playback positions.

## Frame 1.0

The shared wire authority is `AvatarJson.ReadFrame` / `WriteFrame`.
[`frame-v1.json`](frame-v1.json) is a production-tested golden envelope.
[`frame-v1.schema.json`](frame-v1.schema.json) specifies its structural shape.
Readers must also enforce duplicate JSON property rejection, unique semantic
channel keys, finite numbers, maximum depth 16 and a 16,384-byte UTF-8 limit.
JSON Schema cannot detect duplicate properties after an ordinary JSON parser has
discarded them. No unknown fields, versions, enums or channel names are ignored.
Malformed input uses existing sanitized `ContractException` errors; input values
and payloads are not included in error messages.

| Field | Meaning |
| --- | --- |
| `version` | Exactly `{ "major": 1, "minor": 0 }`; reuses Core `ContractVersion` but does not accept later minor versions silently. |
| `ids` | Core `CorrelationIds`: nonempty `session_id`, `turn_id`, `request_id` UUIDs. |
| `source_id` | 1-64 ASCII identifier characters; binds the explicitly selected source instance, not just its backend kind. |
| `epoch`, `sequence` | Integers from 0 through 2,147,483,647. Accepted sequence strictly increases per full binding; gaps are allowed. |
| `sample_rate` | Original source PCM rate: 16,000, 24,000, 44,100 or 48,000 Hz, reusing Core `PcmFormat` validation. |
| `sample_offset` | Original PCM samples **per channel**, from 0 through 9,007,199,254,740,991; exact in JavaScript. |
| `blendshapes` | Required object, at most 52 exact canonical ARKit camelCase names, each a finite coefficient in `[0, 1]`. |
| `semantics` | Required array of `{ "channel": "<exact snake_case enum>", "value": 0.5 }`, unique channels, at most 13 entries. |

At least one channel is required across both payloads. An absent channel is not
zero or neutral. Every mapped channel must appear in each applied source frame;
this is a complete selected-channel sample, **not** a sparse delta protocol.
`AvatarChannels.BlendshapeNames` exposes the immutable canonical name catalog.
`JawOpen`, `jaw_open`, `aa` and `headYaw` are not valid ARKit keys.

Semantic channels are `mouth_open`, `vowel_aa`, `vowel_ih`, `vowel_ou`, `vowel_ee`,
`vowel_oh`, `blink_left`, `blink_right`, `happy`, `angry`, `sad`, `relaxed` and
`surprised`. In particular, VRM's `aa` preset can be an explicitly mapped
`vowel_aa` target; it is never renamed into an ARKit coefficient. Avatar
semantics are bounded data, not LLM instructions or executable per-frame code.

The v1 payload has no arbitrary skeleton, pose, transform, script or asset
fields. ARKit's actual `eyeLook*` channels can represent the gaze aspect when
both source and model mapping expose them. Renderer-local procedural head,
gaze, motion and spring controls remain separate adapter APIs; their existence
does not imply Audio2Face supplies those channels.

## Configuration and inspected capability metadata

`AvatarJson.ReadConfiguration` / `WriteConfiguration` own a separate closed,
versioned document, maximum 65,536 UTF-8 bytes. This is not an `AppSettings`
schema extension. [`configuration-v1.json`](configuration-v1.json) matches
`AvatarConfiguration.Disabled`: avatars off and Audio2Face the preferred rich
backend. Reading or constructing this document has no effects.

`AvatarConfiguration` declares:

* `requested_aspects` and `omitted_aspects`: exact enum tokens `mouth`,
  `expression`, `gaze`, `head`, `body`, `secondary_motion`.
* `assignments`: `{ "aspect": "mouth", "source_id": "audio2face",
  "mapping_id": "model-mouth", "accept_reduced": false }`.
  `mapping_id` can be absent while the user is configuring; the result is
  `RequiresMapping`, never an implicit built-in mapping.
* `mapping_profiles`: `{ "id": "model-mouth", "source_id": "audio2face",
  "model_id": "selected-model", "mappings": [...] }`.
* Each mapping: `{ "source": { "blendshape": "jawOpen" },
  "target_parameter_id": "ParamMouthOpenY", "output_minimum": 0,
  "output_maximum": 1 }`. A source is **exactly one** `blendshape` name or typed
  `semantic` enum, e.g. `{ "semantic": "vowel_aa" }`.

Mappings are explicit normalized-to-authored-range affine conversions.
Inverted output bounds are supported, but both endpoints must be inside the
inspected target parameter's bounds. A profile cannot write a target twice;
arbitrary weighted blending, additive writers and aggregate many-to-one
blendshape sums are not supported. Selecting an aspect masks the profile to
that aspect only. Deliberately partial coverage requires `accept_reduced: true`.
There is no safety override, no unknown blend-policy default and no fallback
from Audio2Face to amplitude.

Capabilities are supplied as in-process facts, not inferred from `.vrm` or
`.model3.json` filenames:

* `SourceCapabilities`: `SourceId`, `Backend`, `ChannelsKnown`, `Readiness`,
  exact `Channels` (`ChannelReference` values).
* `ModelCapabilities`: `ModelId`, `Renderer` (`Live2D` or `Vrm`),
  `MetadataKnown`, `Readiness`, `Parameters`.
* `ModelParameter`: exact model-local `Id`, owning `Aspect`, finite `Minimum`,
  `Maximum`, `Neutral`. Bounds must be ordered and neutral in range.
  A caller derives these from the actual selected asset and adapter.

The source capabilities must describe actual emitted channels, not every channel
the upstream technology could theoretically provide. A model with only vowel
expressions cannot claim an ARKit full-face rig. Optional VRM expressions absent
from the asset stay absent. Live2D custom parameters need their actual SDK/model
bounds; conventional names do not establish support.

Amplitude is only a named configurable capability kind in this foundation, with
`mouth_open` its sole permitted channel. There is no amplitude inference engine
or new alternative rich engine here. Model-native semantic controls likewise
must be explicitly supplied and selected, never inferred from LLM output.

## Compatibility, readiness and composition

`CompatibilityEngine.Assess(source, model, aspect, profile)` returns independent
`Compatibility`, `SourceReadiness` and `ModelReadiness`:

| Compatibility | Interpretation |
| --- | --- |
| `Supported` | All advertised channels for this aspect are explicitly mapped to valid model parameters. This describes declared channel coverage, not photorealistic quality. |
| `RequiresMapping` | Source/model expose the aspect, but there is no selected mapping for it. |
| `Reduced` | A valid explicit mapping covers only some advertised channels for this aspect. |
| `Unsupported` | The aspect is absent or the selected mapping violates source/model binding, channels, aspect ownership or authored bounds. |
| `Unknown` | Source channel or model metadata has not been established. |

Readiness is independently `NotChecked`, `Missing`, `Installed`, `Available`,
`Disabled` or `Unavailable`. Only **both Available** permit composition.
Installed SDK bits do not establish a running usable backend, accepted terms,
available device, available model or consent. These prerequisites belong to the
adapter/host and must be reflected honestly in readiness.

`AvatarComposer.Create(configuration, model, sources)` returns a
`CompositionResult` with a nullable validated `Composition`, per-aspect
`Assessments`, structured `Issues` with stable `Code`, sanitized `Summary`,
`ActionId` and typed `Remedies`. `IsValid` indicates a complete executable
selection, not merely a compatible file type.

Every requested non-omitted aspect must have **one** compatible, ready source.
Multiple owners fail with actionable conflicts; there is no last-writer-wins
ordering. Explicit omission is valid, but cannot coexist with an assignment.
Partial mappings retain their `Reduced` diagnostic even after explicit
acceptance. Acceptance cannot bypass missing runtime prerequisites, absent
channels, wrong model binding or unsafe parameter ranges. Head/body/secondary
motion cannot be assigned from this v1 facial channel catalog.

`RecommendedSourceId` identifies a selected, validated source of the preferred
backend only when the whole plan is executable. The default preference is
Audio2Face (`audio2_face` on the configuration wire). Recommendation never
selects a provider, changes assignments, enables anything, or substitutes an
alternative when Audio2Face fails.

`AvatarComposition.Compose(frame, gate, playback)` validates advertised and
mapped frame channels, applies per-aspect mappings, then gates the result.
Its `ComposedFrame.Parameters` is an immutable dictionary of model-local scalar
targets (for example `{ "ParamMouthOpenY": 0.6 }` or `{ "aa": 0.6 }`); it is
nonempty only for an accepted due frame. This in-process result is **not a
second transport envelope**. A host bridge must preserve the original full
binding and gate disposition when passing outputs to renderer-local sinks.
The library does not execute renderer callbacks or evaluate scripts.

## Playback ownership, cancellation and resampling

`PlaybackFrameGate` takes `PlaybackBinding { Ids, SourceId, Epoch, SampleRate }`.
`PlaybackBinding.FromPcm(frame, sourceId)` reuses a validated Core `PcmFrame`.
`TryAccept(frame, position)` takes `PlaybackPosition { Ids, Epoch, SampleRate,
SampleOffset }`, where the host derives `SampleOffset` from the **actual
device-rendered** position converted into original-source samples. Token time,
transport receipt, synthesis completion and submitted PCM are not playback.
Pass `null` if a trustworthy position is unavailable; result is
`PlaybackUnavailable`, not success.

The gate enforces full correlation, source, epoch and rate identity, monotonic
accepted sequence and frame offsets, and nonregressing playback. Defaults are
250 ms maximum lateness and 1,000 ms maximum lead. Lateness is configurable
from 0 to 1,000 ms; lead from 0 to 5,000 ms. A frame at or just behind playback
within the lateness bound is accepted. A future frame yields `Future` without
consuming its sequence; excessive lead yields `TooFarAhead`. Neither disposition
queues or retains a frame. Hosts must bound their own pending-frame storage and
discard rejected/superseded frames. To avoid old expression state, stop/reset the
renderer as well as its host queue when playback ends.

`Stop()` makes every further frame return `Stopped`. `Reset(binding)` requires
new correlation or a strictly newer epoch; it clears sequencing/playback state.
Use fresh request IDs for new playback and monotonically advance epochs for
interruption/restart within a request. Old callbacks cannot revive a stopped
gate. Gate instances are serialized per source; the host owns synchronization
and must not apply accepted output after cancellation races.

If an analyzer resamples, `SampleClock.ConvertOffset(analysisOffset,
analysisSampleRate, originalSampleRate)` converts with exact integer
arithmetic and rounds down, rather than relabeling sample counts. Supported
rates reuse Core's PCM contract; the conversion also checks JavaScript-safe
offset bounds. Service timecodes likewise need conversion to original playback
samples before constructing `AvatarFrame`.

## Local checks and integration boundary

```powershell
dotnet restore tests\Martlet.Avatars.Tests\Martlet.Avatars.Tests.csproj --locked-mode
dotnet test tests\Martlet.Avatars.Tests\Martlet.Avatars.Tests.csproj --no-restore
```

Tests execute the production serializers, compatibility evaluator, composer and
playback gate. They cover exact canonical names, finite values, malformed and
duplicate JSON, byte/depth bounds, unavailable optional expressions, partial
rigs, custom parameter bounds, conflicting sources, deliberate omission,
unsupported pose requests, immutable plan capture, stop/reset, stale and late
frames, real playback positions and analysis-rate conversion.

The disabled path does not inspect source/model metadata or enumerate source
providers. Required integration remains explicitly outside this project:
Audio2Face authorization/transport and emitted capability discovery, model
inspection, renderer-local controls, Desktop settings/UI, a bounded generated-TTS
tee and queues, actual playback-clock ownership, cancellation ordering and
renderer neutral reset. No startup IO, installer, legal acceptance, GPU
inference, asset download or remote CI is performed here.
