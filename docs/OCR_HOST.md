# OCR host role

The `ocr` host role lets a paired Martlet desktop read text from a screen image.
It runs RapidOCR on the host processor.
It does not need a graphics card.

The role is for the Reading feature.

## Host setup

Install it with the normal host flow:

```sh
martlet-host add ocr
```

The role requires Docker.
It builds an image locally from `workers/ocr/host/Dockerfile`.
The image uses `python:3.12.10-slim-bookworm` and hash-pinned PyPI wheels.

The image installs these main components:

- RapidOCR `1.4.4` through `rapidocr_onnxruntime`.
- PaddleOCR PP-OCRv4 ONNX models that are inside that package.
- ONNX Runtime `1.30.0`.
- OpenCV Python headless `5.0.0.93`.
- NumPy `2.5.3`.

## Gateway route

- Route ID: `martlet.gateway.ocr.v1`
- Path: `/martlet/v1/inference/ocr`
- Contract: `martlet.ocr-relay` version `1.0`
- Role: `GatewayRole.Voice`
- Default model ID: `rapidocr-ppocrv4`
- Port: `127.0.0.1:50087`

Each request has one operation.
`status` returns the engine state.
`read` sends one PNG or JPEG image.
The image limit is 4 MiB.
The result limit is 256 KiB.

A status result has this form:

```json
{"state":"ready","engine":"rapidocr","engine_version":"1.4.4","threads":4}
```

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
- PaddleOCR PP-OCRv4 models: Apache-2.0.
- ONNX Runtime: MIT.
- OpenCV: Apache-2.0.
- NumPy: BSD-3-Clause.
- pyclipper: MIT.
- Shapely: BSD-3-Clause.
- Pillow: MIT-CMU and HPND.
- PyYAML: MIT.
- six: MIT.
- tqdm: MPL-2.0 and MIT.
