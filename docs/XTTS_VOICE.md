# XTTS-v2 voice engine

Martlet's second self-hosted voice engine, next to [F5-TTS](F5_VOICE.md). XTTS-v2
copies a voice from the same reference recordings (Companion > Voice > Voices)
and, unlike F5, **streams while it generates**: `Xtts.inference_stream` yields
audio before a sentence is finished, so the first sound of a reply arrives much
sooner. The engine review that recommended it is in
[Voice Studio](VOICE_STUDIO.md#candidate-review-2026-10-02).

## Licences and consent

| Part | Source | Licence |
| --- | --- | --- |
| Runtime | [`coqui-tts` 0.27.5](https://github.com/idiap/coqui-ai-TTS), Idiap's maintained fork of Coqui TTS (the abandoned `TTS` package is not used) | MPL-2.0 |
| Model | [`coqui/XTTS-v2`](https://huggingface.co/coqui/XTTS-v2) at revision `6c2b0d75eae4b7047358e3b6bd9325f857d43f77` | [Coqui Public Model License 1.0.0](https://huggingface.co/coqui/XTTS-v2/blob/main/LICENSE.txt): **non-commercial use of the model and of its output only** |

Nothing is downloaded until the owner installs the `xtts` host role. The role's
terms (shown by Martlet before **Install**, whose click confirms them, and by
`martlet-host add xtts`) name the runtime, the download and the CPML
non-commercial restriction; handing Speaking to XTTS repeats the restriction in
its confirmation. As with F5, the voice-rights confirmation for each recording
is the owner's assertion, not legal clearance.

## How it runs

- **Host role `xtts`** (`deploy/host/roles/xtts`, port 50081, NVIDIA GPU with
  4 GB+). The image is built on the host from `workers/xtts/host/Dockerfile`:
  `python:3.12.10`, hash-locked PyTorch 2.6.0 CUDA 12.4 (the same wheels as
  F5), `coqui-tts` 0.27.5, `transformers` 4.57.6 (`host/requirements.lock`).
- **Provisioning** (`martlet-xtts provision`) downloads and verifies the four
  files that must agree, all at the one pinned revision, into the
  `martlet-xtts-models` volume: `model.pth` (1,867,929,118 bytes, SHA-256
  `c7ea2000…6187`), `config.json`, `vocab.json` (tokenizer) and
  `speakers_xtts.pth`. The worker re-checks every SHA-256 before loading.
  `martlet-xtts warm` then loads the model so the first reply does not wait.
  The role's choices are the model (`xtts-v2`) and the language (`en` by
  default; the 17 XTTS-v2 languages).
- **Service** `workers/xtts/host/martlet_xtts_host.py` listens on
  127.0.0.1:50081 only and speaks the f5 role's protocol (`/status`,
  `/warmup`, `/synthesize`, `/cancel`) and `martlet.f5.worker` 1.0 event
  stream. The model runs in a child process it starts, warms and **restarts if
  it dies**; a reply that arrives while another is still generating **waits**
  (up to 30 s) instead of failing, like F5 since PR #248. `/status` reports the
  state and the last reply's timing (`first_audio_ms`, `audio_seconds`,
  `total_ms`; never text or audio), and each reply logs the same line.
- **Latency path:** the reference's conditioning latents
  (`get_conditioning_latents`) are computed once per reference revision and
  cached (8 voices); `inference_stream` (20 GPT tokens per piece, text split by
  sentence) yields float audio at 24 kHz, which is converted to signed 16-bit
  PCM and sent at once in frames of at most 4,800 samples. Output therefore
  matches Martlet's speech contract (24 kHz mono 16-bit,
  `SpeechOutputFormat.Pcm24KhzMono16Le`) without resampling. Stop/cancel ends
  generation between pieces.
- **Gateway:** `Martlet.Gateway.Xtts.XttsRelay` is the F5 relay
  (`Martlet.Gateway.F5.F5RelayWorker`) on XTTS's own route,
  `martlet.gateway.xtts-synthesis.v1` at `/martlet/v1/inference/xtts-synthesis`,
  with the same payload, checks (model weights identity on the first event,
  contiguous frames, chunk completions) and discard-only cancellation. A host
  can run `f5` and `xtts` side by side. Host configuration kind: `xtts`.
- **Engine catalog:** `Martlet.Core.Settings.SpeechEngines` lists every
  reference-voice engine (key, host role, route, path, model, weights licence,
  GPU memory). All engines share the voice destination `f5-host`, so one voice
  list and one rights confirmation serve them; a saved TTS gateway route
  (`GatewayF5`) may carry any engine's route.

## Desktop

**Companion > Voice > Voice engine** chooses F5-TTS or XTTS-v2 (automation ID
`SpeakingEngine`, status `SpeakingEngineStatus`; the choice is kept in
`speaking-engine.txt`). When a computer already speaks, choosing the other
engine hands Speaking to that engine on the same computer, installing its role
there first after confirmation; otherwise the choice is used the next time
Speaking goes to a computer (Devices, or **Use XTTS-v2 on this PC**). The
speaking route records the engine's route, and the speech client asks the host
for that route and model. The Devices map lists both roles ("Speaking
(F5-TTS)", "Speaking (XTTS-v2)").

## Verification

- `python -m unittest discover -s workers/xtts/tests` runs the service with the
  **FIXTURE - NOT AI** tone engine (`provision --fixture`): contiguous bounded
  frames, chunk completions, digest checks and restart of a killed worker.
- `tests/Martlet.Gateway.Tests/XttsRelayTests.cs` checks that a host offers
  both engines' routes; with `MARTLET_XTTS_LIVE_ENDPOINT` set to a running
  service it speaks through the real gateway and paired client.
- 2026-10-02, Windows, **CPU only** (no GPU access on that machine), the pinned
  files and the lock's versions with CPU PyTorch: the service streamed real
  XTTS-v2 speech for "Annie (cute anime girl)": first audio after 1.57 s (new
  voice) and 1.12 s (cached latents) for 7.5 s and 6.1 s replies that took 14.1
  s and 9.5 s to finish; through the gateway and paired client, first audio at
  1.45 s of a 4.0 s request. CPU is slower than real time, so these show the
  streaming shape, not GPU latency.

**NOT RUN:** the container image build and `martlet-host add xtts` (Docker was
not running), NVIDIA GPU warmup/latency/VRAM, combined load with an LLM, live
desktop conversation playback through XTTS, and listening review of likeness.
