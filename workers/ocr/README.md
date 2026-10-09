# Martlet OCR worker

The `ocr` host role reads text from screen images. It has two engines:

- `rapidocr` (`host/Dockerfile`): RapidOCR and the PP-OCRv4 ONNX models that are included in `rapidocr_onnxruntime` 1.4.4, on the host CPU.
- `ppocrv5` (`host/Dockerfile.ppocrv5`): RapidOCR 3.10.0 with PaddleOCR's PP-OCRv5 ONNX models. `OCR_MODEL` is `ppocrv5-mobile` or `ppocrv5-server`. `MARTLET_OCR_ACCELERATOR=gpu` (build argument `ACCELERATOR=gpu`) uses an NVIDIA GPU through ONNX Runtime's CUDA 13 build.

The service listens only on `127.0.0.1:50087`. The Martlet gateway relay is its only network client.

- `GET /status` returns the engine state and version.
- `POST /read` accepts one PNG or JPEG image, at most 4 MiB, as the request body.
- The response is one JSON object with `lines`, `width`, `height` and `milliseconds`.
- Images stay in memory. The service writes nothing to disk.

Builds install only hash-pinned wheels: `host/requirements.lock` for `rapidocr`, `host/requirements-ppocrv5.lock` (CPU) or `host/requirements-ppocrv5-cuda.lock` (GPU) for `ppocrv5`. The PP-OCRv5 build runs `martlet_ocr_host.py fetch-models`, which downloads the pinned models and checks their SHA-256, then a self-test with each model.

Run tests with:

```sh
python -m unittest discover -s workers/ocr/tests
```

Run the host outside a container with `MARTLET_OCR_PORT` (and `MARTLET_OCR_ENGINE`, `OCR_MODEL`, `MARTLET_OCR_ACCELERATOR` and `MARTLET_OCR_MODELS` for PP-OCRv5) when the locked packages are installed:

```sh
python workers/ocr/host/martlet_ocr_host.py serve
```
