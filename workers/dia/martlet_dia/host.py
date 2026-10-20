"""Martlet Dia host service: the loopback-only HTTP front of the Dia worker process (martlet_dia.worker).

It runs inside the ``dia`` host role container, next to the Martlet gateway, whose relay is its only client. It speaks
the same HTTP protocol and martlet.f5.worker-shaped NDJSON events as the f5 role, so the gateway relays every
reference-voice engine the same way. One synthesis runs at a time; a reply that arrives while the worker is busy waits
for it, and a dead worker is restarted for the next reply.

Commands:
  serve                 listen on 127.0.0.1:$MARTLET_DIA_PORT (default 50084); warms the worker when provisioned
  fetch-source <dir>    download the pinned Dia source files into <dir> and verify them (image build step)
  provision [--fixture] download and verify the pinned model files, then write the worker config
                        (--fixture: deterministic FIXTURE - NOT AI engine for plumbing checks without a GPU)
  warm                  ask the running service to load the model and wait until it is ready

HTTP (127.0.0.1 only):
  GET  /status      {"state", "ready", "worker", "error", "engine", "nonverbal_tags"}
  POST /warmup      (re)start and warm the worker; 200 when ready, 503 otherwise
  POST /synthesize  {"ids", "deadline_utc", "reference", "chunks"} -> application/x-ndjson events
  POST /cancel      {"request_id"} -> discards the active synthesis (the model cannot stop mid-generation)
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
from pathlib import Path
from typing import Any

from martlet_dia import pins

try:
    import martlet_worker_host as shared
except ModuleNotFoundError:  # a checkout: the shared module is in workers/common (the image copies it beside martlet_dia)
    sys.path.append(str(Path(__file__).resolve().parents[2] / "common"))
    import martlet_worker_host as shared

ROOT = Path(os.environ.get("MARTLET_DIA_ROOT", "/opt/martlet-dia"))
MODELS = ROOT / "models"
CONFIG = MODELS / "worker-config.json"
PORT = int(os.environ.get("MARTLET_DIA_PORT", "50084"))
MODEL = os.environ.get("DIA_MODEL", pins.DEFAULT_MODEL)
DEVICE = os.environ.get("MARTLET_DIA_DEVICE", "cuda:0")
WORKER_ID = "martlet-dia-host"
WARMUP_SECONDS = 600
REQUEST_SECONDS = 90
BUSY_WAIT_SECONDS = 30
TERMINAL = {"completed", "canceled", "failed"}
LOADING = {"starting", "cold", "loading_model"}


def _log(message: str) -> None:
    print(message, flush=True)


def _download(url: str, target: Path, size: int, sha256: str) -> None:
    shared.fetch_pinned(url, target, size, sha256, agent="martlet-dia-host")


# ------------------------------------------------------------------ image build and provisioning


def fetch_source(directory: Path) -> None:
    for name, (size, sha256) in pins.DIA_SOURCE_FILES.items():
        _download(pins.DIA_SOURCE_URL + name, directory / name, size, sha256)
    _log(f"Dia source {pins.DIA_SOURCE_COMMIT[:12]} ({pins.DIA_SOURCE_LICENSE}) verified.")


def provision(fixture: bool) -> None:
    files: dict[str, str] = {}
    artifacts: list[dict[str, Any]] = []
    if fixture:
        _log("FIXTURE - NOT AI: provisioning the deterministic fake engine (no model, no GPU).")
        engine = "fixture"
        for role, (artifact_id, *_rest) in pins.PINNED_MODELS[pins.DEFAULT_MODEL].items():
            artifacts.append({"artifact_id": "fixture-" + artifact_id, "bytes": 0, "license_id": "fixture-only",
                              "revision": "0" * 40, "role": role, "sha256": "0" * 64})
    else:
        pinned = pins.PINNED_MODELS.get(MODEL)
        if pinned is None:
            raise SystemExit(f"Unknown Dia model {MODEL!r}; choose one of: {', '.join(pins.PINNED_MODELS)}")
        engine = "dia"
        for role, (artifact_id, url, revision, size, sha256, license_id, relative) in pinned.items():
            _log(f"Model file {artifact_id} ({license_id}) from {url.split('/resolve/')[0].split('/releases/')[0]}")
            _download(url, MODELS / relative, size, sha256)
            files[role] = str(MODELS / relative)
            artifacts.append({"artifact_id": artifact_id, "bytes": size, "license_id": license_id,
                              "revision": revision, "role": role, "sha256": sha256})
    identity = {
        "artifacts": artifacts,
        "cancellation": "discard_only",
        "dia_source_commit": pins.DIA_SOURCE_COMMIT,
        "engine": engine,
        "evidence": "synthetic_fixture" if fixture else "live_worker",
        "output_format": {"bits_per_sample": 16, "channels": 1, "sample_rate": 24_000},
        "worker_id": WORKER_ID,
    }
    MODELS.mkdir(parents=True, exist_ok=True)
    temporary = CONFIG.with_name(CONFIG.name + ".partial")
    temporary.write_text(json.dumps({"device": DEVICE, "engine": engine, "files": files, "worker": identity},
                                    indent=2, sort_keys=True), encoding="utf-8")
    temporary.replace(CONFIG)
    _log(f"Worker configuration written ({'fixture' if fixture else MODEL}, {engine} engine on {DEVICE}).")


def warm() -> None:
    _log("Loading the Dia voice model (can take a few minutes)...")
    request = urllib.request.Request(f"http://127.0.0.1:{PORT}/warmup", data=b"{}", method="POST")
    try:
        with urllib.request.urlopen(request, timeout=WARMUP_SECONDS + 30) as response:
            status = json.loads(response.read())
    except urllib.error.HTTPError as error:
        status = json.loads(error.read() or b"{}")
    if not status.get("ready"):
        raise SystemExit(f"The Dia worker is not ready ({status.get('state')}): {status.get('error') or 'no detail'}")
    _log("The Dia voice model is loaded and ready.")


# ------------------------------------------------------------------ the worker process


class Worker(shared.ModelProcess):
    """Owns one worker process; only one synthesis runs at a time."""

    name = "dia"
    stopped_error = "The Dia worker process stopped; it restarts with the next reply."
    unavailable_states = frozenset({"not_provisioned", "failed"})

    def __init__(self) -> None:
        super().__init__("not_provisioned" if not CONFIG.is_file() else "stopped")

    def status(self) -> dict[str, Any]:
        with self.lock:
            return {"engine": "dia", "error": self.error, "nonverbal_tags": list(pins.NONVERBAL_TAGS),
                    "ready": self.state == "ready", "state": self.state, "worker": self.identity,
                    "worker_pid": self.process.pid if self.running() else None}

    def settled(self) -> bool:
        return self.state not in LOADING

    def start(self) -> None:
        with self.lock:
            if self.running():
                process = self.process
            else:
                if not CONFIG.is_file():
                    self.state, self.error = "not_provisioned", "Run the dia role's provisioning step first."
                    self.changed.notify_all()
                    return
                try:
                    config = json.loads(CONFIG.read_bytes())
                    process = subprocess.Popen(
                        [sys.executable, "-m", "martlet_dia.worker", "--config", str(CONFIG)],
                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=None,
                        cwd="/tmp" if os.path.isdir("/tmp") else None)
                except (OSError, ValueError, KeyError):
                    self.state, self.error = "failed", "The Dia worker could not start; run 'martlet-host add dia' again."
                    self.changed.notify_all()
                    return
                self.process = process
                self.identity = config["worker"]
                self.state, self.error = "starting", None
                self.read_in_background(process)
            if self.state not in {"ready", "busy"}:
                self.state = "starting"
        self.send({"type": "warmup"})

    def on_message(self, process: subprocess.Popen[bytes], message: dict[str, Any]) -> None:
        if message.get("type") == "state":
            with self.lock:
                if self.process is process:
                    state = str(message.get("state"))
                    # "cold" is the process announcing itself; the warmup already queued behind it decides.
                    if state != "cold":
                        self.state, self.error = state, message.get("error")
                    self.changed.notify_all()
        elif message.get("type") == "event":
            self.deliver(message)


WORKER = Worker()


def _deadline(value: Any) -> datetime:
    return datetime.fromisoformat(str(value).replace("Z", "+00:00")).astimezone(timezone.utc)


class Handler(shared.JsonHandler):
    server_version = "martlet-dia-host"

    def do_GET(self) -> None:  # noqa: N802
        if self.path == "/status":
            self._json(200, WORKER.status())
        else:
            self._json(404, {"error": "not_found"})

    def do_POST(self) -> None:  # noqa: N802
        if self.path == "/warmup":
            WORKER.start()
            status = WORKER.wait_settled(WARMUP_SECONDS)
            self._json(200 if status["ready"] else 503, status)
        elif self.path == "/cancel":
            body = self._body()
            if body is not None:
                WORKER.send({"type": "cancel", "request_id": str(body.get("request_id"))})
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
            request_id = str(body["ids"]["request_id"])
            remaining = (_deadline(body["deadline_utc"]) - datetime.now(timezone.utc)).total_seconds()
            if not isinstance(body["reference"], dict) or not isinstance(body["chunks"], list):
                raise TypeError
        except (KeyError, TypeError, ValueError):
            self._json(400, {"error": "request.invalid"})
            return
        # A stopped reply keeps the model busy until Dia returns (discard only), and a crashed worker stays down: wait
        # for it (restarting a dead one) instead of failing the next reply with "busy".
        with WORKER.lock:
            restart = WORKER.state in {"failed", "stopped"} or not WORKER.running()
        if restart:
            _log("Dia worker is not running; restarting it for a reply.")
            WORKER.start()
        WORKER.wait_admissible(min(BUSY_WAIT_SECONDS, max(0.0, remaining / 2)))
        with WORKER.lock:
            if WORKER.state != "ready" or WORKER.pending:
                busy = bool(WORKER.pending) or WORKER.state == "busy"
                self._json(503, {"error": "worker.busy" if busy else "worker.unavailable", "state": WORKER.state})
                return
            events: queue.Queue[dict[str, Any] | None] = queue.Queue()
            WORKER.pending[request_id] = events
            WORKER.state = "busy"
        deadline = time.monotonic() + REQUEST_SECONDS + 5
        try:
            if not WORKER.send({**body, "type": "synthesize"}):
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
                if event is None:
                    break
                self.wfile.write(json.dumps(event, separators=(",", ":"), sort_keys=True).encode("utf-8") + b"\n")
                self.wfile.flush()
                if event.get("kind") in TERMINAL:
                    break
        except (BrokenPipeError, ConnectionResetError):
            WORKER.send({"type": "cancel", "request_id": request_id})
        finally:
            with WORKER.lock:
                WORKER.pending.pop(request_id, None)
                WORKER.changed.notify_all()


def serve() -> None:
    server = http.server.ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    server.daemon_threads = True
    if CONFIG.is_file():
        threading.Thread(target=WORKER.start, name="dia-warmup", daemon=True).start()
    _log(f"Martlet Dia host service listening on 127.0.0.1:{PORT}")
    server.serve_forever()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="martlet-dia")
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("serve")
    source = commands.add_parser("fetch-source")
    source.add_argument("directory", type=Path)
    provisioning = commands.add_parser("provision")
    provisioning.add_argument("--fixture", action="store_true")
    commands.add_parser("warm")
    args = parser.parse_args(argv)
    if args.command == "serve":
        serve()
    elif args.command == "fetch-source":
        fetch_source(args.directory)
    elif args.command == "provision":
        provision(args.fixture)
    elif args.command == "warm":
        warm()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
