# Martlet Chatterbox Turbo host

This worker hosts Resemble AI's self-hosted Chatterbox Turbo TTS model behind the same loopback HTTP/NDJSON shape as Martlet's F5 host. The gateway posts a reference recording and text chunks to `/synthesize`; the service streams `martlet.f5.worker` events (`started`, `audio_frame`, `chunk_completed`, `completed`, `canceled`, `failed`) with 24 kHz mono signed 16-bit little-endian PCM.

## Commands

- `martlet-chatterbox serve` listens on `127.0.0.1:$MARTLET_CHATTERBOX_PORT` (default `50083`). If `worker-config.json` exists, loading starts in a background thread.
- `martlet-chatterbox provision` downloads the pinned Chatterbox-Turbo files from Hugging Face revision `749d1c1a46eb10492095d68fbcf55691ccf137cd` into `$MARTLET_CHATTERBOX_ROOT/models/chatterbox-turbo`, verifies exact size and SHA-256, and writes `$MARTLET_CHATTERBOX_ROOT/models/worker-config.json`.
- `martlet-chatterbox provision --fixture` writes a deterministic **FIXTURE - NOT AI** sine-tone engine. It imports no torch packages and is only for plumbing tests.
- `martlet-chatterbox warm` posts `/warmup` to a running service and exits non-zero unless the model is ready.

Defaults: `MARTLET_CHATTERBOX_ROOT=/opt/martlet-chatterbox`, `CHATTERBOX_MODEL=chatterbox-turbo`, `MARTLET_CHATTERBOX_DEVICE=cuda:0`.

## Pinned model files

Model card: <https://huggingface.co/ResembleAI/chatterbox-turbo>, MIT license.

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| `t3_turbo_v1.safetensors` | 1915480052 | `fcf1f8c1d651bb7e3acd69ee5be269b4ac10c02980b7708213d598bc9f7cdf87` |
| `s3gen_meanflow.safetensors` | 1064875036 | `d65cb687a2ed581ee6cc297e919ffefa63386944f42364ae13b78a594945514f` |
| `ve.safetensors` | 5695784 | `f0921cab452fa278bc25cd23ffd59d36f816d7dc5181dd1bef9751a7fb61f63c` |
| `added_tokens.json` | 418 | `72e4ab6acb0d9309ac3df4b526ae5fd80a2da5bc5ab7bb02d85096a374f69193` |
| `merges.txt` | 456318 | `1ce1664773c50f3e0cc8842619a93edc4624525b728b188a9e0be33b7726adc5` |
| `special_tokens_map.json` | 470 | `92ba8063bf40aa163eadebbfe0de07c2aebe44cf0d4a9e8726580b0781fd2640` |
| `tokenizer_config.json` | 3878 | `bca16a2ac1ddbd78b8d6228f0031884cc74b6ea54b967d6f6d2ebae9ccde23e6` |
| `vocab.json` | 999186 | `f6bd25a65e4e63ca31360e9fb11c7e4f9a391a78385d640acd814092dd6eee4f` |

## Runtime notes

- Code package: `chatterbox-tts==0.1.7` (MIT). The host uses `ChatterboxTurboTTS.from_local(...)` only and sets Hugging Face/Transformers offline environment variables; it does not call `from_pretrained` or download at runtime.
- A reference WAV is always required. The host never downloads or uses `conds.pt`, so it never falls back to Chatterbox's built-in voice.
- Reference audio must be RIFF/WAVE PCM16 and strictly longer than 5.0 seconds because Turbo's `prepare_conditionals` asserts that bound.
- Chatterbox Turbo applies Resemble AI's Perth watermark in `generate`; Martlet preserves it and does not remove or bypass it.
- Chatterbox Turbo is Martlet's default voice-cloning engine; F5 remains selectable for hosts that prefer it.
- Turbo ignores `exaggeration`, `cfg_weight`, and `min_p`; `tts_turbo.py` logs that those controls are not supported. Martlet uses Turbo's native style/emotion tokens as its emotion control instead.
- Martlet passes these pinned tokenizer tags through unchanged: sounds `[laugh]`, `[chuckle]`, `[sigh]`, `[gasp]`, `[cough]`, `[clear throat]`, `[groan]`, `[sniff]`, `[shush]`; emotions/styles `[happy]`, `[sarcastic]`, `[surprised]`, `[angry]`, `[fear]`, `[crying]`, `[whispering]`, `[dramatic]`.
- The tokenizer also contains `[advertisement]` and `[narration]`, but Martlet intentionally leaves those out.
- `resemble-perth==1.0.1` bundles `perth/perth_net/pretrained/implicit/perth_net_250000.pth.tar` inside the wheel and locates it through `pkg_resources.resource_filename`, so watermark loading is offline-safe.
