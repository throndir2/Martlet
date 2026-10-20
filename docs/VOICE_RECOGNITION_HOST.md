# Voice recognition host role

The `voice-recognition` host role recognizes who is speaking on a paired
Martlet host. It does what Companion › People does on the companion PC's own
processor (`src/Martlet.Sherpa/SpeakerEngine.cs`, see [Voices](VOICES.md)),
with the same models and the same steps:

1. pyannote segmentation 3.0 finds who spoke when. It finds up to three people
   in each ten seconds and groups longer speech by voice (clustering threshold
   0.5).
2. WeSpeaker ResNet34-LM, trained on VoxCeleb, makes a 256-number voiceprint
   from each person's clean speech only (nobody else talking at that time).

Because the models and the steps are the same, a voiceprint from the host
matches the shared voice list exactly like a voiceprint from the desktop.

The host runs the models on its NVIDIA graphics card when it has one (ONNX
Runtime's CUDA provider), and otherwise on its processor. This keeps the work
off a companion PC that is busy with a game.

## Host setup

Install it with the normal host flow:

```sh
martlet-host add voice-recognition
```

The role asks for the accelerator (`gpu` or `cpu`) when the host can run GPU
containers, and for the model (`VOICE_RECOGNITION_MODEL`). There is one model,
`wespeaker-en-voxceleb-resnet34-lm`, the desktop's. ARM64 hosts always use the
processor build (`compose.arm64.yaml`).

The role requires Docker. It builds an image on the host from
`workers/voice-recognition/host/Dockerfile`:

- `python:3.12.10-slim-bookworm` and hash-pinned PyPI wheels.
- On the processor (`martlet-voice-recognition:2`): sherpa-onnx `1.13.8` with
  ONNX Runtime `1.28.2` and NumPy `2.5.3`, for Linux x86_64 and aarch64. These
  are the same pins as the Parakeet worker and the same runtime as the desktop.
- On the graphics card (`martlet-voice-recognition-cuda:2`, `compose.gpu.yaml`):
  sherpa-onnx `1.13.8`'s own CUDA 12 build with ONNX Runtime `1.28.2` (from
  k2-fsa's Hugging Face wheels, pinned to a commit and SHA-256) and NVIDIA's
  CUDA 12 and cuDNN 9 libraries from PyPI, about 2 GB more. It needs an NVIDIA
  driver for CUDA 12.
- The two models from the desktop build's pinned URLs, each checked for its
  size and SHA-256, baked into the image. The container downloads nothing when
  it runs.

The build analyzes generated sound with the models before it finishes. The
images set `ORT_DISABLE_TELEMETRY=1`.

When the service starts, it listens at once and loads the models in the
background. With the `gpu` accelerator it first loads the models with CUDA in a
child process. When that fails (no card, no driver or a driver that is too old),
it uses the processor and says so in its log and its status. The role's warm
step waits until the models are loaded, also after `martlet-host warm`.

## Gateway route

- Route ID: `martlet.gateway.voice-recognition.v1`
- Path: `/martlet/v1/inference/voice-recognition`
- Contract: `martlet.voice-recognition-relay` version `1.0`
- Relay: `Martlet.Gateway.VoiceRecognition`
- Role: `GatewayRole.Voice` (paired computers and friends)
- Model ID: `wespeaker-en-voxceleb-resnet34-lm`, revision
  `speaker-recongition-models` (the sherpa-onnx release tag), SHA-256
  `e9848563da86f263117134dfd7ad63c92355b37de492b55e325400c9d9c39012`
- Port: `127.0.0.1:50091`

Each request has one operation and gets one JSON answer.

`status`:

```json
{"operation":"status"}
```

```json
{"state":"ready","provider":"cuda","model":"wespeaker-en-voxceleb-resnet34-lm","segmentation":"pyannote-segmentation-3-0","accelerator":"gpu","threads":4,"runtime":{"sherpa-onnx":"1.13.8+cuda12.cudnn9.onnxruntime1.28.2","numpy":"2.5.3","onnxruntime":"1.28.2"}}
```

`state` is `ready` or `loading`. While the service loads, `provider` is the
provider it tries. When the service does not answer yet (its container starts,
or restarts after a failed load), the relay answers
`{"state":"loading","model":"wespeaker-en-voxceleb-resnet34-lm"}` without a
`provider`.

`analyze` says who spoke in up to a minute of 16 kHz mono PCM16
(little-endian), as `SpeakerEngine.Analyze` does:

```json
{"operation":"analyze","sample_rate":16000,"pcm16_base64":"..."}
```

```json
{"speakers":[{"local":0,"voiceprint":[0.0123,...],"clean_seconds":2.4,"start":0.5,"end":3.1}],"voices":1,"overlap":false,"whole":null,"speech_seconds":3.0,"milliseconds":42}
```

- `speakers`: the people with at least 2 seconds of clean speech, most first.
  Each `voiceprint` has 256 numbers and length 1. It is the average of up to
  three clean stretches of at most 8 seconds each. Stretches that disagree
  (cosine below 0.45) are left out together.
- `voices`: how many voices segmentation heard. `overlap`: two of them talked
  over each other for more than 0.2 seconds.
- `whole`: a voiceprint of all the audio, only when nobody has enough clean
  speech and the audio is at least 1 second long; otherwise `null`.
- `speech_seconds`: the seconds of speech. Times are rounded to 0.01 s as the
  desktop rounds them.

`voiceprint` makes one voiceprint of all the audio, as
`SpeakerEngine.Voiceprint` does. It is `null` for less than 1 second:

```json
{"operation":"voiceprint","sample_rate":16000,"pcm16_base64":"..."}
```

```json
{"voiceprint":[0.0123,...],"milliseconds":12}
```

Each voiceprint number is the float32 value written with the fewest digits that
read back as the same float32.

Errors: `request.invalid` (another sample rate, odd or empty audio, other
fields), `request.too_large` (more than a minute), `worker.unavailable` (the
service is loading or not answering) and `worker.failed`.

## Same answers as the desktop

`tests/Martlet.Desktop.Tests/VoiceRecognitionHostTests.cs` keeps the host and the
desktop the same:

- The host's model files, their sizes, SHA-256 and URLs, and the thresholds are
  the desktop build's.
- `workers/voice-recognition/tests/clean_cases.json` holds clean-speech and
  rounding cases that both `SpeakerEngine.Clean` and .NET's `Math.Round` and the
  host's port must answer the same way.
- `The_hosts_port_answers_like_this_pc` (opt-in) synthesizes speech with two of
  Windows' voices: one voice alone, two taking turns, two talking over each
  other, a voice that comes back, and audio too short for a voiceprint. It runs
  the desktop engine and the host's port on the same audio and compares every
  answer. Set `MARTLET_VOICE_RECOGNITION_PYTHON` to a Python with sherpa-onnx
  1.13.8 to run the port on this PC, or `MARTLET_VOICE_RECOGNITION_IMAGE` to the
  built image to run it in Docker (`MARTLET_VOICE_RECOGNITION_GPU=1` for the
  graphics card).
- The MCP tool `voice_recognition_engine_check` sends the same kind of speech
  through the whole path: the desktop's client, a real gateway, this role's
  relay and a running service. With `martletDirectory` it compares the answers
  with this PC's engine. See [MCP](MCP.md).

On the processor the answers are the same: the same voices, the same times to
0.01 s and voiceprints with a cosine of 1.0000000. With the Windows wheel of
sherpa-onnx (the same ONNX Runtime build as the desktop's) every voiceprint
number is the same to the last bit; in the Linux images each number is within
0.000002 of the desktop's. On the graphics card the voiceprints differ a little
more, and the test asks for a cosine of at least 0.995 and times within 0.05 s.

## Data handling

The role listens only on loopback. The Martlet gateway is the only LAN entry
point. Paired computers send the speech Martlet hears, one utterance of up to a
minute at a time. The service analyzes it in memory, clears the samples after
each request and does not write or log audio, voiceprints or results.

## Licenses

Using the role means accepting these licenses:

- sherpa-onnx (and its CUDA build): Apache-2.0.
- ONNX Runtime: MIT.
- NumPy: BSD-3-Clause.
- WeSpeaker ResNet34-LM model: CC BY 4.0.
- pyannote segmentation 3.0 model and its ONNX export: MIT.
- With the `gpu` accelerator: NVIDIA CUDA and cuDNN libraries, NVIDIA's
  license.

`workers/voice-recognition/THIRD-PARTY-NOTICES.txt` lists each with its source.
