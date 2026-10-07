"""Martlet OCR host service: RapidOCR for the ``ocr`` host role.

It runs inside the ``ocr`` role container next to the Martlet gateway. The gateway OCR relay
(Martlet.Gateway.Ocr) is its only client. The service reads one PNG or JPEG image in memory and returns the text lines.

Commands:
  serve     load RapidOCR in the background and listen on 127.0.0.1:$MARTLET_OCR_PORT (default 50087)
  selftest  load RapidOCR, OCR a generated image in memory, print JSON and exit

HTTP (127.0.0.1 only):
  GET  /status  {"state":"ready"|"loading","engine":"rapidocr","engine_version":"1.4.4","threads":N}
  POST /read    raw image/jpeg or image/png body, at most 4 MiB -> {"lines", "width", "height", "milliseconds"}
"""

from __future__ import annotations

import argparse
import http.server
import importlib.metadata
import io
import json
import os
import sys
import threading
import time
from typing import Any, Callable

PORT = int(os.environ.get("MARTLET_OCR_PORT", "50087"))
MAX_IMAGE_BYTES = 4 * 1024 * 1024
MAX_LINES = 256
MAX_TEXT_CHARS = 512
ENGINE = "rapidocr"
ENGINE_VERSION = "1.4.4"


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
        box, text, score = item[0], str(item[1]).strip(), float(item[2])
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


class Service:
    """The OCR engine and its HTTP-safe operations."""

    def __init__(self, engine: Callable[[Any], Any] | None = None,
                 decoder: Callable[[bytes, str], Any] = decode_image,
                 engine_version: str = ENGINE_VERSION,
                 thread_count: int | None = None) -> None:
        self._engine = engine
        self._decoder = decoder
        self._engine_version = engine_version
        self._threads = thread_count or max(1, os.cpu_count() or 1)
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
            try:
                self._engine = RapidEngine()
                try:
                    self._engine_version = importlib.metadata.version("rapidocr_onnxruntime")
                except importlib.metadata.PackageNotFoundError:
                    self._engine_version = ENGINE_VERSION
            except Exception as error:  # noqa: BLE001 - keep status reachable while the log names the load failure
                self._load_error = type(error).__name__
                print(f"OCR engine failed to load: {type(error).__name__}", file=sys.stderr, flush=True)

    def status(self) -> dict[str, Any]:
        return {"state": "ready" if self.ready else "loading", "engine": ENGINE,
                "engine_version": self._engine_version, "threads": self._threads}

    def read(self, data: bytes, media_type: str) -> dict[str, Any]:
        if self._engine is None:
            raise NotReady("OCR engine is loading")
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


def selftest() -> None:
    service = Service()
    service.load()
    if not service.ready:
        raise SystemExit("OCR engine did not load")
    payload = service.read(generated_png(), "image/png")
    print(json.dumps(payload, separators=(",", ":")), flush=True)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("command", nargs="?", default="serve", choices=("serve", "selftest"))
    args = parser.parse_args(argv)
    if args.command == "selftest":
        selftest()
        return 0
    service = Service()
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
