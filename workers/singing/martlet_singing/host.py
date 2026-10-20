"""Martlet singing host service: the loopback-only HTTP front of the singing worker process (martlet_singing.worker).

It runs inside the ``singing`` host role container, next to the Martlet gateway, whose relay
(Martlet.Gateway.Singing.SongRelayWorker) is its only client. A song takes from tens of seconds to minutes, so songs are
jobs: start one, poll its stage, fetch its tracks in pages, or cancel it. One job is made at a time; up to four more
wait in order. The worker process starts with the first job, is restarted if it dies, and exits after an idle period,
which frees the graphics card and system memory so the speaking and listening roles can use them.

A job has a kind:
  song     (default) the whole song: music, separation, voice match, mix and timing -> tracks mix, vocals, backing.
  music    the music only (no voice): tracks mix, vocals (the separated vocals) and backing, with the beat and lyric times.
  match    the voice match only: the client uploads 48 kHz mono vocals (often a music job's, from another computer) with
           POST /jobs/<id>/input; the job waits in state "receiving" until all input_frames arrived (at most 300 s), then
           queues. Its only track is vocals, the same length as the input.
  separate a finished song made elsewhere (for example by a cloud music service): the client uploads it (48 kHz, mono or
           stereo: input_channels) like a match job's vocals; Demucs separates it and the beat, lyric and word times come
           from its vocals. Tracks mix, vocals and backing, the same length as the input.
A song or music job may ask for a music model ("model"): ace-step-v15 (the default), ace-step-v15-xl (ACE-Step 1.5 XL
SFT, 50 steps; only when the owner chose SINGING_MUSIC_MODELS standard-xl), or heartmula, diffrhythm or yue (each only when
the owner chose SINGING_HEARTMULA, SINGING_DIFFRHYTHM or SINGING_YUE yes). A song or match job's voice_match is soulx,
vevosing (SINGING_VOICE_MATCHES soulx-vevosing), vevo2 (SINGING_VEVO2 yes) or seedvc (SINGING_SEEDVC yes).

Commands:
  serve                 listen on 127.0.0.1:$MARTLET_SINGING_PORT (default 50085)
  provision [--fixture] download and verify the pinned model files ($SINGING_VOICE_MATCHES: soulx or soulx-vevosing;
                        $SINGING_MUSIC_MODELS: standard or standard-xl; $SINGING_HEARTMULA, $SINGING_DIFFRHYTHM,
                        $SINGING_YUE, $SINGING_VEVO2, $SINGING_SEEDVC: yes or no), then write the worker config (--fixture:
                        deterministic FIXTURE - NOT AI engine for plumbing checks, with every model)

HTTP (127.0.0.1 only; every answer is JSON except track pages):
  GET  /status                         {"state", "ready", "engine", "worker", "qualities", "voice_matches", "music_models",
                                        "kinds", "queue", ...}
  POST /jobs                           a job request ("kind": song, music, match or separate) -> the job's view or {"error"}
  GET  /jobs/<id>                      {"job_id", "kind", "state", "stage", "fraction", "queue_position"?,
                                        "input_frames"? and "received_frames"? (match and separate), "input_channels"?
                                        (separate), "error"?, "result"?}
  POST /jobs/<id>/input?offset=<frame> a match or separate job's input: raw interleaved signed 16-bit little-endian PCM at
                                       48 kHz with the job's channels (match: mono), whole frames up to 4.8 MB, offset =
                                       the frames already received -> the job's view or {"error"}
  GET  /jobs/<id>/tracks/<track>?offset=<frame>&frames=<n>
                                       raw signed 16-bit PCM (X-Song-Sample-Rate, X-Song-Channels, X-Song-Total-Frames)
  POST /jobs/<id>/cancel               the job's view
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

try:
    import martlet_worker_host as shared
except ModuleNotFoundError:  # a checkout: the shared module is in workers/common (the image copies it beside the package)
    sys.path.append(str(Path(__file__).resolve().parents[2] / "common"))
    import martlet_worker_host as shared

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
MAX_BODY_BYTES = shared.MAX_BODY_BYTES
MAX_WAITING = 4
MAX_KEPT = 6
PAGE_BYTES = 4_800_000
STAGES = ("queued", "loading", "writing_music", "separating", "matching_voice", "mixing", "aligning", "completed")
TERMINAL = {"completed", "failed", "canceled"}
KINDS = ("song", "music", "match", "separate")
# The kinds whose job waits in state "receiving" for its input (input.pcm) before it queues.
RECEIVING_KINDS = ("match", "separate")
# The stage a job reports when it starts on a worker that is already running (a new worker reports "loading").
FIRST_STAGES = {"song": "writing_music", "music": "writing_music", "match": "matching_voice", "separate": "separating"}
# SINGING_MUSIC_MODELS -> the music models provisioned.
MUSIC_MODEL_CHOICES = {"standard": ["ace-step-v15"], "standard-xl": ["ace-step-v15", "ace-step-v15-xl"]}
MAX_INPUT_FRAMES = audio.OUTPUT_RATE * 200
RECEIVE_SECONDS = float(os.environ.get("MARTLET_SINGING_RECEIVE_SECONDS", "300"))


def _log(message: str) -> None:
    print(message, flush=True)


def _download(url: str, target: Path, size: int, sha256: str) -> None:
    shared.fetch_pinned(url, target, size, sha256, agent="martlet-singing-host", progress="lines")


# ------------------------------------------------------------------ provisioning


def voice_matches_choice() -> list[str]:
    value = os.environ.get("SINGING_VOICE_MATCHES", "soulx").replace(" ", "")
    matches = [m for m in value.split("-") if m]
    if not matches or matches[0] != "soulx" or any(m not in pins.CONVERTER_IDS for m in matches):
        raise SystemExit("SINGING_VOICE_MATCHES must be soulx or soulx-vevosing.")
    return matches


def music_models_choice() -> list[str]:
    value = os.environ.get("SINGING_MUSIC_MODELS", "standard").replace(" ", "")
    if value not in MUSIC_MODEL_CHOICES:
        raise SystemExit("SINGING_MUSIC_MODELS must be standard or standard-xl.")
    return list(MUSIC_MODEL_CHOICES[value])


def extras_choice() -> list[str]:
    """The models the owner added (pins.EXTRA_CHOICES: each SINGING_<NAME> is yes or no; missing is no)."""
    chosen = []
    for name, variable in pins.EXTRA_CHOICES.items():
        value = os.environ.get(variable, "no").strip().lower()
        if value not in ("yes", "no"):
            raise SystemExit(f"{variable} must be yes or no.")
        if value == "yes":
            chosen.append(name)
    return chosen


def provision(fixture: bool) -> None:
    artifacts: list[dict[str, Any]] = []
    extras = extras_choice()
    matches = voice_matches_choice() + [m for m in pins.EXTRA_MATCHES if m in extras]
    music_models = music_models_choice() + [m for m in pins.EXTRA_MUSIC if m in extras]
    if fixture:
        _log("FIXTURE - NOT AI: provisioning the deterministic tone engine (no model, no GPU).")
        engine = "fixture"
        # The fixture makes every music model, so plumbing checks can ask for each; its voice matches are the chosen ones.
        music_models = list(pins.MUSIC_MODELS)
        artifacts.append({"artifact_id": "fixture-tone-song", "bytes": 0, "license_id": "fixture-only",
                          "revision": "0" * 40, "sha256": "0" * 64})
    else:
        engine = "song"
        groups = ({"core"} | ({"vevosing"} if "vevosing" in matches else set())
                  | ({"xl"} if "ace-step-v15-xl" in music_models else set()) | set(extras))
        for artifact_id, url, revision, size, sha256, license_id, local in pins.files(groups):
            _log(f"Model file {artifact_id} ({license_id})")
            _download(url, MODELS / local, size, sha256)
            artifacts.append({"artifact_id": artifact_id, "bytes": size, "license_id": license_id, "revision": revision,
                              "sha256": sha256})
        # transformers finds the pinned Whisper base (SoulX's content encoder) offline through its cache layout.
        whisper_base = next(p for p in pins.PINNED_FILES if p.repository == "openai/whisper-base")
        refs = MODELS / "huggingface/hub/models--openai--whisper-base/refs"
        refs.mkdir(parents=True, exist_ok=True)
        (refs / "main").write_text(whisper_base.revision, encoding="utf-8")
        # The same for the added models' libraries that load a model by its repository name (DiffRhythm's MuQ-MuLan).
        for group, folder, revision in pins.HF_CACHE_REFS:
            if group in groups:
                (MODELS / folder / "refs").mkdir(parents=True, exist_ok=True)
                (MODELS / folder / "refs" / "main").write_text(revision, encoding="utf-8")
    identity = {
        "artifacts": artifacts, "engine": engine, "evidence": "synthetic_fixture" if fixture else "live_worker",
        "sources": {"ace_step": pins.ACE_STEP_COMMIT, "soulx_singer": pins.SOULX_COMMIT, "amphion": pins.AMPHION_COMMIT,
                    **({"heartlib": pins.HEARTLIB_COMMIT} if "heartmula" in extras else {}),
                    **({"diffrhythm": pins.DIFFRHYTHM_COMMIT} if "diffrhythm" in extras else {}),
                    **({"seed_vc": pins.SEEDVC_COMMIT} if "seedvc" in extras else {})},
        "output_format": {"bits_per_sample": 16, "sample_rate": audio.OUTPUT_RATE,
                          "channels": {"mix": 2, "vocals": 1, "backing": 2}},
        "worker_id": WORKER_ID,
    }
    MODELS.mkdir(parents=True, exist_ok=True)
    temporary = CONFIG.with_name(CONFIG.name + ".partial")
    temporary.write_text(json.dumps({"device": DEVICE, "engine": engine, "models": str(MODELS), "voice_matches": matches,
                                     "music_models": music_models, "worker": identity}, indent=2, sort_keys=True),
                         encoding="utf-8")
    temporary.replace(CONFIG)
    _log(f"Worker configuration written ({engine} engine on {DEVICE}; voice matches: {', '.join(matches)}; "
         f"music models: {', '.join(music_models)}).")


# ------------------------------------------------------------------ jobs and the worker process


class Job:
    def __init__(self, job_id: str, request: dict[str, Any]) -> None:
        self.job_id = job_id
        self.request = request
        self.kind: str = request.get("kind", "song")
        # A match or separate job waits for its input (input.pcm) before it queues.
        self.state = "receiving" if self.kind in RECEIVING_KINDS else "queued"
        self.stage = "queued"
        self.fraction = 0.0
        self.input_frames = int(request.get("input_frames") or 0)
        self.input_channels = int(request.get("input_channels") or 1)
        self.received_frames = 0
        self.error: dict[str, str] | None = None
        self.meta: dict[str, Any] | None = None
        self.created = time.monotonic()
        self.finished: float | None = None
        self.started: float | None = None
        # The worker process making this song.
        self.worker: Any = None

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
                "music_models": _music_models(config) if config else [],
                "kinds": list(KINDS),
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
        if request["kind"] in ("song", "match") and request["voice_match"] not in config.get("voice_matches", ["soulx"]):
            return 200, {"error": {"code": "singing.voice_match_unavailable",
                                   "summary": f"{pins.NAMES.get(request['voice_match'], request['voice_match'])} is not set up on this computer."}}
        if request["kind"] in ("song", "music") and request["model"] not in _music_models(config):
            return 200, {"error": {"code": "singing.model_unavailable",
                                   "summary": f"{pins.NAMES.get(request['model'], request['model'])} is not set up on this computer."}}
        reference = request.pop("reference_audio", None)
        with self.lock:
            receiving = sum(1 for j in self.jobs.values() if j.state == "receiving")
            if len(self.waiting) + receiving >= MAX_WAITING:
                return 200, {"error": {"code": "singing.busy", "summary": "Too many songs are waiting; try again later."}}
            job = Job("song-" + secrets.token_hex(8), request)
            job.directory.mkdir(parents=True, exist_ok=True)
            if reference is not None:
                (job.directory / "reference.wav").write_bytes(reference)
            self.jobs[job.job_id] = job
            if job.state == "receiving":
                (job.directory / "input.pcm").write_bytes(b"")
            else:
                self.waiting.append(job.job_id)
            self.last_active = time.monotonic()
            self.changed.notify_all()
            return 200, self._job_view(job)

    def receive(self, job_id: str, offset: int | None, data: bytes) -> dict[str, Any]:
        """Appends a page of a match job's vocals or a separate job's song; with the last page the job joins the waiting
        queue."""

        def invalid(summary: str) -> dict[str, Any]:
            return {"error": {"code": "request.invalid", "summary": summary}}

        with self.lock:
            job = self.jobs.get(job_id)
            if job is None:
                return self.view(job_id)
            if job.kind not in RECEIVING_KINDS or job.state != "receiving":
                return invalid(job.error["summary"] if job.state == "failed" and job.error else
                               "This job is not waiting for audio.")
            if offset != job.received_frames:
                return invalid(f"offset must be {job.received_frames}, the frames already received.")
            block = 2 * job.input_channels
            if not block <= len(data) <= PAGE_BYTES or len(data) % block:
                return invalid(f"Send {block} to {PAGE_BYTES} bytes of 16-bit PCM in whole frames of {job.input_channels} "
                               f"channel{'s' if job.input_channels > 1 else ''} ({block} bytes each).")
            frames = len(data) // block
            if job.received_frames + frames > job.input_frames:
                return invalid(f"The audio is longer than input_frames ({job.input_frames}).")
            with (job.directory / "input.pcm").open("ab") as stream:
                stream.write(data)
            job.received_frames += frames
            self.last_active = time.monotonic()
            if job.received_frames == job.input_frames:
                job.state = "queued"
                self.waiting.append(job.job_id)
                self.changed.notify_all()
            return self._job_view(job)

    def view(self, job_id: str) -> dict[str, Any]:
        with self.lock:
            job = self.jobs.get(job_id)
            return self._job_view(job) if job else {"job_id": job_id, "state": "unknown",
                                                    "error": {"code": "job.unknown", "summary": "No such song (it may have expired)."}}

    def _job_view(self, job: Job) -> dict[str, Any]:
        view: dict[str, Any] = {"job_id": job.job_id, "kind": job.kind, "state": job.state, "stage": job.stage,
                                "fraction": round(job.fraction, 3)}
        if job.kind in RECEIVING_KINDS:
            view["input_frames"] = job.input_frames
            view["received_frames"] = job.received_frames
        if job.kind == "separate":
            view["input_channels"] = job.input_channels
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
            elif job.state == "receiving":
                self._finish(job, "canceled")
            elif self.running == job_id:
                self._send({"type": "cancel", "job_id": job_id})
            return self._job_view(job)

    def track(self, job_id: str, track: str, offset: int, frames: int) -> tuple[bytes, int, int] | dict[str, Any]:
        with self.lock:
            job = self.jobs.get(job_id)
            if job is not None and job.kind == "match" and track != "vocals":
                return {"error": {"code": "request.invalid", "summary": "A voice match job has only vocals."}}
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
                    if message.get("state") == "released" and self.process is process:
                        # The worker ends after this song (its models were released for lack of system memory): the next
                        # song starts a new one, and this one's last messages are still read below.
                        _log("The singing worker ends after this song to give its memory back.")
                        self.process = None
                        self.worker_state = "stopped"
                    elif self.process is process:
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
        try:
            process.wait(timeout=60)
        except subprocess.TimeoutExpired:
            pass
        with self.lock:
            if self.process is process:
                self.worker_state = "stopped"
            # A song this worker was making fails with the reason (also after it said it would end).
            if self.running is not None and (job := self.jobs.get(self.running)) is not None and job.worker is process:
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
                job.stage = "loading" if not self._running() else FIRST_STAGES[job.kind]
                job.started = time.monotonic()
                try:
                    self._start_worker()
                except OSError as error:
                    self._finish(job, "failed", {"code": "singing.unavailable", "summary": f"The singing worker could not start: {error}"})
                    self.running = None
                    continue
                job.worker = self.process
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
        for job in [j for j in self.jobs.values() if j.state == "receiving" and now - j.created > RECEIVE_SECONDS]:
            self._finish(job, "failed", {"code": "request.invalid", "summary": "The vocals did not arrive in time."
                                         if job.kind == "match" else "The song did not arrive in time."})
        finished = sorted((j for j in self.jobs.values() if j.state in TERMINAL), key=lambda j: j.finished or 0)
        for index, job in enumerate(finished):
            if now - (job.finished or now) > KEEP_SECONDS or index < len(finished) - MAX_KEPT:
                shutil.rmtree(job.directory, ignore_errors=True)
                del self.jobs[job.job_id]


def _gpu() -> dict[str, int] | None:
    from martlet_singing.engines import card_memory

    return card_memory()


def _music_models(config: dict[str, Any]) -> list[str]:
    """The music models set up (a configuration written before XL existed had the standard one only)."""
    return list(config.get("music_models", [pins.MUSIC_MODELS[0]]))


def _validate(body: dict[str, Any]) -> dict[str, Any]:
    """The checked request of a job; song (the default kind) is checked as before kinds existed."""
    kind = body.get("kind")
    if kind is None:
        kind = "song"
    if kind not in KINDS:
        raise ValueError("kind must be song, music, match or separate.")

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

    request: dict[str, Any] = {"kind": kind}
    if kind == "separate":
        request.update({"lyrics": text("lyrics", 4096, newlines=True), "language": text("language", 2),
                        "input_frames": integer("input_frames", 1, MAX_INPUT_FRAMES),
                        "input_channels": integer("input_channels", 1, 2),
                        "bpm": integer("bpm", 40, 240, required=False), "key": body.get("key")})
        if request["key"] is not None and (not isinstance(request["key"], str) or len(request["key"]) > 16):
            raise ValueError("The key is invalid.")
        return request
    if kind != "match":
        request.update({"lyrics": text("lyrics", 4096, newlines=True), "style": text("style", 512),
                        "duration_seconds": integer("duration_seconds", 15, 180), "language": text("language", 2),
                        "quality": body.get("quality")})
        model = body.get("model")
        if model is None:
            model = pins.MUSIC_MODELS[0]
        if model not in pins.MUSIC_MODELS:
            raise ValueError("model must be one of " + ", ".join(pins.MUSIC_MODELS) + ".")
        request["model"] = model
    if kind != "music":
        request.update({"voice_match": body.get("voice_match"), "voice_id": text("voice_id", 128)})
    if kind != "match":
        request.update({"bpm": integer("bpm", 40, 240, required=False), "key": body.get("key"),
                        "seed": integer("seed", 0, 2**32 - 1, required=False)})
        if request["quality"] not in ("fast", "high_quality") or (
                kind == "song" and request["voice_match"] not in pins.CONVERTER_IDS):
            raise ValueError("Choose a quality and a voice match." if kind == "song" else "Choose a quality.")
        if request["key"] is not None and (not isinstance(request["key"], str) or len(request["key"]) > 16):
            raise ValueError("The key is invalid.")
    else:
        if request["voice_match"] not in pins.CONVERTER_IDS:
            raise ValueError("Choose a voice match.")
        request["input_frames"] = integer("input_frames", 1, MAX_INPUT_FRAMES)
    if kind == "music":
        return request
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


class Handler(shared.JsonHandler):
    server_version = "martlet-singing-host"

    def refusal(self, code: str, summary: str) -> dict[str, Any]:
        return {"error": {"code": code, "summary": summary}}

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

    def _input(self, job_id: str, query: dict[str, list[str]]) -> None:
        """A page of a match or separate job's input: raw PCM, not JSON."""
        try:
            number = int(query.get("offset", [""])[0])
        except ValueError:
            number = -1
        offset = number if number >= 0 else None
        try:
            length = int(self.headers.get("Content-Length") or "0")
        except ValueError:
            length = -1
        if not 0 <= length <= PAGE_BYTES:
            if 0 < length <= MAX_BODY_BYTES:
                self.rfile.read(length)
            else:
                self.close_connection = True
            self._json(200, {"error": {"code": "request.invalid",
                                       "summary": f"Send at most {PAGE_BYTES} bytes of 16-bit PCM in whole frames."}})
            return
        data = self.rfile.read(length) if length else b""
        self._json(200, SERVICE.receive(job_id, offset, data))

    def do_POST(self) -> None:  # noqa: N802
        parts, query = self._parts()
        if len(parts) == 3 and parts[0] == "jobs" and parts[2] == "input":
            self._input(parts[1], query)
            return
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
