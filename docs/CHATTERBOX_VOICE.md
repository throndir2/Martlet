# Chatterbox voice engines

Martlet's **default** self-hosted voice engine is Chatterbox Turbo, next to
[F5-TTS](F5_VOICE.md) and [XTTS-v2](XTTS_VOICE.md) (both stay selectable in
Companion > Voice > Voice engine). Chatterbox Turbo (Resemble AI) copies a voice
from the same reference recordings (Companion > Voice > Voices) and, unlike the
others, speaks **tags**: non-word sounds such as `[laugh]` and `[sigh]` written
inline in the reply, and `[whispering]`, which Martlet turns into a real whisper
(see [Tags](#tags)). Two other Chatterbox models are separate engines with their
own host roles: [Chatterbox Nano](#chatterbox-nano) and
[Chatterbox Original](#chatterbox-original-general-and-expressive). Most of this
page is about Turbo; the same service runs all three.

## The Chatterbox models

Resemble AI publishes five Chatterbox models. Martlet installs three of them.
Each one is its own engine in Companion > Voice > Voice engine, with its own
host role, port, route and pinned files, and one role runs on a host at a time
(`exclusive=voice`). The rundown is what Companion > Voice shows
(`VoiceEngineAbilities-<key>` and `VoiceEngineRunsOn-<key>`).

| Model | Engine and role | Size | Voice cloning | Laughs & sighs | Emotions | Runs on |
| --- | --- | --- | --- | --- | --- | --- |
| Chatterbox Turbo | `chatterbox`, port 50083 (default) | 350M, GPT-2 medium | yes | yes | whispering only | NVIDIA GPU, about 3.7 GB, up to 4.2 GB (6 GB+ card) |
| Chatterbox Original | `chatterbox-original`, port 50089 | 500M, Llama | yes | no | calm or expressive | NVIDIA GPU, about 3.9 GB, up to 4.8 GB (6 GB+ card; estimate) |
| Chatterbox Nano | `chatterbox-nano`, port 50088 | 110M, GPT-2 small | yes | yes | whispering only | NVIDIA GPU, about 2.6 GB, up to 3.1 GB (4 GB+ card; estimate), or without one the CPU |
| Chatterbox Multilingual V3 | not installed | 500M | | | | |
| Single Language Pack | not installed | 500M each | | | | |

- **Turbo and Nano** share an architecture, a tokenizer and their tags. Nano's
  decoder (`s3gen_meanflow`), voice encoder and tokenizer files are byte for
  byte Turbo's; only its T3 (`t3_nano_v1.safetensors`) differs. Turbo and Nano
  have no `exaggeration` or `cfg_weight` setting: Resemble's library ignores
  both for them.
- **The original Chatterbox** is the model Resemble's "Original Chatterbox
  Tips" are about: it has the `exaggeration` and `cfg_weight` settings, but no
  tag tokens, so it can't laugh or sigh.
- **Multilingual V3 and the Single Language Pack** are not installed. Their
  weights (`t3_mtl23ls_v3`, `s3gen_v3`) need Chatterbox code that is on GitHub
  but in no `chatterbox-tts` release yet (0.1.7 is still the latest), and they
  speak 23 languages where Martlet's conversations and prompts are English. They
  can be added the same way once Resemble releases that code.

## Chatterbox Nano

Nano runs where Turbo can't: on a smaller graphics card (4 GB+), or on the CPU
of a computer without an NVIDIA GPU. Its role (`deploy/host/roles/chatterbox-nano`)
is `gpu=optional`: `martlet-host` adds the GPU overlay when the host can run GPU
containers and asks GPU or CPU, and the service (`MARTLET_CHATTERBOX_DEVICE=auto`)
uses the card it was given, or the CPU. It uses the same image as Turbo
(`martlet-chatterbox:10`, whose CUDA PyTorch also runs on the CPU) and its own
volume, `martlet-chatterbox-nano-models`.

- On a GPU it decodes like Turbo (the CUDA graph and streaming below).
- On the CPU there is no CUDA graph. T3 draws each speech token with the same
  sampling as the library, within the same speech-token budget as Turbo's,
  and each piece streams on a CPU schedule (below). The idle check doesn't run
  on the CPU (it is for graphics memory).
- **Threads on the CPU** (image `martlet-chatterbox:9`). The service uses at
  most 8 threads, and never more than the CPU's performance cores
  (`MARTLET_CHATTERBOX_CPU_THREADS` sets another count). On native Linux, a
  hybrid Intel CPU (one with performance and efficiency cores) lists its
  performance cores in `/sys/devices/cpu_core/cpus`; the service then pins
  itself to one logical CPU on each of them. Everywhere else it doesn't pin.
  Docker Desktop on Windows never pins: its WSL 2 kernel has no
  `/sys/devices/cpu_core` (checked: 6.18.40.1-microsoft-standard-WSL2 on an
  i7-13700K, whose virtual machine sees 24 CPUs). So a Windows PC that runs the
  role gets the "8 on any core" row below, not the pinned row. PyTorch's own
  choice is every physical core, efficiency cores too, and that was clearly
  slower.
- **Decoder steps on the CPU** (image `martlet-chatterbox:9`). Each decoding
  takes 1 step of the meanflow decoder instead of the library's 2
  (`MARTLET_CHATTERBOX_CPU_DECODER_STEPS`); a GPU keeps 2. `/status` reports
  `cpu` (`threads`, `pinned_cpus`) and `decoder_steps`, and so does
  `voice_engine_check` ([MCP](MCP.md)).
- **Measured** by a separate benchmark on this repository's development PC, in
  native Windows Python (PyTorch 2.8.0's CPU wheel; the pinned rows used
  Windows thread affinity), not in the `martlet-chatterbox:9` container: an
  i7-13700K (8 performance and 8 efficiency cores), DDR5-6000, two voices,
  whole pieces of 3.3-4.3 s of speech, each take repeated when other programs
  kept more than 6 cores busy. Medians, with the slowest take in brackets.
  Resemble says Nano runs "3x faster than realtime on 8 CPU cores". The
  service's own CPU numbers, in its container on Docker Desktop or on Linux,
  are **NOT RUN**.

  | Threads | First audio | Real-time factor | A speech token |
  | --- | --- | --- | --- |
  | 8, one on each performance core (the service on native Linux) | 2.39 s | 0.52 (0.62) | 12.2 ms |
  | 4, one on each of 4 performance cores | 2.54 s | 0.61 (0.70) | 12.5 ms |
  | 8 on any core (the service on Docker Desktop, and wherever performance cores aren't known) | 2.86 s | 0.64 (0.88) | 14 ms |
  | 4 on any core | 3.39 s | 0.73 (1.03) | |
  | 16 on any core (PyTorch's own choice) | 4.10 s | 0.75 (1.23), 6 of 16 slower than real time | 18-21 ms |
  | 8 on the efficiency cores (like an older CPU) | 4.49 s | 1.05, 12 of 16 slower than real time | |

  One decoder step instead of 2 made the decoder take 0.23-0.32 s a piece
  instead of 0.40-0.60 s: first audio for 1.2 s of speech after 0.75 s instead
  of 0.96 s, and for these sentences after 2.14 s instead of 2.39 s. On 20
  paired takes (the same seeds), UTMOS changed by +0.005, speaker similarity by
  +0.0001 and the word error rate not at all. Nobody has compared them by ear
  yet.
- **Streaming on the CPU** (image `martlet-chatterbox:10`). Each decoding
  decodes every speech token so far again, so on the CPU it costs a fixed
  0.35-0.40 s (it reads the voice's reference again) plus about 70 ms per
  second of speech so far, and the vocoder adds about 75-90 ms per second of
  new speech. The GPU's schedule (a chunk after 12 tokens, then 25, 50 and 100
  more) therefore paused in every piece on the CPU: first audio after
  0.66-0.93 s, but 0.79-1.1x real time, with 1.2-1.3 s of pauses for a 4 s
  sentence. One early chunk after 55 tokens and then the rest gave first audio
  after 1.57 s for 3.3-4.3 s sentences without a pause, but a 13 s piece then
  paused for 3.4-4.5 s. So the CPU has its own schedule (`_Playback` in the
  service):
  - The first chunk waits for 55 speech tokens (2.2 s of speech;
    `MARTLET_CHATTERBOX_CPU_FIRST_TOKENS`, 0 speaks whole pieces). A piece
    expected to be longer (7 speech tokens for each text token) waits for more,
    as many as the rest needs to keep up.
  - Each later chunk comes as late as playback allows: when the audio already
    sent would run out before a decoding started later could finish, with
    0.2 s to spare. It always has enough new tokens to pay for its own drawing
    and decoding, because a smaller chunk only brings the next pause sooner.
  - The service measures the time a token takes and how long each decoding
    takes, from the warm-up on. It times every chunk with those measurements, so
    a busy CPU gets bigger chunks. When the CPU is too slow for any later
    chunk, the rest of the piece is decoded whole, once.
  - A short piece (expected under 55 tokens) is spoken whole, as before.
    `/status` reports `streaming` (`on`, `first_tokens`, and the measured
    `token_ms` and `decoding_scale`). `voice_engine_check` reports the pauses
    a listener hears (`pauses`, `pauseMs`).

  Measured with the real Nano on this repository's development PC (i7-13700K,
  8 threads not pinned, native Windows Python, not the container), each
  sentence alternating between a streaming service and one that speaks whole
  pieces, three times, while other programs used 7-92 % of the CPU:

  | Piece | First audio, streamed | First audio, whole | Pauses, streamed |
  | --- | --- | --- | --- |
  | Short reply ("Yes, of course.", 1.2-1.6 s) | 1.7-2.4 s | 1.6-2.4 s (the same: spoken whole) | none |
  | A 5.5 s sentence | 2.0-3.4 s | 4.4-6.3 s | 0.16 s in one of three |
  | Two sentences in one piece (7 s) | 3.1-4.3 s | 5.3-5.8 s | none |
  | One 13 s sentence | 2.8-3.4 s | 8.8-11.1 s | 2.7 s in one of three, during a load spike |

  Timed in one process with a quieter CPU, the 13 s sentence came in four or
  five chunks (after 73, then about 70-110 more tokens each), each sent
  0.02-0.6 s before the audio before it ran out, with first audio after
  2.2-2.3 s. Streaming makes more decodings, so a streamed piece takes longer
  in all (0.8-1.3x real time against 0.7-1.2x whole, under the same load), and
  the next request waits for it.
- **Nano against Turbo** (20 paired takes, scored with faster-whisper
  large-v3-turbo, WavLM-base-plus-sv and UTMOS22): UTMOS 3.74 against 3.83,
  speaker similarity 0.941 against 0.936, word error rate 0.003 against 0.001.
  Short replies ("Yes." to "Oh no!") were word-perfect 31 of 32 times against
  29 of 32, and none ran away. All nine sound tags added sound (Nano 0.42-1.16 s
  a tag, median; Turbo 0.28-0.76 s).
- **Turbo on the CPU is too slow.** On the same 8 performance cores, Turbo took
  4.48 s for these sentences (1.18x real time, 35 ms a speech token), and every
  sentence was slower than real time. That is why the CPU engine is Nano.
- **The CPU must be free.** With other programs keeping about 15 cores busy, a
  4.3 s piece took 4.5 s (1.05x real time), so the voice pauses between pieces.
  Don't give Nano the CPU that also runs Thinking or other heavy work.
- Memory: after loading, the service holds 2.4-2.6 GB, and at most 4.5 GB while
  it speaks (Turbo on the CPU: 3.4 and 6.6 GB). Loading takes 5-8 s, and
  warming up (the first conditionals, about 3-18 s for librosa's first run, and
  the first piece) is done before the role reports ready; after that a new
  voice takes about 0.37 s.
- The welcome wizard never suggests Nano, and on a computer without an NVIDIA
  GPU Companion > Voice keeps recommending a Windows voice; Nano is there for
  the owner to choose.

## Chatterbox Original: General and Expressive

The original 500M Chatterbox (`ResembleAI/chatterbox` at revision
`5bb1f6ee58e50c3b8d408bc82a6d3740c2db6e18`: `t3_cfg`, `s3gen`, `ve` and
`tokenizer.json`, 3.2 GB; not its built-in voice or its multilingual weights)
has two settings for each sentence:

- **Exaggeration** (0.25-2, 0.5 neutral): how much emotion. Resemble warns that
  extreme values can be unstable.
- **CFG weight** (0-1): how closely it follows the voice and the words. Lower is
  slower and more deliberate. Resemble suggests about 0.3 for a reference
  speaker who talks fast.

Martlet gives it two ways of speaking, with Resemble's suggestions as defaults:
**General** (exaggeration 0.5, CFG weight 0.5) for every sentence, and
**Expressive** (0.7 and 0.3, Resemble's tip for expressive or dramatic speech)
for a sentence the reply starts with `[expressive]`. The Thinking prompt
(Companion › Prompts › *Voice sounds and tones*) offers `[expressive]` and
`[whispering]` as its tones, so the model decides sentence by sentence when to
sound animated; the service whispers `[whispering]` sentences as it does for
Turbo. A reply that writes `[excited]`, `(excitedly)` or `[animated]` gets
`[expressive]` (`VoiceTags.Synonyms`). Other engines never read the tag.

The owner sets all four values in **Companion › Voice › Chatterbox Original
style** (shown while that engine speaks or is chosen; *Use Resemble's
suggestions* goes back to the defaults). They are kept on that PC in
`chatterbox-style.json` (`ChatterboxStyle` in Martlet.Core) and sent with every
reply to that engine as `voice_style` (the gateway refuses it on any other route
and checks the ranges), and the service reads them as the request's `style`.
`/status` reports `style` (`default`, `expressive_parts` and `last`, the style
the last reply asked for).

The service speaks each part of a piece whole with its own values: the
reference's voice conditionals are kept per recording, as for Turbo, and only
the exaggeration in them changes from part to part. chatterbox-tts 0.1.7's
decoder always runs two rows (with and without the text, for CFG), so the text
always goes in twice; at CFG weight 0 the second row changes nothing. Each part
may take at most 100 speech tokens plus 10 per text token
(`MARTLET_CHATTERBOX_ORIGINAL_TOKENS_*`, at most 1,000); the measured parts used
at most 2.5 speech tokens per text token, 19% of their budget. A stopped reply
stops after the part it is in.

What the styles change, measured on the CPU (two starter voices, *Annie* and
*arctic-bdl*, two sentences, two takes each, so 8 takes per style; pitch with
librosa's pYIN):

| Style | Length | Pitch spread (SD) | Pitch range (5-95%) | Level |
| --- | --- | --- | --- | --- |
| General (0.5, 0.5) | 3.01 s | 3.20 semitones | 11.7 semitones | -20.0 dBFS |
| Expressive (0.7, 0.3) | 3.00 s | 3.29 semitones | 11.1 semitones | -18.6 dBFS |

With Resemble's suggested values, Expressive was 1.4 dB louder (1.9 dB for the
male voice); pitch and pace changed no more than two takes of the same style
differ. A first look at exaggeration 1.0 (6 takes, cut short) found the male
voice about 5 dB louder than General, with pitch again no different than takes
differ. Nobody listened, so whether it sounds more expressive is NOT RUN; the
owner can raise the Expressive exaggeration in Companion › Voice.

## Licences and consent

| Part | Source | Licence |
| --- | --- | --- |
| Runtime | [`chatterbox-tts` 0.1.7](https://pypi.org/project/chatterbox-tts/0.1.7/) with `transformers` 5.2.0, `resemble-perth` 1.0.1 | MIT (code) |
| Model | [`ResembleAI/chatterbox-turbo`](https://huggingface.co/ResembleAI/chatterbox-turbo) at revision `749d1c1a46eb10492095d68fbcf55691ccf137cd` | MIT (model card, checked 2026-10-02) |
| Model | [`ResembleAI/chatterbox-nano`](https://huggingface.co/ResembleAI/chatterbox-nano) at revision `71ccd1d0081b430592cea481f4307e764e07bc64` | MIT (checked 2026-10-07) |
| Model | [`ResembleAI/chatterbox`](https://huggingface.co/ResembleAI/chatterbox) at revision `5bb1f6ee58e50c3b8d408bc82a6d3740c2db6e18` | MIT (checked 2026-10-07) |

Nothing is downloaded until the owner installs a Chatterbox host role. The
role's terms (shown by Martlet before **Install**, whose click confirms them,
and by `martlet-host add chatterbox`) name the runtime, the download, the MIT
licence and the watermark; handing Speaking to Chatterbox repeats the licence in
its confirmation. Every reply carries Resemble AI's imperceptible **Perth
watermark**, which Martlet keeps (the watermarker's weights ship inside the
pinned `resemble-perth` wheel, so it works offline). As with F5, the
voice-rights confirmation for each recording is the owner's assertion, not legal
clearance.

## How it runs

- **Host role `chatterbox`** (`deploy/host/roles/chatterbox`, port 50083,
  NVIDIA GPU with 6 GB+). The image is built on the host from
  `workers/chatterbox/host/Dockerfile`: `python:3.12.10`, hash-locked PyTorch
  2.8.0 CUDA 12.8 (kernels for compute capability 7.0 to 12.0: GeForce GTX 16
  and RTX 20 series through RTX 50 series; older cards such as the GTX 10
  series are not supported, and the worker says so when it loads),
  `transformers` 5.2.0 and the rest of
  `chatterbox-tts` 0.1.7's closure (`host/requirements.lock`; Gradio and the
  other-language extras are left out). Its dependencies conflict with
  Qwen3-TTS/GPT-SoVITS, so it has its own container.
- **Provisioning** (`martlet-chatterbox provision`) downloads and verifies, at
  the one pinned revision, `t3_turbo_v1.safetensors` (1,915,480,052 bytes,
  SHA-256 `fcf1f8c1…cdf87`), `s3gen_meanflow.safetensors`, `ve.safetensors` and
  the tokenizer files into the `martlet-chatterbox-models` volume. The built-in
  voice `conds.pt` is **not** downloaded, so every reply needs the reference the
  desktop sends. `martlet-chatterbox warm` loads the model so the first reply
  does not wait. Loading is local only (`HF_HUB_OFFLINE`), never
  `from_pretrained`.
- **Service** `workers/chatterbox/martlet_chatterbox_host.py` listens on
  127.0.0.1:50083 only and speaks the f5 role's protocol (`/status`, `/warmup`,
  `/synthesize`, `/cancel`) and `martlet.f5.worker` 1.0 events. Per text chunk it
  speaks with the voice conditionals of the reply's reference and sends
  contiguous 24 kHz mono 16-bit frames as they are made (see **Speed**;
  `model.sr` is 24 kHz; anything else is resampled). Like the f5 role after PR #248, a reply waits
  for a busy model instead of failing, and a failed model is reloaded for the
  next reply. Cancellation is discard-only.
- **Speed** (image `martlet-chatterbox:4`, `FastTurbo` in the service). The
  same words and sound, sooner: the reference's voice conditionals (what
  `generate(text, audio_prompt_path=...)` recomputed for every sentence, about
  140 ms each) are computed once per recording (by its SHA-256, the last two
  kept); loading also warms up the conditioning code, the decoder and the CUDA
  graph, which the first reply after a start used to pay (about 7 s), and
  (image `martlet-chatterbox:5`) the Perth watermarker, which runs on the CPU
  and took 824 ms on its first call and about 20 ms after (measured in the
  service's container), so the first reply after each start no longer waits
  most of a second for its first audio; and T3's
  token-by-token decoding replays one captured CUDA graph per token over a
  static KV cache (2,048 tokens, `MARTLET_CHATTERBOX_GRAPH_TOKENS`) with the
  library's own sampling, instead of launching hundreds of small kernels per
  token. Measured on the RTX 5080 under Docker Desktop: 6 ms a token instead of
  17-25 ms, with identical logits; a 3.8 s sentence in about 0.85 s instead of
  2.7-3.5 s in isolation.
- **Streaming** (on with the CUDA graph; `MARTLET_CHATTERBOX_STREAM=0` turns it
  off). Each piece is sent as it is made: the first audio after about 12 speech
  tokens (half a second of speech), then 25, 50 and 100 more at a time. Each
  chunk decodes every token so far with the same noise and holds back the last
  3 tokens' frames until their lookahead is known (the library's own
  `finalize=False` is broken in 0.1.7: it trims the encoder output but not its
  mask); the vocoder carries its source and an 8-frame mel overlap across
  chunks and the 160 ms where chunks meet is crossfaded, as CosyVoice 2 (where
  S3Gen comes from) streams. Every chunk carries the Perth watermark. Measured
  against a whole-piece decode of the same tokens: the same length, a log-mel
  difference of 0.1-0.39 where two whole decodes with different noise differ by
  0.46-0.82, sample jumps at the seams no larger than elsewhere in the speech,
  and the watermark detected as on the whole piece. Through the service, with
  the character showing, the first audio of a piece came after 350-400 ms
  whatever its length (1.2-4.2 s before), and the whole piece finished sooner
  too (7.7 s of speech in 2.7 s instead of 4.2 s). A piece too long for the
  cache, a machine without CUDA or a capture failure is spoken whole with the
  library's own decoding; `MARTLET_CHATTERBOX_FAST=0` turns the graph (and so
  streaming) off. Each reply logs how much speech it made, how long it took
  and when its first audio left (`docker logs`), never its text; the host's
  gateway log (Diagnostics, *Host gateway*, readable from every PC) says the
  same for each reply and whether it was made slower than real time. A host
  slower than real time (a smaller or busy graphics card, for example one that
  also runs Audio2Face lip-sync) makes the next chunk after the speakers have
  played the last, so the desktop's voice pauses until it arrives (up to 10 s)
  rather than being cut short. A stopped reply (talked over, replaced, or its
  desktop gone) is checked for at every speech token, so the model is free for
  the next reply at once instead of after the rest of its chunk (seconds on a
  busy card, which the next reply was refused for with `worker.busy`). See
  [Voice latency](VOICE_LATENCY.md).
- **Reliability** (image `martlet-chatterbox:6`):
  - *A broken graphics context restarts the service.* A CUDA error such as a
    device-side assert or an illegal memory access leaves the process unable to
    use the card until it exits (resemble-ai/chatterbox#435), so reloading the
    model in place failed every reply until someone restarted the container.
    After a failed reply (or idle check) the service runs a tiny operation on
    the card; if that fails too, the reply says *the voice service is
    restarting*, new replies are refused with why, and the service exits
    (code 75) so Docker (`restart: unless-stopped`) starts it with a fresh
    context and warms it up again. Other failures still drop the model and
    reload it for the next reply, now after its CUDA graph and kept
    conditionals have let go of it, so the card never needs room for two.
  - *A piece Turbo doesn't stop is cut.* Turbo has no stop detector of its own
    (the multilingual model's alignment analyzer isn't used), so a piece that
    misses its stop token babbles or hisses up to 1,000 speech tokens, 40 s
    (resemble-ai/chatterbox#424, #508, #543). Each piece now gets at most 100
    speech tokens plus 25 per text token and 100 per tag (`MARTLET_CHATTERBOX_TOKENS_*`).
    Measured with three starter voices (270 pieces, none ran away): a text
    token took 6.7 speech tokens (median), at most 11.5, and up to 16 in digit
    strings ("3.14159265358979"); a tag up to about 35 more; the busiest piece
    used 41% of its budget. The service log says when a piece was cut.
  - *Double quotation marks are left out* of what Turbo reads: it can say them
    as a breath or a sigh (resemble-ai/chatterbox#433; here they added up to 7
    speech tokens, 0.3 s, to a sentence). A piece of nothing but quotation marks
    is an empty piece, not the library's stand-in sentence.
  - *Decoding asks the GPU less often:* whether the stop token came is checked
    every 4 tokens and at each streamed chunk instead of after every token, and
    a step whose probabilities aren't finite draws the stop token instead of
    reaching the sampler (where NaN probabilities can trigger a device-side
    assert). The tokens are identical (18 of 18 pieces, same seeds, against the
    previous decoder); 11.3 ms a token became 11.1 on an RTX 4070, and 30 ms
    with another program keeping the card busy stayed 30.
  - *Warm-up covers the streamed path.* Loading also streams one piece long
    enough for several chunks, so the first reply after a start no longer pays
    for the vocoder's carried-over source and the growing decodes. On the RTX
    4070: first audio of the first reply 581 ms instead of 709 (later replies
    about 400 ms); a voice's first reply still computes its conditionals
    (about 150-190 ms, then kept).
  - *Idle check.* After 5 minutes without a reply
    (`MARTLET_CHATTERBOX_IDLE_CHECK_SECONDS`, 0 turns it off) the service makes
    one short piece on the warm-up's synthetic voice and throws it away (about
    1 s of the card). Windows moves an idle model out of graphics memory when
    other programs fill the card, and the next reply then waited for it to come
    back; the idle check brings it back first, logs `Idle check took ... ms`
    when that was slow, and finds a broken context before a reply does. A
    reply stops it at its next speech token (the decoder step it is in, at most
    a few hundred ms, finishes first). `/status` reports it as `idle_check`
    (`checks`, `every_seconds`, `fastest_ms`, `last_ms`).
- **Gateway** relay `Martlet.Gateway.F5.ChatterboxRelay` serves it on its own
  route `martlet.gateway.chatterbox-synthesis.v1`
  (`/martlet/v1/inference/chatterbox-synthesis`) with the shared voice
  destination, so one voice list serves every engine on a host.

## Sharing the graphics card

Chatterbox makes speech token by token, so every other program using the same
graphics card slows it down: with another program keeping an RTX 4070 busy, a
token took 30 ms instead of 11 (25 make a second of speech). On **Windows**
(Docker Desktop's WSL 2 machine, where Martlet's host service runs on a PC)
there is a second, worse effect: when the card's memory runs short, the NVIDIA
driver quietly moves part of it into main memory instead of failing, as it does
on Linux. Measured on DIVA's RTX 4070 under Docker Desktop: a program holding
7 GB beside the voice, lip-sync, speech-to-text and singing (more than the
card's 12 GB together) failed nothing; the idle programs' memory was moved out
(the card showed 3.9 GB in use afterwards), and the voice's next reply started
after 1,033 ms instead of about 420 (643 ms with the idle check above). In use,
the same card, shared with lip-sync, speech-to-text and other GPU experiments,
made replies' first audio 10-25 s late, took 64 s for 3 s of speech once, and
a first reply after two idle hours waited 51 s.

So Martlet warns when a voice engine shares a Windows computer's card with other
roles (lip-sync, Thinking, Deep thinking, Listening, Singing, or Thinking in
Ollama on this PC): on Companion > Voice > Voice engine (`SpeakingEngineSharedGpu`,
also before an engine is set up there), on the computer in Devices
(`SelectedDeviceSharedGpu`) and in MCP's `host_service_status` (`sharedGpu`).
A Windows computer is this PC's own host service, or a host whose report names
Windows or a WSL 2 kernel. For a steady voice give it a card of its own: move
the other jobs (or the voice) to another computer, or keep the card's memory
well below full. NVIDIA's *CUDA - Sysmem Fallback Policy* (*Prefer No Sysmem
Fallback*) makes Windows programs fail instead of slow down when the card is
full; whether it reaches programs in Docker Desktop is not verified.

## Reference recordings

Chatterbox needs a recording **longer than 5 seconds** (`prepare_conditionals`
asserts it; it conditions on up to 10/15 s). All of Martlet's starter voices
are 6.5-11 s. Handing Speaking to Chatterbox skips shorter own voices when it
picks a default, and **Use** on a shorter voice explains why it can't. The
transcript is sent but Chatterbox does not need it.

## Tags

The pinned tokenizer (`added_tokens.json`) defines 19 tags. Martlet's catalog
for Chatterbox (`SpeechEngines.ChatterboxTurboTags` in Martlet.Core) passes 17:

- **Non-word sounds:** `[laugh]` `[chuckle]` `[sigh]` `[gasp]` `[cough]`
  `[clear throat]` `[groan]` `[sniff]` `[shush]`
- **Tones of voice:** `[happy]` `[sarcastic]` `[surprised]` `[angry]` `[fear]`
  `[crying]` `[whispering]` `[dramatic]`

`[advertisement]` and `[narration]` (reading genres) are left out. Turbo has no
`exaggeration` or `cfg_weight` setting: those belong to the original 500M
Chatterbox. Turbo's loader turns the exaggeration input (`emotion_adv`) off, its
decoding has no CFG step, and `generate` logs that it ignores both. So these
style tokens are its only emotion control, and the measurements below show that
only `[whispering]` changes the voice (because Martlet makes the whisper).
Companion › Voice says so in Chatterbox Turbo's rundown
(`VoiceEngineAbilities-chatterbox`: voice cloning yes, laughs & sighs yes,
emotions whispering only).

What Resemble documents (checked again 2026-10-07): the
[model card](https://huggingface.co/ResembleAI/chatterbox-turbo), the
[README](https://github.com/resemble-ai/chatterbox), the
[ONNX](https://huggingface.co/ResembleAI/chatterbox-turbo-ONNX) and
[Nano](https://huggingface.co/ResembleAI/chatterbox-nano) model cards and the
[demo page](https://resemble-ai.github.io/chatterbox_turbo_demopage/) name
`[cough]`, `[laugh]`, `[chuckle]` "and more". The official Turbo app
(`gradio_tts_turbo_app.py`) offers exactly the nine sounds as `EVENT_TAGS`.
Resemble does not document the tones anywhere; they are the tokenizer's other
style tokens. `chatterbox-tts` 0.1.7 is still the latest release, and the Turbo
code changed after it only to add Nano. Community reports differ. In
resemble-ai/chatterbox#492, one user says all 19 tags work in the ONNX export,
"sometimes" with artifacts, without audio or measurements. In #557, the ten
style tokens change nothing under `mlx-audio`.

The ONNX export is the same model (the same weights and tokenizer) for another
runtime. Its example decodes greedily: it always takes the most likely speech
token, where Martlet samples (temperature 0.8, top-p 0.95). Greedy decoding
does not work with Turbo: 20 of 35 greedy pieces (*Annie*, every tag) never
drew the stop token and ran to the 1,000-token limit, about 33 s for a 4 s
sentence.

Our measurements (2026-10-07, the library's own sampled decoding, as Martlet
decodes) agree with #557. Every tag was tested on two starter voices (*Annie*
and *arctic-bdl*), two sentences and two seeds: 8 takes for each tag. Each take
was compared with the same voice, sentence and seed without a tag. Two plain
takes of the same sentence differ by 0.23 s, 0.6 LUFS and 1 semitone of pitch
on average.

- **All nine sounds work.** Each added 0.6-0.9 s (sd 0.2-0.5 s) in every take.
  Parakeet speech-to-text still heard every word, and wrote some sounds out
  ("Hm" for `[clear throat]`, "Uh" for `[groan]`, "sh" for `[shush]`). A speech
  emotion classifier (`superb/wav2vec2-base-superb-er`) rated `[laugh]` and
  `[chuckle]` takes "happy" at 0.17-0.18 against 0.03 without a tag.
- **`[whispering]` rarely works.** One take of eight whispered (17% voiced
  against 75%, 8.8 LUFS quieter) and one more half-whispered. The others were
  as voiced as plain speech (55-78%). An earlier check with three takes on
  *Annie* and with three more voices found the same.
- **The other tones do nothing measurable.** `[angry]`, `[happy]`, `[crying]`,
  `[fear]`, `[surprised]`, `[sarcastic]`, `[dramatic]`, `[narration]` and
  `[advertisement]` changed length, level, pitch and pitch range by no more
  than two plain takes differ. The emotion classifier's scores did not move
  (for example "angry" 0.37 with `[angry]`, 0.43 without; "sad" 0.00 with
  `[crying]`).

Two ways to make the model itself whisper did not work. The vocoder (HiFT, the
part that makes the waveform) takes the pitch from the mel spectrogram. When we
set its pitch input to 0, not one sample changed. When we conditioned the model
on a whispered copy of the reference recording, three of four voices stayed
11-30% voiced.

The other tones stay in the catalog because the character's emotes and motions
follow them as cues. The Chatterbox voice does not perform them. When a reply
writes another form of a tag (`[whisper]`, `*whispers*`, `{hushed}`), Martlet
uses the catalog's tag (`VoiceTags.Synonyms`) and does not silence the sentence.

**Whispering.** The service makes the whisper itself (image
`martlet-chatterbox:7`). It whispers each sentence that starts with
`[whispering]`, from the tag to the next `.`, `!` or `?` (or the end of the
piece). Other sentences in the same piece keep the normal voice. Turbo makes
the sentence as usual, with the tag, and `Whisperer` then changes it:

1. It cuts the speech into 25 ms frames.
2. For each frame, it finds the spectral envelope (the shape of the mouth) with
   linear prediction of order 26 at 24 kHz.
3. It shapes white noise with this envelope, in place of the buzz of the voice.
   This is what breath does in a mouth that whispers.
4. It removes everything below 300 Hz, where a whisper has no sound.
5. It sets each frame to the level of the original frame minus 6 dB
   (`MARTLET_CHATTERBOX_WHISPER_DB`).
6. It joins the frames with sine windows, so the noise level stays smooth.

The whisper is made before the Perth watermark, so every whisper has the
watermark. It works on streamed chunks as they arrive and on whole pieces. It
keeps back at most 40 ms of audio, so the first audio does not come later. It
uses about 12 ms of CPU for each second of speech.

We measured it through the service on four starter voices (*Annie*,
*arctic-bdl*, *lj-speech*, *woollybee*), with two sentences and two takes each:

- 1-15% of a whispered sentence was voiced. The pitch tracker calls some breath
  voiced. Plain sentences were 71-86% voiced.
- Whispered sentences were 6.4-8.9 LUFS quieter than plain sentences.
- The local Parakeet speech-to-text understood plain and whispered sentences
  equally well (word error rate 0-1.6%).

`/status` reports `whisper`: `level_db`, and `parts` (the number of whispered
parts since the service started). The log line for each reply gives the number
of whispered parts.

The Thinking prompt (Companion › Prompts › *Voice sounds and tones*) lists both
groups, each under a line saying where its tags go: a sound inline where it
happens, a tone at the very start of the sentence it colors (each spoken piece
is synthesized on its own, so a tone never reaches the next sentence). The
prompt tells the model to start every sentence with the tone when it must keep
the tone, for example in a whispered reply. How tags reach the voice, and stay
out of the chat, is in [Conversation](CONVERSATION.md#voice-tags).

## Verification

`scripts\Invoke-MartletMcp.ps1` with `voice_tags` (engine `chatterbox`)
shows the catalog, the Thinking prompt and what the segmenter sends; `f5_voices`
lists the engine as the default with its `features` and `abilities`; `-Desktop` reads
`VoiceEngine-chatterbox`, `VoiceEngineAbilities-chatterbox` (its rundown: voice
cloning yes, laughs & sighs yes, emotions whispering only), `VoiceEngineRunsOn-chatterbox`
(an NVIDIA GPU, about 3.7 GB of graphics memory, up to 4.2 GB, 6 GB+ card),
`VoiceEngineFeatures-chatterbox` (its chips) and `VoiceEngineUse-chatterbox` on Companion > Voice.
`workers/chatterbox/tests` (stdlib unittest, fixture engine) and
`ChatterboxRelayTests` cover the service and the relay; with
`MARTLET_CHATTERBOX_LIVE_ENDPOINT` set to a running service the latter speaks a
tagged reply through a real paired gateway. `provision --fixture` (with
`MARTLET_CHATTERBOX_FIXTURE_REAL_IDENTITY=1`) runs the service as a
**FIXTURE - NOT AI** tone generator for that plumbing check.
`voice_engine_check` (MCP) speaks a sentence with a running service through
the production relay, a real loopback gateway and the desktop's client. It
reports the service's state, error, torch/CUDA versions, idle check
(`idleCheck`) and whispering (`whisper`). It also reports how much of the
returned audio is voiced (`voicedShare`). A plain sentence gives about 0.6-0.9.
A sentence that starts with `[whispering]` gives nearly 0.

Verified on a GeForce RTX 5080 (compute capability 12.0) under Docker Desktop:
the image builds, `provision` verifies the pinned files, `warm` loads the
model, and `voice_engine_check` returned 3-4 s of audible 24 kHz speech for
tagged sentences (about 0.7x real time once warm). The PyTorch 2.6.0 CUDA 12.4
image before it failed there with "no kernel image is available", and every
reply also failed because the worker user could not create its private
`requests` folder; image `martlet-chatterbox:2` fixes both, and updating a host
that already has the role rebuilds it. Images `martlet-chatterbox:3` and `:4`
add the speed-ups and streaming above, `:5` the watermarker warm-up and
per-token cancellation, and `:6` the reliability changes; updating a host
rebuilds it the same way. Image `:6`'s service was checked on an RTX 4070 under
Docker Desktop (the `:5` image with the new service mounted): the measurements
above, `voice_engine_check` (4.2 s of audible speech for a quoted sentence with
`[laugh]`, first audio 886 ms with a new voice, 0.61x real time) and the
worker's unit tests in the image (a broken context restarts the service, the
idle check, the token budget, quotation marks).

We checked the whispering of image `:7` on the RTX 5080 under Docker Desktop.
We used the `:6` image with the new service mounted, next to the running `:6`
service. The checks were the measurements above, the worker's unit tests in
the image, and `voice_engine_check` through the production relay:

- A plain sentence: `voicedShare` 0.63, -25.3 dBFS.
- The same sentence with `[whispering]`: `voicedShare` 0.02, -33 dBFS, first
  audio after 824 ms.
- The same sentence with `[whispering]` on the old `:6` service: `voicedShare`
  0.69, -27.7 dBFS (no whisper).

**NOT RUN:** voice likeness and how the tags and the whisper sound (nobody
listened; the whisper was measured and transcribed only, and streamed
speech was compared with whole-piece decodes by measurement only), a real
sticky CUDA error inside the running service (unit tests cover the restart
path; on the RTX 4070 the probe reported a real device-side assert as broken
and Docker restarted a container exiting with code 75), and Linux hosts for the
shared-card comparison.
