# Dia voice engine

[Dia](https://github.com/nari-labs/dia) by Nari Labs is a self-hosted voice
engine next to [Chatterbox Turbo](CHATTERBOX_VOICE.md) (the default),
[F5-TTS](F5_VOICE.md), [XTTS-v2](XTTS_VOICE.md) and
[GPT-SoVITS](GPT_SOVITS_VOICE.md). It copies a voice from the same reference
recordings (Companion > Voice > Voices) and **performs nonverbal cues** written
in the reply, such as `(laughs)`, `(sighs)`, `(coughs)` and `(gasps)`. It is
**English only** and is chosen on Companion > Voice > Voice engine; it is never
preselected.

## Licences and consent

| Part | Source | Licence |
| --- | --- | --- |
| Code | [`nari-labs/dia`](https://github.com/nari-labs/dia) at commit `876125e461a03b157ec905b0fe8b57a0f8b9e7a0` (no tagged releases; the last change under `dia/` is `998fc25`). The six `dia/*.py` files and `LICENSE` are fetched by SHA-256 when the image is built. | Apache-2.0 |
| Model | [`nari-labs/Dia-1.6B-0626`](https://huggingface.co/nari-labs/Dia-1.6B-0626) at revision `ef2795fcc29c5abe6ffc91fd33808588b49bbc66`: `pytorch_model.bin` (6,444,787,333 bytes, SHA-256 `8a5106c0…55b9`, byte-identical to `dia-v1.pth`) and `config.json` (1,396 bytes, `4f4e9c50…e04f`) | Apache-2.0 |
| Codec | [Descript Audio Codec](https://github.com/descriptinc/descript-audio-codec) 1.0.0 and its 44 kHz 8 kbps weights (release 0.0.1 `weights.pth`, 306,717,287 bytes, `a88eed82…61aa`), the codec `dia/model.py` uses | MIT |

Nari Labs forbids using Dia to imitate real people without their permission,
for deceptive content, or for anything illegal or harmful. Nothing is
downloaded until the owner installs the `dia` host role: its terms (shown by
Martlet before **Install**, whose click confirms them, and by
`martlet-host add dia`) name the source, both downloads, their licences and
that restriction. As with every engine, the voice-rights confirmation for each
recording is the owner's assertion, not legal clearance.

## How it runs

- **Host role `dia`** (`deploy/host/roles/dia`, port 50084, NVIDIA GPU with
  8 GB+; Dia's README measures about 4.4 GB of VRAM in float16). The image is
  built on the host from `workers/dia/host/Dockerfile`: `python:3.12.10`,
  hash-locked PyTorch 2.6.0 CUDA 12.4 and descript-audio-codec 1.0.0 at the
  versions in Dia's own `uv.lock` (`host/requirements.lock`), then the pinned
  Dia source. Its own container, volume and environment.
- **Provisioning** (`martlet-dia provision`) downloads and verifies the three
  pinned files into the `martlet-dia-models` volume. **Nothing is downloaded at
  run time:** the worker loads Dia with `Dia.from_local` and the codec with
  `dac.DAC.load` from those paths (`HF_HUB_OFFLINE`), instead of Dia's own
  `from_pretrained` and `dac.utils.download()`. `martlet-dia warm` then loads
  the model so the first reply does not wait.
- **Service** `martlet_dia.host` listens on 127.0.0.1:50084 only and speaks the
  f5 role's protocol (`/status`, `/warmup`, `/synthesize`, `/cancel`) and the
  `martlet.f5.worker` 1.0 event stream. The model runs in a separate worker
  process it starts, warms and **restarts if it dies**; a reply that arrives
  while another is still generating **waits** (up to 30 s) instead of failing,
  like F5 since PR #248. `/status` reports the state, the worker identity
  (artifacts, source commit) and Dia's nonverbal cue list.
- **Prompt:** Dia clones from an audio prompt plus its exact transcript, so
  each piece of a reply becomes `[S1] <reference transcript> <reply text>`
  with the reference as the audio prompt; Dia generates audio only for the
  text after the reference. Martlet always speaks as **one speaker**: any
  `[S1]`/`[S2]` in the reply or transcript is removed, so the cloned voice is
  the only voice. Reply text otherwise reaches Dia unchanged, so the cues
  Martlet keeps for Dia are performed. Long replies are split at sentence,
  then clause boundaries to fit Dia's 1,024-byte text window and 3,072 audio
  tokens (about 35 s, reference included); a reference whose transcript or
  length leaves no room is refused with `reference_too_long`. Each piece's
  generation is capped at a generous length for its text, and sampling uses
  `Dia.generate`'s defaults (CFG 3.0, temperature 1.2, top-p 0.95, top-k 45),
  seeded from the voice and text.
- **Output:** Dia's codec produces 44.1 kHz audio; the worker resamples it to
  24 kHz and sends signed 16-bit mono PCM (`SpeechOutputFormat.Pcm24KhzMono16Le`)
  in frames of at most 4,800 samples after each piece is generated (Dia does
  not stream within a piece). Stop discards the reply; the model finishes the
  piece it is generating (discard only).
- **Gateway:** `Martlet.Gateway.Dia.DiaRelay` is the F5 relay
  (`Martlet.Gateway.F5.F5RelayWorker`) on Dia's own route,
  `martlet.gateway.dia-synthesis.v1` at `/martlet/v1/inference/dia-synthesis`,
  model `dia-1.6b-0626`, with the same payload and checks (model weights
  identity on the first event, contiguous frames, chunk completions). Host
  configuration kind: `dia`.

## Nonverbal tags

Dia's README at the pinned commit lists `(laughs)`, `(clears throat)`,
`(sighs)`, `(gasps)`, `(coughs)`, `(singing)`, `(sings)`, `(mumbles)`, `(beep)`,
`(groans)`, `(sniffs)`, `(claps)`, `(screams)`, `(inhales)`, `(exhales)`,
`(applause)`, `(burps)`, `(humming)`, `(sneezes)`, `(chuckle)` and
`(whistles)`, and warns that overusing them or using unlisted ones causes
artifacts. `SpeechEngines.DiaTags` registers the conversational ones (all but
the singing, beep, clapping, applause, burp and scream cues) in Martlet's
[voice tag catalog](CHATTERBOX_VOICE.md): the Thinking prompt then lists exactly
these when Dia speaks, the speech segmenter keeps them for Dia's voice, and
chat text and captions drop them. MCP `voice_tags` with `{"engine":"dia"}`
shows the tags, what the voice and the chat receive and the prompt.

## Desktop

**Companion > Voice > Voice engine** lists Dia (`SpeakingEngine-dia`);
`SpeakingEngineStatus` reads where it speaks and its Apache-2.0 licence, and
`SpeakingEngineTags` its cues. Choosing it hands Speaking to Dia on the
computer that already speaks (installing the `dia` role there after
confirmation) or is used the next time Speaking goes to a computer. The
Devices map lists the role as "Speaking (Dia)". MCP `f5_voices` lists Dia in
`engines` and reports `chosenEngine` and the speaking route's `engine`.

## Verification

- `python -m unittest discover -s tests -t .` in `workers/dia` (with
  `PYTHONPATH=workers/dia`) runs the service with the **FIXTURE - NOT AI** tone
  engine: contiguous bounded frames, chunk completions, single-speaker
  prompts with cues unchanged, splitting, refusal of an over-long reference and
  restart of a killed worker. It also passed inside the built Linux image.
- `tests/Martlet.Gateway.Tests/DiaRelayTests.cs` checks that a host offers Dia
  next to F5 and XTTS-v2 on its own route; with `MARTLET_DIA_LIVE_ENDPOINT`
  (and `MARTLET_DIA_LIVE_FIXTURE=1` for a fixture-provisioned service) it
  speaks through the real gateway and paired client, and
  `MARTLET_DIA_LIVE_OUTPUT` saves the WAV. Through the fixture service: 4.0 s
  of audio via gateway and client.
- MCP on 2026-10-02 (disposable data directory): `f5_voices` lists `dia`
  (model `dia-1.6b-0626`, Apache-2.0, 8 GB, 14 tags, not default) with
  Chatterbox Turbo still the default; on the desktop, Companion > Voice >
  Voice engine starts on Chatterbox Turbo, and selecting
  `SpeakingEngine-dia` changes `SpeakingEngineStatus` to Dia with its
  Apache-2.0 licence, `SpeakingEngineTags` to Dia's cues and `chosenEngine` to
  `dia`. `voice_tags` with `{"engine":"dia"}` keeps `(laughs)` and `(sighs)`
  for the voice, drops Chatterbox's `[laugh]`, hides the cues from the chat
  text and lists Dia's cues in the prompt (its example now uses the engine's
  own first tag, `(laughs)`).
- 2026-10-02, Windows with Docker Desktop, **CPU only** (no NVIDIA GPU on that
  machine): the image built; `martlet-dia provision` verified the three pinned
  files and `martlet-dia warm` loaded the real model (72 s, offline). The
  worker cloned "Annie (cute, chatty)" (7.5 s reference) with the real model,
  checked with Whisper (`small.en`, which transcribes words but not laughs or
  sighs):
  - with the hotter settings of Dia's voice-clone example (CFG 4, temperature
    1.8), "Oh, that's so funny! (laughs) I really didn't expect that." gave
    3.45 s: all the words, with a one-second break where the laugh is; but
    "(sighs) Well, I suppose we have to start again tomorrow." gave 15.7 s of
    noise and no words;
  - with `Dia.generate`'s defaults (what Martlet uses), the sigh line gave
    3.7 s: 1.4 s of quiet breathy sound, then every word; the laugh line gave
    all the words but 7 s between "funny!" and "I", 1.5 s of silence then
    about 4.5 s of quiet sound. Martlet therefore also caps each piece at
    about 1 s per 9 characters plus 1.5 s per cue and 1 s (at least 3 s).
  Dia samples randomly, so cue timing varies between runs. CPU took 1-4
  minutes per sentence, longer than the 90 s request bound, so real speech
  through the gateway was not run on CPU.

**NOT RUN:** NVIDIA GPU warmup, latency and VRAM, `martlet-host add dia` on a
GPU host, live desktop conversation playback through Dia, and a listening
review of likeness and of how each cue sounds (no one listened to the output;
Whisper cannot hear laughs or sighs).
