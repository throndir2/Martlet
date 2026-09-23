# F5 worker and reference-voice foundation

**H04a/H04b software foundation only; no model, GPU, network, or audio
qualification.** `Martlet.F5` is an isolated .NET 10 library with a dedicated
test project. It is intentionally outside `Martlet.slnx`, Desktop, Core
settings, packaging, and the existing provider graph. Nothing in this slice
installs or starts Python, F5-TTS, weights, CUDA, Docker, services, a gateway,
or an audio device. The deterministic worker is **FIXTURE - NOT AI**.

## Reuse and installation boundary

This slice is a focused mechanical import of
`b46e49be729977411a8c73bc14baeaeb2d8a2ceb` ("Add isolated F5 voice foundation"),
limited to `src/Martlet.F5`, `tests/Martlet.F5.Tests`, and this document.
It does not import the old branch's ancestry, later Python worker, or final
Gateway/Desktop settings integration. The original central-document changes
are intentionally excluded; the current installation plan remains authoritative.
Independent review of the imported slice also led to two store-lifecycle fixes:
disposal retains exclusive ownership through snapshot commit/cleanup, and applied
selection is revalidated atomically with lease reservation. Barrier-controlled
regressions exercise concurrent disposal and both Apply/acquisition orderings.

The [installation requirements](INSTALLATION_SUPPORT.md), upstream findings
[S30-S35](RESEARCH.md#s30), and
[pinned artifact catalog](../src/Martlet.HostArtifacts/README.md) are separate
from these typed contracts. The current F5 GHCR source/build closure and locked
runtime/native/transitive dependency closure remain **unresolved**. A pinned
image inventory is not a reproducible build, approved runtime, or deployable
reference preset. This library neither changes candidate metadata nor enables
any catalog candidate.

The noncommercial pretrained-weight card is not approval for an intended use
and is not reference-voice rights approval. Explicit destination-bound user
acknowledgment preserves the consent boundary but does not replace either gate.
TTS remains optional: typed text, API TTS, or no TTS do not require F5. App/setup
launch must not activate presets, preview, synthesis, or worker installation.

## Delivered boundary

The native contract ID is `martlet.f5.worker`, version `1.0`. It is a Martlet
worker contract, **not** a generic OpenAI-compatible endpoint or claim.
`IF5WorkerTransport` is injectable so a later H03-authenticated transport can
map the typed messages without changing reference, authorization, or stream
validation rules.

Each request fixes:

- session, turn, request, and action IDs; exact destination; UTC deadline;
- exact worker/build, F5 source/package, Python, Torch, torchaudio, and CUDA
  runtime versions;
- five exact runtime/model/vocabulary/vocoder artifact identities, each with
  immutable revision, SHA-256, byte count, and license ID;
- one or more ordered bounded text chunks;
- the selected preset/revision, exact snapshotted WAV bytes and SHA-256, the
  user-supplied matching transcript and transcript SHA-256 revision; and
- a non-configurable execution policy that forbids automatic transcription,
  artifact download, default voice, model fallback, and provider fallback.

Worker events echo the request IDs, reference revision, and exact worker
identity. PCM frames are mono 24 kHz signed 16-bit little-endian with explicit
event sequence, frame sequence, chunk index, sample offset, sample count, and
aligned bytes. Frames are contiguous and bounded to 4,800 samples (200 ms);
the whole request is bounded to 90 seconds. Every successful chunk and stream
has an explicit completion record and final sample count. EOF without a
terminal response is `StreamTruncated`; duplicate, skipped, out-of-order, or
misaligned frames fail closed.

This is truthful transport streaming only. The inspected F5 path completes
sampling and vocoding for a text chunk before yielding slices, so
`IncrementalWithinChunkSynthesis` is always false. Cancellation immediately
discards local output and sends an idempotent cancel request, but separately
reports `DiscardOnly`, `RequestAbort`, or `CooperativeComputeCancel`.
`WorkerMayContinue` stays true unless cooperative compute cancellation is
actually acknowledged. A local Stop therefore never claims that GPU work
stopped.

## Reference preset lifecycle

`F5ReferencePresetStore` owns a selected local directory and a strict
schema-v1 manifest. It supports up to 16 named presets, 16 snapshots per
preset, and 64 snapshots total. Snapshotting and applying are explicit;
neither action synthesizes, uploads, plays audio, downloads a model, or starts
a worker.

Accepted reference files are:

| Property | Bound |
| --- | --- |
| Path | Fully qualified local fixed/removable/RAM path, at most 1,024 characters; no UNC, alternate data stream, directory, or reparse/link ancestor |
| Container | Readable RIFF/WAVE with one PCM format and one aligned data chunk |
| Samples | Mono signed PCM16 at 16, 22.05, 24, 44.1, or 48 kHz |
| Duration | 1,000 through 30,000 ms |
| File size | At most 4 MiB |
| Content | At least one nonzero sample |
| Transcript | User-supplied/reviewed, nonblank, at most 4,096 characters and 8 KiB UTF-8 |

The store copies the exact WAV bytes into its own generated path and records
the audio digest, transcript digest, combined reference revision, source path,
format, creation time, and a versioned rights acknowledgment bound to the
exact processing destination. It does not infer that the user owns a voice.
The acknowledgment records the user's assertion; legal/product review and
the actual subject's permission remain external gates.

Every Apply and every later acquisition rereads and validates the original
source. Missing, malformed, or same-path replacement blocks use. A changed
file must be snapshotted and explicitly applied as a new reference revision;
the old immutable snapshot remains available for deliberate re-selection.
Requests use only the acquired snapshot bytes, so editing a file cannot mutate
an active turn. Active synthesis/preview leases block Apply until cleanup.

Apply uses a revision-bound, one-use authorization. Preview has a different
one-use authorization type and can target an unapplied snapshot; creating a
preview request has no side effect. The adapter invalidates the prior/current
conditioning-cache revisions only as part of the next authorized synthesis or
preview, before any reference bytes are dispatched. A missing or malformed
invalidation acknowledgment blocks dispatch. Cache keys include the combined
audio/transcript revision, so an old conditioning entry cannot satisfy a new
reference.

The store uses generated relative audio paths, bounded strict JSON, duplicate
property rejection, exclusive ownership, staged writes, flush-before-replace,
and exact digest checks when loading stored bytes. Public string formatting
omits text, paths, and audio. No reference content belongs in ordinary logs or
support bundles.

## Deterministic evidence

`DeterministicF5WorkerTransport` produces bounded deterministic synthetic PCM
bytes in memory and truthfully identifies itself as `SyntheticFixture`. It
does not import or emulate F5 model behavior and must never be used as voice
quality evidence. Configurable fixture cases cover completion, worker failure,
truncation, cancellation, late frames, and conditioning-cache lifecycle.

Run only the isolated project with the SDK pinned by `global.json`:

```powershell
$artifacts = 'C:\YOUR-PRIVATE-DEVELOPMENT-DIRECTORY\f5-artifacts'
$env:CI = 'true'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
dotnet restore tests\Martlet.F5.Tests\Martlet.F5.Tests.csproj --locked-mode --artifacts-path $artifacts
dotnet build tests\Martlet.F5.Tests\Martlet.F5.Tests.csproj -c Release --no-restore --artifacts-path $artifacts
dotnet test tests\Martlet.F5.Tests\Martlet.F5.Tests.csproj -c Release --no-build --no-restore --artifacts-path $artifacts
```

The suite uses generated local PCM fixtures only. It covers strict
format/duration/size/path/text/identity bounds, rights and authorization,
atomic snapshot persistence, stale and missing sources, same-path replacement,
transcript revisions, mid-turn Apply exclusion, exact worker identity,
contiguous PCM, truncation, cancellation, late-frame discard, cache
invalidation, and preview separation. It opens no network or audio device.

## Gates still not run

This foundation does **not** complete H04, AC-19, H06, or G3. Remaining gates
include:

- a selected, fully locked Python/F5/PyTorch/torchaudio/CUDA worker image and
  locally verified hashes for every runtime/model/vocabulary/vocoder artifact;
- approved F5 weight usage and attribution plus case-specific reference-voice
  rights; a user's acknowledgment is not legal clearance;
- H03 paired TLS/authentication and a production transport implementation;
- actual model loading, reference conditioning, A/B voice difference,
  intelligibility/listening review, clipping and inter-sentence behavior;
- real GPU compatibility, warm/cold TTFA, throughput, VRAM/RAM/disk, combined
  LLM/F5 load, OOM recovery, and whether cancellation stops compute;
- real playback/device/Stop evidence and separately qualified live VAD,
  feedback protection, and speech-interruption behavior; and
- Desktop picker/settings integration, lifecycle UI, support remedies,
  packaging, Ubuntu service operation, and clean-host qualification.

Unavailable model/GPU/audio/rights evidence remains **NOT RUN**, never inferred
from the fake worker or managed tests.
