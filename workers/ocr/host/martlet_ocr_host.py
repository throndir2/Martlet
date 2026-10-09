"""Martlet OCR host service for the ``ocr`` host role (Reading).

It runs inside the ``ocr`` role container next to the Martlet gateway. The gateway OCR relay
(Martlet.Gateway.Ocr) is its only client. The service reads one PNG or JPEG image in memory and returns the text lines.

Engines ($MARTLET_OCR_ENGINE, one per image):
  rapidocr  RapidOCR 1.4.4 with its PP-OCRv4 models, on the processor (host/Dockerfile, the default)
  ppocrv5   RapidOCR 3.10.0 with PaddleOCR's PP-OCRv5 models ($OCR_MODEL: ppocrv5-mobile or ppocrv5-server), on the
            processor or, with $MARTLET_OCR_ACCELERATOR=gpu, an NVIDIA graphics card (host/Dockerfile.ppocrv5)

Commands:
  serve               load the engine in the background and listen on 127.0.0.1:$MARTLET_OCR_PORT (default 50087)
  selftest            load the engine, OCR a generated image in memory, print JSON and exit
  fetch-models <dir>  download the pinned PP-OCRv5 models into <dir> and check their SHA-256 (the image build runs it)

HTTP (127.0.0.1 only):
  GET  /status  {"state":"ready"|"loading"|"failed","engine":"rapidocr"|"ppocrv5","engine_version":"1.4.4",
                 "model":"rapidocr-ppocrv4","accelerator":"cpu"|"gpu","threads":N}, with "providers" (ONNX Runtime's, for
                 ppocrv5) and "error" (when it failed to load)
  POST /read    raw image/jpeg or image/png body, at most 4 MiB -> {"lines", "width", "height", "milliseconds"}
"""

from __future__ import annotations

import argparse
import hashlib
import http.client
import http.server
import importlib.metadata
import io
import json
import os
import sys
import threading
import time
import unicodedata
import urllib.request
from typing import Any, Callable, Mapping, Sequence

PORT = int(os.environ.get("MARTLET_OCR_PORT", "50087"))
MAX_IMAGE_BYTES = 4 * 1024 * 1024
MAX_LINES = 256
MAX_TEXT_CHARS = 512
ENGINE = "rapidocr"
ENGINE_VERSION = "1.4.4"
ENGINE_MODEL = "rapidocr-ppocrv4"
PPOCRV5 = "ppocrv5"
PPOCRV5_VERSION = "3.10.0"
MODEL_ROOT = os.environ.get("MARTLET_OCR_MODELS", "/opt/martlet-ocr/models")
# RapidOCR 3 shrinks an image whose longer side is above this before it reads it; 2000 (its default) loses a 4K screen's
# small text, so a screen up to 8K is read at its full size.
MAX_SIDE = 8192
# Where the models come from, in order: RapidOCR's ModelScope release v3.10.0, then a Hugging Face copy of the same files at
# a pinned commit (ModelScope's CDN sometimes answers with a certificate for another name). Every file must match its pinned
# SHA-256 whichever source gives it.
MODEL_SOURCES = (
    "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/PP-OCRv5/",
    "https://huggingface.co/sukode/RapidOCR/resolve/327b631738f91239c310a1b848c5dbd6a7f41690/onnx/PP-OCRv5/",
)
# PaddleOCR's PP-OCRv5 models (Apache-2.0) as RapidOCR 3.10.0 publishes them in ONNX: the "ch" models read Chinese,
# English and Japanese; mobile is small and fast, server is the most accurate.
MODELS: dict[str, dict[str, tuple[str, str]]] = {
    "ppocrv5-mobile": {
        "det": ("det/ch_PP-OCRv5_det_mobile.onnx", "4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae"),
        "rec": ("rec/ch_PP-OCRv5_rec_mobile.onnx", "5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5"),
    },
    "ppocrv5-server": {
        "det": ("det/ch_PP-OCRv5_det_server.onnx", "0f8846b1d4bba223a2a2f9d9b44022fbc22cc019051a602b41a7fda9667e4cad"),
        "rec": ("rec/ch_PP-OCRv5_rec_server.onnx", "e09385400eaaaef34ceff54aeb7c4f0f1fe014c27fa8b9905d4709b65746562a"),
    },
}
ACCELERATORS = ("cpu", "gpu")


class Settings:
    """The engine, model and accelerator this container runs, from its environment."""

    def __init__(self, engine: str = ENGINE, model: str = ENGINE_MODEL, accelerator: str = "cpu") -> None:
        if engine == ENGINE:
            if model != ENGINE_MODEL or accelerator != "cpu":
                raise ValueError("rapidocr runs rapidocr-ppocrv4 on the cpu")
        elif engine == PPOCRV5:
            if model not in MODELS:
                raise ValueError(f"OCR_MODEL must be one of: {' '.join(MODELS)}")
            if accelerator not in ACCELERATORS:
                raise ValueError("MARTLET_OCR_ACCELERATOR must be cpu or gpu")
        else:
            raise ValueError(f"MARTLET_OCR_ENGINE must be {ENGINE} or {PPOCRV5}")
        self.engine = engine
        self.model = model
        self.accelerator = accelerator

    @classmethod
    def from_env(cls, env: Mapping[str, str] = os.environ) -> "Settings":
        engine = (env.get("MARTLET_OCR_ENGINE") or ENGINE).strip().lower()
        if engine == ENGINE:
            return cls()
        model = (env.get("OCR_MODEL") or "ppocrv5-mobile").strip().lower()
        accelerator = (env.get("MARTLET_OCR_ACCELERATOR") or "cpu").strip().lower()
        return cls(engine, model, accelerator)


class BadRequest(Exception):
    """The request is not one bounded image that this service can read."""


class NotReady(Exception):
    """The OCR engine is not loaded yet."""


def error_payload(code: str, summary: str) -> dict[str, Any]:
    return {"error": {"code": code, "summary": summary}}


def decode_image(data: bytes, media_type: str) -> Any:
    """Decode the request image with OpenCV and return a BGR image array."""
    import cv2  # noqa: PLC0415
    import numpy  # noqa: PLC0415

    array = numpy.frombuffer(data, dtype=numpy.uint8)
    image = cv2.imdecode(array, cv2.IMREAD_COLOR)
    if image is None:
        raise BadRequest("send a decodable JPEG or PNG image")
    return image


def image_size(image: Any) -> tuple[int, int]:
    shape = getattr(image, "shape", None)
    if not shape or len(shape) < 2:
        raise BadRequest("send a decodable JPEG or PNG image")
    return int(shape[1]), int(shape[0])


def normalize_results(result: Any) -> list[Any]:
    # RapidOCR 3 returns one RapidOCROutput (boxes, txts and scores, None when it found no text); 1.x a (lines, times) tuple.
    if hasattr(result, "txts") and hasattr(result, "boxes"):
        boxes, txts, scores = result.boxes, result.txts, getattr(result, "scores", None)
        if boxes is None or txts is None:
            return []
        scores = list(scores) if scores is not None else [0.0] * len(txts)
        return list(zip(list(boxes), list(txts), scores))
    if isinstance(result, tuple) and result:
        result = result[0]
    if result is None:
        return []
    return list(result)


def box_rect(points: Any) -> list[int]:
    xs: list[float] = []
    ys: list[float] = []
    for point in points:
        if len(point) < 2:
            raise ValueError("bad OCR box")
        xs.append(float(point[0]))
        ys.append(float(point[1]))
    if not xs or not ys:
        raise ValueError("bad OCR box")
    left = round(min(xs))
    top = round(min(ys))
    right = round(max(xs))
    bottom = round(max(ys))
    return [left, top, max(0, right - left), max(0, bottom - top)]


def line_from_result(item: Any) -> dict[str, Any] | None:
    try:
        # NFKC turns full-width forms (PP-OCRv5 reads "87／100") into plain characters for speech and the model.
        box, text, score = item[0], unicodedata.normalize("NFKC", str(item[1])).strip(), float(item[2])
    except (TypeError, ValueError, IndexError):
        return None
    if not text:
        return None
    text = text[:MAX_TEXT_CHARS]
    return {"text": text, "score": round(score, 3), "box": box_rect(box)}


class RapidEngine:
    """One RapidOCR engine, loaded once."""

    def __init__(self) -> None:
        import numpy  # noqa: PLC0415
        from rapidocr_onnxruntime import RapidOCR  # noqa: PLC0415

        self._engine = RapidOCR()
        tiny = numpy.full((16, 64, 3), 255, dtype=numpy.uint8)
        self._engine(tiny, use_cls=False)

    def __call__(self, image: Any) -> Any:
        return self._engine(image, use_cls=False)


def generated_image(text: str = "MARTLET") -> Any:
    """A BGR image array with <text> on it, to load and warm up an engine's models."""
    import numpy  # noqa: PLC0415
    from PIL import Image  # noqa: PLC0415

    image = Image.open(io.BytesIO(generated_png(text))).convert("RGB")
    return numpy.ascontiguousarray(numpy.asarray(image)[:, :, ::-1])


def session_providers(engine: Any) -> list[str]:
    """ONNX Runtime's execution providers for RapidOCR 3's text detector ("CUDAExecutionProvider" on the graphics card)."""
    for path in (("text_det", "session", "session"), ("text_det", "session"), ("text_det", "infer", "session")):
        target = engine
        for name in path:
            target = getattr(target, name, None)
            if target is None:
                break
        get = getattr(target, "get_providers", None)
        if callable(get):
            return [str(provider) for provider in get()]
    return []


class PpOcrV5Engine:
    """RapidOCR 3 with the PP-OCRv5 models baked into the image (no download), loaded and warmed up once."""

    def __init__(self, model: str, accelerator: str, model_root: str = MODEL_ROOT) -> None:
        if accelerator == "gpu":
            import onnxruntime  # noqa: PLC0415

            # ONNX Runtime finds the CUDA and cuDNN libraries that pip installed (nvidia-* packages) only when they are loaded first.
            preload = getattr(onnxruntime, "preload_dlls", None)
            if callable(preload):
                preload()
        from rapidocr import LangDet, LangRec, ModelType, OCRVersion, RapidOCR  # noqa: PLC0415

        files = MODELS[model]
        paths = {}
        for task, (name, _) in files.items():
            path = os.path.join(model_root, name)
            if not os.path.isfile(path):
                raise FileNotFoundError(f"PP-OCRv5 model missing: {name}")
            paths[task] = path
        model_type = ModelType.SERVER if model.endswith("-server") else ModelType.MOBILE
        params = {
            "Global.use_cls": False,
            "Global.max_side_len": MAX_SIDE,
            "Global.log_level": "warning",
            "Det.ocr_version": OCRVersion.PPOCRV5, "Det.model_type": model_type, "Det.lang_type": LangDet.CH,
            "Det.model_path": paths["det"],
            "Rec.ocr_version": OCRVersion.PPOCRV5, "Rec.model_type": model_type, "Rec.lang_type": LangRec.CH,
            "Rec.model_path": paths["rec"],
            "EngineConfig.onnxruntime.use_cuda": accelerator == "gpu",
        }
        self._engine = RapidOCR(params=params)
        # Both models load on the first read with text: read one now, so a screenshot never waits for them.
        self._engine(generated_image(), use_cls=False)
        self.providers = session_providers(self._engine)
        if accelerator == "gpu" and "CUDAExecutionProvider" not in self.providers:
            raise RuntimeError("ONNX Runtime can't use the NVIDIA graphics card here (no CUDAExecutionProvider)")

    def __call__(self, image: Any) -> Any:
        return self._engine(image, use_cls=False)


class Service:
    """The OCR engine and its HTTP-safe operations."""

    def __init__(self, engine: Callable[[Any], Any] | None = None,
                 decoder: Callable[[bytes, str], Any] = decode_image,
                 engine_version: str | None = None,
                 thread_count: int | None = None,
                 settings: Settings | None = None) -> None:
        self._settings = settings or Settings()
        self._engine = engine
        self._decoder = decoder
        self._engine_version = engine_version or (ENGINE_VERSION if self._settings.engine == ENGINE else PPOCRV5_VERSION)
        self._threads = thread_count or max(1, os.cpu_count() or 1)
        self._providers: list[str] = list(getattr(engine, "providers", []) or [])
        self._lock = threading.Lock()
        self._load_lock = threading.Lock()
        self._load_error: str | None = None

    @property
    def ready(self) -> bool:
        return self._engine is not None

    def start_loading(self) -> None:
        thread = threading.Thread(target=self.load, daemon=True)
        thread.start()

    def load(self) -> None:
        with self._load_lock:
            if self._engine is not None:
                return
            self._load_error = None
            try:
                if self._settings.engine == PPOCRV5:
                    engine = PpOcrV5Engine(self._settings.model, self._settings.accelerator)
                    self._providers = engine.providers
                    package = "rapidocr"
                else:
                    engine = RapidEngine()
                    package = "rapidocr_onnxruntime"
                try:
                    self._engine_version = importlib.metadata.version(package)
                except importlib.metadata.PackageNotFoundError:
                    pass
                self._engine = engine
            except Exception as error:  # noqa: BLE001 - keep status reachable while it and the log name the load failure
                self._load_error = f"{type(error).__name__}: {error}"[:300]
                print(f"OCR engine failed to load: {self._load_error}", file=sys.stderr, flush=True)

    def status(self) -> dict[str, Any]:
        state = "ready" if self.ready else "failed" if self._load_error else "loading"
        payload: dict[str, Any] = {"state": state, "engine": self._settings.engine, "engine_version": self._engine_version,
                                   "model": self._settings.model, "accelerator": self._settings.accelerator,
                                   "threads": self._threads}
        if self._providers:
            payload["providers"] = self._providers
        if state == "failed":
            payload["error"] = self._load_error
        return payload

    def read(self, data: bytes, media_type: str) -> dict[str, Any]:
        if self._engine is None:
            raise NotReady(f"OCR engine failed to load: {self._load_error}" if self._load_error else "OCR engine is loading")
        if media_type not in ("image/jpeg", "image/png"):
            raise BadRequest("send image/jpeg or image/png")
        if not 0 < len(data) <= MAX_IMAGE_BYTES:
            raise BadRequest(f"send 1 to {MAX_IMAGE_BYTES} image bytes")
        image = self._decoder(data, media_type)
        width, height = image_size(image)
        start = time.perf_counter()
        with self._lock:
            raw = self._engine(image)
        elapsed = int(round((time.perf_counter() - start) * 1000))
        lines = [line for item in normalize_results(raw) if (line := line_from_result(item)) is not None]
        lines.sort(key=lambda line: (line["box"][1], line["box"][0]))
        return {"lines": lines[:MAX_LINES], "width": width, "height": height, "milliseconds": elapsed}


class Handler(http.server.BaseHTTPRequestHandler):
    server_version = "MartletOCR/1"
    service: Service

    def log_message(self, format: str, *args: Any) -> None:  # noqa: A002 - no image data in logs
        pass

    def _send(self, status: int, payload: dict[str, Any]) -> None:
        body = json.dumps(payload, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self) -> None:  # noqa: N802
        if self.path == "/status":
            self._send(200, self.service.status())
        else:
            self._send(404, error_payload("request.not_found", "not found"))

    def do_POST(self) -> None:  # noqa: N802
        if self.path != "/read":
            self._send(404, error_payload("request.not_found", "not found"))
            return
        media_type = (self.headers.get("Content-Type") or "").split(";", 1)[0].strip().lower()
        try:
            length = int(self.headers.get("Content-Length", ""))
        except ValueError:
            self.close_connection = True
            self._send(400, error_payload("request.invalid", "send Content-Length"))
            return
        if media_type not in ("image/jpeg", "image/png") or not 0 < length <= MAX_IMAGE_BYTES:
            self.close_connection = True
            self._send(400, error_payload("request.invalid", "send one PNG or JPEG image of at most 4 MiB"))
            return
        data = self.rfile.read(length)
        try:
            self._send(200, self.service.read(data, media_type))
        except BadRequest as error:
            self._send(400, error_payload("request.invalid", str(error)))
        except NotReady:
            self._send(503, error_payload("worker.loading", "OCR engine is loading"))
        except Exception as error:  # noqa: BLE001 - the relay maps this to worker.failed
            print(f"OCR failed: {type(error).__name__}", file=sys.stderr, flush=True)
            self._send(500, error_payload("worker.failed", "OCR failed"))


def serve(service: Service, port: int = PORT) -> http.server.ThreadingHTTPServer:
    handler = type("BoundHandler", (Handler,), {"service": service})
    return http.server.ThreadingHTTPServer(("127.0.0.1", port), handler)


def generated_png(text: str = "MARTLET") -> bytes:
    from PIL import Image, ImageDraw, ImageFont  # noqa: PLC0415

    image = Image.new("RGB", (320, 96), "white")
    draw = ImageDraw.Draw(image)
    try:
        font = ImageFont.truetype("arial.ttf", 42)
    except OSError:
        font = ImageFont.load_default()
    draw.text((20, 20), text, fill="black", font=font)
    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    return buffer.getvalue()


def sha256_file(path: str) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as file:
        for chunk in iter(lambda: file.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def download(url: str, path: str, opener: Callable[[str], Any], pause: Callable[[float], None]) -> None:
    for attempt in range(3):
        try:
            with opener(url) as response, open(path, "wb") as file:
                while chunk := response.read(1024 * 1024):
                    file.write(chunk)
            return
        except (OSError, http.client.HTTPException):
            if attempt == 2:
                raise
            pause(5 * (attempt + 1))


def fetch_models(root: str, sources: Sequence[str] = MODEL_SOURCES,
                 opener: Callable[[str], Any] = lambda url: urllib.request.urlopen(url, timeout=120),
                 pause: Callable[[float], None] = time.sleep) -> list[str]:
    """Downloads every pinned PP-OCRv5 model into <root> (once; kept when its SHA-256 matches) and checks it. Tries each
    source in turn; fails when no source gives a copy that matches the pinned SHA-256."""
    fetched = []
    for files in MODELS.values():
        for name, sha in files.values():
            path = os.path.join(root, name)
            if os.path.isfile(path) and sha256_file(path) == sha:
                continue
            os.makedirs(os.path.dirname(path), exist_ok=True)
            partial = path + ".part"
            problems = []
            for base in sources:
                try:
                    download(base + name, partial, opener, pause)
                except (OSError, http.client.HTTPException) as error:
                    problems.append(f"{base}: {error}")
                    continue
                if sha256_file(partial) == sha:
                    os.replace(partial, path)
                    fetched.append(name)
                    break
                problems.append(f"{base}: does not match its pinned SHA-256")
            else:
                if os.path.exists(partial):
                    os.remove(partial)
                raise SystemExit(f"{name}: no source gave a copy that matches its pinned SHA-256 ({'; '.join(problems)})")
    return fetched


def selftest(settings: Settings | None = None) -> None:
    service = Service(settings=settings or Settings.from_env())
    service.load()
    if not service.ready:
        raise SystemExit(f"OCR engine did not load: {service.status().get('error')}")
    payload = service.read(generated_png(), "image/png")
    payload["status"] = service.status()
    print(json.dumps(payload, separators=(",", ":")), flush=True)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("command", nargs="?", default="serve", choices=("serve", "selftest", "fetch-models"))
    parser.add_argument("directory", nargs="?")
    args = parser.parse_args(argv)
    if args.command == "fetch-models":
        for name in fetch_models(args.directory or MODEL_ROOT):
            print(f"Fetched {name}", flush=True)
        return 0
    try:
        settings = Settings.from_env()
    except ValueError as error:
        print(f"Martlet OCR host: {error}", file=sys.stderr, flush=True)
        return 2
    if args.command == "selftest":
        selftest(settings)
        return 0
    service = Service(settings=settings)
    service.start_loading()
    server = serve(service)
    print(f"Martlet OCR host listening on 127.0.0.1:{server.server_address[1]}", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
