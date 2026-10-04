# Chatterbox Turbo voice engine

Martlet's **default** self-hosted voice engine, next to [F5-TTS](F5_VOICE.md) and
[XTTS-v2](XTTS_VOICE.md) (both stay selectable in Companion > Voice > Voice
engine). Chatterbox Turbo (Resemble AI) copies a voice from the same reference
recordings (Companion > Voice > Voices) and, unlike the others, speaks
**tags**: non-word sounds such as `[laugh]` and `[sigh]` and tones of voice such
as `[whispering]`, written inline in the reply.

## Licences and consent

| Part | Source | Licence |
| --- | --- | --- |
| Runtime | [`chatterbox-tts` 0.1.7](https://pypi.org/project/chatterbox-tts/0.1.7/) with `transformers` 5.2.0, `resemble-perth` 1.0.1 | MIT (code) |
| Model | [`ResembleAI/chatterbox-turbo`](https://huggingface.co/ResembleAI/chatterbox-turbo) at revision `749d1c1a46eb10492095d68fbcf55691ccf137cd` | MIT (model card, checked 2026-10-02) |

Nothing is downloaded until the owner installs the `chatterbox` host role. The
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

`[advertisement]` and `[narration]` (reading genres) are left out. Turbo's
`generate` ignores the older `exaggeration`/`cfg_weight` sliders, so these style
tokens are its only emotion control.

What Resemble documents (checked 2026-10-04): the
[model card](https://huggingface.co/ResembleAI/chatterbox-turbo) and the
[README](https://github.com/resemble-ai/chatterbox) name `[cough]`, `[laugh]`,
`[chuckle]` "and more"; the official Turbo apps (`gradio_tts_turbo_app.py` and
the `chatterbox-turbo-demo` Space) offer exactly the nine sounds as
`EVENT_TAGS`. The tones are the tokenizer's other style tokens and are not
documented. Community reports differ (resemble-ai/chatterbox#492: all 19 work in
the ONNX export, with occasional artifacts; #557: the ten style tokens change
nothing under `mlx-audio`), and a measurement here agrees with the latter: on
the starter voice *Annie*, five takes each,
`[whispering]` and `[angry]` changed neither the level (-27.1 dBFS against
-26.5 without a tag) nor how noise-like the voice is (spectral flatness 0.158
and 0.175 against 0.170; a whisper would be far flatter), while `[laugh]` added
0.56 s and a flatter, louder stretch. The tones stay in the catalog because
characters' emotes and motions can follow them as cues.

The Thinking prompt (Companion › Prompts › *Voice sounds and tones*) lists both
groups, each under a line saying where its tags go: a sound inline where it
happens, a tone at the very start of the sentence it colors (each spoken piece
is synthesized on its own, so a tone never reaches the next sentence). How tags
reach the voice, and stay out of the chat, is in
[Conversation](CONVERSATION.md#voice-tags).

## Verification

`scripts\Invoke-MartletMcp.ps1` with `voice_tags` (engine `chatterbox`)
shows the catalog, the Thinking prompt and what the segmenter sends; `f5_voices`
lists the engine as the default with its `features`; `-Desktop` reads
`VoiceEngine-chatterbox`, `VoiceEngineFeatures-chatterbox` (its chips, including
*Laughs & sighs* and *Emotions*) and `VoiceEngineUse-chatterbox` on Companion > Voice.
`workers/chatterbox/tests` (stdlib unittest, fixture engine) and
`ChatterboxRelayTests` cover the service and the relay; with
`MARTLET_CHATTERBOX_LIVE_ENDPOINT` set to a running service the latter speaks a
tagged reply through a real paired gateway. `provision --fixture` (with
`MARTLET_CHATTERBOX_FIXTURE_REAL_IDENTITY=1`) runs the service as a
**FIXTURE - NOT AI** tone generator for that plumbing check.
`voice_engine_check` (MCP) speaks a sentence with a running service through
the production relay, a real loopback gateway and the desktop's client, and
reports the service's state, error, torch/CUDA versions and idle check
(`idleCheck`).

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

**NOT RUN:** voice likeness and how the tags sound (nobody listened; streamed
speech was compared with whole-piece decodes by measurement only), a real
sticky CUDA error inside the running service (unit tests cover the restart
path; on the RTX 4070 the probe reported a real device-side assert as broken
and Docker restarted a container exiting with code 75), and Linux hosts for the
shared-card comparison.
