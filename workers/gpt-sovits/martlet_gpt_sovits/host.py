"""Martlet GPT-SoVITS host service: the loopback-only HTTP front of the GPT-SoVITS worker (worker.py).

It runs inside the ``gpt-sovits`` host role container, next to the Martlet gateway, whose reference-voice relay is its
only client. It adds no model logic: it spawns the worker, warms it up and forwards one synthesis at a time as
newline-delimited martlet.f5.worker 1.0 events. There is no endpoint that restarts or stops the process, switches
weights or names a file.

Commands:
  serve                 listen on 127.0.0.1:$MARTLET_GPT_SOVITS_PORT (default 50082); warms the worker when provisioned
  provision [--fixture] download and verify the pinned model files, then write the worker config
                        (--fixture: deterministic FIXTURE - NOT AI engine for plumbing checks without a GPU)
  warm                  ask the running service to load the model and wait until it is ready

HTTP (127.0.0.1 only):
  GET  /status      {"state", "ready", "worker", "error"}
  POST /warmup      start and warm the worker if it is not running; 200 when ready, 503 otherwise
  POST /synthesize  {"ids", "deadline_utc", "reference", "chunks"} -> application/x-ndjson worker events
  POST /cancel      {"request_id"} -> discards the active synthesis
"""

from __future__ import annotations

import argparse
import http.server
import json
import os
import queue
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from typing import Any

from . import pins, wire
from .worker import CONFIG, MODELS, shared

PORT = int(os.environ.get("MARTLET_GPT_SOVITS_PORT", "50082"))
MODEL = os.environ.get("GPT_SOVITS_MODEL", pins.MODEL_ID)
DEVICE = os.environ.get("MARTLET_GPT_SOVITS_DEVICE", "cuda:0")
DEBUG = os.environ.get("MARTLET_GPT_SOVITS_DEBUG") == "1"
WARMUP_SECONDS = 290
REQUEST_SECONDS = 90
BUSY_WAIT_SECONDS = 30
TERMINAL = {"completed", "canceled", "failed"}
SETTLED = {"ready", "failed", "stopped", "not_provisioned"}


def _log(text: str) -> None:
    print(text, flush=True)


# ------------------------------------------------------------------ provisioning (post-start role step)


def provision(fixture: bool) -> None:
    if fixture:
        _log("FIXTURE - NOT AI: provisioning the deterministic fake engine (no model, no GPU).")
        engine = "fixture"
    else:
        if MODEL != pins.MODEL_ID:
            raise SystemExit(f"Unknown GPT-SoVITS model {MODEL!r}; this image runs {pins.MODEL_ID} only.")
        _log(f"GPT-SoVITS {pins.SOURCE_TAG} ({pins.SOURCE_LICENSE}) with the {pins.MODEL_VERSION} weights pair from "
             f"huggingface.co/{pins.WEIGHTS_REPOSITORY} at {pins.WEIGHTS_REVISION[:12]}.")
        for item in pins.ARTIFACTS:
            _log(f"Model file {item.artifact_id} ({item.license_id})")
            shared.fetch_pinned(item.source_url, MODELS / item.path, item.bytes, item.sha256, agent="martlet-gpt-sovits-host")
        engine = "gpt-sovits"
    MODELS.mkdir(parents=True, exist_ok=True)
    staged = CONFIG.with_name(CONFIG.name + ".partial")
    staged.write_bytes(wire.canonical({"device": DEVICE, "engine": engine, "model": pins.MODEL_ID}))
    staged.replace(CONFIG)
    _log(f"Worker configuration written ({engine} engine, {pins.MODEL_ID}).")


def warm() -> None:
    _log("Loading GPT-SoVITS on the GPU (verifies every pinned file first; can take a few minutes)...")
    request = urllib.request.Request(f"http://127.0.0.1:{PORT}/warmup", data=b"{}", method="POST")
    try:
        with urllib.request.urlopen(request, timeout=WARMUP_SECONDS + 30) as response:
            status = json.loads(response.read())
    except urllib.error.HTTPError as error:
        status = json.loads(error.read() or b"{}")
    if not status.get("ready"):
        raise SystemExit(f"The GPT-SoVITS worker is not ready ({status.get('state')}): {status.get('error') or 'no detail'}")
    _log("GPT-SoVITS is loaded and ready.")


# ------------------------------------------------------------------ the worker process


class Worker(shared.ModelProcess):
    """Owns one worker process. Only one synthesis runs at a time; loading happens once per process."""

    name = "gpt-sovits"
    settled_states = SETTLED
    stopped_error = "The GPT-SoVITS worker process stopped; it restarts with the next reply."

    def __init__(self) -> None:
        super().__init__("cold" if CONFIG.is_file() else "not_provisioned")

    def encode(self, message: dict[str, Any]) -> bytes:
        return wire.canonical(message)

    def start(self) -> None:
        with self.lock:
            if self.state == "starting" or (self.running() and self.state not in {"failed", "stopped"}):
                return
            if not CONFIG.is_file():
                self.state, self.error = "not_provisioned", "Run the gpt-sovits role's provisioning step first."
                return
            old, self.process = self.process, None
            # Claimed under the lock so concurrent callers do not start a second worker.
            self.state, self.error, self.identity = "starting", None, None
        if old is not None and old.poll() is None:
            old.kill()
            old.wait(10)
        try:
            process = subprocess.Popen(
                [sys.executable, "-m", "martlet_gpt_sovits.worker"],
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=None if DEBUG else subprocess.DEVNULL,
                env={**os.environ, "PYTHONDONTWRITEBYTECODE": "1"},
            )
        except OSError:
            with self.lock:
                self.state, self.error = "failed", "The GPT-SoVITS worker could not start; run 'martlet-host add gpt-sovits' again."
                self.changed.notify_all()
            return
        with self.lock:
            self.process = process
            self.changed.notify_all()
        self.read_in_background(process)
        self.send({"type": "warmup"})

    def on_worker_state(self, message: dict[str, Any]) -> None:
        super().on_worker_state(message)
        self.identity = message.get("worker") or self.identity


WORKER = Worker()


class Handler(shared.JsonHandler):
    server_version = "martlet-gpt-sovits-host"

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
            if body is not None:
                WORKER.send({"request_id": str(body.get("request_id")), "type": "cancel"})
                self._json(200, {"local_discard_acknowledged": True})
        elif self.path == "/synthesize":
            self._synthesize()
        else:
            self._json(404, {"error": "not_found"})

    def _synthesize(self) -> None:
        body = self._body()
        if body is None:
            return
        try:
            message = wire.check_request(body)
        except wire.RequestError as error:
            self._json(400, {"error": "request.invalid", "detail": str(error)})
            return
        remaining = (wire.parse_utc(message["deadline_utc"]) - datetime.now(timezone.utc)).total_seconds()
        # A stopped reply keeps the GPU busy until its sentence finishes, and a crashed worker stays down: wait for it
        # (restarting a dead worker) instead of failing the next reply with "busy".
        with WORKER.lock:
            restart = WORKER.state in {"failed", "stopped"} or (WORKER.state == "cold" and not WORKER.running())
        if restart:
            _log("GPT-SoVITS worker is not running; restarting it for a reply.")
            WORKER.start()
        WORKER.wait_admissible(min(BUSY_WAIT_SECONDS, max(0.0, remaining / 2)))
        request_id = message["ids"]["request_id"]
        events: queue.Queue[dict[str, Any] | None] = queue.Queue()
        with WORKER.lock:
            if WORKER.state != "ready" or WORKER.pending:
                busy = bool(WORKER.pending) or WORKER.state == "busy"
                self._json(503, {"error": "worker.busy" if busy else "worker.unavailable", "state": WORKER.state})
                return
            WORKER.pending[request_id] = events
        deadline = time.monotonic() + min(REQUEST_SECONDS, max(1.0, remaining)) + 5
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
                    WORKER.send({"request_id": request_id, "type": "cancel"})
                    break
                if event is None:
                    break
                self.wfile.write(wire.canonical(event) + b"\n")
                self.wfile.flush()
                if event.get("kind") in TERMINAL:
                    break
        except (BrokenPipeError, ConnectionResetError):
            WORKER.send({"request_id": request_id, "type": "cancel"})
        finally:
            with WORKER.lock:
                WORKER.pending.pop(request_id, None)
                WORKER.changed.notify_all()


def serve() -> None:
    server = http.server.ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    server.daemon_threads = True
    if CONFIG.is_file():
        threading.Thread(target=WORKER.start, name="gpt-sovits-warmup", daemon=True).start()
    _log(f"Martlet GPT-SoVITS host service listening on 127.0.0.1:{PORT}")
    server.serve_forever()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="martlet-gpt-sovits")
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("serve")
    provisioning = commands.add_parser("provision")
    provisioning.add_argument("--fixture", action="store_true")
    commands.add_parser("warm")
    args = parser.parse_args(argv)
    if args.command == "serve":
        serve()
    elif args.command == "provision":
        provision(args.fixture)
    elif args.command == "warm":
        warm()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
