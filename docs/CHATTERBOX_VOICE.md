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
  2.6.0 CUDA 12.4 (the same wheels as F5), `transformers` 5.2.0 and the rest of
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
  calls `ChatterboxTurboTTS.generate(text, audio_prompt_path=<the reference>)`
  and sends contiguous 24 kHz mono 16-bit frames (`model.sr` is 24 kHz;
  anything else is resampled). Like the f5 role after PR #248, a reply waits
  for a busy model instead of failing, and a failed model is reloaded for the
  next reply. Cancellation is discard-only.
- **Gateway** relay `Martlet.Gateway.F5.ChatterboxRelay` serves it on its own
  route `martlet.gateway.chatterbox-synthesis.v1`
  (`/martlet/v1/inference/chatterbox-synthesis`) with the shared voice
  destination, so one voice list serves every engine on a host.

## Reference recordings

Chatterbox needs a recording **longer than 5 seconds** (`prepare_conditionals`
asserts it; it conditions on up to 10/15 s). All of Martlet's starter voices
are 6.5-11 s. Handing Speaking to Chatterbox skips shorter own voices when it
picks a default, and **Use** on a shorter voice explains why it can't. The
transcript is sent but Chatterbox does not need it.

## Tags

The pinned tokenizer (`added_tokens.json`) defines 19 tags. Martlet's catalog
for Chatterbox (`SpeechEngines.ChatterboxTurboTags` in Martlet.Core) passes 17:

- **Sounds:** `[laugh]` `[chuckle]` `[sigh]` `[gasp]` `[cough]`
  `[clear throat]` `[groan]` `[sniff]` `[shush]`
- **Tones:** `[happy]` `[sarcastic]` `[surprised]` `[angry]` `[fear]`
  `[crying]` `[whispering]` `[dramatic]`

`[advertisement]` and `[narration]` (reading genres) are left out. Turbo's
`generate` ignores the older `exaggeration`/`cfg_weight` sliders, so these style
tokens are its only emotion control. How tags reach the voice, and stay out of
the chat, is in [Conversation](CONVERSATION.md#voice-tags).

## Verification

`scripts\Invoke-MartletMcp.ps1` with `voice_tags` (engine `chatterbox`)
shows the catalog, the Thinking prompt and what the segmenter sends; `f5_voices`
lists the engine as the default; `-Desktop` reads `SpeakingEngine`,
`SpeakingEngineTags` and `SetupF5About` on Companion > Voice.
`workers/chatterbox/tests` (stdlib unittest, fixture engine) and
`ChatterboxRelayTests` cover the service and the relay; with
`MARTLET_CHATTERBOX_LIVE_ENDPOINT` set to a running service the latter speaks a
tagged reply through a real paired gateway. `provision --fixture` (with
`MARTLET_CHATTERBOX_FIXTURE_REAL_IDENTITY=1`) runs the service as a
**FIXTURE - NOT AI** tone generator for that plumbing check.

**NOT RUN:** building the image, downloading the model and real GPU synthesis
(no GPU host was available to this change), so voice likeness, how the tags
sound, latency and VRAM are unmeasured.
