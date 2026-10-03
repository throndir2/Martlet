# GPT-SoVITS voice

GPT-SoVITS is Martlet's third self-hosted voice engine, next to [F5-TTS](F5_VOICE.md)
and [XTTS-v2](XTTS_VOICE.md). It suits anime-style voices, clones a voice from a
**3-10 second** recording with its exact transcript, and speaks each sentence as
soon as it is generated. Code and weights are MIT-licensed. Choose it on
**Companion > Voice > Voice engine**; it uses the same voice list as the other
engines. For a voice made from several recordings, the first 3-10 second one is
the prompt (its words and language) and the others add their tone
(`aux_ref_audio_paths`), so a voice longer than 10 seconds joined can still be
used when one of its recordings fits.

## What runs

| Piece | Details |
| --- | --- |
| Host role | `gpt-sovits` (`martlet-host add gpt-sovits`), files in `deploy/host/roles/gpt-sovits`. Needs an NVIDIA GPU with 4 GB+ and the NVIDIA Container Toolkit. Loopback service on 127.0.0.1:50082 |
| Release | GPT-SoVITS [`20250606v2pro`](https://github.com/RVC-Boss/GPT-SoVITS/releases/tag/20250606v2pro), commit `d7c2210d`, source archive SHA-256 `467fc3ba...`, MIT |
| Weights pair | v2Pro from [`lj1995/GPT-SoVITS`](https://huggingface.co/lj1995/GPT-SoVITS) at revision `336b2ec4`: GPT `s1v3.ckpt` and SoVITS `v2Pro/s2Gv2Pro.pth` (model card: MIT) |
| v2Pro auxiliary models | ERes2NetV2 speaker verification (Apache-2.0), `chinese-hubert-base` (MIT), `chinese-roberta-wwm-ext-large` (Apache-2.0), all from the same revision; the fastText `lid.176.bin` language-ID model (CC-BY-SA-3.0) its Japanese/English splitter loads |
| Image | `workers/gpt-sovits/host/Dockerfile`: `python:3.11.13` by digest, hash-locked inference dependencies (`host/requirements.lock`, PyTorch 2.6.0 CUDA 12.4, transformers 4.50, gradio 4.44), NLTK English data and the OpenJTalk dictionary, each SHA-256 checked. Its own environment: GPT-SoVITS's pins conflict with Chatterbox and F5 |
| Worker | `martlet_gpt_sovits` (see its [README](../workers/gpt-sovits/README.md)): GPT-SoVITS's native `TTS_infer_pack` pipeline, never `api_v2.py` |
| Gateway relay | `Martlet.Gateway.GptSovits`: the F5 relay on route `martlet.gateway.gpt-sovits-synthesis.v1` (`/martlet/v1/inference/gpt-sovits-synthesis`), model `gpt-sovits-v2pro` |

`martlet-host add gpt-sovits` shows every licence above before anything is
downloaded (the role's `terms`), builds the image on the host, then
`martlet-gpt-sovits provision` downloads the pinned files into the volume
`martlet-gpt-sovits-models` (each checked by size and SHA-256; nothing is installed
on a mismatch) and `martlet-gpt-sovits warm` loads them.

## Identity, loading and safety

- The worker loads weights exactly once, at warmup, from fixed paths in the
  models volume after every pinned file matches. No request names a path, and
  there is no endpoint to switch weights, restart or stop the service; the
  loopback front offers only `/status`, `/warmup`, `/synthesize` and `/cancel`,
  and the gateway exposes only the synthesis route.
- After loading, the worker checks that GPT-SoVITS loaded both halves of the pair
  from those paths and recognized SoVITS as v2Pro (32 kHz) before it reports
  ready. Every event names both artifacts; the relay checks the SoVITS weights
  against the route and the GPT weights against its own pin, so a mismatched
  pair fails the reply instead of speaking.
- As with F5, the host waits (bounded) for a busy worker and restarts a dead
  one for the next reply instead of failing it. Cancellation discards the reply
  and stops GPT-SoVITS after its current sentence.
- The model stays offline at run time (`HF_HUB_OFFLINE`, `TRANSFORMERS_OFFLINE`);
  GPT-SoVITS's own prints, which include reply text, are discarded unless
  `MARTLET_GPT_SOVITS_DEBUG=1`.

## Reference voices and languages

GPT-SoVITS clones only recordings of 3 to 10 seconds. Desktop marks other voices
"wrong length for this engine" and turns off their **Use** button; handing
Speaking to GPT-SoVITS skips them when it picks the voice; the speech client,
the gateway (`SpeechEngine.MinimumReferenceMilliseconds`/`Maximum...`) and the
worker refuse them. All included voices fit except "Bee (cute, bubbly)" (10.6 s).
Martlet's default, "Annie (cute anime girl)", fits.

The request carries `reference_language`: `ja` when the recording's transcript
has kana or kanji, otherwise `en` (`SpeechEngines.ReferenceLanguage`). Each reply
chunk is read as English, or as Japanese with English words (`ja`) when it
contains kana or kanji. Chinese, Korean and Cantonese front ends are not
provisioned.

## Output and streaming

v2Pro writes 32 kHz; the worker resamples each sentence to 24 kHz mono signed
16-bit PCM (polyphase) and sends it as contiguous `martlet.f5.worker` 1.0 frames
of at most 200 ms. It uses GPT-SoVITS's fragment mode (`return_fragment`), so the
first sentence plays while later ones are generated; within a sentence audio
arrives when that sentence is done.

## Verification

Run on a Windows dev machine without an NVIDIA GPU (Intel UHD 770), on CPU, on
2026-10-02:

- **Real model, CPU.** The real `provision` downloaded and verified every pinned
  file; the worker loaded the pair (21 s), checked both identities and became
  ready. Through the loopback front: "Hello! I'm your companion, speaking with
  GPT-SoVITS..." with the LJ voice (9.0 s of audio) and a line with "Annie (cute
  anime girl)" (6.5 s; warm: first audio 1.3 s, done in 5.5 s). Whisper base.en
  transcribed both correctly; the Annie clone kept a high pitch (median 355 Hz;
  LJ 228 Hz).
- **Role image.** `workers/gpt-sovits/host/Dockerfile` built on Docker Desktop
  (6.7 GB; the English and Japanese front ends load at build time). In the
  container on CPU: `martlet-gpt-sovits provision` and `warm` succeeded, and an
  English and a Japanese reply transcribed as "Good evening. Shall we read a
  story together?" and "こんにちは、今日はいい天気だすな" (expected "...ですね").
- **Gateway end to end.** `GptSovitsRelayTests` ran Martlet's gateway with the
  relay and the desktop's speech client against that container (English, then
  Japanese): 5.6 s and 3.3 s of 24 kHz audio, both identities accepted; a 10.6 s
  voice is refused before sending.
- **Desktop through Martlet MCP.** `f5_voices` lists `gpt-sovits` (3,000-10,000 ms,
  MIT) and per voice the engines that can clone it; on Companion > Voice,
  choosing GPT-SoVITS sets `SpeakingEngineStatus` (the engine combo box the Voice
  engine rows later replaced) and marks
  `F5VoiceRow-librivox-woollybee` "wrong length for this engine" with
  `F5VoiceUse-librivox-woollybee` disabled.
- **Plumbing.** `python -m unittest discover -s tests` (FIXTURE - NOT AI engine)
  covers contiguous frames, the 3-10 s bound, a voice of several recordings
  (prompt and auxiliary files, refused when none fits or they overlap) and
  restarting a dead worker.

**NOT RUN** (no NVIDIA GPU here): CUDA synthesis, GPU latency and VRAM,
`martlet-host add gpt-sovits` on a GPU host (it requires `gpu`), and a
conversation speaking through a paired host. CPU timings above are not GPU
evidence.
