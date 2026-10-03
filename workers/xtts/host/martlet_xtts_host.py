"""Martlet XTTS-v2 host service: the loopback-only voice service of the ``xtts`` host role.

It runs inside the ``xtts`` host role container, next to the Martlet gateway, whose relay (Martlet.Gateway.Xtts, the F5
relay on XTTS's own route) is its only client. It speaks the f5 role's HTTP protocol and ``martlet.f5.worker`` event
stream, so the gateway checks XTTS output exactly like F5's: contiguous 24 kHz mono signed 16-bit PCM frames of at most
4,800 samples, chunk completions and one terminal event.

Unlike F5, XTTS streams: ``Xtts.inference_stream`` yields audio while the GPT is still generating a sentence, and each
piece is sent at once, so the first audio leaves long before the sentence is finished. The reference voice's conditioning
latents (``Xtts.get_conditioning_latents``) are computed once per reference revision and cached. For a voice made from
several recordings, the relay sends where each lies in the joined recording (``reference.clips``) and the latents come
from all of them, each as its own reference file.

The model runs in a separate worker process (``worker`` command) that this service starts, warms and restarts if it
dies; a reply waits (bounded) for a busy worker instead of failing. Stopping a reply stops generation between pieces.

Commands:
  serve                 listen on 127.0.0.1:$MARTLET_XTTS_PORT (default 50081); warms the worker when provisioned
  provision [--fixture] download and verify the pinned XTTS-v2 files, then write the worker config
                        (--fixture: deterministic FIXTURE - NOT AI tone engine for plumbing checks without a GPU)
  warm                  ask the running service to load the model and wait until it is ready
  worker                the model process (started by serve; newline-delimited JSON on stdin/stdout)

HTTP (127.0.0.1 only):
  GET  /status      {"state", "ready", "worker", "error", "last_reply"}
  POST /warmup      (re)start and warm the worker; 200 when ready, 503 otherwise
  POST /synthesize  {"ids", "deadline_utc", "reference", "chunks"} -> application/x-ndjson worker events
  POST /cancel      {"request_id"} -> stops the active synthesis between generated pieces
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import http.server
import importlib.metadata
import json
import math
import os
import platform
import queue
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
from collections import OrderedDict
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any

ROOT = Path(os.environ.get("MARTLET_XTTS_ROOT", "/opt/martlet-xtts"))
MODELS = ROOT / "models"
CONFIG = MODELS / "worker-config.json"
PORT = int(os.environ.get("MARTLET_XTTS_PORT", "50081"))
MODEL = os.environ.get("XTTS_MODEL", "xtts-v2")
LANGUAGE = os.environ.get("XTTS_LANGUAGE", "en")
DEVICE = os.environ.get("MARTLET_XTTS_DEVICE", "cuda:0")
WORKER_ID = "martlet-xtts-host"
CONTRACT_ID = "martlet.f5.worker"
PROTOCOL_VERSION = {"major": 1, "minor": 0}
SAMPLE_RATE = 24_000
MAX_FRAME_SAMPLES = 4_800
MAX_SAMPLES = SAMPLE_RATE * 90
MAX_BODY_BYTES = 6 * 1024 * 1024
MAX_CHUNKS = 16
MAX_CHUNK_CHARACTERS = 2_048
WARMUP_SECONDS = 290
REQUEST_SECONDS = 90
BUSY_WAIT_SECONDS = 30
# GPT tokens per streamed piece: smaller pieces reach the speaker sooner (20 is upstream's default, about 0.4 s of audio).
STREAM_CHUNK_TOKENS = int(os.environ.get("XTTS_STREAM_CHUNK", "20"))
LATENT_CACHE = 8
TERMINAL = {"completed", "canceled", "failed"}
LANGUAGES = ("en", "es", "fr", "de", "it", "pt", "pl", "tr", "ru", "nl", "cs", "ar", "zh-cn", "hu", "ko", "ja", "hi")

# Pinned files per selectable model: role -> (file, revision, bytes, SHA-256, license). Hugging Face coqui/XTTS-v2 at one
# revision; the checkpoint, its config, tokenizer and speaker file are only ever used together.
XTTS_REPOSITORY = "https://huggingface.co/coqui/XTTS-v2/resolve"
PINNED_MODELS: dict[str, dict[str, tuple[str, str, int, str, str]]] = {
    "xtts-v2": {
        "model_weights": (
            "model.pth",
            "6c2b0d75eae4b7047358e3b6bd9325f857d43f77",
            1_867_929_118,
            "c7ea20001c6a0a841c77e252d8409f6a74fb423e79b3206a0771ba5989776187",
            "CPML-1.0",
        ),
        "model_configuration": (
            "config.json",
            "6c2b0d75eae4b7047358e3b6bd9325f857d43f77",
            4_368,
            "ef262b1454dd2a77e1461b0b2cd53e19b8a7624cc131b837d36df67356bc75e8",
            "CPML-1.0",
        ),
        "vocabulary": (
            "vocab.json",
            "6c2b0d75eae4b7047358e3b6bd9325f857d43f77",
            361_219,
            "928260878a59da8a72a2a5b7687fea29d5106137669d90945430fe17e415304a",
            "CPML-1.0",
        ),
        "speakers": (
            "speakers_xtts.pth",
            "6c2b0d75eae4b7047358e3b6bd9325f857d43f77",
            7_754_818,
            "f0f6137c19a4eab0cbbe4c99b5babacf68b1746e50da90807708c10e645b943b",
            "CPML-1.0",
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


def _utc(value: str) -> datetime:
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        raise ValueError("deadline_utc needs a UTC offset")
    return parsed.astimezone(timezone.utc)


def _version(distribution: str) -> str:
    try:
        return importlib.metadata.version(distribution)
    except importlib.metadata.PackageNotFoundError:
        return "not-installed"


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
    request = urllib.request.Request(url, headers={"User-Agent": "martlet-xtts-host"})
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


def provision(fixture: bool) -> None:
    artifacts: list[dict[str, Any]] = []
    if fixture:
        _log("FIXTURE - NOT AI: provisioning the deterministic tone engine (no model, no GPU).")
        artifacts.append(
            {
                "artifact_id": "fixture-xtts",
                "bytes": 0,
                "license_id": "fixture-only",
                "revision": "0" * 40,
                "role": "model_weights",
                "sha256": hashlib.sha256(b"").hexdigest(),
            }
        )
        engine, directory = "fake", None
    else:
        pinned = PINNED_MODELS.get(MODEL)
        if pinned is None:
            raise SystemExit(f"Unknown XTTS model {MODEL!r}; choose one of: {', '.join(PINNED_MODELS)}")
        if LANGUAGE not in LANGUAGES:
            raise SystemExit(f"Unknown XTTS language {LANGUAGE!r}; choose one of: {', '.join(LANGUAGES)}")
        directory = f"models/{MODEL}"
        _log("XTTS-v2 files from huggingface.co/coqui/XTTS-v2 (Coqui Public Model License 1.0.0: non-commercial use only)")
        for role, (name, revision, size, sha256, license_id) in pinned.items():
            _download(f"{XTTS_REPOSITORY}/{revision}/{name}", ROOT / directory / name, size, sha256)
            artifacts.append(
                {
                    "artifact_id": MODEL if role == "model_weights" else f"{MODEL}-{role.replace('_', '-')}",
                    "bytes": size,
                    "license_id": license_id,
                    "revision": revision,
                    "role": role,
                    "sha256": sha256,
                }
            )
        engine = "xtts"
    config = {
        "device": DEVICE,
        "directory": directory,
        "engine": engine,
        "language": LANGUAGE,
        "model": MODEL,
        "worker": {
            "artifacts": artifacts,
            "cancellation": "discard_only",
            "contract_id": CONTRACT_ID,
            "evidence": "synthetic_fixture" if fixture else "live_worker",
            "incremental_within_chunk_synthesis": not fixture,
            "pcm_transport_streaming": True,
            "protocol_version": dict(PROTOCOL_VERSION),
            "runtime": {
                "coqui_tts_version": _version("coqui-tts"),
                "python_version": platform.python_version(),
                "torch_version": _version("torch"),
                "torchaudio_version": _version("torchaudio"),
                "transformers_version": _version("transformers"),
            },
            "worker_id": "martlet-xtts-deterministic-fixture" if fixture else WORKER_ID,
        },
    }
    MODELS.mkdir(parents=True, exist_ok=True)
    temporary = CONFIG.with_name(CONFIG.name + ".partial")
    temporary.write_text(json.dumps(config, sort_keys=True, indent=1), encoding="utf-8")
    temporary.replace(CONFIG)
    _log(f"Worker configuration written ({'fixture' if fixture else MODEL}, language {LANGUAGE}, {engine} engine).")


def warm() -> None:
    _log("Loading the XTTS-v2 model (checks every pinned file first; can take a minute)...")
    request = urllib.request.Request(f"http://127.0.0.1:{PORT}/warmup", data=b"{}", method="POST")
    try:
        with urllib.request.urlopen(request, timeout=WARMUP_SECONDS + 30) as response:
            status = json.loads(response.read())
    except urllib.error.HTTPError as error:
        status = json.loads(error.read() or b"{}")
    if not status.get("ready"):
        raise SystemExit(f"The XTTS worker is not ready ({status.get('state')}): {status.get('error') or 'no detail'}")
    _log("The XTTS-v2 voice model is loaded and ready.")


# ------------------------------------------------------------------ the model process ("worker" command)


class Canceled(Exception):
    pass


class DeadlineExceeded(Exception):
    pass


class ToneEngine:
    """FIXTURE - NOT AI: a deterministic tone per text chunk, streamed in pieces like XTTS, for plumbing checks."""

    def latents(self, audios: list[bytes]) -> Any:
        return sum(len(audio) for audio in audios)

    def stream(self, text: str, latents: Any):
        samples = min(SAMPLE_RATE * 2, 2_400 * max(1, len(text) // 4))
        for start in range(0, samples, 9_600):
            time.sleep(0.05)
            count = min(9_600, samples - start)
            yield [0.2 * math.sin(2 * math.pi * 220 * (start + i) / SAMPLE_RATE) for i in range(count)]


class XttsEngine:
    def __init__(self, config: dict[str, Any]) -> None:
        import torch  # noqa: PLC0415 - only the model process imports the model stack
        from TTS.tts.configs.xtts_config import XttsConfig  # noqa: PLC0415
        from TTS.tts.models.xtts import Xtts  # noqa: PLC0415

        directory = ROOT / config["directory"]
        pinned = PINNED_MODELS[config["model"]]
        for artifact in config["worker"]["artifacts"]:
            name = pinned[artifact["role"]][0]
            if _sha256_file(directory / name) != (artifact["bytes"], artifact["sha256"]):
                raise RuntimeError(f"{name} does not match its pinned SHA-256; run the xtts role's provisioning again.")
        device = config["device"]
        if device.startswith("cuda") and not torch.cuda.is_available():
            raise RuntimeError("No NVIDIA GPU is visible to the XTTS container.")
        self.torch = torch
        self.language = config["language"]
        model_config = XttsConfig()
        model_config.load_json(str(directory / "config.json"))
        self.config = model_config
        model = Xtts.init_from_config(model_config)
        model.load_checkpoint(model_config, checkpoint_dir=str(directory), use_deepspeed=False)
        model.to(device)
        model.eval()
        self.model = model

    def latents(self, audios: list[bytes]) -> Any:
        # One file per recording: a voice made from several recordings is learned from all of them at once (XTTS averages
        # the speaker over its reference files).
        paths: list[str] = []
        try:
            for audio in audios:
                handle, path = tempfile.mkstemp(suffix=".wav")
                paths.append(path)
                with os.fdopen(handle, "wb") as output:
                    output.write(audio)
            with self.torch.inference_mode():
                return self.model.get_conditioning_latents(
                    audio_path=paths,
                    gpt_cond_len=self.config.gpt_cond_len,
                    gpt_cond_chunk_len=self.config.gpt_cond_chunk_len,
                    max_ref_length=self.config.max_ref_len,
                    sound_norm_refs=self.config.sound_norm_refs,
                )
        finally:
            for path in paths:
                os.unlink(path)

    def stream(self, text: str, latents: Any):
        gpt_latent, speaker_embedding = latents
        with self.torch.inference_mode():
            for piece in self.model.inference_stream(
                text,
                self.language,
                gpt_latent,
                speaker_embedding,
                stream_chunk_size=STREAM_CHUNK_TOKENS,
                enable_text_splitting=True,
            ):
                yield piece.detach().float().clamp(-1, 1).cpu().numpy()


def _pcm16(samples: Any) -> bytes:
    try:
        import numpy  # noqa: PLC0415

        return (numpy.asarray(samples, dtype=numpy.float32).clip(-1, 1) * 32767).astype("<i2").tobytes()
    except ImportError:
        return b"".join(int(max(-1.0, min(1.0, value)) * 32767).to_bytes(2, "little", signed=True) for value in samples)


class ModelWorker:
    def __init__(self, config: dict[str, Any], output: Any) -> None:
        self.config = config
        self.identity = config["worker"]
        self.output = output
        self.write_lock = threading.Lock()
        self.engine: Any = None
        self.load_error: str | None = None
        self.latents: OrderedDict[str, Any] = OrderedDict()
        self.canceled: set[str] = set()
        self.jobs: queue.Queue[dict[str, Any] | None] = queue.Queue()

    def emit(self, message: dict[str, Any]) -> None:
        with self.write_lock:
            self.output.write(json.dumps(message, separators=(",", ":"), sort_keys=True).encode("utf-8") + b"\n")
            self.output.flush()

    def state(self, state: str, error: str | None = None) -> None:
        self.emit({"type": "worker_state", "state": state, "ready": state == "ready", "error": {"summary": error} if error else None})

    def read(self, fd: int) -> None:
        # Raw reads: a thread blocked in a buffered stdin read can stall imports in the main thread on Windows.
        pending = b""
        while chunk := os.read(fd, 1 << 20):
            pending += chunk
            *lines, pending = pending.split(b"\n")
            for line in lines:
                try:
                    message = json.loads(line)
                except ValueError:
                    continue
                if message.get("type") == "cancel":
                    self.canceled.add(str(message.get("request_id")))
                else:
                    self.jobs.put(message)
        self.jobs.put(None)

    def run(self) -> None:
        while (message := self.jobs.get()) is not None:
            if message.get("type") == "warmup":
                self.warmup()
            elif message.get("type") == "synthesize":
                self.synthesize(message)

    def warmup(self) -> None:
        if self.engine is not None:
            self.state("ready")
            return
        if self.load_error is not None:
            self.state("failed", self.load_error)
            return
        self.state("loading_model")
        try:
            import numpy  # noqa: F401, PLC0415 - imported before the stdin reader starts (see worker_main)

            self.engine = ToneEngine() if self.config["engine"] == "fake" else XttsEngine(self.config)
        except Exception as error:  # noqa: BLE001 - reported to the service, which reports it to the owner
            self.load_error = f"The XTTS model could not load: {type(error).__name__}: {error}"[:300]
            self.state("failed", self.load_error)
            return
        self.state("ready")

    def synthesize(self, message: dict[str, Any]) -> None:
        ids = message["ids"]
        request_id = ids["request_id"]
        reference = message["reference"]
        sequence = 0
        frames = 0
        total = 0

        def event(kind: str, **fields: Any) -> None:
            nonlocal sequence
            self.emit(
                {
                    "type": "event",
                    "kind": kind,
                    "sequence": sequence,
                    "ids": ids,
                    "reference_revision": reference["reference_revision"],
                    "worker": self.identity,
                    **fields,
                }
            )
            sequence += 1

        deadline = _utc(message["deadline_utc"])

        def check() -> None:
            if request_id in self.canceled:
                raise Canceled
            if datetime.now(timezone.utc) >= deadline:
                raise DeadlineExceeded

        try:
            if self.engine is None:
                raise RuntimeError("model_not_ready")
            event("started")
            clips = reference.get("clips")
            key = reference["reference_revision"] + ":" + reference["audio_sha256"] + (f":{len(clips)}" if clips else "")
            latents = self.latents.get(key)
            if latents is None:
                audio = base64.b64decode(reference["audio_base64"], validate=True)
                if hashlib.sha256(audio).hexdigest() != reference["audio_sha256"]:
                    raise ValueError("reference_mismatch")
                latents = self.engine.latents(_split_clips(audio, clips) if clips else [audio])
                self.latents[key] = latents
                while len(self.latents) > LATENT_CACHE:
                    self.latents.popitem(last=False)
            else:
                self.latents.move_to_end(key)
            for chunk in message["chunks"]:
                check()
                for piece in self.engine.stream(chunk["text"], latents):
                    check()
                    pcm = _pcm16(piece)
                    for start in range(0, len(pcm), MAX_FRAME_SAMPLES * 2):
                        data = pcm[start : start + MAX_FRAME_SAMPLES * 2]
                        count = len(data) // 2
                        if count == 0:
                            continue
                        if total + count > MAX_SAMPLES:
                            raise ValueError("output_limit")
                        event(
                            "audio_frame",
                            frame={
                                "chunk_index": chunk["index"],
                                "data_base64": base64.b64encode(data).decode("ascii"),
                                "sample_count": count,
                                "sample_offset": total,
                                "sequence": frames,
                            },
                        )
                        frames += 1
                        total += count
                event("chunk_completed", chunk_index=chunk["index"], final_sample_count=total)
            event("completed", final_sample_count=total)
        except Canceled:
            event("canceled", final_sample_count=total)
        except DeadlineExceeded:
            event("failed", error={"code": "deadline_exceeded", "summary": "The reply ran out of time."})
        except Exception as error:  # noqa: BLE001 - one failed reply must not end the worker
            code = "model_not_ready" if str(error) == "model_not_ready" else "synthesis_failed"
            event("failed", error={"code": code, "summary": type(error).__name__})
        finally:
            self.canceled.discard(request_id)


def worker_main() -> None:
    # The protocol owns the real stdout; anything the model stack prints goes to stderr, which the service discards
    # (it may contain reply text).
    output = os.fdopen(os.dup(1), "wb")
    os.dup2(2, 1)
    sys.stdout = sys.stderr
    config = json.loads(CONFIG.read_text(encoding="utf-8"))
    worker = ModelWorker(config, output)
    # Load the model before reading stdin: on Windows, importing numpy/torch stalls while another thread blocks reading a
    # pipe. The service's warmup message then only reports the outcome.
    worker.warmup()
    threading.Thread(target=worker.read, args=(sys.stdin.fileno(),), name="xtts-worker-input", daemon=True).start()
    worker.run()


# ------------------------------------------------------------------ the service (owns and restarts the model process)


class Worker:
    """Owns one model process. Only one synthesis runs at a time."""

    def __init__(self) -> None:
        self.lock = threading.Lock()
        self.write_lock = threading.Lock()
        self.changed = threading.Condition(self.lock)
        self.process: subprocess.Popen[bytes] | None = None
        self.identity: dict[str, Any] | None = None
        self.state = "not_provisioned" if not CONFIG.is_file() else "cold"
        self.error: str | None = None
        self.pending: dict[str, queue.Queue[dict[str, Any] | None]] = {}
        self.last_reply: dict[str, Any] | None = None

    def status(self) -> dict[str, Any]:
        with self.lock:
            return {
                "error": self.error,
                "last_reply": self.last_reply,
                "ready": self.state == "ready",
                "state": self.state,
                "worker": self.identity,
            }

    def running(self) -> bool:
        return self.process is not None and self.process.poll() is None

    def start(self) -> None:
        with self.lock:
            if self.state in {"starting", "loading_model"} or self.state == "ready" and self.running():
                return
            if not CONFIG.is_file():
                self.state, self.error = "not_provisioned", "Run the xtts role's provisioning step first."
                return
            old = self.process
            self.process = None
            self.state, self.error = "starting", None
        if old is not None and old.poll() is None:
            old.kill()
        try:
            config = json.loads(CONFIG.read_text(encoding="utf-8"))
            process = subprocess.Popen(
                [sys.executable, os.path.abspath(__file__), "worker"],
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL,
                cwd=tempfile.gettempdir(),
                env={**os.environ, "PYTHONDONTWRITEBYTECODE": "1"},
            )
        except (OSError, ValueError, KeyError):
            with self.lock:
                self.state, self.error = "failed", "The XTTS worker could not start; run 'martlet-host add xtts' again."
                self.changed.notify_all()
            return
        with self.lock:
            self.process = process
            self.identity = config["worker"]
            self.changed.notify_all()
        threading.Thread(target=self._read, args=(process,), name="xtts-worker-reader", daemon=True).start()
        self.send({"type": "warmup"})

    def wait_settled(self, seconds: float) -> dict[str, Any]:
        deadline = time.monotonic() + seconds
        with self.lock:
            while self.state not in {"ready", "failed", "not_provisioned"}:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                self.changed.wait(remaining)
        return self.status()

    def wait_admissible(self, seconds: float) -> None:
        """Wait (bounded) until the worker is ready with no reply in flight, or has settled into a non-loading state."""
        deadline = time.monotonic() + seconds
        with self.lock:
            while (self.state != "ready" or self.pending) and self.state not in {"not_provisioned", "failed"}:
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
                process.stdin.write(json.dumps(message, separators=(",", ":")).encode("utf-8") + b"\n")
                process.stdin.flush()
            return True
        except (BrokenPipeError, OSError):
            return False

    def _read(self, process: subprocess.Popen[bytes]) -> None:
        assert process.stdout is not None
        for line in process.stdout:
            try:
                message = json.loads(line)
            except ValueError:
                continue
            if message.get("type") == "worker_state":
                with self.lock:
                    if self.process is process:
                        self.state = "ready" if message.get("ready") else str(message.get("state"))
                        self.error = (message.get("error") or {}).get("summary")
                        self.changed.notify_all()
            elif message.get("type") == "event":
                with self.lock:
                    target = self.pending.get(str((message.get("ids") or {}).get("request_id")))
                if target is not None:
                    target.put(message)
        with self.lock:
            if self.process is process:
                self.state = "failed"
                self.error = self.error or "The XTTS worker process stopped; it restarts for the next reply."
                self.changed.notify_all()
            waiting = list(self.pending.values())
        for target in waiting:
            target.put(None)


WORKER = Worker()


MAX_CLIPS = 10


def _split_clips(audio: bytes, clips: list[dict[str, Any]]) -> list[bytes]:
    """Each recording of a voice made from several, cut from its joined recording at the sample ranges the gateway sent
    (in order, not overlapping, within the recording); raises ValueError otherwise."""
    import io  # noqa: PLC0415
    import wave  # noqa: PLC0415

    if not isinstance(clips, list) or not 2 <= len(clips) <= MAX_CLIPS:
        raise ValueError("reference")
    try:
        with wave.open(io.BytesIO(audio), "rb") as source:
            if source.getnchannels() != 1 or source.getsampwidth() != 2 or source.getcomptype() != "NONE":
                raise ValueError("reference")
            rate, total = source.getframerate(), source.getnframes()
            frames = source.readframes(total)
    except (wave.Error, EOFError) as error:
        raise ValueError("reference") from error
    parts: list[bytes] = []
    end = 0
    for clip in clips:
        start, count = clip["start_sample"], clip["sample_count"]
        if type(start) is not int or type(count) is not int or start < end or count <= 0 or start + count > total:
            raise ValueError("reference")
        output = io.BytesIO()
        with wave.open(output, "wb") as part:
            part.setnchannels(1)
            part.setsampwidth(2)
            part.setframerate(rate)
            part.writeframes(frames[start * 2 : (start + count) * 2])
        parts.append(output.getvalue())
        end = start + count
    return parts


def _worker_message(request: dict[str, Any]) -> dict[str, Any]:
    reference = request["reference"]
    audio = base64.b64decode(reference["audio_base64"], validate=True)
    if len(audio) > 4 * 1024 * 1024 or audio[:4] != b"RIFF" or audio[8:12] != b"WAVE":
        raise ValueError("reference")
    if hashlib.sha256(audio).hexdigest() != reference["audio_sha256"]:
        raise ValueError("reference")
    # A voice made from several recordings: where each lies in the joined recording, checked here and cut apart in the worker.
    clips = reference.get("clips")
    if clips is not None:
        _split_clips(audio, clips)
        clips = [{"start_sample": clip["start_sample"], "sample_count": clip["sample_count"]} for clip in clips]
    chunks = request["chunks"]
    if not isinstance(chunks, list) or not 0 < len(chunks) <= MAX_CHUNKS:
        raise ValueError("chunks")
    for index, chunk in enumerate(chunks):
        if chunk["index"] != index or not isinstance(chunk["text"], str) or not 0 < len(chunk["text"]) <= MAX_CHUNK_CHARACTERS:
            raise ValueError("chunks")
    deadline = min(_utc(request["deadline_utc"]), datetime.now(timezone.utc) + timedelta(seconds=REQUEST_SECONDS - 1))
    return {
        "type": "synthesize",
        "ids": {
            "parent_request_id": request["ids"].get("parent_request_id"),
            "request_id": str(request["ids"]["request_id"]),
            "session_id": request["ids"]["session_id"],
            "turn_id": request["ids"]["turn_id"],
        },
        "deadline_utc": deadline.isoformat().replace("+00:00", "Z"),
        "reference": {
            "audio_base64": reference["audio_base64"],
            "audio_sha256": reference["audio_sha256"],
            "reference_revision": reference["reference_revision"],
            **({"clips": clips} if clips else {}),
        },
        "chunks": [{"chunk_id": chunk.get("chunk_id"), "index": chunk["index"], "text": chunk["text"]} for chunk in chunks],
    }


class Handler(http.server.BaseHTTPRequestHandler):
    server_version = "martlet-xtts-host"
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
            message = _worker_message(body)
        except (KeyError, TypeError, ValueError, AttributeError):
            self._json(400, {"error": "request.invalid"})
            return
        # An interrupted reply keeps the worker busy until it notices the cancel, and a crashed worker stays down: wait
        # for it (restarting a dead worker, or one whose model failed to load, for example while another voice engine
        # still held the graphics card's memory) instead of failing the next reply with "busy".
        remaining = (_utc(message["deadline_utc"]) - datetime.now(timezone.utc)).total_seconds()
        with WORKER.lock:
            restart = WORKER.state == "failed" or (WORKER.state == "cold" and not WORKER.running())
        if restart:
            _log("XTTS worker is down or failed to load; restarting it for a reply.")
            WORKER.start()
        WORKER.wait_admissible(min(BUSY_WAIT_SECONDS, max(0.0, remaining / 2)))
        request_id = message["ids"]["request_id"]
        with WORKER.lock:
            if WORKER.state != "ready" or WORKER.pending:
                busy = bool(WORKER.pending)
                self._json(503, {"error": "worker.busy" if busy else "worker.unavailable", "state": WORKER.state})
                return
            events: queue.Queue[dict[str, Any] | None] = queue.Queue()
            WORKER.pending[request_id] = events
        started = time.monotonic()
        first_audio: float | None = None
        samples = 0
        outcome = "truncated"
        deadline = started + REQUEST_SECONDS + 5
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
                if event is None:
                    break
                if event.get("kind") == "audio_frame":
                    samples += event["frame"]["sample_count"]
                    if first_audio is None:
                        first_audio = time.monotonic() - started
                self.wfile.write(json.dumps(event, separators=(",", ":"), sort_keys=True).encode("utf-8") + b"\n")
                self.wfile.flush()
                if event.get("kind") in TERMINAL:
                    outcome = event["kind"]
                    break
        except (BrokenPipeError, ConnectionResetError):
            outcome = "disconnected"
            WORKER.send({"type": "cancel", "request_id": request_id})
        finally:
            reply = {
                "audio_seconds": round(samples / SAMPLE_RATE, 2),
                "first_audio_ms": None if first_audio is None else round(first_audio * 1000),
                "outcome": outcome,
                "total_ms": round((time.monotonic() - started) * 1000),
            }
            # Timing only: never the reply text or the voice.
            _log(f"xtts reply {outcome}: first audio {reply['first_audio_ms']} ms, {reply['audio_seconds']} s audio in {reply['total_ms']} ms")
            with WORKER.lock:
                WORKER.pending.pop(request_id, None)
                WORKER.last_reply = reply
                WORKER.changed.notify_all()


def serve() -> None:
    server = http.server.ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    server.daemon_threads = True
    if CONFIG.is_file():
        threading.Thread(target=WORKER.start, name="xtts-warmup", daemon=True).start()
    _log(f"Martlet XTTS host service listening on 127.0.0.1:{PORT}")
    server.serve_forever()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="martlet-xtts")
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("serve")
    provisioning = commands.add_parser("provision")
    provisioning.add_argument("--fixture", action="store_true")
    commands.add_parser("warm")
    commands.add_parser("worker")
    args = parser.parse_args(argv)
    if args.command == "serve":
        serve()
    elif args.command == "provision":
        provision(args.fixture)
    elif args.command == "warm":
        warm()
    elif args.command == "worker":
        worker_main()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
