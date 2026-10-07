# Multi-engine voice setup and comparison

**Accepted scope, 2026-09-23:** support F5-TTS, Qwen3-TTS, Chatterbox,
GPT-SoVITS and XTTS-v2, including user-supplied audio, comparison before
selection, and engine-supported fine-tuning. Research precedes implementation.
This expands the original plan's F5-only self-hosted TTS direction and removes
training/fine-tuning from the later self-hosted feature's non-goals. It does
not add training to the API MVP or authorize downloads, host changes, paid
services, publication, or use of someone else's voice.

**First implementation slice (VS01), now replaced:** the passive five-engine
Voice Library window never reached a voice route, so it was removed. The
working voice library is one list on every computer: Companion > Voice > Voices
adds recordings with their transcripts and rights, plays them, switches the
active voice in one click and removes voices, keeping Martlet's own copy of each
recording. There are no built-in voices: a new list starts with a few starter
voices that are removed like any other. The list, the chosen voice and every
recording are shared with the owner's paired Martlet computers, so a reply names
its recording instead of carrying it ([shared speaking voices](CLUSTER.md#the-shared-speaking-voices)). Other engines
add to that one list when they run: [Chatterbox Turbo](CHATTERBOX_VOICE.md) (the
default engine, with sound tags and whispering), [Chatterbox Original](CHATTERBOX_VOICE.md#chatterbox-original-general-and-expressive)
(calm or expressive sentences), [Chatterbox Nano](CHATTERBOX_VOICE.md#chatterbox-nano) (also on the CPU), [XTTS-v2](XTTS_VOICE.md),
[GPT-SoVITS](GPT_SOVITS_VOICE.md) and [Dia](DIA_VOICE.md) now do, chosen on
Companion > Voice > Voice engine (`Martlet.Core.Settings.SpeechEngines`). The existing OpenAI conversation and F5
route retain their current boundaries. [Singing](SINGING.md) sings songs in the same voices (ACE-Step 1.5 with
SoulX-Singer-SVC zero-shot singing voice conversion; Seed-VC and RVC were rejected as below).

## Research: implementation, not marketing compatibility

Primary sources below were read on **2026-09-23**. Mutable main/master URLs
record research, not immutable artifact pins. Versions observed in metadata
are not tested runtime selections. Each engine needs its own reproducible
source, model, auxiliary weights, Python/native dependencies and license
closure before VS02 can enable an installation.

| Engine / initial candidate | Inference and reference preparation | Training | Setup implications |
| --- | --- | --- | --- |
| F5-TTS / F5TTS_v1_Base | `F5TTS(ckpt_file=..., vocab_file=..., vocoder_local_path=..., device=...)`, then `infer(ref_file, ref_text, gen_text)` returns waveform/sample rate. Preprocessing may clip at 12 s, remove silence and append silence. Empty transcript invokes ASR. | Official CSV preparation (`audio_file|text` with absolute worker-local paths), Accelerate CLI and fine-tuning Gradio tools. | Python/PyTorch/torchaudio plus vocabulary/vocoder and audio decoder closure. Pass explicit local artifacts, reviewed transcript and fixed device. Do not allow implicit Whisper download, hidden clipping with mismatched transcript, source transcript logging, or model fallback. Transport chunking does not prove incremental synthesis. [V1] |
| Qwen3-TTS / 12Hz-0.6B-Base, then 1.7B-Base | `Qwen3TTSModel.from_pretrained(local_path, ...)`; `create_voice_clone_prompt` / `generate_voice_clone` with reference audio/text. Returns waveform list/sample rate. Base is the cloning model, not CustomVoice/VoiceDesign. Advertises 3-second cloning. | Official single-speaker SFT: JSONL `audio`, `text`, `ref_audio`; `prepare_data.py` tokenizes; `sft_12hz.py` writes checkpoints. | README recommends isolated Python 3.12. Observed package 0.1.1 pins transformers 4.57.3 and accelerate 1.12.0; includes SoX and ONNX-related dependencies. Tokenizer is a separate artifact. The wrapper accepts URLs/base64: Martlet must accept only managed local assets, not forward arbitrary URL/path inputs. Streaming claims require verification against the exact deployed wrapper. [V2] |
| Chatterbox / Turbo for English | `ChatterboxTurboTTS.from_local(ckpt_dir, device)`, then `generate(text, audio_prompt_path=...)`. `prepare_conditionals` asserts duration strictly greater than 5 s; conditioning uses bounded 10/15 s excerpts. Returns waveform; sample rate is `model.sr`. | No supported training recipe was verified in the inspected official quickstart; label managed fine-tuning unavailable, not silently equivalent to cloning. | Observed 0.1.7 metadata pins transformers 5.2.0 and, on common Python versions, torch/torchaudio 2.6.0; a Perth dependency follows mutable master. Incompatible with Qwen/GPT-SoVITS in one environment. Pin Perth, preserve watermarking, require an explicit reference rather than built-in `conds.pt`, and use local loading rather than automatic downloads. Other family variants need separate manifests. [V3] |
| GPT-SoVITS / **pinned:** release `20250606v2pro`, v2Pro pair (GPT `s1v3.ckpt` + SoVITS `s2Gv2Pro.pth`, [GPT-SoVITS](GPT_SOVITS_VOICE.md)) | Native Python `TTS` pipeline or `api_v2.py` POST `/tts`, with text/language, reference and prompt language/text. Inspected semantic-reference path enforces 3-10 s. Stream modes and output rates vary by version. | Upstream WebUI prepares datasets, transcripts and GPT/SoVITS training; advertises few-shot fine-tuning with about a minute of data. No guarantee that a minute achieves acceptable similarity. | Requires matching GPT and SoVITS weights plus version-specific language/auxiliary models. API paths are server-local, not Windows client paths. Native weight-switch endpoints mutate shared state: serialize the pair and verify both identities before readiness. Never expose restart/exit or arbitrary filesystem weight paths through the public gateway. Gradio <5 / transformers <5 differ from Chatterbox. [V4] **Done in the `gpt-sovits` role:** native `TTS_infer_pack` in its own Python 3.11 image; weights load once from pinned paths and both identities are checked before ready; the gateway exposes only synthesis; v2Pro's 32 kHz is resampled to 24 kHz and streamed per sentence (`return_fragment`). |
| XTTS-v2 / explicitly pinned Coqui-compatible runtime | `TTS(...).tts_to_file(speaker_wav=[...], language=...)`, or `Xtts.get_conditioning_latents` followed by `inference` / `inference_stream`. Multiple reference files supported. | Documented training is GPT encoder fine-tuning, not every model component; prepare reviewed datasets and export a version-bound checkpoint. | Evaluate maintained `coqui-tts` from Idiap rather than assuming the old `TTS` package works on current Python. Current fork documents separate PyTorch installation from 0.27.4 and Ubuntu 24.04 testing. Model config, tokenizer, checkpoint and speaker files must agree. Docs/model card differ on minimum clip length/language counts: query pinned capabilities, do not hard-code marketing totals. [V5] |

### Licensing and rights

F5 code is MIT but official weights are CC-BY-NC-4.0. XTTS-v2's actual CPML
1.0.0 text restricts **the model and outputs to noncommercial use**; this is
more specific than merely "review a custom license." Qwen's inspected 0.6B
Base model card is Apache-2.0; Chatterbox-Turbo's is MIT. GPT-SoVITS code's
license does not establish the complete auxiliary/model distribution rights.
Do not carry a family-level license assertion across arbitrary checkpoints.

Show code and model licenses separately, intended use, reference-speaker
permission and processing destination. An acknowledgment cannot authorize a
prohibited use. Adding a voice is consent to keep it and, at the owner's
direction (2026-10-02), to share it with the owner's own paired Martlet
computers so any of them can speak with it; training, synthesis and playback
each still require scoped approval.
No public-demo upload or remote training/experiment telemetry by default.

### Candidate review, 2026-10-02

The owner asked about OpenVoice V2, F5-TTS, XTTS-v2, GPT-SoVITS and Seed-VC.
Repository license/activity was read from GitHub on 2026-10-02:

| Candidate | Code / weights | Upstream activity | Fit for Martlet |
| --- | --- | --- | --- |
| OpenVoice V2 | MIT / MIT | Last push 2025-04 | Fast and permissive, but it is MeloTTS plus a tone-colour converter: timbre only, weaker likeness and prosody than F5. Not added. |
| F5-TTS | MIT / CC-BY-NC-4.0 | Active | Current engine. |
| XTTS-v2 | MPL-2.0 (`idiap/coqui-ai-TTS`) / CPML noncommercial | Fork active; Coqui closed | **Added** as the `xtts` host role ([XTTS-v2](XTTS_VOICE.md)): `inference_stream` sends audio while it generates, with cached conditioning latents. |
| GPT-SoVITS | MIT / MIT (v2Pro pair and auxiliary models: MIT/Apache-2.0; fastText LID CC-BY-SA-3.0) | Active (2026-08) | **Added** as the `gpt-sovits` host role ([GPT-SoVITS](GPT_SOVITS_VOICE.md)): good for anime-style voices from a 3-10 s reference; streams each sentence as it is generated. One-minute fine-tunes remain VS05. |
| Dia (Nari Labs) | Apache-2.0 / Apache-2.0 | Last code change 2025-06 (Dia2 is a separate repo) | **Added** as the `dia` host role ([Dia](DIA_VOICE.md)): clones from the reference and its transcript and performs nonverbal cues such as `(laughs)`, `(sighs)`, `(coughs)` and `(gasps)`. English only; not streaming. |
| Seed-VC | GPL-3.0 | **Archived** (2025-04) | Voice conversion, not TTS: needs another TTS first and adds latency. Not added. |

"ElevenLabs-Clone" is a third-party demo app, not a model. Chatterbox Turbo (MIT) is now **added** as the `chatterbox` host role and Martlet's default cloning engine ([Chatterbox Turbo](CHATTERBOX_VOICE.md)): it clones from a >5 s reference and speaks inline tags such as `[laugh]` and `[sigh]`. The original Chatterbox (MIT) and Chatterbox Nano are **added** too, as the `chatterbox-original` and `chatterbox-nano` roles on the same service ([the Chatterbox models](CHATTERBOX_VOICE.md#the-chatterbox-models)); Qwen3-TTS (Apache-2.0) remains a permissive cloning target above.
Every candidate needs the same GPU host and VRAM as F5, so switching engines
alone would not fix host/Docker/network failures; intermittent F5 silence after
an interrupted reply was a host admission bug, fixed in the `f5` role service.
A host runs one voice engine at a time (`exclusive=voice` in each engine's
`role.conf`): switching engines there stops the previous one, keeping its
downloads, so its model frees the GPU memory before the new engine loads. Two
engines resident together could starve the new one (it then failed to load and
stayed silent); a model that failed to load now loads again on the next reply.

## Guided experience

1. **Choose a destination and engine.** Show all five without loading anything.
   Reuse H08's host/ownership plan: this PC, paired Ubuntu, advanced Windows
   Docker/WSL, or an existing compatible service. Ubuntu managed workers are
   the first qualification lane; native Windows is not inferred from Python
   package availability. Text-only and current API speech remain usable.
2. **Review installation.** Present exact version/weights/license, known
   download and disk totals (unknown is a blocker), CPU/GPU suitability,
   shared LLM VRAM demand, required privileges and destination. Install only
   chosen engines in separate environments/containers. No global Python,
   automatic CUDA/driver replacement, surprise model/ASR downloads or
   administrative shell commands copied from unpinned upstream READMEs.
3. **Add a voice.** File picker imports an immutable local copy. Choose
   "reference clip (no training)" or "training material"; supply a name and
   reviewed transcript. Preserve originals. Later decoding supports WAV,
   FLAC, MP3/M4A with a pinned sandboxed decoder, size/time limits and visible
   resampling/trimming. VS01 accepts PCM16 WAV only and explains conversion;
   it does not silently reinterpret another format or claim dataset readiness.
4. **Prepare for this engine.** Show duration/language/transcript requirements.
   Offer an explicit segment/crop preview for shorter reference windows.
   Transcribe only with separate local-ASR selection/permission and allow
   correction. Training material gets a segmentation/transcript review,
   train/evaluation split, disk/compute estimate, job limits and explicit Start.
   Never auto-train simply because an engine is selected.
5. **Compare.** The same editable preview sentence, language and source voice
   are used for each compatible engine. Default to sequential generation on
   one GPU; show queued/loading/conditioning/generating/playable separately.
   Retain bounded A/B results so users can replay without reloading/training.
   Record cold load, time to first audio, total generation and actual settings;
   do not present a developer's advertised latency as observed performance.
6. **Apply.** Preview selection is separate from the active conversation route.
   Apply only after the exact engine/voice/checkpoint is ready, at a safe turn
   boundary. If loading fails or is canceled, retain the old saved selection;
   explicitly reload it if it was evicted. No silent cloud/provider fallback.

State dimensions stay separate: artifact availability, worker connectivity,
model residency, voice preparation, training-job state, preview state and
conversation selection. "Installed" never means "ready" or "heard."

### Switching, progress and recovery

Switch requests carry monotonic selection generations and exact engine/model/
voice revisions. Stop playback and discard stale PCM first; retain worker
ownership until cleanup or a deliberate owned-process termination is confirmed.
No completion from engine A may replace or play over later selection B.
Do not unload a runtime leased to a conversation/training job. Show a busy
reason or explicitly queue/cancel with approval. Only keep multiple models warm
when measured resource capacity allows it; unload/load delay is visible.

Checkpoint download/install steps durably and verify hashes on resume. Never
equate partial downloads or a process listening on a port with success.
Offline missing artifacts, insufficient space, model access denial, unknown
license eligibility, GPU OOM, decoder errors, host loss and canceled jobs
have distinct actionable failures. Training pause/resume is enabled only when
the pinned engine supports valid resumable checkpoints; otherwise offer Stop
and explain restart cost. Failed training leaves the prior voice untouched.

## Owning boundaries

Use one Voice Library for source assets and dataset revisions, not five
incompatible import UIs. VS01 stores local immutable, versioned WAV/transcript
bundles; an engine preference in a bundle is preparation metadata, not a
provider binding. Reference and training files remain separate purposes.
Future derived segments/embeddings/checkpoints reference source digests and
exact runtime/preprocessing revisions. Reuse the F5 consent/snapshot concepts
without pretending F5-specific worker messages are a universal contract.

Each engine runs in an isolated process behind a named adapter and existing
H03 trust boundary. A versioned common worker envelope carries job/request
IDs, deadlines, bounded asset transfers, exact artifacts, capabilities,
ordered PCM and terminal states. Engine-specific options remain typed.
No raw client paths, arbitrary model imports, pickle/checkpoint uploads or
upstream admin APIs on the public transport. Audio decoding is not permission
to execute user-supplied training code. Normalize output through the existing
bounded PCM/playback path, preserving truthful incremental-synthesis and
compute-cancellation capabilities.

All setup/preview/audio operations must share the existing app effect owner.
Long training jobs belong to a durable worker scheduler with explicit resource
leases, not a WPF event handler or the ordinary conversation request timeout.
Training stdout and automatic experiment trackers must not leak transcripts,
paths or audio. Support bundles contain metadata only. User voice assets are
private data kept on the owner's own computers (shared only with the owner's
paired Martlet computers), excluded from settings backup and ordinary support
export; explicit portable voice export is later work.

## Delivery slices and acceptance

| Slice | Deliverable | Acceptance before calling it complete |
| --- | --- | --- |
| VS01 | Local engine catalog and persistent Voice Library | All five targets distinguish upstream features from implemented capabilities; explicit reference/training WAV import validates size/format/duration/transcript/rights; restart retains byte-identical audio; list/inspect/delete are explicit; malformed/newer assets fail visibly and originals remain; no startup IO/network/model/audio effects. |
| VS02 | Exact artifacts and per-engine isolated installation recipes | Per engine, source/model/dependency/license closure; selected-host download consent, verified resume/cancel/disk handling, offline reinstall and removal preserving user data; no unresolved pin, mutable image or missing dependency silently passes. Depends on H02/H05/H08. |
| VS03 | Five named worker adapters, one common capability/job envelope | Real local inference for each pinned candidate, exact model/reference binding, bounded PCM, malformed/truncated streams, deadlines, cancellation and no secret/text logging. Per-engine source tests plus actual authorized hardware trials; fixtures do not qualify models. |
| VS04 | Reference preparation and switchable A/B previews | Same sentence and reviewed source across engines; incompatible clip prompts explicit preparation; no stale playback on rapid A/B/A changes; model OOM/host loss/cancel preserves selection; cache invalidation covers text/voice/language/options/model changes; physical Stop target remains AC-08. |
| VS05 | Dataset import/review and engine-supported fine-tuning | F5/Qwen/GPT-SoVITS/XTTS recipes each gated by actual training/export/reload evidence; Chatterbox clearly unavailable until a reviewed recipe exists. Bound jobs/epochs/disk/time, transcript correction, train/eval split, checkpoint provenance and cancel/resume behavior. No forced training to preview. |
| VS06 | Apply to conversation and novice setup qualification | Versioned settings/route migration, preview vs active selection, safe turn boundary and shared output ownership; user installs one chosen engine, imports a permitted voice, compares two engines and applies one without a terminal. Qualify per host/runtime tuple; all-five completion requires five actual engine trials. |

VS01 can proceed independently of host installation and GPU access. VS02 and
VS03 qualify one candidate at a time but keep all five in scope. VS04 follows
at least two qualified workers; VS05 can proceed per engine after its runtime
closure. Never badge the overall feature "supports five engines" on the
strength of VS01's catalog/import alone. No remote CI/training is authorized.

## VS01 local implementation evidence (historical)

VS01's window, Core library and their tests were removed when the F5 voice
list replaced them; only the shared PCM16 WAV inspector (`PcmWaveInfo`) remains.
The record below describes the removed slice.

The implementation used existing Core/Desktop projects, the app-shared
`SetupOperationRunner`, local-path guards and strict JSON validation. F5 and
Voice Library share the managed PCM16 WAV inspector; F5's narrower limits and
private store are preserved. No new external package or model is installed.
Bundles contain exactly `manifest.json` and `audio.wav`, not original paths,
credentials or executable model code. Audio hashes and bundle revisions are
verified before inspection/removal. Temporary files are owned until publication
or cleanup; a normal app Exit is blocked while voice IO is outstanding.

Developer-host checks on 2026-09-23 used the pinned .NET 10.0.401 SDK and
session-local C: build artifacts with `CI=true`: Core 411, Desktop 178 and
isolated F5 46 cases passed. The actual Desktop executable smoke also passed,
including passive main-window Voice Library navigation without data-directory
creation. These counts include existing suites, not just new voice tests.
Independent review identified the close-then-exit cleanup race; the app-level
guard and a held real-import regression address it.

Repeat with `dotnet test` on `tests\Martlet.Core.Tests`,
`tests\Martlet.Desktop.Tests` and `tests\Martlet.F5.Tests`, Release, `CI=true`,
and a chosen local C: `--artifacts-path`. Run `scripts\Smoke-Desktop.ps1`
against that build's actual Desktop executable. Keep artifacts out of source.
Voice quality, actual inference/training, decoder conversion, GPU/CPU
latency/VRAM, host lifecycle and novice setup qualification remain **NOT RUN**.

### Primary sources

- **V1:** [F5 Python API](https://github.com/SWivid/F5-TTS/blob/main/src/f5_tts/api.py),
  [reference preprocessing and synthesis](https://github.com/SWivid/F5-TTS/blob/main/src/f5_tts/infer/utils_infer.py),
  [training](https://github.com/SWivid/F5-TTS/blob/main/src/f5_tts/train/README.md),
  [dependencies](https://github.com/SWivid/F5-TTS/blob/main/pyproject.toml),
  [weights](https://huggingface.co/SWivid/F5-TTS).
- **V2:** [Qwen model variants](https://github.com/QwenLM/Qwen3-TTS),
  [Python wrapper](https://github.com/QwenLM/Qwen3-TTS/blob/main/qwen_tts/inference/qwen3_tts_model.py),
  [fine-tuning](https://github.com/QwenLM/Qwen3-TTS/tree/main/finetuning),
  [dependencies](https://github.com/QwenLM/Qwen3-TTS/blob/main/pyproject.toml),
  [0.6B Base license/card](https://huggingface.co/Qwen/Qwen3-TTS-12Hz-0.6B-Base).
- **V3:** [Chatterbox variants](https://github.com/resemble-ai/chatterbox),
  [Turbo loader/conditioning](https://github.com/resemble-ai/chatterbox/blob/master/src/chatterbox/tts_turbo.py),
  [dependencies](https://github.com/resemble-ai/chatterbox/blob/master/pyproject.toml),
  [Turbo license/card](https://huggingface.co/ResembleAI/chatterbox-turbo).
- **V4:** [GPT-SoVITS training/install](https://github.com/RVC-Boss/GPT-SoVITS),
  [native API](https://github.com/RVC-Boss/GPT-SoVITS/blob/main/api_v2.py),
  [reference validation](https://github.com/RVC-Boss/GPT-SoVITS/blob/main/GPT_SoVITS/TTS_infer_pack/TTS.py),
  [dependencies](https://github.com/RVC-Boss/GPT-SoVITS/blob/main/requirements.txt).
- **V5:** [XTTS inference and training](https://docs.coqui.ai/en/latest/models/xtts.html),
  [maintained runtime candidate](https://github.com/idiap/coqui-ai-TTS),
  [model card](https://huggingface.co/coqui/XTTS-v2),
  [actual CPML text](https://huggingface.co/coqui/XTTS-v2/blob/main/LICENSE.txt).
