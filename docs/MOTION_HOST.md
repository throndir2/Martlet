# Motion host role

The `motion` host role lets a paired Martlet desktop make body motions for VRM characters on a Martlet host. It runs
Tencent's [HY-Motion 1.0](https://github.com/Tencent-Hunyuan/HY-Motion-1.0), an open text-to-motion model, in a container.
The container listens only on `127.0.0.1:50090` in the gateway's network namespace. The gateway relay
(`Martlet.Gateway.Motion`) is the only way in.

A motion is made from a short English description of body movement, its length in seconds and an optional seed. The host
returns a VRMA clip (VRM Animation: a binary glTF file with the `VRMC_vrm_animation` 1.0 extension) that a VRM character can
play. This role only makes the clips. Playback on the character is a separate feature, and the conversation asks for
motions as described in [Motions in conversation](#motions-in-conversation).

The role is optional. It needs a large graphics card, and its license does not apply in some countries (see
[Licenses and use](#licenses-and-use)).

## What the host needs

- An x86_64 Linux or Windows (Docker Desktop) host with Docker and the NVIDIA Container Toolkit.
- An NVIDIA graphics card with at least 24 GB of memory for `hy-motion-1.0-lite`, or more than 24 GB (Tencent says 26 GB)
  for `hy-motion-1.0`. The model loads on one card.
- About 32 GB of system memory: the 16 GB Qwen3-8B text encoder loads into main memory before it goes to the card.
- About 20 GB of disk space for the downloads (Lite: 19.9 GB; standard: 22.3 GB), plus the image Martlet builds there (about 15 GB, mostly PyTorch and its CUDA libraries).

`martlet-host add motion` stops before it shows the terms, downloads anything or builds the image when the chosen model
does not fit on the card. It says, for example: *motion needs an NVIDIA graphics card with at least 23000 MiB of memory
(HY-Motion-1.0-Lite needs a 24 GB card); this host's card has 8192 MiB.* `martlet-host describe motion` prints the same
reason as `role.unavailable=` when the largest card is too small for either model, and each model's minimum as
`role.min_vram_when=MOTION_MODEL=<model>|<MiB>|<why>`. The role.conf key is `min_vram=<MiB>|<why>`
([the uniform role flow](../deploy/host/README.md#the-uniform-role-flow)).

## Install it

1. Open Martlet hosts in Martlet, or open a terminal on the host.
2. Add the role:

   ```sh
   martlet-host add motion
   ```

3. Choose the model. `hy-motion-1.0-lite` (0.46B parameters) is the default. Martlet suggests `hy-motion-1.0` (1.0B
   parameters, better motion) only on a card with 26000 MiB or more.
4. Read the terms. Type `yes` (or click Install in Martlet) only when you accept them.

`martlet-host` then builds the image from `workers/motion/host/Dockerfile`, starts the service and runs
`martlet-motion provision`. Provisioning downloads the chosen model and both text encoders into the
`martlet-motion-models` volume and checks the size and SHA-256 of each file. A file that is already there and correct is not
downloaded again. Removing the role keeps the volume, so adding it again downloads nothing.

The model loads when the first motion is made, not when the service starts. The service unloads it after five minutes
without a motion (`MARTLET_MOTION_IDLE_MINUTES`), and the desktop asks it to unload three minutes after its last motion, so
the voice and thinking roles get the graphics card back. The first motion after an unload loads about 20 GB again.

## What is pinned

The image (`python:3.12-slim-bookworm`) installs PyTorch `2.10.0` from the CUDA 12.8 index (it also runs RTX 50 series cards)
and the pinned packages in `workers/motion/host/requirements.txt` (transformers 4.53.3, accelerate 1.10.1, torchdiffeq 0.2.5,
numpy 1.26.4 and others). HY-Motion's own `requirements.txt` asks for torch 2.5.1 and lists the Autodesk FBX SDK, the
prompt rewriter's OpenAI client, diffusers and bitsandbytes; the path Martlet runs needs none of those.

The build fetches the HY-Motion code from `github.com/Tencent-Hunyuan/HY-Motion-1.0` at commit
`4e426f5a1021cbcf7f375458c37b840ee7225229` and checks the commit. It then downloads the Git LFS files the pipeline reads and
checks each one by size and SHA-256: `stats/Mean.npy`, `stats/Std.npy` and the wooden body model in
`scripts/gradio/static/assets/dump_wooden/` (`v_template.bin`, `j_template.bin`, `skinWeights.bin`, `skinIndice.bin`,
`kintree.bin`, `faces.bin`). The pins are in `workers/motion/martlet_motion/pins.py`.

Provisioning downloads these model files (`MOTION_MODEL` picks the first two):

| Repository and revision | File | Bytes | SHA-256 |
| --- | --- | ---: | --- |
| `tencent/HY-Motion-1.0` `620dd559` | `HY-Motion-1.0-Lite/latest.ckpt` | 1,843,542,538 | `d83f118f8d74db76249db86dcf9982a8229f43ef4e9fa11f683019d6230dd486` |
| `tencent/HY-Motion-1.0` `620dd559` | `HY-Motion-1.0/latest.ckpt` | 4,171,703,514 | `4fa91c6085f30c49bccda150b28d6c8d0887dc07e6d5d4ec0055482732635f10` |
| `tencent/HY-Motion-1.0` `620dd559` | each model's `config.yml`, `LICENSE.txt` | small | in `pins.py` |
| `Qwen/Qwen3-8B` `b968826d` | `model-00001-of-00005.safetensors` to `model-00005-of-00005.safetensors` | 16,381,516,776 | in `pins.py` |
| `Qwen/Qwen3-8B` `b968826d` | config, generation config, index, tokenizer files, `LICENSE` | 15.9 MB | in `pins.py` |
| `openai/clip-vit-large-patch14` `32bd6428` | `model.safetensors` | 1,710,540,580 | `a2bf730a0c7debf160f7a6b50b3aaf3703e7e88ac73de7a314903141db026dcb` |
| `openai/clip-vit-large-patch14` `32bd6428` | config and tokenizer files | 3.7 MB | in `pins.py` |

The service loads everything from the volume with `HF_HUB_OFFLINE=1`, `TRANSFORMERS_OFFLINE=1` and `USE_HF_MODELS=0`. It
never downloads anything at run time.

### No SMPL or SMPL-H files

HY-Motion's output is SMPL-H joint rotations, but inference does not need the SMPL, SMPL-H or MANO body model files from the
Max Planck Institute, which have their own license. HY-Motion's pipeline uses its own wooden mesh (the LFS files above, part
of the HY-Motion repository and its license) to put the feet on the ground, and the mean hand pose is a list of numbers in
its code (`hymotion/pipeline/body_model.py`). Martlet bundles no body model files.

### Not used

- HY-Motion's FBX export (`hymotion/utils/smplh2woodfbx.py`) needs the proprietary Autodesk FBX SDK. Martlet does not
  install or use it.
- The optional prompt rewriter and duration estimator (`Text2MotionPrompter`) is not installed. Martlet's Thinking model
  writes the English prompt and chooses the length itself.
- HY-Motion's gradio app and `T2MRuntime` are not used. The service calls the pipeline (`MotionFlowMatching.generate`)
  directly with one seed and the model's classifier-free guidance scale (5.0).

## From HY-Motion's output to a VRMA clip

`workers/motion/martlet_motion/generator.py` runs the pipeline for one seed. The output is `rot6d` (frames, 22, 6), the
pelvis and 21 body joints as 6D rotations, and `transl` (frames, 3), the SMPL translation already moved so the lowest point
of the clip is at y = 0. HY-Motion's own `construct_smpl_data_dict` turns the rotations into axis-angle and adds its mean hand
pose, so `poses` is (frames, 52, 3) in SMPL-H joint order. The output already uses the SMPL frame (Y up, the body faces +Z,
its left side at +X, T-pose rest), so no axis change is needed. `transl` goes to the converter unchanged.
`martlet_motion.vrma.smplh_to_vrma(poses, trans, 30)` then writes the clip ([the converter](../workers/motion/README.md)).
HY-Motion makes 30 frames a second; Martlet asks for 1 to 12 seconds (HY-Motion makes 20 to 360 frames).

## The service

`martlet-motion serve` answers on `127.0.0.1:50090`:

- `GET /status`: `{"state": "ready" | "not_provisioned", "engine": "hy-motion", "model", "code_commit", "loaded", "busy",
  "fps", "minimum_seconds", "maximum_seconds", "idle_unload_minutes", "made", "last_milliseconds"}`, plus `missing` (up to
  five missing files) when it is not provisioned and `gpu` (name and memory) while the model is loaded.
- `POST /generate` with `{"prompt", "seconds", "seed"}` (seed optional): the VRMA bytes as `model/gltf-binary`, with the
  headers `X-Martlet-Motion-Frames`, `-Fps`, `-Seed`, `-Milliseconds` and `-Load-Milliseconds`.
- `POST /free`: unloads the model; `{"state": "freed", "was_loaded"}`.

It makes one motion at a time. A failure is `{"error": {"code", "summary"}}`: `motion.busy` (409), `request.invalid` (400),
`motion.unavailable` (503: not provisioned or no GPU), `motion.out_of_memory` (507; the model is unloaded) or
`motion.failed` (500). A prompt must be 3-400 characters and at most 60 words of English text.

`martlet-motion selftest [OUT]` runs HY-Motion's real pipeline code with its random initial weights, no text encoder and two
flow steps on the processor, then the converter. It checks the image without the model files (it downloads only the 1 KB
`config.yml` when the role is not provisioned). The motion is noise: FIXTURE - NOT AI. `serve --random-weights STEPS`
serves such motions, and `serve --listen ADDRESS` listens on another address; both are for checks only.

## Gateway route

- Route ID: `martlet.gateway.motion.v1`
- Path: `/martlet/v1/inference/motion`
- Contract: `martlet.motion-relay` version `1.0`
- Role: `GatewayRole.Voice`; a friend's computer that shares the host cannot use it
- Model IDs: `hy-motion-1.0-lite` (default) and `hy-motion-1.0`, each identified by the HY-Motion revision and its
  checkpoint's SHA-256
- Longest request: 8 minutes (the first motion after an unload loads the model first)

Each request carries one `operation` and returns JSON text events:

- `status`: the service's status object, or `{"state": "loading"}` while the service does not answer.
- `generate` (`prompt`, `seconds` 1-12, optional `seed` 0-2147483647): the clip as pieces of at most 180,000 bytes
  (`media_type`, `total_bytes`, `offset`, `bytes`, `data_base64`), then one summary (`state` `done`, `model`, `frames`,
  `fps`, `seed`, `milliseconds`, `load_milliseconds`, `total_bytes`, `sha256`).
- `free`: `{"state": "freed", "was_loaded"}`.

A failure the service reports comes back as one `{"error": {"code", "summary"}}` object. The relay accepts no path, URL or
model choice from the client, and clips larger than 4 MiB are refused.

## Where Martlet makes motions

### The motion pool

Companion › Motion (under *Optional extras*) controls motion, the same way as the other pooled areas
([Pools](CLUSTER.md#pools-one-ordered-list-of-members-per-area)). Its list, *Where Martlet makes motions*, is the motion pool
(`PoolAreas.Motion`, area `motion` in `pools.json`). The list is the same on all the owner's computers.

- **Members.** A member is *This PC* (each companion PC's own host service) or one of your paired computers. A host runs one
  motion route on one card, so a graphics card is not a member of its own. Hosts that friends share are never used.
- **Order.** A motion goes to the first member that isn't making another motion. A member that is busy (`motion.busy`), doesn't
  answer or isn't set up is passed over for the next. When every member is busy, the motion waits for the first to finish.
  A motion that fails on its computer is not made again elsewhere.
- **On and off.** Each member has its own **On** box and keeps its place when it is off. With no member on, motion is off:
  Martlet makes no new motions, but the motions it kept still play by their tags.
- **Kept for.** Each member can be kept for one companion PC, as in every pool list.
- **Set up.** A member's **Settings** say where Motion stands there. **Set up Motion here** runs `martlet-host add motion` with
  the role's own dialog, which shows the model choice and Tencent's license terms. This PC without its host service sets
  that up first, in the same window. A computer that gets the role from anywhere else (Devices, a fix card) joins the list
  at its end.

`MotionPool` (`src/Martlet.Core/Motion/MotionPool.cs`) gives the order from local files only, and sends each motion through
`WorkQueue.Shared` (lane `motion`, background priority). In the desktop, `MotionClient` and `PoolMotionMaker`
(`src/Martlet.Desktop/MotionClient.cs`) make the maker from the list. Martlet frees the graphics card of each computer that
made a motion three minutes after its last motion. Every member that is on is kept away from background thinks (its duty is
`motions`). `MARTLET_MOTION_FIXTURE=1` makes Martlet use the FIXTURE - NOT AI wave maker.

### Before the list: the older choice

The `motion` shared setting (`motion.json`) was the first choice, and it still applies while no Motion list exists.
`Automatic` (the default) uses every paired computer that the shared plan says runs the motion role (this PC's own host
service first), so adding the role turns motion on. `Host` names one computer, and `Off` turns motion off. A computer that is
not paired with the chosen host keeps the choice and says why it cannot make motions there.

When Companion › Motion opens for the first time, Martlet makes the list once from this choice (`MotionPool.FirstList`): the
chosen computer first, then the other computers that run Motion; with `Off`, the same computers, each turned off. The list
takes every computer the shared plan says runs Motion, not only the pairings of the PC that makes it, because the list is
shared: each companion PC then uses the ones it is paired with. With `Automatic` and no computer that runs Motion yet,
Martlet makes no list, so adding the role later still turns motion on.

A computer that is making another motion still counts as ready, so a motion waits for it in the queue instead of failing.

MCP: `motion_status` reads the Motion list, the computers a motion tries and the older choice from a data directory.
`motion_pool_check` rehearses the pool with simulated computers. `motion_check` makes one motion (fixture, a worker on
loopback, or a paired host's route) and saves the `.vrma` file ([MCP](MCP.md#motion)).

## Motions in conversation

Martlet uses this role in conversation for a VRM character
([Motions in conversation](AVATARS.md#motions-in-conversation)). While motions are set up and the character shown is a VRM,
replies get `body_motion`: a motion the character has plays at once, and a new one is made in the background on the motion
pool, kept as a shared `motion` creation and played on the character when it is ready. A motion job holds the first computer
of the list away from background thinks (its duty is `motions`), and Martlet frees the graphics card three minutes after the
last motion. Every VRM character on every computer of the account then does a kept motion again by its tag, without this
role. Martlet also makes five starter motions once (`cheer`, `clap`, `deep_bow`, `stretch` and `facepalm`) when this role is
first ready. MCP `kept_motions_status` and `kept_motions_check` check that part ([MCP](MCP.md#body-motions)).

A Live2D character doesn't get `body_motion`: HY-Motion makes motions for a humanoid skeleton, which only VRM models have.
With a Live2D character, the Motion list stays as it is and is used again when you choose a VRM character. Companion › Motion
says which kind the character is (`MotionCharacter`).

## Licenses and use

The HY-Motion code and models are licensed under the TENCENT HY-MOTION 1.0 COMMUNITY LICENSE AGREEMENT (`LICENSE.txt` in the
model repository; Hugging Face names it `tencent-hunyuan-community`). The terms Martlet shows before the install summarize
it:

- It does **not** apply in the European Union, the United Kingdom or South Korea. Do not install or use the role there.
- A licensee whose products or services had more than 1 million monthly active users on the release date must ask Tencent
  for a license first.
- Follow its Acceptable Use Policy: for example, no use to harm people or minors, to impersonate people without consent, or
  for military purposes, and mark motions you publish as machine-generated.
- Do not use the models or their output to improve any other AI model.

Qwen3-8B is Apache-2.0. The Hugging Face CLIP repository names no license; OpenAI publishes CLIP under the MIT license
(github.com/openai/CLIP). The notices are in `workers/motion/THIRD-PARTY-NOTICES.txt`.

Prompts must be English descriptions of body movement. HY-Motion does not make emotions or looks, objects or scenes, two or
more people, seamless loops or motions in place, and it works best with prompts under 30 words and motions under 5 seconds.

## Checked and not checked

Checked on a Windows PC with an 8 GB RTX 2070 SUPER (too small for the model): the image builds; the selftest runs
HY-Motion's real pipeline code (random weights) and the converter; the service, the relay and the MCP tools pass their
tests; and `host_engine_check` checks the `min_vram` refusal.

**NOT RUN:** a real motion with the model weights, its speed and its graphics memory use, because they need a 24 GB card.
