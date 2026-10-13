# Martlet voice recognition worker

The `voice-recognition` host role: Martlet's speaker recognition (who is speaking) on a paired host,
on its NVIDIA graphics card when it has one and otherwise on its processor. It runs the same steps
and the same models as the desktop does on its own processor (`src/Martlet.Sherpa/SpeakerEngine.cs`):
pyannote segmentation 3.0 finds who spoke when, and WeSpeaker ResNet34-LM makes a 256-number
voiceprint from each person's clean speech. A voiceprint from the host therefore matches the shared
voice list exactly like one from the desktop. See the `voice-recognition` row in
[the host roles](../../deploy/host/README.md) and [Voice recognition on a host](../../docs/VOICE_RECOGNITION_HOST.md).

- `host/martlet_voice_recognition_host.py`: the service. `analyze` and `voiceprint` are ports of
  `SpeakerEngine.Analyze` and `SpeakerEngine.Voiceprint` with the same thresholds, order of steps and
  float32 and .NET rounding arithmetic. It listens on 127.0.0.1:50091 at once and loads the models in
  the background. With `MARTLET_VOICE_RECOGNITION_ACCELERATOR=gpu` it first checks in a child process
  that CUDA works and otherwise runs on the processor; `/status` says which (`provider`).
- `GET /status`: `{"state": "ready"|"loading", "provider": "cuda"|"cpu", "model", ...}`.
  `POST /analyze` and `POST /voiceprint`: 16 kHz mono PCM16 little-endian (at most 60 s) as
  `application/octet-stream`, answered with one JSON object. Audio stays in memory and is never
  written or logged.
- `host/Dockerfile`, `host/requirements.lock` (processor: sherpa-onnx 1.13.8 from PyPI, Linux
  x86_64 and aarch64) and `host/requirements-cuda.in`/`.lock` (graphics card: sherpa-onnx 1.13.8's
  CUDA 12 build with ONNX Runtime 1.28.2 and NVIDIA's CUDA 12 and cuDNN 9 libraries from PyPI,
  x86_64). The build downloads both models from the desktop build's pinned URLs, checks each size
  and SHA-256, bakes them in and runs a self-test.
- Other commands: `wait-ready` (martlet-host's warm step), `fetch-models <dir>`, `selftest`,
  `probe --provider cuda` and `analyze-files [--models DIR] [--provider cpu|cuda] FILE...`, which
  prints one JSON answer per 16 kHz PCM16 file.
- `tests/test_host.py`: the port with stand-in models, the audio and request checks, the pinned
  downloads and the HTTP contract (`python -m unittest discover -s workers/voice-recognition/tests`).
  `tests/clean_cases.json` is shared with `tests/Martlet.Desktop.Tests/VoiceRecognitionHostTests.cs`,
  which also compares the port with the desktop engine on the same speech (opt-in, see that file).
