"""Martlet F5 host service: the loopback-only HTTP front of the bounded stdio worker (martlet_f5_worker).

It runs inside the ``f5`` host role container, next to the Martlet gateway. The gateway's relay
(Martlet.Gateway.F5) is its only client; the worker stays a separate stdio process with its own audit hook,
identity checks and bounds. This process adds no model logic: it spawns the worker, warms it up with the
provisioned identity and forwards one synthesis at a time as newline-delimited worker events.

Commands:
  serve                 listen on 127.0.0.1:$MARTLET_F5_PORT (default 50080); warms the worker when provisioned
  inventory <file>      write the canonical runtime inventory of the pinned packages (image build step)
  provision [--fixture] download and verify the pinned model files, then write the worker config
                        (--fixture: deterministic FIXTURE - NOT AI engine for plumbing checks without a GPU)
  warm                  ask the running service to load the model and wait until it is ready

HTTP (127.0.0.1 only):
  GET  /status      {"state", "ready", "worker", "error"}
  POST /warmup      (re)start and warm the worker; 200 when ready, 503 otherwise
  POST /synthesize  {"ids", "deadline_utc", "reference", "chunks"} -> application/x-ndjson worker events
  POST /cancel      {"request_id"} -> discards the active synthesis (the worker is discard_only)
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import http.server
import importlib.metadata
import json
import os
import platform
import queue
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any

from martlet_f5_worker.contract import (
    ARTIFACT_ROLES,
    CONTRACT_ID,
    PROTOCOL_VERSION,
    ContractError,
    WorkerIdentity,
    format_utc,
    parse_utc,
    parse_wave,
)
from martlet_f5_worker.identity import (
    FIXTURE_BUILD_ID,
    FIXTURE_SOURCE_REVISION,
    FIXTURE_VERSION,
    WORKER_BUILD_ID,
    RuntimePins,
    compute_worker_build_revision,
    config_wire,
)
from martlet_f5_worker.jsonio import canonical_json_bytes

ROOT = Path(os.environ.get("MARTLET_F5_ROOT", "/opt/martlet-f5"))
MODELS = ROOT / "models"
CONFIG = MODELS / "worker-config.json"
INVENTORY = "runtime/runtime-inventory.json"
PORT = int(os.environ.get("MARTLET_F5_PORT", "50080"))
MODEL = os.environ.get("F5_MODEL", "f5tts-v1-base")
DEVICE = os.environ.get("MARTLET_F5_DEVICE", "cuda:0")
DESTINATION = "martlet-host-f5"
WORKER_ID = "martlet-f5-host"
MAX_BODY_BYTES = 6 * 1024 * 1024
WARMUP_SECONDS = 290
REQUEST_SECONDS = 90
BUSY_WAIT_SECONDS = 30
TERMINAL = {"completed", "canceled", "failed"}

# Pinned model files per selectable model: role -> (artifact ID, URL, revision, bytes, SHA-256, license, path).
PINNED_MODELS: dict[str, dict[str, tuple[str, str, str, int, str, str, str]]] = {
    "f5tts-v1-base": {
        "model_weights": (
            "f5tts-v1-base",
            "https://huggingface.co/SWivid/F5-TTS/resolve/84e5a410d9cead4de2f847e7c9369a6440bdfaca/F5TTS_v1_Base/model_1250000.safetensors",
            "84e5a410d9cead4de2f847e7c9369a6440bdfaca",
            1_348_435_761,
            "670900fd14e6c458b95da6e9ed317cdb20dbaf7a1c02ac06a05475a9d32b6a38",
            "CC-BY-NC-4.0",
            "models/f5tts-v1-base/model_1250000.safetensors",
        ),
        "vocabulary": (
            "f5tts-v1-base-vocab",
            "https://huggingface.co/SWivid/F5-TTS/resolve/84e5a410d9cead4de2f847e7c9369a6440bdfaca/F5TTS_v1_Base/vocab.txt",
            "84e5a410d9cead4de2f847e7c9369a6440bdfaca",
            13_800,
            "2a05f992e00af9b0bd3800a8d23e78d520dbd705284ed2eedb5f4bd29398fa3c",
            "CC-BY-NC-4.0",
            "models/f5tts-v1-base/vocab.txt",
        ),
        "vocoder_weights": (
            "vocos-mel-24khz",
            "https://huggingface.co/charactr/vocos-mel-24khz/resolve/0feb3fdd929bcd6649e0e7c5a688cf7dd012ef21/pytorch_model.bin",
            "0feb3fdd929bcd6649e0e7c5a688cf7dd012ef21",
            54_365_991,
            "97ec976ad1fd67a33ab2682d29c0ac7df85234fae875aefcc5fb215681a91b2a",
            "MIT",
            "models/vocos-mel-24khz/pytorch_model.bin",
        ),
        "vocoder_configuration": (
            "vocos-mel-24khz-config",
            "https://huggingface.co/charactr/vocos-mel-24khz/resolve/0feb3fdd929bcd6649e0e7c5a688cf7dd012ef21/config.yaml",
            "0feb3fdd929bcd6649e0e7c5a688cf7dd012ef21",
            461,
            "da9033922f969a47f0c160010226919e59f27761fd5066f3828d46de6650b0fc",
            "MIT",
            "models/vocos-mel-24khz/config.yaml",
        ),
    }
}


def _log(text: str) -> None:
    print(text, flush=True)


def _sha256_file(path: Path) -> tuple[int, str]:
    digest = hashlib.sha256()
    size = 0
    with path.open("rb") as stream:
        while chunk := stream.read(1_048_576):
            size += len(chunk)
            digest.update(chunk)
    return size, digest.hexdigest()


# ------------------------------------------------------------------ image build: runtime inventory


def build_inventory() -> bytes:
    pins = RuntimePins.load()
    modules = {"f5-tts": "f5_tts", "torch": "torch", "torchaudio": "torchaudio", "vocos": "vocos"}
    packages = []
    for distribution in sorted(modules):
        module = modules[distribution]
        installed = importlib.metadata.distribution(distribution)
        if installed.version != pins.packages[distribution]:
            raise SystemExit(f"{distribution} {installed.version} differs from its pin {pins.packages[distribution]}")
        root = Path(installed.locate_file(module))
        files = []
        for path in root.rglob("*"):
            if path.is_symlink() or not path.is_file():
                continue
            size, sha256 = _sha256_file(path)
            files.append({"bytes": size, "path": path.relative_to(root).as_posix(), "sha256": sha256})
        files.sort(key=lambda item: item["path"])
        packages.append({"distribution": distribution, "files": files, "module": module, "version": installed.version})
    size, sha256 = _sha256_file(Path(sys.executable))
    return canonical_json_bytes(
        {
            "f5_source_revision": pins.f5_source_revision,
            "format_version": 1,
            "kind": "martlet_f5_runtime_inventory",
            "packages": packages,
            "python_executable": {"bytes": size, "sha256": sha256},
        }
    )


# ------------------------------------------------------------------ provisioning (post-start role step)


def _download(url: str, target: Path, size: int, sha256: str) -> None:
    if target.is_file():
        if _sha256_file(target) == (size, sha256):
            _log(f"{target.name}: already downloaded and verified.")
            return
        target.unlink()
    target.parent.mkdir(parents=True, exist_ok=True)
    partial = target.with_name(target.name + ".partial")
    digest = hashlib.sha256()
    received = 0
    shown = -1
    request = urllib.request.Request(url, headers={"User-Agent": "martlet-f5-host"})
    with urllib.request.urlopen(request, timeout=60) as response, partial.open("wb") as output:
        while chunk := response.read(1_048_576):
            received += len(chunk)
            if received > size:
                raise SystemExit(f"{target.name} is larger than its pinned size; stopped.")
            digest.update(chunk)
            output.write(chunk)
            percent = received * 100 // size
            if percent != shown:
                shown = percent
                sys.stdout.write(f"\r{target.name}: {percent}% of {size // 1_048_576 or 1} MiB")
                sys.stdout.flush()
    sys.stdout.write("\n")
    if received != size or digest.hexdigest() != sha256:
        partial.unlink(missing_ok=True)
        raise SystemExit(f"{target.name} does not match its pinned SHA-256; nothing was installed.")
    partial.replace(target)
    _log(f"{target.name}: verified (SHA-256 {sha256[:12]}...).")


def _identity(engine: str, artifacts: list[dict[str, Any]]) -> WorkerIdentity:
    fixture = engine == "fake"
    pins = None if fixture else RuntimePins.load()
    return WorkerIdentity.parse(
        {
            "artifacts": artifacts,
            "cancellation": "discard_only",
            "contract_id": CONTRACT_ID,
            "evidence": "synthetic_fixture" if fixture else "live_worker",
            "incremental_within_chunk_synthesis": False,
            "pcm_transport_streaming": True,
            "protocol_version": dict(PROTOCOL_VERSION),
            "runtime": {
                "cuda_runtime_version": FIXTURE_VERSION if fixture else pins.cuda_runtime_version,
                "f5_package_version": FIXTURE_VERSION if fixture else pins.packages["f5-tts"],
                "f5_source_revision": FIXTURE_SOURCE_REVISION if fixture else pins.f5_source_revision,
                "python_version": pins.python_version if pins else platform.python_version(),
                "torch_version": FIXTURE_VERSION if fixture else pins.packages["torch"],
                "torchaudio_version": FIXTURE_VERSION if fixture else pins.packages["torchaudio"],
                "worker_build_id": FIXTURE_BUILD_ID if fixture else WORKER_BUILD_ID,
                "worker_build_revision": compute_worker_build_revision(),
            },
            "worker_id": "martlet-f5-deterministic-fixture" if fixture else WORKER_ID,
        }
    )


def provision(fixture: bool) -> None:
    files: dict[str, str] = {}
    artifacts: list[dict[str, Any]] = []
    if fixture:
        _log("FIXTURE - NOT AI: provisioning the deterministic fake engine (no model, no GPU).")
        for index, role in enumerate(ARTIFACT_ROLES, 1):
            relative = f"models/fixture/{role}.fixture"
            content = f"{role}-fixture-{index}".encode("ascii")
            path = ROOT / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(content)
            files[role] = relative
            artifacts.append(
                {
                    "artifact_id": f"fixture-{role.replace('_', '-')}",
                    "bytes": len(content),
                    "license_id": "fixture-only",
                    "revision": str(index) * 40,
                    "role": role,
                    "sha256": hashlib.sha256(content).hexdigest(),
                }
            )
        engine, device = "fake", None
    else:
        pinned = PINNED_MODELS.get(MODEL)
        if pinned is None:
            raise SystemExit(f"Unknown F5 model {MODEL!r}; choose one of: {', '.join(PINNED_MODELS)}")
        inventory = ROOT / INVENTORY
        size, sha256 = _sha256_file(inventory)
        files["runtime_image"] = INVENTORY
        artifacts.append(
            {
                "artifact_id": "martlet-f5-runtime-inventory",
                "bytes": size,
                "license_id": "MIT",
                "revision": sha256[:40],
                "role": "runtime_image",
                "sha256": sha256,
            }
        )
        for role, (artifact_id, url, revision, size, sha256, license_id, relative) in pinned.items():
            _log(f"Model file {artifact_id} ({license_id}) from {url.split('/resolve/')[0]}")
            _download(url, ROOT / relative, size, sha256)
            files[role] = relative
            artifacts.append(
                {
                    "artifact_id": artifact_id,
                    "bytes": size,
                    "license_id": license_id,
                    "revision": revision,
                    "role": role,
                    "sha256": sha256,
                }
            )
        engine, device = "f5", DEVICE
    identity = _identity(engine, artifacts)
    MODELS.mkdir(parents=True, exist_ok=True)
    temporary = CONFIG.with_name(CONFIG.name + ".partial")
    temporary.write_bytes(
        config_wire(engine=engine, artifact_root=ROOT, artifact_files=files, worker=identity, device=device)
    )
    temporary.replace(CONFIG)
    _log(f"Worker configuration written ({'fixture' if fixture else MODEL}, {engine} engine).")


def warm() -> None:
    _log("Loading the voice model on the GPU (verifies every pinned file first; can take a few minutes)...")
    request = urllib.request.Request(f"http://127.0.0.1:{PORT}/warmup", data=b"{}", method="POST")
    try:
        with urllib.request.urlopen(request, timeout=WARMUP_SECONDS + 30) as response:
            status = json.loads(response.read())
    except urllib.error.HTTPError as error:
        status = json.loads(error.read() or b"{}")
    if not status.get("ready"):
        raise SystemExit(f"The F5 worker is not ready ({status.get('state')}): {status.get('error') or 'no detail'}")
    _log("The F5 voice model is loaded and ready.")


# ------------------------------------------------------------------ the worker process


class Worker:
    """Owns one worker process. Only one synthesis runs at a time (the worker admits exactly one job)."""

    def __init__(self) -> None:
        self.lock = threading.Lock()
        self.write_lock = threading.Lock()
        self.changed = threading.Condition(self.lock)
        self.process: subprocess.Popen[bytes] | None = None
        self.identity: dict[str, Any] | None = None
        self.state = "not_provisioned" if not CONFIG.is_file() else "cold"
        self.error: str | None = None
        self.pending: dict[str, queue.Queue[dict[str, Any] | None]] = {}
        self.started: set[str] = set()
        self.active: dict[str, Any] | None = None

    def status(self) -> dict[str, Any]:
        with self.lock:
            return {"error": self.error, "ready": self.state == "ready", "state": self.state, "worker": self.identity}

    def start(self) -> None:
        with self.lock:
            if self.state in {"ready", "busy", "starting", "verifying_runtime", "verifying_artifacts", "loading_model", "warming"}:
                return
            if self.state == "cold" and self.running():
                return
            if not CONFIG.is_file():
                self.state, self.error = "not_provisioned", "Run the f5 role's provisioning step first."
                return
            old = self.process
            self.process = None
            # Claimed under the lock so concurrent callers do not start a second worker.
            self.state, self.error = "starting", None
        if old is not None and old.poll() is None:
            old.stdin.close()
            try:
                old.wait(10)
            except subprocess.TimeoutExpired:
                old.kill()
        try:
            config = json.loads(CONFIG.read_bytes())
            _prepare_matplotlib()
            process = subprocess.Popen(
                [sys.executable, "-m", "martlet_f5_worker", "--config", str(CONFIG)],
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL,
                cwd="/tmp" if os.path.isdir("/tmp") else None,
                env={**os.environ, "PYTHONDONTWRITEBYTECODE": "1"},
            )
        except (OSError, ValueError, KeyError):
            with self.lock:
                self.state, self.error = "failed", "The F5 worker could not start; run 'martlet-host add f5' again."
                self.changed.notify_all()
            return
        with self.lock:
            self.process = process
            self.identity = config["worker"]
            self.state, self.error = "starting", None
            self.changed.notify_all()
        threading.Thread(target=self._read, args=(process,), name="f5-worker-reader", daemon=True).start()
        self.send(
            {
                "contract_id": CONTRACT_ID,
                "control_id": str(uuid.uuid4()),
                "deadline_utc": format_utc(datetime.now(timezone.utc) + timedelta(seconds=WARMUP_SECONDS)),
                "expected_worker": self.identity,
                "protocol_version": dict(PROTOCOL_VERSION),
                "type": "warmup",
            }
        )

    def wait_settled(self, seconds: float) -> dict[str, Any]:
        deadline = time.monotonic() + seconds
        with self.lock:
            while self.state not in {"ready", "failed", "stopped", "not_provisioned"}:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                self.changed.wait(remaining)
        return self.status()

    def running(self) -> bool:
        return self.process is not None and self.process.poll() is None

    def wait_admissible(self, seconds: float) -> None:
        """Wait (bounded) until the worker is ready with no reply in flight, or has settled into a non-loading state."""
        deadline = time.monotonic() + seconds
        with self.lock:
            while (self.state != "ready" or self.pending) and self.state not in {"not_provisioned", "failed", "stopped"}:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                self.changed.wait(remaining)

    def send(self, message: dict[str, Any]) -> bool:
        process = self.process
        if process is None or process.stdin is None:
            return False
        try:
            with self.write_lock:
                process.stdin.write(canonical_json_bytes(message) + b"\n")
                process.stdin.flush()
            return True
        except (BrokenPipeError, OSError, ContractError):
            return False

    def _read(self, process: subprocess.Popen[bytes]) -> None:
        assert process.stdout is not None
        for line in process.stdout:
            try:
                message = json.loads(line)
            except ValueError:
                continue
            kind = message.get("type")
            summary = ((message.get("last_error") or message.get("error")) or {}).get("summary")
            if kind == "worker_state":
                with self.lock:
                    if self.process is process:
                        self.state = "ready" if message.get("ready") else str(message.get("state"))
                        self.error = summary
                        self.changed.notify_all()
            elif kind == "event":
                request_id = str((message.get("ids") or {}).get("request_id"))
                with self.lock:
                    target = self.pending.get(request_id)
                    self.started.add(request_id)
                if target is not None:
                    target.put(message)
            elif kind == "protocol_error":
                # A synthesize message rejected before its IDs were read: fail the request still waiting for them.
                with self.lock:
                    waiting = [q for request_id, q in self.pending.items() if request_id not in self.started]
                    if self.state not in {"ready", "busy"}:
                        self.error = summary
                for target in waiting:
                    target.put({"type": "rejected", "summary": summary})
        with self.lock:
            if self.process is process:
                self.state = "failed"
                self.error = self.error or "The F5 worker process stopped; run 'martlet-host add f5' again or restart the role."
                self.changed.notify_all()
            waiting = list(self.pending.values())
        for target in waiting:
            target.put(None)


WORKER = Worker()


def _prepare_matplotlib() -> None:
    # f5_tts imports matplotlib, whose font manager runs fc-list without a font cache; the worker denies child processes,
    # so its writable MPLCONFIGDIR starts with the cache built into the image.
    target = Path(os.environ.get("MPLCONFIGDIR", "/tmp/matplotlib"))
    source = ROOT / "matplotlib"
    if not source.is_dir():
        return
    target.mkdir(parents=True, exist_ok=True)
    for cache in source.glob("fontlist-*.json"):
        (target / cache.name).write_bytes(cache.read_bytes())


def _worker_message(request: dict[str, Any], identity: dict[str, Any], action_id: str) -> dict[str, Any]:
    reference = request["reference"]
    audio = base64.b64decode(reference["audio_base64"], validate=True)
    source_format = parse_wave(audio).wire()
    deadline = min(
        parse_utc(request["deadline_utc"], "deadline_utc"),
        datetime.now(timezone.utc) + timedelta(seconds=REQUEST_SECONDS - 1),
    )
    return {
        "action_id": action_id,
        "chunks": [
            {"chunk_id": chunk["chunk_id"], "index": chunk["index"], "text": chunk["text"]}
            for chunk in request["chunks"]
        ],
        "contract_id": CONTRACT_ID,
        "deadline_utc": format_utc(deadline),
        "destination_id": DESTINATION,
        "expected_worker": identity,
        "ids": {
            "parent_request_id": request["ids"].get("parent_request_id"),
            "request_id": request["ids"]["request_id"],
            "session_id": request["ids"]["session_id"],
            "turn_id": request["ids"]["turn_id"],
        },
        "output_format": {
            "bits_per_sample": 16,
            "channels": 1,
            "encoding": "signed16_little_endian",
            "sample_rate": 24_000,
        },
        "policy": {
            "artifact_download_allowed": False,
            "automatic_transcription_allowed": False,
            "default_voice_allowed": False,
            "model_fallback_allowed": False,
            "provider_fallback_allowed": False,
            "reference_transcript_supplied": True,
        },
        "protocol_version": dict(PROTOCOL_VERSION),
        "reference": {
            "audio_base64": reference["audio_base64"],
            "audio_sha256": reference["audio_sha256"],
            "preset_id": reference["preset_id"],
            "reference_revision": reference["reference_revision"],
            "source_format": source_format,
            "transcript": reference["transcript"],
            "transcript_revision": reference["transcript_revision"],
        },
        "type": "synthesize",
    }


class Handler(http.server.BaseHTTPRequestHandler):
    server_version = "martlet-f5-host"
    sys_version = ""

    def log_message(self, format: str, *args: Any) -> None:  # noqa: A002 - no request content in logs
        return

    def _json(self, status: int, value: dict[str, Any]) -> None:
        body = json.dumps(value, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _body(self) -> dict[str, Any] | None:
        length = int(self.headers.get("Content-Length") or "0")
        if not 0 < length <= MAX_BODY_BYTES:
            self._json(413, {"error": "request.too_large"})
            return None
        try:
            value = json.loads(self.rfile.read(length))
        except ValueError:
            value = None
        if not isinstance(value, dict):
            self._json(400, {"error": "request.invalid"})
            return None
        return value

    def do_GET(self) -> None:  # noqa: N802
        if self.path == "/status":
            self._json(200, WORKER.status())
        else:
            self._json(404, {"error": "not_found"})

    def do_POST(self) -> None:  # noqa: N802
        if self.path == "/warmup":
            WORKER.start()
            status = WORKER.wait_settled(WARMUP_SECONDS + 10)
            self._json(200 if status["ready"] else 503, status)
        elif self.path == "/cancel":
            body = self._body()
            if body is None:
                return
            with WORKER.lock:
                active = (WORKER.active or {}).get(str(body.get("request_id")))
            if active is not None:
                WORKER.send(active)
            self._json(200, {"local_discard_acknowledged": True})
        elif self.path == "/synthesize":
            self._synthesize()
        else:
            self._json(404, {"error": "not_found"})

    def _synthesize(self) -> None:
        body = self._body()
        if body is None:
            return
        # A stopped or interrupted reply keeps the GPU busy until F5 returns (discard_only), and a crashed worker
        # stays down: wait for it (restarting a dead worker) instead of failing the next reply with "busy".
        try:
            remaining = (parse_utc(body["deadline_utc"], "deadline_utc") - datetime.now(timezone.utc)).total_seconds()
        except (ContractError, KeyError, TypeError, ValueError):
            remaining = 0.0
        with WORKER.lock:
            restart = WORKER.state in {"failed", "stopped"} or (WORKER.state == "cold" and not WORKER.running())
        if restart:
            _log("F5 worker is not running; restarting it for a reply.")
            WORKER.start()
        WORKER.wait_admissible(min(BUSY_WAIT_SECONDS, max(0.0, remaining / 2)))
        with WORKER.lock:
            identity = WORKER.identity
            if WORKER.state != "ready" or identity is None or WORKER.pending:
                busy = bool(WORKER.pending) or WORKER.state == "busy"
                self._json(503, {"error": "worker.busy" if busy else "worker.unavailable", "state": WORKER.state})
                return
            action_id = str(uuid.uuid4())
            try:
                message = _worker_message(body, identity, action_id)
            except (ContractError, KeyError, TypeError, ValueError):
                self._json(400, {"error": "request.invalid"})
                return
            request_id = message["ids"]["request_id"]
            events: queue.Queue[dict[str, Any] | None] = queue.Queue()
            WORKER.pending[request_id] = events
            WORKER.active = {
                request_id: {
                    "action_id": action_id,
                    "contract_id": CONTRACT_ID,
                    "destination_id": DESTINATION,
                    "ids": message["ids"],
                    "protocol_version": dict(PROTOCOL_VERSION),
                    "reference_revision": message["reference"]["reference_revision"],
                    "type": "cancel",
                }
            }
        deadline = time.monotonic() + REQUEST_SECONDS + 5
        try:
            if not WORKER.send(message):
                self._json(503, {"error": "worker.unavailable", "state": "failed"})
                return
            self.send_response(200)
            self.send_header("Content-Type", "application/x-ndjson")
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            while True:
                try:
                    event = events.get(timeout=max(0.1, deadline - time.monotonic()))
                except queue.Empty:
                    break
                if event is None or event.get("type") == "rejected":
                    break
                self.wfile.write(canonical_json_bytes(event) + b"\n")
                self.wfile.flush()
                if event.get("kind") in TERMINAL:
                    break
        except (BrokenPipeError, ConnectionResetError):
            with WORKER.lock:
                cancel = (WORKER.active or {}).get(request_id)
            if cancel is not None:
                WORKER.send(cancel)
        finally:
            with WORKER.lock:
                WORKER.pending.pop(request_id, None)
                WORKER.started.discard(request_id)
                WORKER.active = None
                WORKER.changed.notify_all()


def serve() -> None:
    server = http.server.ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    server.daemon_threads = True
    if CONFIG.is_file():
        threading.Thread(target=WORKER.start, name="f5-warmup", daemon=True).start()
    _log(f"Martlet F5 host service listening on 127.0.0.1:{PORT}")
    server.serve_forever()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="martlet-f5")
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("serve")
    inventory = commands.add_parser("inventory")
    inventory.add_argument("output", type=Path)
    provisioning = commands.add_parser("provision")
    provisioning.add_argument("--fixture", action="store_true")
    commands.add_parser("warm")
    args = parser.parse_args(argv)
    if args.command == "serve":
        serve()
    elif args.command == "inventory":
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_bytes(build_inventory())
    elif args.command == "provision":
        provision(args.fixture)
    elif args.command == "warm":
        warm()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
