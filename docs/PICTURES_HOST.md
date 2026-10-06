# Pictures host role

The `pictures` host role lets a paired Martlet desktop draw pictures on a Martlet host through the authenticated gateway. It runs ComfyUI in a container, bound only to `127.0.0.1:50086` inside the host/gateway network namespace. The gateway relay is a thin, validated pass-through to ComfyUI's own HTTP API; there is no custom image service.

The user-facing picture feature is described in [Pictures](PICTURES.md).

## Host setup

Install it with the normal host flow:

```sh
martlet-host add pictures
```

The role requires Docker, NVIDIA Container Toolkit and an NVIDIA GPU with at least 8 GB of VRAM. The image is built locally from `workers/pictures/host/Dockerfile`, using:

- ComfyUI `v0.39.0`, commit `b0b743566f65daafc423b4fea8a2fbda94b3384a` (GPL-3.0).
- PyTorch `2.10.0` / torchvision `0.25.0` / torchaudio `2.10.0` from the CUDA 12.8 PyTorch index.
- Offline model loading (`HF_HUB_OFFLINE=1`, `TRANSFORMERS_OFFLINE=1`).

`martlet-host` runs `martlet-pictures provision` after the container starts. Provisioning downloads model files into the `martlet-pictures-models` volume, verifies size and SHA-256, and is idempotent.

ComfyUI runs with `--lowvram --listen 127.0.0.1 --port 50086 --disable-auto-launch`, plus output and temp directories inside Martlet-owned volumes. Models stay loaded between pictures (so the next one doesn't reload 20 GB); a few minutes after the last picture, the desktop calls the relay's `free` operation so ComfyUI unloads models and frees GPU memory for voice and thinking roles.

## Pins

Z-Image Turbo files come from `https://huggingface.co/Comfy-Org/z_image_turbo`, revision `6fc90a3b1b653e935a0d175e260736de25b84df5` (Apache-2.0):

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| `split_files/diffusion_models/z_image_turbo_bf16.safetensors` | 12,309,866,400 | `2407613050b809ffdff18a4ac99af83ea6b95443ecebdf80e064a79c825574a6` |
| `split_files/text_encoders/qwen_3_4b.safetensors` | 8,044,982,048 | `6c671498573ac2f7a5501502ccce8d2b08ea6ca2f661c458e708f36b36edfc5a` |
| `split_files/vae/ae.safetensors` | 335,304,388 | `afc8e28272cd15db3919bacdb6918ce9c1ed22e96cb12c4d5ed0fba823529e38` |

Total download size is 20,690,152,836 bytes (about 19.3 GiB).

## Gateway route

- Route ID: `martlet.gateway.picture.v1`
- Path: `/martlet/v1/inference/picture`
- Contract: `martlet.picture-relay` version `1.0`
- Role: `GatewayRole.Voice`
- Default model ID: `z-image-turbo`

Each request carries exactly one `operation` and returns JSON text events (one JSON object per event):

- `status`: returns readiness, ComfyUI version, provisioned model lists, device VRAM and queue counts:
  `{"state":"ready"|"loading"|"not_provisioned","engine":"comfyui","comfyui_version":"...","models":{"diffusion_models":[...],"checkpoints":[...],"text_encoders":[...],"vae":[...],"loras":[...]},"devices":[{"name":"...","vram_total":...,"vram_free":...}],"queue_running":0,"queue_pending":0}`.
- `prompt`: accepts a bounded ComfyUI API-format workflow JSON object and posts it to `/prompt` with a Martlet client ID. ComfyUI validation failures are returned as `{"error":{"code":"prompt.invalid","summary":"..."},"node_errors":{...}}`.
- `history`: accepts `prompt_id` and returns `GET /history/<prompt_id>`.
- `queue`: returns `GET /queue`.
- `view`: accepts `filename`, `subfolder`, `type` (`output` or `temp`), `offset` and `maximum`; it pages `GET /view` as base64 image chunks with `media_type`, `total_bytes`, `offset`, `bytes` and `data_base64`.
- `cancel`: accepts `prompt_id`, deletes it from ComfyUI's queue and interrupts the currently running prompt when it matches.
- `free`: posts `{"unload_models":true,"free_memory":true}` to ComfyUI `/free` and returns `{"state":"freed"}`.

The relay never accepts paths or URLs from the client. Filenames and subfolders are simple names only: no path separators, `..`, control characters or leading dots. Images larger than 32 MiB are rejected, and one `view` page is bounded to 3 MiB before event chunking.

## Licenses and use

Using the role means accepting ComfyUI's GPL-3.0 license, PyTorch's license, and the Apache-2.0 license for Z-Image Turbo model files. Do not generate illegal or harmful content, do not depict real people without their consent, and disclose AI-generated images.
