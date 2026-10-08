# F5 worker and reference-voice foundation

**H04a/H04b software foundation only; no model, GPU, network, or audio
qualification.** `Martlet.F5` is an isolated .NET 10 library with a dedicated
test project. It is intentionally outside `Martlet.slnx`, Desktop, Core
settings, packaging, and the existing provider graph. Nothing in this slice
installs or starts Python, F5-TTS, weights, CUDA, Docker, services, a gateway,
or an audio device. The deterministic worker is **FIXTURE - NOT AI**.

`Martlet.Core.Voices.PcmWaveInfo` is the strict PCM16 WAV inspector this F5
adapter uses. F5 retains its own 4 MiB, 1-30 second reference bounds and
failure codes.

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
preset, and 64 snapshots total. Snapshotting, applying and deleting are
explicit; none of them synthesizes, uploads, plays audio, downloads a model, or
starts a worker. F5 copies a voice from the reference at each request; nothing
is trained.

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

The stored copy is authoritative once snapshotted: Apply and every later
acquisition read the store's own copy and verify its digest, never the original
source, so the original may be moved, edited or deleted afterwards. A changed
recording becomes a voice only when it is snapshotted again (a new reference
revision), and the old immutable snapshot remains available. Requests use only
the acquired snapshot bytes. Active synthesis/preview leases block Apply until
cleanup. `DeleteAsync` removes a preset, its snapshots and stored copies; the
applied preset cannot be deleted, so the selection never points at a missing
voice.

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
atomic snapshot persistence, stored copies that outlive a changed or missing
original, deletion, transcript revisions, mid-turn Apply exclusion, exact
worker identity,
contiguous PCM, truncation, cancellation, late-frame discard, cache
invalidation, and preview separation. It opens no network or audio device.

## Desktop voices and playback

There are no built-in voices. A new voice list starts with six **starter
voices** that are free to use and share (`Martlet.F5.F5BundledVoices`, clips in
`src\Martlet.F5\BundledVoices`); once in the list they are ordinary voices the
owner can use, share and remove like any other. **Jenny (Dioco)** comes first
and is the default: a professional Irish voice-over artist who recorded the
Jenny TTS dataset for speech synthesis, here an 8.35 s line of Meg's from
Little Women, a natural, cheerful adult voice (a median 183 Hz). Its licence
allows any use, including commercial use, and requires the voice to be called
"Jenny (Dioco)". Next are the two **cute voices** (`Cute`): two LibriVox
readers voicing Anne Shirley, the excitable young heroine of Anne of Green
Gables, in Chapter II, as read: Annie Coleman Rothenberg (public domain, "Annie
(cute, chatty)") and WoollyBee (CC0, "Bee (cute, bubbly)"). Locally on F5 v1
Base (3 sentences, 2 seeds each) they spoke at a median 265 Hz (Annie) and
about 340 Hz (Bee) against LJ's 217 Hz, with every word recognized. Then come a
select few others: LJ Speech (public domain, female narrator) and two CMU
ARCTIC speakers (slt, US female; bdl, US male; free for any use with the notice
kept).
Earlier versions offered seven more (two LibriVox narrators and five more CMU
ARCTIC speakers); a list that already holds one keeps it as an ordinary voice.
A starter voice added in an update (Jenny) joins an existing list once
(`F5SharedVoices.WithStarters`, at revision 1 like every starter entry); one
the owner removed doesn't come back.
`BundledVoices\NOTICES.txt` lists each source file with its SHA-256, transcript
and the marked modifications, and `scripts\Build-F5BundledVoices.py` rebuilds
the clips from the pinned sources (reproducibly). Each clip is 6.5-11
seconds of speech with its exact transcript; an embedded clip is verified
against its SHA-256 before use. The default voice is
`F5BundledVoices.Default`, the first starter voice: Jenny (Dioco). A starter
entry's place in the list follows its place in `F5BundledVoices.All`, so Jenny
is also the first voice of a list from an earlier release. Owners who already
chose a voice keep it.

Some recordings are no longer shipped (`F5BundledVoices.Retired`): the F5-TTS
example clip and the two "anime" voices, the cute voices with pitch and
formants raised ("Annie (cute anime girl)", once the default, and "Bee (cute
anime girl)"), which sounded artificial. The first updated desktop removes them
from the shared list, so every computer drops them and an older copy of the
list can't bring them back. A speaking route or applied voice that still uses
one moves to the chosen or first voice, which is Jenny when the chosen voice
was removed (she then becomes the chosen voice), and this PC's copy is deleted
once nothing speaks with it.
When Speaking is first handed to a host, Desktop uses the voice chosen on all
computers, else the one applied here, else the first voice in the list the
engine can clone, with no picker. Starter recordings are snapshotted with the
`PublishedSample` rights basis when the list is first saved.

### One voice list on every computer

The list (`Martlet.Core.Voices.SpeakingVoiceLibrary`, `speaking-voices.json` in
the data directory) names every voice by its reference revision (the SHA-256 of
the recording's SHA-256 followed by the transcript's), so the same recording and
words are the same voice everywhere. Each voice and the voice chosen on all
computers is a last-writer-wins entry with hybrid revisions, like the
[cluster plan](CLUSTER.md#the-shared-speaking-voices); starter entries are
written at revision 1 by `martlet`, so every computer writes the same ones and
removing one anywhere wins everywhere. Each desktop's F5 reference store keeps its
copy of every recording (`Martlet.F5.F5SharedVoices` copies missing ones in,
deletes removed ones and adds recordings only the store had, such as voices
added before sharing). Every paired host keeps the list and every recording, so
a speaking request names its recording by SHA-256 and carries only the
transcript; the recording travels once per host, not with every reply. Nothing
is written until the owner first uses, adds or removes a voice, or the desktop
shares voices with a paired host.

Earlier versions bundled F5-TTS's English example clip (`basic_ref_en.wav`), a
male voice, and started F5 with it. Its transcript matches a line from a 2014
celebrity-narrated campaign film and upstream does not identify the speaker, so
it is no longer included. It never joins the shared list and is never chosen
automatically. When Desktop loads settings and the speaking route still records
it, Desktop applies the default voice (as above) and saves it on the route
(consent carried over as for **Use**); without an F5 speaking route, a store
whose applied voice is the retired clip applies the default instead. It then
leaves the list.

**Companion > Voice > Voices** is the voice list: every voice in the order it
joined (the starter voices first), each with **Play** (this PC's copy or the
starter clip, locally), **Use** and **Remove**, and **Add a voice...**
(recording, name, exact transcript, whose voice and the rights confirmation).
The transcript is required because F5-TTS, GPT-SoVITS and Dia read it along
with the recording (Chatterbox and XTTS-v2 ignore it), and one voice serves
every engine. Martlet fills it in when a recording is chosen, with
speech-to-text that keeps the recording among the owner's computers: whatever
Listening uses when that is Parakeet on this PC or a paired host's whisper,
otherwise a downloaded Parakeet model, the most accurate for Windows' display
language (v2 for English, otherwise v3, 110M only when it is the one
downloaded; loaded only while the dialog is open). Words the owner already
typed are kept, **Fill in the words** redoes it, and a cloud Listening route
is never used. Parakeet's v3 knows 25 European languages and the other two
only English, so other languages may need typing.
**Add another recording** adds more recordings of the same voice, each with its
exact transcript (up to 10; **Browse** can pick several at once and **Play
joined** plays them as most engines will hear them). Several recordings become
one voice: Martlet joins them after a 0.5 s pause each (30 seconds in all) and
the shared list remembers where each lies, so XTTS-v2 and GPT-SoVITS learn from
each recording and the other engines clone the joined one
([shared speaking voices](CLUSTER.md#the-shared-speaking-voices)). The list
shows such a voice as "3 recordings, 10.5 seconds joined" and whether the
speaking engine learns from each or hears them joined.
Each recording can be almost any audio or video file: Desktop's
`VoiceRecordingImport` (Martlet.Audio) decodes it on that PC (Ogg Vorbis and Ogg
Opus in managed code: NVorbis and Concentus' managed Opus decoder, since Windows
reads no Ogg in a desktop app; everything else through Windows' Media
Foundation; uncompressed WAV and AIFF through NAudio's managed readers), mixes
it to mono, resamples a rate the store doesn't accept to the next accepted one
(at most 48 kHz; Opus, which always decodes at 48 kHz, to the rate its header
says it was recorded at) and writes a mono PCM16 WAV; an acceptable WAV is kept byte for
byte. Joining and the store then work on those WAVs. Decoding stops just past 30
seconds, and a longer recording is refused rather than cut. Only WAVs reach the
store and the paired computers, which still accept nothing else, so no codec
ever runs on audio another computer sent.
**Use** applies the voice in the store, makes it the voice chosen on all
computers and, when a computer speaks, records it on the speaking route with a
refreshed selection, so the next conversation speaks with it; other desktops
follow the choice when they next share voices. The speech client reads the
exact preset/revision the route records. The voice in use (or chosen on all
computers) cannot be removed; any other can, including starter voices, and is
removed on every computer. A voice whose recording is still being copied to
this PC shows *Copying to this PC...* with **Use** off. The worker policy is
unchanged: the host's gateway hands its engine an explicit reference with every
request, and the worker never chooses a voice or keeps it. The same voice list
serves [Chatterbox Turbo](CHATTERBOX_VOICE.md), [XTTS-v2](XTTS_VOICE.md),
[GPT-SoVITS](GPT_SOVITS_VOICE.md) and [Dia](DIA_VOICE.md) (Companion > Voice >
Voice engine), whose routes use this reference contract; GPT-SoVITS adds the
recording's language and accepts only 3-10 second recordings.

## Gates still not run

This foundation does **not** complete H04, AC-22, H06, or G3. Remaining gates
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
