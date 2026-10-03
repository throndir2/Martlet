# Martlet GPT-SoVITS voice worker

The `gpt-sovits` host role: GPT-SoVITS speaking Martlet's replies in a voice cloned from a 3-10 second reference
recording, on a paired host's NVIDIA GPU. See [docs/GPT_SOVITS_VOICE.md](../../docs/GPT_SOVITS_VOICE.md).

| Piece | What it is |
| --- | --- |
| `martlet_gpt_sovits/pins.py` | The one release Martlet runs: GPT-SoVITS `20250606v2pro` (commit `d7c2210d`, MIT), the v2Pro pair (GPT `s1v3.ckpt`, SoVITS `v2Pro/s2Gv2Pro.pth`) and its auxiliary models from `lj1995/GPT-SoVITS` at `336b2ec4`, plus the fastText language-ID model, each with size, SHA-256 and licence |
| `martlet_gpt_sovits/worker.py` | The worker process: verifies every pinned file, loads the pair once through GPT-SoVITS's native `TTS_infer_pack` pipeline, checks it loaded both pinned halves as v2Pro, then streams each sentence (`return_fragment`) resampled from 32 kHz to 24 kHz mono PCM16 as `martlet.f5.worker` 1.0 events |
| `martlet_gpt_sovits/host.py` | Loopback HTTP front (`/status`, `/warmup`, `/synthesize`, `/cancel` on 127.0.0.1:50082) and the `provision` / `warm` role steps. Waits for a busy worker and restarts a dead one for the next reply. No endpoint restarts or stops it, switches weights or takes a path |
| `martlet_gpt_sovits/wire.py` | Request checks (reference 3-10 s mono PCM16 WAV, transcript and revision, `en`/`ja` reference language), reply language choice, 24 kHz conversion and event shape |
| `host/Dockerfile`, `host/requirements.*` | The role image: `python:3.11.13`, hash-locked inference dependencies with PyTorch 2.6.0 CUDA 12.4, NLTK English data and the OpenJTalk dictionary, in its own environment (GPT-SoVITS's pins conflict with Chatterbox and F5) |

Languages: the reference recording is English or Japanese (`ja` when its transcript has kana or kanji). Each reply
chunk is read as English, or as Japanese with English words when it contains kana or kanji. Chinese, Korean and
Cantonese front ends are not provisioned.

## Checks

```powershell
# Plumbing with the FIXTURE - NOT AI engine (no model, no GPU), from this directory:
python -m unittest discover -s tests
```

The live engine was run on CPU on a Windows dev machine (Python 3.11 venv, PyTorch 2.6.0 CPU, real pinned weights):
see the verification notes in [docs/GPT_SOVITS_VOICE.md](../../docs/GPT_SOVITS_VOICE.md#verification).
