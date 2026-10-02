# Local Audio2Face service (NVIDIA's open-source Audio2Face-3D SDK)

The `audio2face` host role's default **`local` engine**: lip-sync on a Martlet
host's own NVIDIA GPU with no NVIDIA account, NGC key or NIM container. It
builds NVIDIA's open-source [Audio2Face-3D SDK](https://github.com/NVIDIA/Audio2Face-3D-SDK)
(MIT) and serves it behind the same gRPC contract as NVIDIA's Audio2Face-3D NIM
(`nvidia_ace.services.a2f_controller.v1.A2FControllerService/ProcessAudioStream`,
the pinned protos in `protos/`), so the Martlet gateway's
`Martlet.Gateway.Audio2Face` relay and the desktop's
[Audio2Face client](../../src/Martlet.Avatar.Audio2Face/README.md) use it
unchanged. The role's other engine, `nim`, still runs NVIDIA's NIM with an NGC
key; see [Martlet host](../../deploy/host/README.md#roles).

```text
desktop --pinned TLS--> gateway (:9443) --Audio2Face relay--> 127.0.0.1:52000 martlet-a2f-server
                                                               Audio2Face-3D SDK (CUDA, TensorRT)
```

## Install

From Martlet: Devices (or Companion > Lip-sync) > install Audio2Face on a host
and keep the engine **local**. On the host itself:
`martlet-host add audio2face` and answer `local`. The first install builds the
image (`martlet-audio2face:1`, several GB: CUDA 12.8 and TensorRT 10.9 from
NVIDIA's public `nvidia/cuda` images and CUDA apt repository), then the first
start downloads the chosen model and builds its TensorRT engine (a few minutes).
Both are kept in the `martlet-audio2face-models` volume, so restarts and re-adds
are quick.

Needs: an NVIDIA GPU of the RTX 20 series (Turing) or newer with 4 GB+ memory, a
driver for CUDA 12.8 (570 or newer) and Docker's NVIDIA support (the NVIDIA
Container Toolkit on Linux, WSL 2 GPU support in Docker Desktop).

## What runs

- **Image** (`Dockerfile`): the SDK at commit
  `1ca0f02535ed774f5dbcd724a31cd486368dc783`, its pinned build dependencies from
  the SDK's own `fetch_deps.sh`, and `server/martlet_a2f_server.cpp` built with
  Ubuntu's gRPC. Only `libaudio2x.so`, the server and TensorRT's `trtexec` are kept
  in the runtime image.
- **`martlet-a2f serve`** (the container's command):
  1. Downloads the `A2F_3D_MODEL_NAME` model (`claire` v2.3.1, `james` v2.3.1 or
     `mark` v2.3) from `huggingface.co/nvidia` at the revisions in
     `models.lock`, checking every file's SHA-256; a mismatch
     stops it. Files already matching are not downloaded again.
  2. Builds the model's FP16 TensorRT engine with `trtexec` for this GPU and
     TensorRT version (shapes from the model's pinned `trt_info.json`), cached
     under `engines/<model>-<key>/`.
  3. Starts `martlet-a2f-server` on `127.0.0.1:52000` (the gateway's network
     namespace in Docker mode, the host's loopback natively).
- **Server**: loads the regression model with the SDK's GPU blendshape solver
  (skin and tongue, 30 frames per second) and warms it up with a second of
  silence. Each `ProcessAudioStream` call takes the header (mono 16-bit PCM,
  8-144 kHz), the audio and `EndOfAudio`, resamples to the model's 16 kHz
  (windowed sinc), animates the clip and streams back the header with the 68
  ARKit/tongue blendshape names in the NIM's PascalCase, frames of weights
  timed on the clip's clock (30 per message), the end-of-processing event and a
  final `SUCCESS` status. It applies the header's `bs_weight_multipliers`,
  `bs_weight_offsets` and `enable_clamping_bs_weight` like the NIM. Requests run
  one at a time; clips are limited to 120 s; audio stays in memory and is never
  written or logged (the log has only durations and frame counts).

Differences from the NIM: no Audio2Emotion (the face animates with a neutral
emotion; emotions in the request are ignored), no face-parameter overrides from
the header (the model's own `model_config.json` applies) and no echoed audio or
emotion metadata in the reply; Martlet's client uses none of them.

## Check it

On the host: `martlet-host status` shows whether 127.0.0.1:52000 listens and
the service's log (`docker compose -p martlet-audio2face logs`). From Martlet's
MCP server, `audio2face_check` animates a synthesized test signal through the
production client against a loopback endpoint (see [MCP](../../docs/MCP.md)).

Licenses: see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt). The models are
not in the image; they are downloaded on the host under the NVIDIA Open Model
License, which installing the local engine accepts.
