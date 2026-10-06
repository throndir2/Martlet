# Martlet Parakeet worker

The `stt` host role's `parakeet` engine: NVIDIA Parakeet speech-to-text on the host's CPU through
sherpa-onnx, the same models Martlet's desktop runs on its own processor
(`src/Martlet.Sherpa/ParakeetModels.cs`). It answers whisper.cpp's server contract on
127.0.0.1:8178, so the gateway's speech-to-text relay (`Martlet.Gateway.Stt`) treats both engines
alike. See the `stt` row in [the host roles](../../deploy/host/README.md).

- `host/martlet_parakeet_host.py`: the service (`serve`, `provision`). `STT_MODEL` names the model:
  `parakeet-tdt-110m-en` (English, the fastest), `parakeet-tdt-0.6b-v2-int8` (English, the most
  accurate) or `parakeet-tdt-0.6b-v3-int8` (25 European languages). On its first start it downloads
  that model from Hugging Face at a pinned revision, each file checked for its exact size and
  SHA-256, into `models/<id>` (the `martlet-stt-parakeet-models` volume); with `MARTLET_PREPARE=1`
  it only downloads and exits.
- `POST /inference`: one utterance as a 16-bit PCM mono WAV (8-48 kHz, at most 60 s) in the
  multipart field `file`, answered with `{"text": "..."}`. Audio stays in memory. `GET /status`:
  engine, model, threads and runtime versions.
- `host/Dockerfile`, `host/requirements.lock`: the hash-locked image the role builds (Linux x86_64;
  the aarch64 wheels are pinned too, and the image builds and imports them under emulation).
- `tests/test_host.py`: the form, WAV, download checks and HTTP contract with a stand-in
  transcriber (`python -m unittest discover -s workers/parakeet/tests`).

Run it outside a container with `MARTLET_PARAKEET_ROOT` (models), `MARTLET_PARAKEET_PORT`,
`STT_MODEL` and, optionally, `PARAKEET_THREADS` (default: up to 4) in an environment that has the
lock's packages.
