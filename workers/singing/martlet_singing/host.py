"""Martlet singing host service: the loopback-only HTTP front of the singing worker process (martlet_singing.worker).

It runs inside the ``singing`` host role container, next to the Martlet gateway, whose relay
(Martlet.Gateway.Singing.SongRelayWorker) is its only client. A song takes from tens of seconds to minutes, so songs are
jobs: start one, poll its stage, fetch its tracks in pages, or cancel it. One song is made at a time; up to four more
wait in order. The worker process starts with the first song, is restarted if it dies, and exits after an idle period,
which frees the graphics card and system memory so the speaking and listening roles can use them.

Commands:
  serve                 listen on 127.0.0.1:$MARTLET_SINGING_PORT (default 50085)
  provision [--fixture] download and verify the pinned model files ($SINGING_VOICE_MATCHES: soulx or soulx-vevosing), then
                        write the worker config (--fixture: deterministic FIXTURE - NOT AI engine for plumbing checks)

HTTP (127.0.0.1 only; every answer is JSON except track pages):
  GET  /status                         {"state", "ready", "engine", "worker", "qualities", "voice_matches", "queue", ...}
  POST /jobs                           a song request -> {"job_id", "state", "queue_position"} or {"error"}
  GET  /jobs/<id>                      {"job_id", "state", "stage", "fraction", "queue_position", "error"?, "result"?}
  GET  /jobs/<id>/tracks/<track>?offset=<frame>&frames=<n>
                                       raw signed 16-bit PCM (X-Song-Sample-Rate, X-Song-Channels, X-Song-Total-Frames)
  POST /jobs/<id>/cancel               {"job_id", "state"}
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import http.server
import json
import os
import secrets
import shutil
import subprocess
import sys
import threading
import time
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any

from martlet_singing import audio, pins

ROOT = Path(os.environ.get("MARTLET_SINGING_ROOT", "/opt/martlet-singing"))
MODELS = ROOT / "models"
CONFIG = MODELS / "worker-config.json"
JOBS = Path(os.environ.get("MARTLET_SINGING_JOBS", str(ROOT / "jobs")))
PORT = int(os.environ.get("MARTLET_SINGING_PORT", "50085"))
DEVICE = os.environ.get("MARTLET_SINGING_DEVICE", "cuda:0")
IDLE_SECONDS = float(os.environ.get("MARTLET_SINGING_IDLE_SECONDS", "300"))
JOB_SECONDS = float(os.environ.get("MARTLET_SINGING_JOB_SECONDS", "1800"))
KEEP_SECONDS = float(os.environ.get("MARTLET_SINGING_KEEP_SECONDS", "1800"))
WORKER_ID = "martlet-singing-host"
MAX_BODY_BYTES = 6 * 1024 * 1024
MAX_WAITING = 4
MAX_KEPT = 6
PAGE_BYTES = 4_800_000
STAGES = ("queued", "loading", "writing_music", "separating", "matching_voice", "mixing", "aligning", "completed")
TERMINAL = {"completed", "failed", "canceled"}


def _log(message: str) -> None:
    print(message, flush=True)


def _sha256_file(path: Path) -> tuple[int, str]:
    digest = hashlib.sha256()
    size = 0
    with path.open("rb") as stream:
        while chunk := stream.read(1_048_576):
            size += len(chunk)
            digest.update(chunk)
    return size, digest.hexdigest()


def _download(url: str, target: Path, size: int, sha256: str) -> None:
    if target.is_file():
        if target.stat().st_size == size and _sha256_file(target) == (size, sha256):
            _log(f"{target.name}: already downloaded and verified.")
            return
        target.unlink()
    target.parent.mkdir(parents=True, exist_ok=True)
    partial = target.with_name(target.name + ".partial")
    digest = hashlib.sha256()
    received = 0
    shown = -1
    request = urllib.request.Request(url, headers={"User-Agent": "martlet-singing-host"})
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


# ------------------------------------------------------------------ provisioning


def voice_matches_choice() -> list[str]:
    value = os.environ.get("SINGING_VOICE_MATCHES", "soulx").replace(" ", "")
    matches = [m for m in value.split("-") if m]
    if not matches or matches[0] != "soulx" or any(m not in pins.CONVERTER_IDS for m in matches):
        raise SystemExit("SINGING_VOICE_MATCHES must be soulx or soulx-vevosing.")
    return matches


def provision(fixture: bool) -> None:
    artifacts: list[dict[str, Any]] = []
    matches = voice_matches_choice()
    if fixture:
        _log("FIXTURE - NOT AI: provisioning the deterministic tone engine (no model, no GPU).")
        engine = "fixture"
        artifacts.append({"artifact_id": "fixture-tone-song", "bytes": 0, "license_id": "fixture-only",
                          "revision": "0" * 40, "sha256": "0" * 64})
    else:
        engine = "song"
        groups = {"core"} | ({"vevosing"} if "vevosing" in matches else set())
        for artifact_id, url, revision, size, sha256, license_id, local in pins.files(groups):
            _log(f"Model file {artifact_id} ({license_id})")
            _download(url, MODELS / local, size, sha256)
            artifacts.append({"artifact_id": artifact_id, "bytes": size, "license_id": license_id, "revision": revision,
                              "sha256": sha256})
        # transformers finds the pinned Whisper base (SoulX's content encoder) offline through its cache layout.
        whisper_base = next(p for p in pins.PINNED_FILES if p.repository == "openai/whisper-base")
        refs = MODELS / "hf-cache/hub/models--openai--whisper-base/refs"
        refs.mkdir(parents=True, exist_ok=True)
        (refs / "main").write_text(whisper_base.revision, encoding="utf-8")
    identity = {
        "artifacts": artifacts, "engine": engine, "evidence": "synthetic_fixture" if fixture else "live_worker",
        "sources": {"ace_step": pins.ACE_STEP_COMMIT, "soulx_singer": pins.SOULX_COMMIT, "amphion": pins.AMPHION_COMMIT},
        "output_format": {"bits_per_sample": 16, "sample_rate": audio.OUTPUT_RATE,
                          "channels": {"mix": 2, "vocals": 1, "backing": 2}},
        "worker_id": WORKER_ID,
    }
    MODELS.mkdir(parents=True, exist_ok=True)
    temporary = CONFIG.with_name(CONFIG.name + ".partial")
    temporary.write_text(json.dumps({"device": DEVICE, "engine": engine, "models": str(MODELS), "voice_matches": matches,
                                     "worker": identity}, indent=2, sort_keys=True), encoding="utf-8")
    temporary.replace(CONFIG)
    _log(f"Worker configuration written ({engine} engine on {DEVICE}; voice matches: {', '.join(matches)}).")


# ------------------------------------------------------------------ jobs and the worker process


class Job:
    def __init__(self, job_id: str, request: dict[str, Any]) -> None:
        self.job_id = job_id
        self.request = request
        self.state = "queued"
        self.stage = "queued"
        self.fraction = 0.0
        self.error: dict[str, str] | None = None
        self.meta: dict[str, Any] | None = None
        self.created = time.monotonic()
        self.finished: float | None = None
        self.started: float | None = None

    @property
    def directory(self) -> Path:
        return JOBS / self.job_id


class Service:
    def __init__(self) -> None:
        self.lock = threading.RLock()
        self.changed = threading.Condition(self.lock)
        self.jobs: dict[str, Job] = {}
        self.waiting: list[str] = []
        self.running: str | None = None
        self.process: subprocess.Popen[bytes] | None = None
        self.write_lock = threading.Lock()
        self.last_active = time.monotonic()
        self.worker_state = "stopped"
        self.error: str | None = None
        self.restarts = 0

    # ---- status

    def config(self) -> dict[str, Any] | None:
        try:
            return json.loads(CONFIG.read_bytes())
        except (OSError, ValueError):
            return None

    def status(self) -> dict[str, Any]:
        config = self.config()
        with self.lock:
            busy = self.running is not None
            state = ("not_provisioned" if config is None else "busy" if busy else
                     "loading" if self.worker_state == "starting" else "ready")
            return {
                "state": state, "ready": config is not None, "engine": config["engine"] if config else None,
                "worker": config["worker"] if config else None, "error": self.error,
                "qualities": ["fast", "high_quality"], "voice_matches": config.get("voice_matches", []) if config else [],
                "queue": len(self.waiting), "running": self.running, "worker_running": self._running(),
                "worker_pid": self.process.pid if self._running() else None, "restarts": self.restarts,
                "idle_release_seconds": IDLE_SECONDS, "gpu": _gpu(),
            }

    def _running(self) -> bool:
        return self.process is not None and self.process.poll() is None

    # ---- jobs

    def submit(self, body: dict[str, Any]) -> tuple[int, dict[str, Any]]:
        config = self.config()
        if config is None:
            return 200, {"error": {"code": "singing.unavailable", "summary": "The singing role is not set up yet."}}
        try:
            request = _validate(body)
        except ValueError as error:
            return 200, {"error": {"code": "request.invalid", "summary": str(error)}}
        if request["voice_match"] not in config.get("voice_matches", ["soulx"]):
            return 200, {"error": {"code": "singing.voice_match_unavailable",
                                   "summary": "VevoSing is not set up on this computer."}}
        reference = request.pop("reference_audio")
        with self.lock:
            if len(self.waiting) >= MAX_WAITING:
                return 200, {"error": {"code": "singing.busy", "summary": "Too many songs are waiting; try again later."}}
            job = Job("song-" + secrets.token_hex(8), request)
            job.directory.mkdir(parents=True, exist_ok=True)
            (job.directory / "reference.wav").write_bytes(reference)
            self.jobs[job.job_id] = job
            self.waiting.append(job.job_id)
            self.last_active = time.monotonic()
            self.changed.notify_all()
            return 200, self._job_view(job)

    def view(self, job_id: str) -> dict[str, Any]:
        with self.lock:
            job = self.jobs.get(job_id)
            return self._job_view(job) if job else {"job_id": job_id, "state": "unknown",
                                                    "error": {"code": "job.unknown", "summary": "No such song (it may have expired)."}}

    def _job_view(self, job: Job) -> dict[str, Any]:
        view: dict[str, Any] = {"job_id": job.job_id, "state": job.state, "stage": job.stage,
                                "fraction": round(job.fraction, 3)}
        if job.job_id in self.waiting:
            view["queue_position"] = self.waiting.index(job.job_id) + (1 if self.running else 0)
        if job.error:
            view["error"] = job.error
        if job.state == "completed" and job.meta:
            view["result"] = job.meta
        return view

    def cancel(self, job_id: str) -> dict[str, Any]:
        with self.lock:
            job = self.jobs.get(job_id)
            if job is None:
                return {"job_id": job_id, "state": "unknown"}
            if job_id in self.waiting:
                self.waiting.remove(job_id)
                self._finish(job, "canceled")
            elif self.running == job_id:
                self._send({"type": "cancel", "job_id": job_id})
            return self._job_view(job)

    def track(self, job_id: str, track: str, offset: int, frames: int) -> tuple[bytes, int, int] | dict[str, Any]:
        with self.lock:
            job = self.jobs.get(job_id)
            if job is None or job.state != "completed" or not job.meta:
                return self.view(job_id) if job is None else self._job_view(job)
        channels = audio.TRACKS[track]
        total = int(job.meta["frames"])
        frames = max(0, min(frames, PAGE_BYTES // (2 * channels), total - offset))
        with (job.directory / f"{track}.pcm").open("rb") as stream:
            stream.seek(offset * 2 * channels)
            return stream.read(frames * 2 * channels), channels, total

    def _finish(self, job: Job, state: str, error: dict[str, str] | None = None) -> None:
        job.state = state
        job.stage = "completed" if state == "completed" else job.stage
        job.fraction = 1.0 if state == "completed" else job.fraction
        job.error = error
        job.finished = time.monotonic()
        if state != "completed":
            shutil.rmtree(job.directory, ignore_errors=True)
        self.changed.notify_all()

    # ---- the worker process

    def _send(self, message: dict[str, Any]) -> bool:
        process = self.process
        if process is None or process.stdin is None:
            return False
        try:
            with self.write_lock:
                process.stdin.write(json.dumps(message, separators=(",", ":")).encode("utf-8") + b"\n")
                process.stdin.flush()
            return True
        except (BrokenPipeError, OSError):
            return False

    def _start_worker(self) -> None:
        if self._running():
            return
        process = subprocess.Popen([sys.executable, "-m", "martlet_singing.worker", "--config", str(CONFIG)],
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=None,
                                   cwd="/tmp" if os.path.isdir("/tmp") else None)
        self.process = process
        self.worker_state = "starting"
        threading.Thread(target=self._read, args=(process,), name="singing-worker-reader", daemon=True).start()

    def _read(self, process: subprocess.Popen[bytes]) -> None:
        assert process.stdout is not None
        for line in process.stdout:
            try:
                message = json.loads(line)
            except ValueError:
                continue
            with self.lock:
                kind = message.get("type")
                if kind == "state":
                    self.worker_state = str(message.get("state"))
                    self.changed.notify_all()
                    continue
                job = self.jobs.get(str(message.get("job_id")))
                if job is None or job.job_id != self.running:
                    continue
                if kind == "progress":
                    job.stage = str(message.get("stage"))
                    job.fraction = float(message.get("fraction") or 0.0)
                elif kind == "completed":
                    job.meta = message.get("meta") or {}
                    job.meta.setdefault("timings", []).append(
                        {"stage": "total", "seconds": round(time.monotonic() - (job.started or job.created), 3)})
                    self._finish(job, "completed")
                    self.running = None
                elif kind == "failed":
                    self._finish(job, "failed", message.get("error") or {"code": "song.failed", "summary": "The song failed."})
                    self.running = None
                elif kind == "canceled":
                    self._finish(job, "canceled")
                    self.running = None
                self.last_active = time.monotonic()
                self.changed.notify_all()
        with self.lock:
            if self.process is process:
                self.worker_state = "stopped"
                if self.running is not None and (job := self.jobs.get(self.running)) is not None:
                    self._finish(job, "failed", {"code": "song.failed",
                                                 "summary": "The singing worker stopped; it restarts for the next song."})
                    self.restarts += 1
                    self.running = None
                self.changed.notify_all()

    def dispatch(self) -> None:
        """Runs queued songs one at a time; releases the worker after an idle period and drops old results."""
        while True:
            with self.lock:
                self._expire()
                if self.running is not None:
                    job = self.jobs.get(self.running)
                    if job and job.started and time.monotonic() - job.started > JOB_SECONDS:
                        self._finish(job, "failed", {"code": "song.timeout", "summary": "The song took too long."})
                        self.running = None
                        self._stop_worker()
                    self.changed.wait(1.0)
                    continue
                if not self.waiting:
                    if self._running() and time.monotonic() - self.last_active > IDLE_SECONDS:
                        _log("Releasing the singing models after an idle period.")
                        self._stop_worker()
                    self.changed.wait(1.0)
                    continue
                job = self.jobs[self.waiting.pop(0)]
                self.running = job.job_id
                job.state = "running"
                job.stage = "loading" if not self._running() else "writing_music"
                job.started = time.monotonic()
                try:
                    self._start_worker()
                except OSError as error:
                    self._finish(job, "failed", {"code": "singing.unavailable", "summary": f"The singing worker could not start: {error}"})
                    self.running = None
                    continue
                message = {"type": "run", "job": {**job.request, "job_id": job.job_id, "directory": str(job.directory)}}
            if not self._send(message):
                with self.lock:
                    self._finish(job, "failed", {"code": "singing.unavailable", "summary": "The singing worker is not running."})
                    self.running = None

    def _stop_worker(self) -> None:
        process = self.process
        self.process = None
        self.worker_state = "stopped"
        if process is not None and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=20)
            except subprocess.TimeoutExpired:
                process.kill()

    def _expire(self) -> None:
        now = time.monotonic()
        finished = sorted((j for j in self.jobs.values() if j.state in TERMINAL), key=lambda j: j.finished or 0)
        for index, job in enumerate(finished):
            if now - (job.finished or now) > KEEP_SECONDS or index < len(finished) - MAX_KEPT:
                shutil.rmtree(job.directory, ignore_errors=True)
                del self.jobs[job.job_id]


def _gpu() -> dict[str, int] | None:
    try:
        output = subprocess.run(["nvidia-smi", "--query-gpu=memory.used,memory.total", "--format=csv,noheader,nounits"],
                                capture_output=True, text=True, timeout=3, check=True).stdout.split("\n")[0]
        used, total = (int(v.strip()) for v in output.split(","))
        return {"used_mib": used, "total_mib": total}
    except (OSError, subprocess.SubprocessError, ValueError):
        return None


def _validate(body: dict[str, Any]) -> dict[str, Any]:
    def text(name: str, maximum: int, newlines: bool = False) -> str:
        value = body.get(name)
        if not isinstance(value, str) or not value.strip() or len(value) > maximum:
            raise ValueError(f"{name} is missing or too long.")
        if any(ord(c) < 32 and not (newlines and c in "\r\n\t") for c in value):
            raise ValueError(f"{name} must be plain text.")
        return value

    def integer(name: str, low: int, high: int, required: bool = True) -> int | None:
        value = body.get(name)
        if value is None and not required:
            return None
        if not isinstance(value, int) or isinstance(value, bool) or not low <= value <= high:
            raise ValueError(f"{name} must be {low} to {high}.")
        return value

    request = {
        "lyrics": text("lyrics", 4096, newlines=True), "style": text("style", 512),
        "duration_seconds": integer("duration_seconds", 15, 180), "language": text("language", 2),
        "quality": body.get("quality"), "voice_match": body.get("voice_match"), "voice_id": text("voice_id", 128),
        "bpm": integer("bpm", 40, 240, required=False), "key": body.get("key"),
        "seed": integer("seed", 0, 2**32 - 1, required=False),
    }
    if request["quality"] not in ("fast", "high_quality") or request["voice_match"] not in ("soulx", "vevosing"):
        raise ValueError("Choose a quality and a voice match.")
    if request["key"] is not None and (not isinstance(request["key"], str) or len(request["key"]) > 16):
        raise ValueError("The key is invalid.")
    reference = body.get("reference")
    if not isinstance(reference, dict):
        raise ValueError("The voice's recording is missing.")
    try:
        data = base64.b64decode(str(reference.get("audio_base64")), validate=True)
    except ValueError as error:
        raise ValueError("The voice's recording is not valid base64.") from error
    if hashlib.sha256(data).hexdigest() != reference.get("audio_sha256"):
        raise ValueError("The voice's recording does not match its SHA-256.")
    audio.parse_reference(data)
    request["reference_audio"] = data
    return request


SERVICE = Service()


class Handler(http.server.BaseHTTPRequestHandler):
    server_version = "martlet-singing-host"
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
            self._json(413, {"error": {"code": "request.too_large", "summary": "The request is too large."}})
            return None
        try:
            value = json.loads(self.rfile.read(length))
        except ValueError:
            value = None
        if not isinstance(value, dict):
            self._json(400, {"error": {"code": "request.invalid", "summary": "The request is not a JSON object."}})
            return None
        return value

    def _parts(self) -> tuple[list[str], dict[str, list[str]]]:
        url = urllib.parse.urlsplit(self.path)
        return [p for p in url.path.split("/") if p], urllib.parse.parse_qs(url.query)

    def do_GET(self) -> None:  # noqa: N802
        parts, query = self._parts()
        if parts == ["status"]:
            self._json(200, SERVICE.status())
        elif len(parts) == 2 and parts[0] == "jobs":
            self._json(200, SERVICE.view(parts[1]))
        elif len(parts) == 4 and parts[0] == "jobs" and parts[2] == "tracks" and parts[3] in audio.TRACKS:
            try:
                offset = int(query.get("offset", ["0"])[0])
                frames = int(query.get("frames", ["0"])[0])
                if offset < 0 or frames <= 0:
                    raise ValueError
            except ValueError:
                self._json(400, {"error": {"code": "request.invalid", "summary": "offset and frames must be whole numbers."}})
                return
            page = SERVICE.track(parts[1], parts[3], offset, frames)
            if isinstance(page, dict):
                self._json(200, page)
                return
            data, channels, total = page
            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Length", str(len(data)))
            self.send_header("X-Song-Sample-Rate", str(audio.OUTPUT_RATE))
            self.send_header("X-Song-Channels", str(channels))
            self.send_header("X-Song-Total-Frames", str(total))
            self.end_headers()
            self.wfile.write(data)
        else:
            self._json(404, {"error": {"code": "not_found", "summary": "No such path."}})

    def do_POST(self) -> None:  # noqa: N802
        parts, _ = self._parts()
        body = self._body()
        if body is None:
            return
        if parts == ["jobs"]:
            status, value = SERVICE.submit(body)
            self._json(status, value)
        elif len(parts) == 3 and parts[0] == "jobs" and parts[2] == "cancel":
            self._json(200, SERVICE.cancel(parts[1]))
        else:
            self._json(404, {"error": {"code": "not_found", "summary": "No such path."}})


def serve() -> None:
    shutil.rmtree(JOBS, ignore_errors=True)
    JOBS.mkdir(parents=True, exist_ok=True)
    server = http.server.ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    server.daemon_threads = True
    threading.Thread(target=SERVICE.dispatch, name="singing-dispatch", daemon=True).start()
    _log(f"Martlet singing host service listening on 127.0.0.1:{PORT}")
    server.serve_forever()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="martlet-singing")
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("serve")
    provisioning = commands.add_parser("provision")
    provisioning.add_argument("--fixture", action="store_true")
    args = parser.parse_args(argv)
    if args.command == "serve":
        serve()
    elif args.command == "provision":
        provision(args.fixture)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
