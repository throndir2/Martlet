# Avatar guide: Live2D, VRM and speech animation

**Accepted direction, 2026-09-23; standalone implementations exist, Desktop
integration in development, not end-user support.** Both Live2D and VRM are required renderer targets. Audio2Face is
the first implementation priority and preferred speech-animation backend.
The coordinator has locally integrated the standalone compatibility, Audio2Face,
Live2D and VRM modules plus initial clock/PCM-observation code. Local controlled
checks do not establish a usable normal Desktop renderer or qualified physical
synchronization. Standalone module findings and the known clock-refill defect
are corrected and reviewed; native qualification remains NOT RUN.
This guide describes the intended experience and its remaining
gates, not instructions to turn on an available feature.

Avatars remain **OFF until the user enables them**. No-avatar voice/text is a
normal supported product path, not a fallback error. Development authorization
is not permission to download models/SDKs, accept licenses, install hosts or
drivers, run GPU inference, spend money, publish or run remote CI. Live2D
development may proceed now; its release/Expandable Application classification
and artist asset rights remain separate unresolved gates.

## 1. Choose a renderer, analyzer and feature owners separately

A renderer draws a model. An analyzer derives animation from speech. A mapping
converts that output into parameters the particular model actually has.
Motion sources supply other behavior. Selecting one does not select all four.

**Recommended enabled preset:** Audio2Face speech animation plus a suitable
detailed VRM face with an authored, calibrated ARKit mapping gives the richest
intended facial output. Live2D is a fully supported **renderer target**, not a
second-class fallback: select an approved reduced mapping to its authored
parameters. Neither an arbitrary `.vrm` nor a Cubism model is guaranteed to
have ARKit shapes. Audio2Face produces facial animation, not whole-body gestures.
If the preferred backend is unavailable, report why and keep voice usable;
never silently select amplitude, another model or a paid/cloud service.

| Choice | Role and constraints |
| --- | --- |
| Live2D | Cubism model parameters, motions and physics. Lip-sync groups identify parameters, not a universal viseme rig. Model-specific ranges and mouth/expression mapping are required. |
| VRM | Humanoid model, expressions, look-at and spring-bone capabilities depend on the actual model/version. Basic `aa`, `ih`, `ou`, `ee`, `oh`, blink/emotion expressions are all optional. Detailed ARKit shapes/custom expressions must be authored and mapped. |
| Audio2Face | Preferred speech-to-face source. First lane: explicitly user-provisioned, already-running literal-loopback Audio2Face-3D NIM v2 service and official bidirectional `ProcessAudioStream` gRPC contract (v2 reuses `nvidia_ace` v1.2). Not a native SDK bridge or bundled NIM installation. |
| Amplitude | Supported baseline target for mouth opening from outgoing PCM. Less articulation than phonemes/visemes; still requires an existing mapped mouth parameter. Explicit selection only, never automatic fallback. |
| Procedural/clip motion | Separate blink, idle, breathing, gaze, head, body and secondary-motion sources, subject to supported channels and model capability. Not inferred from Audio2Face availability. |

Primary-source constraints are recorded in [Research S36-S41](RESEARCH.md#s36);
[Architecture](ARCHITECTURE.md#avatar-boundaries-accepted-direction-2026-09-23)
owns the system boundaries.

## 2. Current slice versus the complete product

| Deliverable | Current scope / evidence limit |
| --- | --- |
| Shared contracts, A01a | Implemented standalone [Martlet.Avatars contracts](../contracts/avatars/README.md), locally integrated/reviewed with production-path checks. Strict v1 facial frames use Core correlation IDs, epoch/sequence and **original PCM** sample rate/offset; exact 52-name ARKit catalog plus separate typed semantic vowels/blinks/emotions. `aa` is not an ARKit name. No skeleton or general pose/body wire payload. |
| Audio2Face, A01b | Implemented/reviewed standalone [NIM gRPC adapter](../src/Martlet.Avatar.Audio2Face/README.md), including bounded nonblocking live `GeneratedSpeechStream` ingress and separate buffered comparison/offline clips. Actual HTTP/2 fixtures exercise protocol behavior, not a normal Desktop animation route. Speech never waits for analysis. Requires separately provisioned NIM service, permitted models and supported NVIDIA GPU/runtime; no automatic probe, installation or measured GPU/model result. SDK MIT does not license NIM or weights. |
| Live2D, A02a | Implemented/reviewed standalone [importer/mapping/renderer adapter](../src/Martlet.Avatar.Live2D/README.md), with local checks. Targets official Cubism Web Framework 5-r.4 (`8df84780f2aa1298f3b30965cdae143e049f3c8e`) and matching host-supplied Core **05.01.0000** (`0x05010000`, not editor/SDK marketing version). Embedded shaders avoid the newer asynchronous shader lifecycle; unsupported MOC/Core versions and Cubism 5.3 blend/offscreen features are rejected, not downgraded. Motions/expressions/physics remain inactive; host-composed bounded parameter writes apply without a second mapping. No proprietary runtime/artist model acquired; full SDK linking, native parsing/GPU rendering remain NOT RUN. |
| VRM, A02b | Implemented/reviewed standalone [VRM importer/facial mapping/renderer adapter](../src/Martlet.Avatar.Vrm/README.md): Three.js 0.180.0 + three-vrm 3.5.5, conservative local self-contained VRM 1 subset. VRM 0, unsupported extensions, non-PNG textures, sparse accessors and embedded animations are rejected. Trusted local gaze/head controls are separate from A2F facial frames. Real-loader/control and bundle checks exist; review corrections are cleared. Actual graphics/artist-rig/device-sync qualification is NOT RUN. VRMA/body playback is **unsupported in the first slice**. |
| Composition/app integration, A02c | In development: opt-in `IPlaybackClockDevice`, `PlaybackRun.DeviceClock` with nullable sample offset, and bounded generated-PCM observation exist locally. The zero-padding/queued-refill invalidation defect is corrected, locally regression-checked and reviewed; physical sync remains NOT RUN. Normal WPF UI/renderer host/configuration/publish integration is not complete or usable as a normal app surface. One owner, no competing global settings migration. |
| Rich motion, A02d | Planned capability/wire extensions and procedural/clip integration for head, body, gaze and secondary motion. A facial gaze mapping may consume actual ARKit eye-look channels; that does not create general gaze/pose support. |
| Qualification, A03 | Planned local real-model/renderer/GPU/device trials, performance and lifecycle evidence; separately reviewed rights/release. No end-to-end avatar acceptance is passed by this guide. |

The [contracts guide](../contracts/avatars/README.md) owns exact APIs/schema.
This user guide does not invent competing payload fields,
numerical import limits or settings versions. The current foundation supports
explicit per-aspect source selection and validated scalar mappings, **not**
arbitrary blending. The broader composition requirements below remain work
to deliver, not permission to silently reduce the product scope.

## 3. Per-model capability and mapping checklist

Before offering an enabled preset, inspect the selected local model and bind
its capability record to an immutable asset identity, renderer/version and
mapping revision. Keep unknown facts unknown until inspected.

| Model fact | Required check and remedy |
| --- | --- |
| Identity and rights | Record author/source, model/texture/motion licenses, allowed use and import identity. Unclear rights/access blocks the affected use; user-supplied files are not automatically unrestricted. |
| Live2D rig | Inspect `.model3.json` references, lip-sync/blink groups, actual parameter IDs, minima/maxima/defaults, motions and physics. Author/calibrate missing mappings; reject nonexistent parameters. Never infer capability from a conventional parameter name alone. |
| VRM rig | Check supported VRM version, actual expression/morph bindings, binary versus continuous expressions, humanoid/look-at/spring-bone presence and expression overrides. Optional presets may be absent. Unsupported versions/features are visible, not silently stripped. |
| Detailed face | Compare every requested Audio2Face coefficient to an authored target. Record covered/omitted channels, ranges, neutral values and calibration. A reduced mouth mapping cannot be described as full ARKit fidelity. |
| Coupled controls | List every parameter/bone affected by an expression, clip or physics source. VRM `overrideMouth`, `overrideBlink` and `overrideLookAt` and Cubism motion/physics writes must be reflected in ownership. |
| Safety and cost | Enforce exact versioned import limits: archive/file bytes, expanded size/count/depth, decoded textures, geometry/parameters, motion duration and frame/queue budgets. Reject unsafe input before activation; do not guess GPU fit from file size. |

Mappings are bounded declarative data, not scripts. Calibrate neutral pose,
gain/range, smoothing and clipping with consented sample speech and a visible
preview. Replacing bytes at the same path invalidates the old capability and
mapping evidence. Saving a mapping is not evidence of rendering quality or
runtime readiness.

## 4. Feature ownership and deliberate partial combinations

| Aspect | Example owner | Conflict to prevent |
| --- | --- | --- |
| Mouth | Audio2Face, amplitude, supported provider visemes or later analyzer | Two sources opening the same mouth; expression or clip also writes jaw/lips |
| Non-mouth expression | A2F facial channels or authored emotion/blink controller | Two eyelid/brow writers; a supposedly non-mouth expression also deforms lips |
| Gaze | Mapped eye-look facial channels, later look-at or clip source | Eye expressions, eye bones and a clip independently drive the same gaze |
| Head | Later procedural or authored motion | Clip and procedural controller fight over neck/head transforms |
| Body | Later VRM humanoid/VRMA or authored Live2D motion | Two pose/parameter writers; pretending A2F generates body gestures |
| Secondary motion | Model-supported spring bones/Cubism physics | Multiple simulations or clips write the same affected target |

Default to a **single writer per conflicting channel and underlying target**.
Users may deliberately keep only selected aspects: for example A2F mouth,
authored blink and body-only idle with all clip mouth/face writes disabled.
The preview must list active sources, masks, omitted aspects, reduced mappings
and exact conflicts before activation. First-slice scalar ownership selection
is not general blending; later blends require explicit supported masks,
deterministic priorities and tested range/composition rules. Never resolve a
conflict by last callback wins, unchecked addition or a silent source switch.

An advanced resolver can disable conflicting aspects or select a **known**
alternate source/mapping with explicit approval. It cannot manufacture a
missing rig channel, reinterpret malformed schema, approve an unsafe asset,
override licensing/access, supply a missing runtime/GPU, or turn an unknown
measurement into a pass.

## 5. Permutations: works in principle is not verified working

**Compatible** describes a supported structural combination, not a witnessed
Martlet run. **Requires mapping / degraded** means a known reduction or mapping
step. **Blocked / unsupported** means a known missing requirement or absent
implementation. **Unknown / unqualified** means insufficient evidence, not
proof of incompatibility. In the contract these are represented by
`Supported`, `RequiresMapping`, `Reduced`, `Unsupported`, `Unknown` separately
from `RuntimeReadiness`. Record readiness and performance independently.

| Selection | Compatibility disposition | Concrete remedy / qualification |
| --- | --- | --- |
| Audio2Face + detailed authored VRM face | Compatible with a validated exact mapping; otherwise requires mapping | Author/calibrate target shapes and channels; verify NIM access/readiness and actual renderer/GPU playback. Richest intended face, not yet verified here. |
| Audio2Face + VRM with basic vowel expressions only | Requires mapping / degraded | Explicitly map a limited mouth subset, report omitted face detail; choose a richer authored rig for full output. Missing vowels remain unsupported. |
| Audio2Face + Live2D with approved mouth/face parameters | Requires mapping / degraded | Approve per-model reduction and masks; retain only expressible channels. Do not claim generic Cubism ARKit support. |
| Amplitude + existing Live2D mouth or VRM mouth expression | Compatible baseline after calibration | Explicitly select it and verify actual played-PCM timing; no viseme/detailed-emotion quality claim. |
| Audio2Face + amplitude both owning mouth | Blocked conflict by default | Disable one mouth writer; retain only disjoint supported aspects. Deliberate blends require a later qualified rule, not unchecked addition. |
| Audio2Face mouth + idle non-mouth/pose | Compatible in principle only with disjoint actual targets | Mask clip/expression mouth writes and respect rig overrides. First-slice facial parts only; pose/body integration remains planned. |
| VRMA + capable VRM | Format-compatible; first-slice playback unsupported | Implement A02d/version-aware retargeting, masks and local motion qualification before enabling. |
| VRMA directly + Live2D | Unsupported | Use an authored Cubism motion or implement and qualify an explicit conversion/mapping; selecting a resolver cannot make skeletal data into Live2D motion. |
| Full A2F face requested on model lacking shapes | Unsupported for missing aspects | Author rig/mapping, select a suitable model, or explicitly omit those aspects and label reduced output. |
| Compatible A2F rig, missing NIM/GPU/model/access | Structurally compatible but readiness blocked/unknown | User separately provisions/authorizes exact prerequisites; otherwise disable speech animation or deliberately choose a supported alternate. Never silent fallback. |
| Arbitrary model/version/backend or untested combination | Unknown / unqualified | Inspect capability/schema and run exact permitted local checks; label verified/unsupported only when evidence supports it. |
| Scripted/remote-reference/oversized asset or invalid frame schema | Blocked safety/schema failure | Obtain a valid bounded local asset/frame; no advanced override. |
| Disabled/missing/crashed avatar | No-avatar voice remains independent | Show animation unavailable/off, preserve conversation settings; independent restart never replays canceled speech. |

Later candidates are not promises of currently implemented options:

| Candidate | Intended lane and restrictions |
| --- | --- |
| Live2D MotionSync | Later Live2D-specific viseme candidate; authored viseme shapes plus separate MotionSync Core/framework/runtime/terms qualification. Verified Unity plugin is not proof of a ready Web integration; do not add Unity for this candidate. Not the same as amplitude lip sync. |
| `wawa-lipsync` | Later CPU/web-audio candidate for a selected browser host; model mapping and Martlet playback-clock integration required. Do not add a second audible playback path. |
| `uLipSync` | Conditional Unity-only candidate with per-character calibration/runtime or prebake. Do not introduce Unity into WPF merely to list this option. |
| Rhubarb Lip Sync | Buffered/offline mouth-cue candidate; not presumed streaming realtime analysis. |
| Provider visemes | Only a named, explicitly implemented provider/model/format with timestamp support; generic TTS audio availability does not imply visemes. |

## 6. Playback, failure and comparison behavior

Analyze only authorized outgoing speech PCM, never implicitly activate the
microphone. Analysis may run ahead within bounded queues; animation presentation
must follow **timestamped actual playback progress**. Preserve original sample
offsets through resampling and analyzer seconds-to-samples conversion. Queued
PCM, TTS receipt and wall-clock time are not evidence of audible progress.
Pause/underrun freezes speech progression; absent reliable progress is unknown,
not permission to animate ahead.

**Integration checkpoint, 2026-09-23:** opt-in `IPlaybackClockDevice` now exposes
native WASAPI clock observations, and `PlaybackRun.DeviceClock` supplies clock
state and a nullable original-PCM sample offset. The existing audio
`PlaybackSnapshot.DeviceConsumedSamples` is derived from committed samples
minus device padding; `AudibleSamples` remains null. These legacy counters
are not the presentation clock. Clock/tee code is locally integrated with
controlled checks. The known zero-padding plus queued-refill defect is corrected:
clock validity is revoked before refilling an emptied running endpoint, even
when more PCM is already queued. Its regression and review are complete,
not native-device qualification. Normal app integration and actual device/
physical synchronization remain incomplete/unqualified.
Unavailable/invalidated clock state must disable synchronized
animation, never substitute queue counters. A native clock still does not
prove physical audibility; device/listening qualification remains separate.

The Audio2Face adapter now implements bounded nonblocking live ingress;
buffered `GeneratedSpeechClip` remains a comparison/offline path, not the
normal live path. Concurrent input/output is exercised with actual HTTP/2
fixtures; that evidence is not integrated Desktop playback or NVIDIA inference.
The generated-PCM observation tee also exists locally; its app-to-analyzer
wiring remains in development. Speech never waits for analyzer initialization, buffering,
completion, backpressure or renderer readiness. If animation cannot keep up,
report/drop obsolete animation or stop that optional path within its bounds;
do not delay speech, grow an unbounded buffer or replay late facial motion.

Stop, turn replacement, output loss or avatar disable invalidates the current
epoch and queued speech frames. Discard stale session/turn/request/epoch,
sequence and mapping/model revision output without changing current state;
only trusted Stop/reset/dispose resets speech-owned channels without
overwriting another source's legitimate channels. Late callbacks, stalled
cleanup and renderer restart must never resurrect canceled speech. Report
upstream compute abort separately from local discard.

The renderer host is an independent failure boundary. Bounded IPC/import/frame
streams must reject scripts, remote asset references, traversal/links escaping
the import root, malformed/nonfinite values and oversized/expansion-bomb assets.
Neither an unavailable analyzer nor a crashed renderer may block voice, change
provider credentials or force a session-core restart.

Comparison presets keep conversation provider/model, persona, TTS voice,
sample speech, playback settings and asset identity unchanged when comparing
analyzers/mappings. Renderer comparisons necessarily use different models:
record both identities and the rig difference instead of attributing all
quality change to the analyzer. Configuration changes apply at a fresh safe
boundary; preserve previous settings and disclose selected/omitted features.
Offline replay of consented/licensed PCM avoids regenerating different speech
or incurring unapproved charges. Multi-renderer comparison must not duplicate
audible output or accidentally run additional paid/GPU jobs.

Report mapping coverage, visible artifacts, audio-to-animation offset,
frame drops, queue peaks, Stop/stale-frame outcomes, CPU/private RAM and GPU/
VRAM with exact versions, hardware, duration and sample count. Separate
compatibility, readiness, measured performance and perceived quality. Missing
measurements are **NOT RUN / unknown**, never zero latency or zero cost.

## 7. Dependency-aware delivery

```mermaid
flowchart TD
    C["A01a Shared facial contracts and compatibility"]
    A["A01b Audio2Face FIRST / preferred adapter"]
    L["A02a Live2D renderer adapter"]
    V["A02b VRM renderer adapter"]
    I["A02c Composition / config / WPF host / playback"]
    M["A02d Extended channels and motion"]
    Q["A03a Local model / renderer / GPU qualification"]
    R["A03b Rights / release qualification"]
    C --> I
    A --> I
    L --> I
    V --> I
    I --> M
    I --> Q
    M -->|"For motion claims"| Q
    Q --> R
```

A01a/A01b start early; Live2D and VRM develop in parallel against coordinated
interfaces. First-wave fixtures do not depend on licensed model/GPU access.
A02c follows the locally integrated contracts/adapters and must finish wiring
and qualifying the opt-in clock/nonblocking PCM handoff in the normal app.
A02d extends channels without claiming the v1 face frame carries
poses. A03a qualifies each advertised subset; facial-only trials need not wait
for body implementation, but full-motion claims do. Rights research may run
throughout; A03b release approval remains separate from development.

One owner integrates root solution, app, configuration/recovery and packaging.
Do not collide with installation/reconciliation settings lineage or introduce
a competing global `AppSettings` schema or authority. The coordinator approved
**one authoritative avatar-only sidecar**: a single atomic outer envelope
containing shared `AvatarConfiguration`, profile binding, resource identities
and runtime references, not a second unsynchronized paths file or global
settings version. The integration owner owns validation, atomic saves/revisions
and explicit recovery/deletion semantics. Disable preserves saved preferences
but revokes activation; no activation permission is persisted. Existing global
backup/restore excludes the sidecar with visible disclosure until that lifecycle
integration is qualified. These are approved boundaries, not a claim that
sidecar persistence/recovery is already implemented.
G2 voice reliability and G5 avatar release remain
intact; neither G2 nor G4 blocks independent avatar development. All execution
and review are local. [Delivery](DELIVERY.md#m5-optional-avatar) owns
A01-A03, AC-17 and new AC-27-AC-32; AC-18-AC-26 retain existing meanings.
