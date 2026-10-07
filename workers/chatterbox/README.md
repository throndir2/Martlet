# Martlet Chatterbox host

This worker hosts one of Resemble AI's self-hosted Chatterbox TTS models (`CHATTERBOX_MODEL`: `chatterbox-turbo`, `chatterbox-nano` or `chatterbox-original`; the `chatterbox`, `chatterbox-nano` and `chatterbox-original` host roles run the same image with one each) behind the same loopback HTTP/NDJSON shape as Martlet's F5 host. The gateway posts a reference recording and text chunks to `/synthesize`; the service streams `martlet.f5.worker` events (`started`, `audio_frame`, `chunk_completed`, `completed`, `canceled`, `failed`) with 24 kHz mono signed 16-bit little-endian PCM.

## Commands

- `martlet-chatterbox serve` listens on `127.0.0.1:$MARTLET_CHATTERBOX_PORT` (default `50083`). If `worker-config.json` exists, loading starts in a background thread.
- `martlet-chatterbox provision` downloads the pinned files of `CHATTERBOX_MODEL` (below) into `$MARTLET_CHATTERBOX_ROOT/models/<model>`, verifies exact size and SHA-256, and writes `$MARTLET_CHATTERBOX_ROOT/models/worker-config.json`.
- `martlet-chatterbox provision --fixture` writes a deterministic **FIXTURE - NOT AI** sine-tone engine. It imports no torch packages and is only for plumbing tests.
- `martlet-chatterbox warm` posts `/warmup` to a running service and exits non-zero unless the model is ready.

Defaults: `MARTLET_CHATTERBOX_ROOT=/opt/martlet-chatterbox`, `CHATTERBOX_MODEL=chatterbox-turbo`, `MARTLET_CHATTERBOX_DEVICE=cuda:0` (`auto`, which the chatterbox-nano role sets, is the NVIDIA GPU when the container has one and the CPU otherwise). `/status` reports `model` and `device`.

## Pinned model files

**Chatterbox Turbo** (`chatterbox-turbo`): <https://huggingface.co/ResembleAI/chatterbox-turbo> at revision `749d1c1a46eb10492095d68fbcf55691ccf137cd`, MIT license.

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

**Chatterbox Nano** (`chatterbox-nano`): <https://huggingface.co/ResembleAI/chatterbox-nano> at revision `71ccd1d0081b430592cea481f4307e764e07bc64`, MIT license. Its decoder, voice encoder and tokenizer files are byte for byte Turbo's (the same sizes and SHA-256); only T3 differs: `t3_nano_v1.safetensors`, 869899204 bytes, `72b110185087d945dbdf54dee4e333848e1811bdd5fd6cb16ceb8da50006f0c9`. chatterbox-tts 0.1.7 predates Nano, so the service builds it as Resemble's own loader on master does: Turbo with the GPT-2 small backbone, whose configuration it adds to the library's table.

**Chatterbox Original** (`chatterbox-original`): <https://huggingface.co/ResembleAI/chatterbox> at revision `5bb1f6ee58e50c3b8d408bc82a6d3740c2db6e18`, MIT license. Not its built-in voice (`conds.pt`) or its multilingual weights.

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| `t3_cfg.safetensors` | 2129653744 | `914cb1696f47527fe8852ca8f1fe1fa63cb34f76f9c715e84e067b744dd0da81` |
| `s3gen.safetensors` | 1056484620 | `2b78103c654207393955e4900aac14a12de8ef25f4b09424f1ef91941f161d4e` |
| `ve.safetensors` | 5695784 | `f0921cab452fa278bc25cd23ffd59d36f816d7dc5181dd1bef9751a7fb61f63c` |
| `tokenizer.json` | 25470 | `d71e3a44eabb1784df9a68e9f95b251ecbf1a7af6a9f50835856b2ca9d8c14a5` |

## Runtime notes

- Code package: `chatterbox-tts==0.1.7` (MIT). The host loads only from local files (`ChatterboxTurboTTS.from_local`, `ChatterboxTTS.from_local` or Nano's own loader) and sets Hugging Face/Transformers offline environment variables; it does not call `from_pretrained` or download at runtime.
- A reference WAV is always required. The host never downloads or uses `conds.pt`, so it never falls back to Chatterbox's built-in voice.
- Reference audio must be RIFF/WAVE PCM16 and strictly longer than 5.0 seconds because Turbo's `prepare_conditionals` asserts that bound.
- Chatterbox Turbo applies Resemble AI's Perth watermark in `generate`; Martlet preserves it and does not remove or bypass it.
- Chatterbox Turbo is Martlet's default voice-cloning engine; F5 remains selectable for hosts that prefer it.
- A failed reply says why: the `failed` event's summary ends with the exception's type and first line (never the reply
  text), and a 503 while the model isn't ready adds `detail` (why it failed to load or failed the last reply). Martlet's
  relay writes both into the host's own log, which paired desktops show on their Diagnostics page. When the graphics card
  runs out of memory, the worker frees PyTorch's cached memory and tries the sentence once more; if it runs out again the
  reply fails with `gpu_out_of_memory` and the model stays loaded (reloading would need more memory). After any other engine
  failure (`internal_failure`) the worker runs a tiny operation on the card: if that fails too, the CUDA context is broken
  (a device-side assert or an illegal memory access lasts until the process exits), the summary adds "the voice service is
  restarting" and the service exits with code 75 a second later so Docker restarts it; otherwise it drops the model (its
  CUDA graph and kept conditionals let go of it first) and the next reply reloads it.
- Each piece may take at most `MARTLET_CHATTERBOX_TOKENS_BASE` (100) speech tokens plus `..._PER_TEXT_TOKEN` (25) per text
  token and `..._PER_TAG` (100) per tag, never more than 1,000: Turbo has no stop detector of its own. The log line of a reply
  says when a piece was cut. Double quotation marks are removed from the text first.
- While no reply has come for `MARTLET_CHATTERBOX_IDLE_CHECK_SECONDS` (300; 0 turns it off), the worker makes one short
  piece on the warm-up's synthetic voice (**FIXTURE - NOT a voice**) and throws it away, so a model Windows moved out of
  graphics memory comes back before the next reply; a reply stops it at its next speech token. `/status` reports
  `idle_check` (`checks`, `every_seconds`, `fastest_ms`, `last_ms`) and the log says when one was slow.
- Turbo and Nano ignore `exaggeration`, `cfg_weight`, and `min_p`; `tts_turbo.py` logs that those controls are not supported. Of their style tokens only `[whispering]` changes the voice, because the service whispers it (docs/CHATTERBOX_VOICE.md).
- The original model reads no tags. The service splits each piece into parts: a sentence the reply starts with `[expressive]` uses the request's `style.expressive` exaggeration and CFG weight, every other sentence `style.general` (defaults 0.7/0.3 and 0.5/0.5, Resemble's tips; ranges 0.25-2 and 0-1), `[whispering]` sentences are whispered, and both tags are left out of what the model reads. Each part may take at most `MARTLET_CHATTERBOX_ORIGINAL_TOKENS_BASE` (100) plus `..._PER_TEXT_TOKEN` (10) speech tokens per text token, never more than 1,000. chatterbox-tts 0.1.7's T3 always decodes two rows for CFG, so the text always goes in twice. `/status` reports `style` (`default`, `expressive_parts`, `last`).
- On the CPU (no CUDA) Turbo and Nano speak whole pieces with the library's own decoding, within the same speech-token budget, and the idle check doesn't run.
- Martlet passes these pinned tokenizer tags through unchanged: sounds `[laugh]`, `[chuckle]`, `[sigh]`, `[gasp]`, `[cough]`, `[clear throat]`, `[groan]`, `[sniff]`, `[shush]`; emotions/styles `[happy]`, `[sarcastic]`, `[surprised]`, `[angry]`, `[fear]`, `[crying]`, `[whispering]`, `[dramatic]`.
- The tokenizer also contains `[advertisement]` and `[narration]`, but Martlet intentionally leaves those out.
- `resemble-perth==1.0.1` bundles `perth/perth_net/pretrained/implicit/perth_net_250000.pth.tar` inside the wheel and locates it through `pkg_resources.resource_filename`, so watermark loading is offline-safe.
