# Martlet OCR worker

The `ocr` host role reads text from screen images on the host CPU. It uses RapidOCR and the PP-OCRv4 ONNX models that are included in `rapidocr_onnxruntime` 1.4.4.

The service listens only on `127.0.0.1:50087`. The Martlet gateway relay is its only network client.

- `GET /status` returns the engine state and version.
- `POST /read` accepts one PNG or JPEG image, at most 4 MiB, as the request body.
- The response is one JSON object with `lines`, `width`, `height` and `milliseconds`.
- Images stay in memory. The service writes nothing to disk.

Builds use `host/Dockerfile`. It installs only hash-pinned wheels from `host/requirements.lock`.

Run tests with:

```sh
python -m unittest discover -s workers/ocr/tests
```

Run the host outside a container with `MARTLET_OCR_PORT` when the locked packages are installed:

```sh
python workers/ocr/host/martlet_ocr_host.py serve
```
