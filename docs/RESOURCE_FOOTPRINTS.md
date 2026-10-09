# Resource footprints

**2026-10-06.** How much graphics memory (VRAM), system memory (RAM), processor
and disk each Martlet component takes on a machine, per selectable model or
engine. These numbers are the data of the placement planner's catalog,
[`FootprintCatalog.Seed.cs`](../src/Martlet.Core/Planning/FootprintCatalog.Seed.cs)
(`Martlet.Core.Planning.ComponentOption`); the wizard and Devices show each
component's share of a machine from them. Every number says how it is known:

- **M (measured):** run and observed on real hardware, where and how stated.
- **S (sourced):** from the vendor, model card, pinned download or upstream docs (linked).
- **E (estimate):** reasoned from the above; replace it when someone measures it.

Never treat an E as an M. Each catalog option has one `Evidence` value, for
its most important number (VRAM for a graphics card option); its `Source`
string names what else is estimated.

## How to read the numbers

- **GB** are 10^9 bytes, as nvidia-smi, Docker and Ollama report them.
- **Steady** is what a component holds while it works (loaded model, typical
  busy threads). **Peak** is the most it takes at once; the planner reserves
  peak memory and steady processor threads. **Idle** CPU is what it uses
  loaded but unused (0 unless stated).
- **Graphics memory is not the same all the time.** A model in Ollama holds
  its weights, KV cache and compute buffers from the moment it loads, so its
  steady and peak are the same. PyTorch engines (the voices, Singing,
  Pictures) load their weights first and then grow while they work: each
  request adds activations and buffers. PyTorch's allocator keeps freed memory
  for the next request unless the engine gives it back (Chatterbox only after
  it runs out; Singing between stages, and its worker reports
  `peak_vram_mib`). So their peak is higher (Chatterbox 3.7 to
  4.2 GB, Dia 4.4 to 9.8 GB). The Devices page shows both: a range from steady
  to peak, *tight* when the steady amounts fit a card but the peaks do not, and
  over when even the steady amounts do not fit
  (`ComponentOption.Usual` beside `ComponentOption.Reserve`).
- **CPU** is hardware threads kept busy (processor time per second), not cores
  reserved. A burst of 8 threads for 0.2 s per utterance is cheap on average.
- **Disk** is download plus install: model files plus the host role's Docker
  image (images share base layers, so several roles on one host take less
  than the sum).
- **Context.** Thinking runs at Martlet's 8,192-token context
  (`GenerationSettings.DefaultHostContextTokens`); its VRAM includes that KV
  cache, so `ContextGb` is 0. Deep thinking gets 32,768 tokens a think
  (`MaximumHostContextTokens`); its `ContextGb` is the extra cache and buffers
  for one think beyond 8,192 tokens.
- The planner also keeps 0.8 GB of every card for Windows and the desktop
  (`PlacementEngine.GpuReserveGb`).

## Footprint table

GPU: **N** NVIDIA (CUDA) only, **any** any vendor's card (Ollama), **CPU**
runs on the processor. Sharing: every option can share a card, but on Windows
an overfilled card pages memory into system RAM and every role on it slows
3-5x ([Voice latency](VOICE_LATENCY.md#where-the-time-goes-today)); leave
headroom.

### Thinking (local models in Ollama, 8,192 tokens)

| Option id | GPU | VRAM steady / peak | RAM | CPU while answering | Disk | First word | Evidence |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `gemma4:e2b` (hears) | any | **3.3** / 3.3 M | 1.5-2 E | 1-2 E | 7.5 S | 154 ms M | M (VRAM) |
| `qwen3.5:4b` | any | **4.1** / 4.1 M | 0.5-1 E | 1-2 E | 4.0 S | 328 ms M | M (VRAM) |
| `gemma4:e4b` (hears) | any | **4.9** / 4.9 M | 2.5-3 E | 1-2 E | 9.5 S | 208 ms M | M (VRAM) |
| `gemma4:12b` (hears) | any | 9.0 / 9.0 E | 1-1.5 E | 1-2 E | 8.0 S | ~400 ms E | E |
| `gemma4:26b` (MoE, 4B active) | any | 18.5 / 18.5 E | 1.5-2 E | 1-2 E | 19 S | ~600 ms E | E |
| `gemma4:e2b-cpu` | CPU | 0 | 6 / 7.5 E | 6 / 8 E | 7.5 S | ~2.5 s E | E |

- **Measured** 2026-10-04 on DIVA (RTX 4070 12 GB, i7-10700K), Ollama 0.35.1,
  the model's own runner process at 8,192 tokens with Thinking steps Off
  ([Voice latency: small models in Ollama](VOICE_LATENCY.md#local-options-measured-voicebench)).
  Same run: `qwen3.5:2b` 3.3 GB, `qwen3.5:9b` 6.6 GB, `ministral-3:3b` 4.0 GB,
  `ministral-3:8b` 6.4 GB (not in the catalog). In llama.cpp: E2B Q8 3.5 GB,
  E4B Q4 4.1 GB.
- **Disk** is [ollama.com/library/gemma4](https://ollama.com/library/gemma4/tags)
  and [qwen3.5](https://ollama.com/library/qwen3.5/tags), 2026-10-06: each
  tag lists a range (`e2b` 4.6-7.5 GB, `e4b` 6.6-9.5, `12b` 7.7-8.0, `26b`
  16-19, `qwen3.5:4b` 3.3-4.0); the upper end includes the speculative-decoding
  draft Gemma 4 tags bundle ([`OllamaDraftHead`](../src/Martlet.Providers/Ollama/OllamaDraftHead.cs)).
- **RAM (E).** Gemma 4 E2B/E4B measured 3.3/4.9 GB on the card from 4.6-9.5 GB
  downloads: the per-layer embeddings stay in system memory. RAM is the
  download less the card's share plus the runner, not measured.
- **12B and 26B (E):** the download, plus the KV cache below, plus about
  0.5 GB of compute buffers. Not measured here (no 16 GB+ card).
- **On the processor (E):** the whole model in RAM; Ollama uses one thread
  per physical core while generating.

### KV cache (context) per model

From each model's `config.json` on Hugging Face
([E2B](https://huggingface.co/google/gemma-4-E2B-it/blob/main/config.json),
[E4B](https://huggingface.co/google/gemma-4-E4B-it/blob/main/config.json),
[12B](https://huggingface.co/google/gemma-4-12b-it/blob/main/config.json),
[26B-A4B](https://huggingface.co/google/gemma-4-26B-A4B-it/blob/main/config.json)),
f16 cache, K and V. Gemma 4 is hybrid: four of five (E-models) or five of six
layers attend to a sliding window (512 or 1,024 tokens) and only the full-attention layers grow with
context, with 1-2 key/value heads of 512; the E-models also share the cache of
their last 18-20 layers. This assumes Ollama sizes sliding layers to the window
(its Gemma caches do); a runtime that doesn't would need several GB more.

| Model | Own-cache layers (full / sliding) | Full-attention bytes per token | 8,192 tokens | 32,768 tokens | Sliding (fixed) |
| --- | --- | --- | --- | --- | --- |
| E2B | 3 / 12 | 6 KiB | 0.05 GB | 0.2 GB | 0.01 GB |
| E4B | 4 / 20 | 16 KiB | 0.13 GB | 0.54 GB | 0.02 GB |
| 12B | 8 / 40 | 16 KiB | 0.13 GB | 0.54 GB | 0.34 GB |
| 26B-A4B | 5 / 25 | 20 KiB | 0.17 GB | 0.67 GB | 0.21 GB |

So context is cheap for Gemma 4 at Martlet's sizes: a 32,768-token think adds
about 0.4-0.5 GB of cache beyond 8,192 tokens; the catalog adds 0.2-0.4 GB of
buffer growth (E) for `ContextGb` 0.7 (E4B, 12B) and 0.9 (26B). Each extra
*Thinks at once* slot (`OLLAMA_NUM_PARALLEL`) adds one more context; the role's
recommended slot count (`DeepThinkingSlots`) uses these `ContextGb` values and
the catalog's VRAM for Thinking and the host's other roles.

### Deep thinking

| Option id | VRAM | ContextGb (one 32k think) | RAM | Disk | Evidence |
| --- | --- | --- | --- | --- | --- |
| `deep-thinking:gemma4:e2b` | 3.3 M | 0.3 E | 1.5-2 E | 7.5 S | M (model VRAM) |
| `deep-thinking:gemma4:e4b` | 4.9 M | 0.7 E | 2.5-3 E | 9.5 S | M (model VRAM) |
| `deep-thinking:gemma4:12b` | 9.0 E | 0.7 E | 1-1.5 E | 8.0 S | E |
| `deep-thinking:gemma4:26b` | 18.5 E | 0.9 E | 1.5-2 E | 19 S | E |

The same model as Thinking in a second Ollama server
([Thinking longer](CONVERSATION.md#thinking-longer-and-background-work)).
When Thinking and Deep thinking use the same model on one card, each server
holds its own copy.

### Voice

| Option id | GPU (min card) | VRAM steady / peak | RAM | CPU while speaking | Disk | First audio | Evidence |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `chatterbox-turbo` | N (6 GB) | **3.7 / 4.2** M | 2.5-3 E | 1-1.5 E | 11 (3.0 S weights + ~8 E image) | 417-560 ms M | M |
| `f5-tts` | N (6 GB) | 0.9 M / 2 E | 2.5-3 E | 1-1.5 E | 9.5 E | 1.4-2.2 s M (whole pieces) | E |
| `xtts-v2` | N (4 GB) | 2.2 S / 3 E | 2.5-3 E | 1-1.5 E | 10 E | not measured (streams) | S |
| `gpt-sovits` | N (4 GB) | 3 / 4 E | 2.5-3 E | 1-2 E | 8.1 (6.7 M image + 1.4 E) | ~1.3 s M (CPU container) | E |
| `dia` | N (8 GB) | 4.4 S / **9.8** M | 3-4 E | 1 E (CPU-bound) | 15 E | 17-60 s M | M (peak) |
| `chatterbox-original` | N (6 GB) | 3.9 / 4.8 E | 3-3.5 E | 1-1.5 E | 11.2 (3.2 S weights + ~8 E image) | ~2 s E (whole pieces) | E |
| `chatterbox-nano` | N (4 GB) | 2.6 / 3.1 E | 2.5-3 E | 1-1.5 E | 9.9 (1.9 S weights + ~8 E image) | ~0.45 s E (streams as Turbo) | E |
| `chatterbox-nano-cpu` | CPU | 0 | 2.5 / 4.5 M | 8 M | 9.9 | ~1.4 s M (streamed: 2.0-3.4 s for a 5.5 s sentence on a busy CPU; whole pieces 0.75 s for 1.2 s of speech, 2.1 s for 4 s) | M |
| `windows-speech` | CPU | 0 | 0.1-0.2 M | 0.9-1 M | 0 (built in) | ~50 ms E | M |

- **Chatterbox Original and Nano:** their graphics-card numbers are estimates
  from their pinned weights (3.2 GB and 1.9 GB) plus Turbo's measured overhead;
  no NVIDIA GPU was available to measure them. Nano on the processor was
  measured on an i7-13700K in native Windows Python, not in the role's
  container (the service's own CPU numbers there are not measured yet): whole
  pieces at 0.52x real time (median) with 8 threads, one on each performance
  core (what the service does on native Linux), 0.64x with 8 threads on any
  core (what it does on Docker Desktop, which can't pin), 0.75x with PyTorch's
  own 16 threads, and 1.05x when other programs kept about 15 cores busy; it
  held 2.4-2.6 GB and at most 4.5 GB
  ([Chatterbox Nano](CHATTERBOX_VOICE.md#chatterbox-nano)). Windows voices were
  removed, so the planner picks it when no card has room for a voice engine.
- **Chatterbox Turbo, Dia, F5:** voicebench on DIVA's RTX 4070, each alone on
  the card, 2026-10-04 ([Voice latency](VOICE_LATENCY.md#local-options-measured-voicebench),
  [Dia](DIA_VOICE.md)). F5's 0.9 GB was read beside the resident roles; its
  peak is an estimate. Dia also cites about 4.4 GB in float16 from
  [its README](https://github.com/nari-labs/dia); Martlet's Windows bench
  peaked at 9.8 GB with the card only 30% busy (the processor holds it back),
  so it cannot share a 12 GB card.
- **Chatterbox weights:** `t3_turbo_v1.safetensors` 1.92 GB and
  `s3gen_meanflow.safetensors` 1.06 GB, pinned in
  [`workers/chatterbox`](../workers/chatterbox/README.md).
- **XTTS-v2:** about 2-2.2 GB in fp16, 1.8-2 GB of weights
  ([nexgpu.net](https://nexgpu.net/en/models/xtts/), a third-party guide; not
  measured by Martlet: no image on the dev PC).
- **GPT-SoVITS:** role image 6.7 GB measured on Docker Desktop
  ([GPT-SoVITS](GPT_SOVITS_VOICE.md)); GPU memory NOT RUN (no NVIDIA GPU there).
  Third-party guides quote up to about 6 GB for v2Pro; the 4 GB minimum card is
  Martlet's role requirement.
- **Docker images (E):** a PyTorch CUDA image is about 6-15 GB (GPT-SoVITS
  6.7 GB and Singing 14.7 GB measured); about 8 GB is assumed for the others.
- **Shown to the owner:** Companion › Voice reads each voice's line from this
  table (`ComponentOption.WhereItRuns`, `VoiceEngineRunsOn-<key>`): "Runs on an
  NVIDIA GPU: about *steady* GB of graphics memory, up to *peak* GB (*min* GB+
  card).", with the same steady (`Usual`) and peak numbers as the Devices page,
  "Runs on the CPU: no graphics card needed." or "Runs online: nothing runs on
  your computers." Change a number here and the line follows.

### Listening

| Option id | GPU | VRAM | RAM | CPU while transcribing (2 / 4 threads) | Disk | Latency | Evidence |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `parakeet-tdt-110m-en-cpu` | CPU | 0 | **0.67** M | **3.9-4.0 / 9.7-9.9** M | 0.477 S | 151-213 ms for 12.2 s M; 86 ms short prompts M | M |
| `parakeet-tdt-0.6b-v2-cpu` | CPU | 0 | **0.87** M | **2.3-2.6 / 6.7-8.6** M | 0.661 S | 0.58-1.24 s for 12.2 s M; 223 ms short M | M |
| `parakeet-tdt-0.6b-v3-cpu` | CPU | 0 | **0.88** M | **2.6-2.7 / 7.0-8.4** M | 0.670 S | 0.56-1.04 s for 12.2 s M; 234 ms short M | M |
| `whisper-large-v3-turbo-cuda` | N (4 GB) | **~2** M / 2.5 E | 0.5-1 E | 1 E | 4 (1.6 S model + image E) | 210 ms short M | M |
| `whisper-small-cuda` | N (2 GB) | 0.9 S / 1.2 E | 0.5-1 E | 1 E | 2.5 E | 150 ms E | S |
| `whisper-small-cpu` | CPU | 0 | 0.85-1 S | 4 E | 1.5 E | ~1.5 s E | S |

- **Parakeet (M):** sherpa-onnx 1.13.8 (the version Martlet bundles), the
  pinned int8/fp32 models from
  [`ParakeetModels`](../src/Martlet.Sherpa/ParakeetModels.cs), greedy search
  on the processor, decoding a 12.2 s Windows-voice clip back to back for
  10-20 s, on an i7-13700K (16 cores, 24 threads, 32 GB), 2026-10-06, with
  [`scripts/Measure-Footprint.ps1`](../scripts/Measure-Footprint.ps1). RAM
  includes about 0.05 GB of the Python host. Martlet gives Parakeet
  `clamp(logical processors / 4, 2, 4)` threads
  ([`ParakeetEngine`](../src/Martlet.Sherpa/ParakeetEngine.cs)): 2 on an
  8-thread PC, 4 on 16+. ONNX Runtime's worker threads spin, so 4 threads keep
  about 7-10 hardware threads busy, 2 threads about 2.3-4. It works only while
  transcribing (well under a second per utterance) and is idle otherwise: the
  catalog's steady CPU is the 2-thread figure, peak the 4-thread one.
  Short-prompt latency is the voicebench median on an i7-10700K
  ([Voice latency](VOICE_LATENCY.md#local-options-measured-voicebench)).
- **Disk** is the exact pinned download: 477,410,591, 661,190,513 and
  670,478,772 bytes.
- **Whisper large-v3-turbo:** the whisper.cpp host role; removing it from
  DIVA's shared RTX 4070 gave back about 2 GB
  ([Voice latency](VOICE_LATENCY.md#local-options-measured-voicebench));
  faster-whisper large-v3-turbo measured 2.4-3.0 GB in the same bench.
  `ggml-large-v3-turbo.bin` is about 1.6 GB.
- **Whisper small:** [whisper.cpp's README](https://github.com/ggml-org/whisper.cpp#memory-usage)
  lists `small` at 466 MiB on disk and about 852 MB of memory (`tiny` 75 MiB /
  ~273 MB, `base` 142 MiB / ~388 MB, `medium` 1.5 GiB / ~2.1 GB, `large`
  2.9 GiB / ~3.9 GB); faster-whisper `small.en` measured 0.8 GB on the RTX 4070.
- **Windows speech recognizer (M, not a catalog option):** System.Speech
  dictation of the same clip in a loop: 0.17 GB with its PowerShell host,
  about 0.9 threads while recognizing.

### Character and lip-sync

| Option id | GPU | VRAM | RAM | CPU | Disk | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| `character-live2d-vrm` | any (iGPU fine) | within the 0.8 GB desktop reserve E | 0.4 / 0.8 E | 0.5 / 1 E | 0.1 E | E |
| `loudness-lipsync` | CPU | 0 | ~0 | <0.1 E | 0 | E |
| `audio2face-3d` | N, RTX 20+ (4 GB) | 1.2 / 1.5 E | 1.5-2 E | 1 E | 8.3 (0.3 S models + ~8 E image) | E |

- **Character (E):** a WebView2 overlay drawing Live2D or VRM at the display's
  frame rate; a texture up to 8,192 px is 256 MB uncompressed, so a heavy
  custom model can exceed this. NOT RUN here: this dev build has no bundled
  Hiyori model to show.
- **Audio2Face-3D (E):** derived, not measured alone: on DIVA's RTX 4070 the
  Chatterbox, whisper.cpp large-v3-turbo and Audio2Face roles together held
  about 7.0 GB ([Singing](SINGING.md#does-singing-need-its-own-graphics-card)),
  less Chatterbox's 3.7-4.2 and Whisper's ~2 leaves about 1-1.3 GB. NVIDIA
  asks for a 4 GB+ card ([Audio2Face-3D SDK](https://github.com/NVIDIA/Audio2Face-3D-SDK)).
  Models from [`models.lock`](../workers/audio2face/models.lock): Claire
  0.30 GB, James 0.22 GB, Mark 0.32 GB, plus its TensorRT engine built on
  first start.

### Singing and Pictures

| Option id | GPU (min card) | VRAM steady / peak | RAM | CPU | Disk | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| `singing-acestep-soulx` | N (6 GB) | **5.1 / 7.2** M | 8 / 12 E | 2 / 4 E | **30.6** M (15.9 models + 14.7 image) | M |
| `singing-acestep-vevosing` | N (8 GB) | **6.8** M | 8 / 12 E | 2 / 4 E | **35** M (20.3 + 14.7) | M |
| `pictures-comfyui` | N (8 GB) | 7 / 8 E | 12 / 20 E | 2 / 4 E | 31 (20.7 S models + ~10 E image) | E |

- **Singing (M):** MCP `singing_check` on DIVA's RTX 4070: worker peak 5.1-5.2 GB
  in the host role with int8 music weights, 6.0-7.2 GB standalone; full
  precision 9.6-10.7 GB (used only with 11 GB free)
  ([Singing](SINGING.md)). Only while a song is made; the worker exits after
  five idle minutes, so steady use between songs is 0. Docker Desktop's WSL VM
  had 15.6 GB and was short of memory (RAM is an estimate).
- **Pictures:** Z-Image Turbo bf16 files are 20,690,152,836 bytes
  ([Pictures host](PICTURES_HOST.md)). ComfyUI runs `--lowvram`, so it fills
  the card it has and offloads the rest (about 12 GB diffusion model and 8 GB
  text encoder) to system memory: bf16 wants 14-16 GB to stay on the card
  ([Thunder Compute guide](https://www.thundercompute.com/blog/z-image-turbo-comfyui)).
  Martlet frees it a few minutes after the last picture. Not measured.

### Vision, Reading, Hearing and Smart home

| Option id | GPU | VRAM steady / peak | RAM | CPU | Disk | Speed | Evidence |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `vision:thinking`, `hearing:thinking` | none of its own | 0 | 0 | 0 | 0 | | Thinking's own model takes the pictures or recordings |
| `vision:gemma4:e2b`, `hearing:gemma4:e2b` | any | **3.3** / 3.3 M | 1.5-2 E | 1-2 E | 7.5 S | not measured | M (as Thinking) |
| `vision:qwen3.5:4b` | any | **4.1** / 4.1 M | 0.5-1 E | 1-2 E | 4.0 S | not measured | M (as Thinking) |
| `hearing:gemma4:e4b` | any | **4.9** / 4.9 M | 2.5-3 E | 1-2 E | 9.5 S | not measured | M (as Thinking) |
| `vision:qwen2.5vl:7b` | any | 7 / 7.5 E | 1-1.5 E | 1-2 E | 6.0 S | not measured | E |
| `reading:windows-ocr` | CPU (in Martlet, Windows) | 0 | 0.05-0.1 E | 1 E while it reads | 0 (built in) | ~140 ms per full-size 1920 x 1080 screenshot ([Reading](READING.md)) | E |
| `reading:rapidocr` | CPU (`ocr` role) | 0 | 0.4-0.6 E | 4 S ([OCR host](OCR_HOST.md)) | 0.5 E (15 MB models + image) | **0.9 s** M on a 24-thread processor | E |
| `smart-home:home-assistant` | CPU (`home-assistant` role, Linux Docker Engine) | 0 | 0.5-1 E | 0.2 / 1 E | 2 E (image) | | E |

- **Image and audio models** run in Ollama (on this PC, or a paired
  computer's for pictures), not in a host role of their own. Their numbers are
  the same models' numbers as Thinking at 8,192 tokens; a picture or a
  recording adds buffers that nobody measured. Qwen2.5-VL 7B is the ollama.com
  download (6.0 GB) plus its KV cache at 8,192 tokens (about 0.5 GB: 28
  layers, 4 KV heads, 57 KiB a token) and a picture's buffers. Description
  times are not measured, so the catalog has none.
- **Reading:** Windows OCR is built into Windows; the time is from
  [Reading](READING.md). The Reading role's read time was measured there; its
  memory and image are estimates.
- **Home Assistant** asks for 2 GB of memory for a whole Home Assistant OS;
  the container alone with a few integrations takes less. Not measured.

### Always on in the app (not catalog options)

Measured on the i7-13700K the same way as Parakeet:

| Part | RAM | CPU | Disk |
| --- | --- | --- | --- |
| Desktop app, idle, no character (Release build, disposable data folder) | 0.15 GB | ~0 (0.23 peak) | |
| Silero VAD v6.2.1 (ONNX Runtime, 1 thread, 32 ms frames in real time) | 0.06 GB (with Python) | 0.02-0.05 | 1.3 MB model + 16.5 MB runtime |
| Speaker recognition (WeSpeaker ResNet34-LM, 4 threads) | 0.16 GB (with Python) | 4 threads for ~156 ms per 12 s utterance | 26.5 MB (41 MB with segmentation) |
| Echo cancellation, memory, perception capture | E: small (lexical memory, WebRTC-class AEC) | E: <0.5 | |

### Hosted options

`hosted:*` options (NVIDIA Build, OpenRouter, OpenAI, OpenAI voice and
transcription, the online image, audio and picture models) use no local
resources beyond the app's network request.

## What could not be measured here

The dev PC for this pass (i7-13700K, 32 GB, Intel UHD 770) has no usable
NVIDIA card (nvidia-smi denies access) and no Ollama, and no validation hosts
are listed in `~\.martlet-dev\validation.json`. So every graphics card number
above is either DIVA's earlier measurements (cited) or sourced/estimated:
**NOT RUN** for Gemma 4 12B/26B, XTTS-v2, GPT-SoVITS and Audio2Face VRAM, the
Live2D/VRM renderer, Pictures, host-role RAM and CPU, and Gemma on the
processor.

## How to re-measure

[`scripts/Measure-Footprint.ps1`](../scripts/Measure-Footprint.ps1) samples a
process tree (or a Docker container) and prints peak and steady VRAM, RAM and
busy threads as JSON. It starts nothing but the command you give it.

```powershell
# A process you start (here a sherpa-onnx benchmark); -DiskPath sizes its model folder.
.\scripts\Measure-Footprint.ps1 -Name parakeet-v3 -Command python -Arguments bench.py,v3,4,20 -DiskPath .\models\v3

# Ollama on this PC: load the model first (ollama run gemma4:e2b ""), then sample while you talk.
.\scripts\Measure-Footprint.ps1 -Name gemma4-e2b -ProcessName ollama -Seconds 60 -WholeCard

# A host role in Docker Desktop: container memory and CPU from docker stats, VRAM as the card's
# used memory less the baseline taken before the run (container PIDs aren't visible on Windows).
.\scripts\Measure-Footprint.ps1 -Name chatterbox -Container martlet-chatterbox-chatterbox-1 -Seconds 120 -WholeCard
```

Steps for a GPU role: stop other GPU work, note `nvidia-smi` memory, start the
measurement, load the role (`martlet-<role> warm`) and drive real requests
through MCP (`latency_report`, `singing_check`, `pictures_status`) or
voicebench ([`scripts/voice-bench`](../scripts/voice-bench/README.md)) until it
ends. `ollama ps` shows a loaded model's own size and how much is on the card.
Record the machine, driver, versions and context, change the option in
`FootprintCatalog.Seed.cs` with `Evidence = Measured` and a `Source` naming
where, and update this page.
