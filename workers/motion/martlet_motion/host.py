"""Martlet motion host role command (HY-Motion 1.0).

Usage:
  martlet-motion provision        download and verify the chosen model and its text encoders (MOTION_MODEL)
  martlet-motion serve            answer on 127.0.0.1:50090: GET /status, POST /generate, POST /free
                                  (--listen ADDRESS and --random-weights STEPS are for checks only, see selftest)
  martlet-motion selftest [OUT]   run HY-Motion's real pipeline with RANDOM weights (FIXTURE - NOT AI) on the processor,
                                  then the VRMA converter; no model download (only its 1 KB config.yml when not provisioned)
  martlet-motion code-assets DIR  (image build) put the pinned Git LFS files of the HY-Motion code into DIR

POST /generate takes {"prompt": English text, "seconds": 1-12, "seed": optional 0-2147483647} and answers the VRMA clip
(model/gltf-binary) with X-Martlet-Motion-Frames, -Fps, -Seed, -Milliseconds and -Load-Milliseconds headers. Failures are
{"error": {"code", "summary"}}: motion.busy (409), request.invalid (400), motion.unavailable (503), motion.out_of_memory
(507) or motion.failed (500). The model loads on the first motion and is unloaded by POST /free or after
MARTLET_MOTION_IDLE_MINUTES (default 5) without a motion, so voice and thinking roles get the graphics card back.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import secrets
import sys
import tempfile
import threading
import time
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any, Callable

from . import pins

ROOT = Path(os.environ.get("MARTLET_MOTION_ROOT", "/opt/martlet-motion"))
CODE = ROOT / "HY-Motion-1.0"
MODELS = ROOT / "models"
CKPTS = MODELS / "ckpts"
MANIFEST = MODELS / "martlet-motion.json"
PORT = int(os.environ.get("MARTLET_MOTION_PORT", "50090"))
MAXIMUM_BODY_BYTES = 16 * 1024
MAXIMUM_PROMPT_CHARACTERS = 400
MAXIMUM_PROMPT_WORDS = 60


def _log(message: str) -> None:
    print(message, flush=True)


def _model() -> str:
    return os.environ.get("MOTION_MODEL", pins.DEFAULT_MODEL)


# ------------------------------------------------------------------ downloads


def _sha256_file(path: Path) -> tuple[int, str]:
    digest = hashlib.sha256()
    size = 0
    with path.open("rb") as stream:
        while chunk := stream.read(1_048_576):
            size += len(chunk)
            digest.update(chunk)
    return size, digest.hexdigest()


def _download(url: str, target: Path, size: int, sha256: str) -> None:
    if target.is_file() and not target.is_symlink():
        if target.stat().st_size == size and _sha256_file(target) == (size, sha256):
            _log(f"{target.name}: already downloaded and verified.")
            return
        target.unlink()
    target.parent.mkdir(parents=True, exist_ok=True)
    partial = target.with_name(target.name + ".partial")
    digest = hashlib.sha256()
    received = 0
    shown = -1
    request = urllib.request.Request(url, headers={"User-Agent": "martlet-motion-host"})
    with urllib.request.urlopen(request, timeout=60) as response, partial.open("wb") as output:
        while chunk := response.read(1_048_576):
            received += len(chunk)
            if received > size:
                partial.unlink(missing_ok=True)
                raise SystemExit(f"{target.name} is larger than its pinned size; stopped.")
            digest.update(chunk)
            output.write(chunk)
            percent = received * 100 // size
            if size > 50_000_000 and percent != shown and percent % 5 == 0:
                shown = percent
                _log(f"{target.name}: {percent}% of {size // 1_048_576} MiB")
    if received != size or digest.hexdigest() != sha256:
        partial.unlink(missing_ok=True)
        raise SystemExit(f"{target.name} does not match its pinned SHA-256; nothing was installed.")
    partial.replace(target)
    _log(f"{target.name}: verified (SHA-256 {sha256[:12]}...).")


def code_assets(directory: Path) -> None:
    """Replaces the Git LFS pointer files of the pinned HY-Motion checkout with the pinned files."""
    for asset in pins.CODE_ASSETS:
        _download(asset.url, directory / asset.path, asset.size, asset.sha256)


def provision(model: str | None = None) -> None:
    model = model or _model()
    files = pins.files(model)
    _log(f"Motion model {model}: {len(files)} pinned files, {pins.total_bytes(model) / 1e9:.1f} GB "
         f"(HY-Motion {pins.MODEL_REVISION[:7]}, Qwen3-8B {pins.QWEN_REVISION[:7]}, CLIP ViT-L/14 {pins.CLIP_REVISION[:7]}).")
    artifacts: list[dict[str, Any]] = []
    for pinned in files:
        _log(f"Model file {pinned.artifact_id} ({pinned.license_id})")
        _download(pinned.url, CKPTS / pinned.local, pinned.size, pinned.sha256)
        artifacts.append({"artifact_id": pinned.artifact_id, "bytes": pinned.size, "license_id": pinned.license_id,
                          "revision": pinned.revision, "sha256": pinned.sha256})
    MODELS.mkdir(parents=True, exist_ok=True)
    temporary = MANIFEST.with_name(MANIFEST.name + ".partial")
    temporary.write_text(json.dumps({
        "engine": "hy-motion", "model": model, "repository": pins.MODEL_REPOSITORY, "revision": pins.MODEL_REVISION,
        "sources": {"hy-motion": pins.CODE_COMMIT}, "artifacts": artifacts,
    }, indent=2, sort_keys=True), encoding="utf-8")
    temporary.replace(MANIFEST)
    _log("Motion models are ready.")


# ------------------------------------------------------------------ the service


def parse_request(body: Any) -> tuple[str, float, int]:
    """The prompt, seconds and seed of a generate request (a random seed when none); raises MotionError request.invalid."""
    from .generator import MotionError

    def invalid(summary: str) -> MotionError:
        return MotionError("request.invalid", summary, 400)

    if not isinstance(body, dict) or not set(body) <= {"prompt", "seconds", "seed"} or "prompt" not in body:
        raise invalid("Send a JSON object with prompt, seconds and an optional seed.")
    prompt = body["prompt"].strip() if isinstance(body["prompt"], str) else ""
    if not 3 <= len(prompt) <= MAXIMUM_PROMPT_CHARACTERS or any(ord(c) < 32 or ord(c) == 127 for c in prompt) or \
            not any("a" <= c.lower() <= "z" for c in prompt):
        raise invalid(f"The prompt must be 3-{MAXIMUM_PROMPT_CHARACTERS} characters of English text.")
    if len(prompt.split()) > MAXIMUM_PROMPT_WORDS:
        raise invalid(f"The prompt must be at most {MAXIMUM_PROMPT_WORDS} words.")
    seconds = body.get("seconds", 4)
    if isinstance(seconds, bool) or not isinstance(seconds, (int, float)) or not math.isfinite(seconds) or \
            not pins.MINIMUM_SECONDS <= seconds <= pins.MAXIMUM_SECONDS:
        raise invalid(f"seconds must be {pins.MINIMUM_SECONDS:g}-{pins.MAXIMUM_SECONDS:g}.")
    seed = body.get("seed")
    if seed is None:
        seed = secrets.randbelow(2**31)
    elif isinstance(seed, bool) or not isinstance(seed, int) or not 0 <= seed < 2**31:
        raise invalid("seed must be a whole number 0-2147483647.")
    return prompt, float(seconds), seed


class Service:
    """One motion at a time on one runtime; frees the model after <idle_minutes> without a motion."""

    def __init__(self, runtime: Any, idle_minutes: float = 5.0, make_vrma: Callable[[dict], tuple[bytes, int]] | None = None,
                 clock: Callable[[], float] = time.monotonic) -> None:
        from .generator import to_vrma

        self.runtime = runtime
        self.idle_minutes = idle_minutes
        self.make_vrma = make_vrma or to_vrma
        self.clock = clock
        self.lock = threading.Lock()
        self.last_used = clock()
        self.last_milliseconds: int | None = None
        self.made = 0

    def status(self) -> dict[str, Any]:
        missing = self.runtime.missing()
        answer: dict[str, Any] = {
            "state": "ready" if not missing else "not_provisioned", "engine": "hy-motion", "model": self.runtime.model,
            "code_commit": pins.CODE_COMMIT[:7], "loaded": self.runtime.loaded, "busy": self.lock.locked(), "fps": pins.FPS,
            "minimum_seconds": pins.MINIMUM_SECONDS, "maximum_seconds": pins.MAXIMUM_SECONDS,
            "idle_unload_minutes": self.idle_minutes, "made": self.made, "last_milliseconds": self.last_milliseconds,
        }
        if missing:
            answer["missing"] = missing[:5]
        if fixture := getattr(self.runtime, "fixture", None):
            answer["fixture"] = fixture
        if not self.lock.locked() and (gpu := self.runtime.gpu()) is not None:
            answer["gpu"] = gpu
        return answer

    def generate(self, body: Any) -> tuple[bytes, dict[str, str]]:
        from .generator import MotionError

        prompt, seconds, seed = parse_request(body)
        if not self.lock.acquire(blocking=False):
            raise MotionError("motion.busy", "The motion service is making another motion.", 409)
        try:
            started = time.monotonic()
            was_loaded = self.runtime.loaded
            output = self.runtime.generate(prompt, seconds, seed)
            vrma, frames = self.make_vrma(output)
            milliseconds = int((time.monotonic() - started) * 1000)
            self.last_milliseconds = milliseconds
            self.made += 1
            _log(f"Made a {frames}-frame motion ({len(vrma)} bytes) in {milliseconds} ms"
                 f"{'' if was_loaded else f', loading the model took {self.runtime.load_milliseconds} ms'}.")
            return vrma, {
                "X-Martlet-Motion-Frames": str(frames), "X-Martlet-Motion-Fps": str(pins.FPS), "X-Martlet-Motion-Seed": str(seed),
                "X-Martlet-Motion-Milliseconds": str(milliseconds),
                "X-Martlet-Motion-Load-Milliseconds": str(0 if was_loaded else self.runtime.load_milliseconds),
            }
        finally:
            self.last_used = self.clock()
            self.lock.release()

    def free(self) -> dict[str, Any]:
        from .generator import MotionError

        if not self.lock.acquire(blocking=False):
            raise MotionError("motion.busy", "The motion service is making a motion; free it when that is done.", 409)
        try:
            return {"state": "freed", "was_loaded": self.runtime.free()}
        finally:
            self.lock.release()

    def idle_check(self) -> bool:
        """Frees the model when it is loaded and idle for <idle_minutes>; true when it did."""
        if self.idle_minutes <= 0 or not self.runtime.loaded or self.clock() - self.last_used < self.idle_minutes * 60:
            return False
        if not self.lock.acquire(blocking=False):
            return False
        try:
            if self.runtime.free():
                _log(f"Freed the graphics card after {self.idle_minutes:g} idle minutes.")
                return True
            return False
        finally:
            self.lock.release()


def make_handler(service: Service) -> type[BaseHTTPRequestHandler]:
    from .generator import MotionError

    class Handler(BaseHTTPRequestHandler):
        server_version = "martlet-motion"
        protocol_version = "HTTP/1.1"

        def log_message(self, format: str, *args: Any) -> None:  # noqa: A002 - the base class names it format
            return

        def _send(self, status: int, body: bytes, media: str, headers: dict[str, str] | None = None) -> None:
            self.send_response(status)
            self.send_header("Content-Type", media)
            self.send_header("Content-Length", str(len(body)))
            for name, value in (headers or {}).items():
                self.send_header(name, value)
            self.end_headers()
            self.wfile.write(body)

        def _json(self, status: int, value: dict[str, Any]) -> None:
            self._send(status, json.dumps(value).encode("utf-8"), "application/json")

        def _error(self, error: MotionError) -> None:
            self._json(error.status, {"error": {"code": error.code, "summary": error.summary}})

        def _body(self) -> Any:
            length = int(self.headers.get("Content-Length") or 0)
            if length > MAXIMUM_BODY_BYTES:
                raise MotionError("request.invalid", "The request is too large.", 400)
            try:
                return json.loads(self.rfile.read(length) or b"{}")
            except (ValueError, UnicodeDecodeError) as error:
                raise MotionError("request.invalid", "The request is not JSON.", 400) from error

        def do_GET(self) -> None:  # noqa: N802 - http.server's name
            if self.path != "/status":
                self._json(404, {"error": {"code": "request.invalid", "summary": "Unknown path."}})
                return
            self._json(200, service.status())

        def do_POST(self) -> None:  # noqa: N802 - http.server's name
            try:
                if self.path == "/generate":
                    vrma, headers = service.generate(self._body())
                    self._send(200, vrma, "model/gltf-binary", headers)
                elif self.path == "/free":
                    self._body()
                    self._json(200, service.free())
                else:
                    self._json(404, {"error": {"code": "request.invalid", "summary": "Unknown path."}})
            except MotionError as error:
                self._error(error)
            except Exception as error:  # an unexpected failure still answers, and the service keeps running
                _log(f"Motion request failed: {type(error).__name__}: {error}")
                self._error(MotionError("motion.failed", f"{type(error).__name__}: {error}"[:400]))

    return Handler


def _config_dir(model: str) -> Path:
    """The folder with the model's config.yml: the models volume's, or the pinned file downloaded to a temporary folder."""
    folder = CKPTS / pins.MODEL_DIRECTORIES[model]
    if (folder / "config.yml").is_file():
        return folder
    config = next(f for f in pins.MODEL_FILES[model] if f.path.endswith("config.yml"))
    folder = Path(tempfile.mkdtemp(prefix="martlet-motion-config-"))
    _download(config.url, folder / "config.yml", config.size, config.sha256)
    return folder


class RandomWeights:
    """`serve --random-weights` and `selftest`: HY-Motion's real pipeline code with its RANDOM initial weights and no text
    encoder, a few flow steps, on the processor (FIXTURE - NOT AI: the motion is noise). It checks the service, the image's
    code and the converter end to end without the model files."""

    fixture = "random weights - NOT AI"

    def __init__(self, runtime: Any, steps: int) -> None:
        self.runtime = runtime
        self.steps = steps
        self.model = runtime.model
        self.config_dir: Path | None = None

    @property
    def loaded(self) -> bool:
        return self.runtime.loaded

    @property
    def load_milliseconds(self) -> int:
        return self.runtime.load_milliseconds

    def missing(self) -> list[str]:
        return []

    def generate(self, prompt: str, seconds: float, seed: int) -> dict[str, Any]:
        if not self.runtime.loaded:
            self.config_dir = self.config_dir or _config_dir(self.model)
            self.runtime.load(weights=False, config_dir=self.config_dir)
        return self.runtime.generate(prompt, seconds, seed, steps=self.steps)

    def free(self) -> bool:
        return self.runtime.free()

    def gpu(self) -> dict[str, Any] | None:
        return None


def serve(listen: str = "127.0.0.1", random_steps: int = 0) -> None:
    from .generator import Runtime

    runtime: Any = Runtime(_model(), CODE, CKPTS, os.environ.get("MARTLET_MOTION_DEVICE", "cpu" if random_steps else "cuda"))
    if random_steps:
        runtime = RandomWeights(runtime, random_steps)
        _log(f"FIXTURE - NOT AI: motions use HY-Motion's code with RANDOM weights and {random_steps} flow steps.")
    elif missing := runtime.missing():
        _log(f"Motion role is not provisioned yet ({len(missing)} files missing); run martlet-motion provision.")
    service = Service(runtime, float(os.environ.get("MARTLET_MOTION_IDLE_MINUTES", "5")))

    def unload_when_idle() -> None:
        while True:
            time.sleep(30)
            service.idle_check()

    threading.Thread(target=unload_when_idle, name="idle-unload", daemon=True).start()
    server = ThreadingHTTPServer((listen, PORT), make_handler(service))
    _log(f"Motion service ({runtime.model}) listening on {listen}:{PORT}.")
    server.serve_forever()


def selftest(out: Path | None, seconds: float = 2.0, steps: int = 2) -> dict[str, Any]:
    """HY-Motion's real pipeline code with random weights and no text encoder on the processor, then the VRMA converter:
    checks the image's code, assets and converter without the model files. The motion is noise (FIXTURE - NOT AI)."""
    from .generator import Runtime, to_vrma

    model = _model()
    runtime = RandomWeights(Runtime(model, CODE, CKPTS, os.environ.get("MARTLET_MOTION_DEVICE", "cpu")), steps)
    started = time.monotonic()
    output = runtime.generate("A person waves with the right hand.", seconds, 7)
    vrma, frames = to_vrma(output)
    if out is not None:
        out.write_bytes(vrma)
    result = {
        "ok": vrma[:4] == b"glTF" and frames == round(seconds * pins.FPS), "fixture": "random weights - NOT AI", "model": model,
        "frames": frames, "fps": pins.FPS, "bytes": len(vrma), "sha256": hashlib.sha256(vrma).hexdigest(),
        "rot6d": list(output["rot6d"].shape), "transl": list(output["transl"].shape),
        "milliseconds": int((time.monotonic() - started) * 1000), "saved": str(out) if out else None,
    }
    print(json.dumps(result), flush=True)
    return result


def main() -> None:
    parser = argparse.ArgumentParser(prog="martlet-motion")
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("provision")
    serving = commands.add_parser("serve")
    serving.add_argument("--listen", default="127.0.0.1", help="address to listen on (the role uses 127.0.0.1)")
    serving.add_argument("--random-weights", type=int, default=0, metavar="STEPS",
                         help="checks only: HY-Motion's code with RANDOM weights and STEPS flow steps (FIXTURE - NOT AI)")
    test = commands.add_parser("selftest")
    test.add_argument("out", nargs="?", type=Path)
    test.add_argument("--seconds", type=float, default=2.0)
    test.add_argument("--steps", type=int, default=2)
    assets = commands.add_parser("code-assets")
    assets.add_argument("directory", type=Path)
    args = parser.parse_args()
    if args.command == "provision":
        provision()
    elif args.command == "serve":
        serve(args.listen, args.random_weights)
    elif args.command == "selftest":
        sys.exit(0 if selftest(args.out, args.seconds, args.steps)["ok"] else 1)
    elif args.command == "code-assets":
        code_assets(args.directory)


if __name__ == "__main__":
    main()
