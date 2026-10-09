# OCR host role

The `ocr` host role lets a paired Martlet desktop read text from a screen image.
It has two engines, one at a time:

- `rapidocr`: RapidOCR 1.4.4 with PaddleOCR's PP-OCRv4 models, on the processor.
  It is the smallest.
- `ppocrv5`: RapidOCR 3.10.0 with PaddleOCR's PP-OCRv5 models. It is the most
  accurate. The `ppocrv5-mobile` model runs on the processor. The
  `ppocrv5-server` model is for an NVIDIA graphics card only.

The `rapidocr` engine and the mobile model do not need a graphics card.

The role is for the Reading feature.

## Host setup

Install it with the normal host flow:

```sh
martlet-host add ocr
```

The role asks for the engine (`OCR_ENGINE`) and the model (`OCR_MODEL`). For
`ppocrv5`, it also asks for the accelerator (`cpu` or `gpu`). Companion ›
Reading answers these questions for you. To change the model later, choose it
on that page and select **Switch to**, or run `martlet-host add ocr` again.
Hosts that installed the role before it had engines run `rapidocr`.

The role requires Docker.
It builds an image locally.
The images use `python:3.12.10-slim-bookworm` and hash-pinned PyPI wheels.
They set `ORT_DISABLE_TELEMETRY=1`, so ONNX Runtime sends no usage data.

The `rapidocr` image (`workers/ocr/host/Dockerfile`) installs these main
components:

- RapidOCR `1.4.4` through `rapidocr_onnxruntime`.
- PaddleOCR PP-OCRv4 ONNX models that are inside that package.
- ONNX Runtime `1.30.0`.
- OpenCV Python headless `5.0.0.93`.
- NumPy `2.5.3`.

The `ppocrv5` image (`workers/ocr/host/Dockerfile.ppocrv5`) installs these main
components:

- RapidOCR `3.10.0`.
- PaddleOCR PP-OCRv5 mobile and server ONNX models (about 190 MB). The build
  downloads them from RapidOCR's ModelScope release v3.10.0, or from an
  identical Hugging Face copy, and checks each SHA-256. Both models are in the
  image, so a model change only restarts the service.
- ONNX Runtime `1.31.0`. With the `gpu` accelerator (`compose.gpu.yaml`), it is
  ONNX Runtime's CUDA 13 build with NVIDIA's CUDA and cuDNN libraries from PyPI
  (about 2 GB more). That needs NVIDIA driver 580 or newer.
- OpenCV Python headless `5.0.0.93` and NumPy `2.5.3`.

The CPU image is about 685 MB. The CUDA image is about 3.1 GB. Each build reads
a generated test image with both models before it finishes.

## Gateway route

- Route ID: `martlet.gateway.ocr.v1`
- Path: `/martlet/v1/inference/ocr`
- Contract: `martlet.ocr-relay` version `1.0`
- Role: `GatewayRole.Voice`
- Model IDs: `rapidocr-ppocrv4` (the default), `ppocrv5-mobile` and
  `ppocrv5-server`, each pinned with its model's SHA-256
- Port: `127.0.0.1:50087`

Each request has one operation.
`status` returns the engine state.
`read` sends one PNG or JPEG image.
The image limit is 4 MiB.
Martlet sends the full-size screenshot as a JPEG at quality 90.
When that is more than 4 MiB, Martlet tries quality 75, then quality 60.
The result limit is 256 KiB.

A status result has this form:

```json
{"state":"ready","engine":"ppocrv5","engine_version":"3.10.0","model":"ppocrv5-mobile","accelerator":"cpu","threads":4,"providers":["CPUExecutionProvider"]}
```

The `rapidocr` engine reports `"engine":"rapidocr"` and
`"model":"rapidocr-ppocrv4"`. When `accelerator` is `gpu` and ONNX Runtime
cannot use the card, the state is `failed`.

A read result has this form:

```json
{"lines":[{"text":"Score","score":0.99,"box":[1,2,30,10]}],"width":100,"height":50,"milliseconds":12}
```

The `box` values are `x`, `y`, `width` and `height`.
The service returns at most 256 lines.
Each line has at most 512 characters.

## Data handling

The role listens only on loopback.
The Martlet gateway is the only LAN entry point.
Paired desktops send screenshots while Martlet watches the screen.
The worker decodes and reads each image in memory.
It does not write screenshots or OCR text to disk.

## Licenses

Using the role means accepting these licenses:

- RapidOCR: Apache-2.0.
- PaddleOCR PP-OCRv4 and PP-OCRv5 models: Apache-2.0.
- With the `gpu` accelerator: NVIDIA CUDA and cuDNN libraries, NVIDIA's
  license.
- omegaconf and antlr4-python3-runtime (PP-OCRv5 image): BSD-3-Clause.
- ONNX Runtime: MIT.
- OpenCV: Apache-2.0.
- NumPy: BSD-3-Clause.
- pyclipper: MIT.
- Shapely: BSD-3-Clause.
- Pillow: MIT-CMU and HPND.
- PyYAML: MIT.
- six: MIT.
- tqdm: MPL-2.0 and MIT.
