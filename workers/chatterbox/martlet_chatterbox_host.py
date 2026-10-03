"""Martlet Chatterbox Turbo host service.

Loopback HTTP front for Resemble AI Chatterbox Turbo. It mirrors the F5 host's
HTTP shape and NDJSON worker-event protocol so Martlet's relay can consume it.

Commands:
  serve                 listen on 127.0.0.1:$MARTLET_CHATTERBOX_PORT (default 50083)
  provision [--fixture] download pinned model files, or write a deterministic FIXTURE - NOT AI engine
  warm                  POST /warmup to the running service and fail unless ready
"""

from __future__ import annotations

import argparse
import base64
import binascii
import hashlib
import http.client
import http.server
import json
import math
import os
import platform
import queue
import struct
import sys
import tempfile
import threading
import time
import traceback
import urllib.request
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, NoReturn

ROOT = Path(os.environ.get("MARTLET_CHATTERBOX_ROOT", "/opt/martlet-chatterbox"))
MODELS = ROOT / "models"
CONFIG = MODELS / "worker-config.json"
MODEL = os.environ.get("CHATTERBOX_MODEL", "chatterbox-turbo")
DEVICE = os.environ.get("MARTLET_CHATTERBOX_DEVICE", "cuda:0")
PORT = int(os.environ.get("MARTLET_CHATTERBOX_PORT", "50083"))

CONTRACT_ID = "martlet.f5.worker"
PROTOCOL_VERSION = {"major": 1, "minor": 0}
WORKER_ID = "martlet-chatterbox-host"
REQUEST_SECONDS = 90
WARMUP_SECONDS = 600
BUSY_WAIT_SECONDS = 30
MAX_BODY_BYTES = 6 * 1024 * 1024
MAX_FRAME_SAMPLES = 4_800
MAX_FRAME_BYTES = MAX_FRAME_SAMPLES * 2
MAX_SAMPLES = 24_000 * 90
TERMINAL = {"completed", "canceled", "failed"}
REVISION = "749d1c1a46eb10492095d68fbcf55691ccf137cd"
BASE_URL = f"https://huggingface.co/ResembleAI/chatterbox-turbo/resolve/{REVISION}/"

# Sizes and SHA-256 for tokenizer files were computed from the pinned HF revision.
PINNED_FILES: dict[str, tuple[str, int, str, str, str]] = {
    "model_weights": (
        "t3_turbo_v1.safetensors",
        1_915_480_052,
        "fcf1f8c1d651bb7e3acd69ee5be269b4ac10c02980b7708213d598bc9f7cdf87",
        "chatterbox-turbo",
        "MIT",
    ),
    "s3gen_weights": (
        "s3gen_meanflow.safetensors",
        1_064_875_036,
        "d65cb687a2ed581ee6cc297e919ffefa63386944f42364ae13b78a594945514f",
        "chatterbox-turbo-s3gen-meanflow",
        "MIT",
    ),
    "voice_encoder_weights": (
        "ve.safetensors",
        5_695_784,
        "f0921cab452fa278bc25cd23ffd59d36f816d7dc5181dd1bef9751a7fb61f63c",
        "chatterbox-turbo-voice-encoder",
        "MIT",
    ),
    "tokenizer_added_tokens": (
        "added_tokens.json",
        418,
        "72e4ab6acb0d9309ac3df4b526ae5fd80a2da5bc5ab7bb02d85096a374f69193",
        "chatterbox-turbo-added-tokens",
        "MIT",
    ),
    "tokenizer_merges": (
        "merges.txt",
        456_318,
        "1ce1664773c50f3e0cc8842619a93edc4624525b728b188a9e0be33b7726adc5",
        "chatterbox-turbo-merges",
        "MIT",
    ),
    "tokenizer_special_tokens": (
        "special_tokens_map.json",
        470,
        "92ba8063bf40aa163eadebbfe0de07c2aebe44cf0d4a9e8726580b0781fd2640",
        "chatterbox-turbo-special-tokens",
        "MIT",
    ),
    "tokenizer_config": (
        "tokenizer_config.json",
        3_878,
        "bca16a2ac1ddbd78b8d6228f0031884cc74b6ea54b967d6f6d2ebae9ccde23e6",
        "chatterbox-turbo-tokenizer-config",
        "MIT",
    ),
    "tokenizer_vocab": (
        "vocab.json",
        999_186,
        "f6bd25a65e4e63ca31360e9fb11c7e4f9a391a78385d640acd814092dd6eee4f",
        "chatterbox-turbo-vocab",
        "MIT",
    ),
}


class ContractError(Exception):
    def __init__(
        self,
        code: str,
        summary: str,
        *,
        stage: str = "request_validation",
        action_id: str = "chatterbox.correct-request",
        retryable: bool = False,
    ) -> None:
        super().__init__(summary)
        self.code = code
        self.summary = summary
        self.stage = stage
        self.action_id = action_id
        self.retryable = retryable


def fail(code: str, summary: str, *, stage: str = "request_validation", action_id: str = "chatterbox.correct-request") -> NoReturn:
    raise ContractError(code, summary, stage=stage, action_id=action_id)


def require(condition: bool, code: str, summary: str) -> None:
    if not condition:
        fail(code, summary)


def require_object(value: Any, name: str) -> dict[str, Any]:
    require(type(value) is dict, "invalid_request", f"{name} must be an object.")
    return value


def require_string(value: Any, name: str) -> str:
    require(type(value) is str, "invalid_request", f"{name} must be a string.")
    return value


def parse_utc(value: Any, name: str) -> datetime:
    text = require_string(value, name)
    if not text.endswith("Z"):
        fail("invalid_request", f"{name} must be UTC.")
    try:
        result = datetime.fromisoformat(text[:-1] + "+00:00")
    except ValueError:
        fail("invalid_request", f"{name} must be a valid UTC timestamp.")
    if result.tzinfo != timezone.utc:
        fail("invalid_request", f"{name} must be UTC.")
    return result


def format_utc(value: datetime) -> str:
    return value.astimezone(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z")


def canonical_json_bytes(value: Any) -> bytes:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), sort_keys=True).encode("utf-8")


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


def _download(url: str, target: Path, size: int, sha256: str) -> None:
    if target.is_file() and _sha256_file(target) == (size, sha256):
        _log(f"{target.name}: already downloaded and verified.")
        return
    if target.exists():
        target.unlink()
    target.parent.mkdir(parents=True, exist_ok=True)
    partial = target.with_name(target.name + ".partial")
    partial.unlink(missing_ok=True)
    digest = hashlib.sha256()
    received = 0
    shown = -1
    request = urllib.request.Request(url, headers={"User-Agent": "martlet-chatterbox-host"})
    with urllib.request.urlopen(request, timeout=60) as response, partial.open("wb") as output:
        while chunk := response.read(1_048_576):
            received += len(chunk)
            if received > size:
                partial.unlink(missing_ok=True)
                raise SystemExit(f"{target.name} is larger than its pinned size; stopped.")
            digest.update(chunk)
            output.write(chunk)
            percent = received * 100 // size
            if percent != shown:
                shown = percent
                sys.stdout.write(f"\r{target.name}: {percent}% of {max(1, size // 1_048_576)} MiB")
                sys.stdout.flush()
    sys.stdout.write("\n")
    if received != size or digest.hexdigest() != sha256:
        partial.unlink(missing_ok=True)
        raise SystemExit(f"{target.name} does not match its pinned SHA-256; nothing was installed.")
    partial.replace(target)
    _log(f"{target.name}: verified (SHA-256 {sha256[:12]}...).")


def _artifact(role: str, size: int, sha256: str, artifact_id: str, license_id: str) -> dict[str, Any]:
    return {"artifact_id": artifact_id, "bytes": size, "license_id": license_id, "revision": REVISION, "role": role, "sha256": sha256}


def _real_artifacts() -> list[dict[str, Any]]:
    return [_artifact(role, size, sha256, artifact_id, license_id) for role, (_name, size, sha256, artifact_id, license_id) in PINNED_FILES.items()]


def _fixture_artifacts(real_model_identity: bool = False) -> list[dict[str, Any]]:
    artifacts: list[dict[str, Any]] = []
    for index, role in enumerate(PINNED_FILES, 1):
        if real_model_identity and role == "model_weights":
            _name, size, sha256, artifact_id, license_id = PINNED_FILES[role]
            artifacts.append(_artifact(role, size, sha256, artifact_id, license_id))
            continue
        content = f"FIXTURE - NOT AI: {role}:{index}\n".encode("utf-8")
        artifacts.append(
            {
                "artifact_id": f"fixture-{role.replace('_', '-')}",
                "bytes": len(content),
                "license_id": "fixture-only",
                "revision": (str(index) * 40)[:40],
                "role": role,
                "sha256": hashlib.sha256(content).hexdigest(),
            }
        )
    return artifacts


def _identity(engine: str, *, real_model_identity: bool = False) -> dict[str, Any]:
    fixture = engine == "fake"
    source = Path(__file__)
    return {
        "artifacts": _fixture_artifacts(real_model_identity) if fixture else _real_artifacts(),
        "cancellation": "discard_only",
        "contract_id": CONTRACT_ID,
        "evidence": "synthetic_fixture" if fixture else "live_worker",
        "incremental_within_chunk_synthesis": False,
        "pcm_transport_streaming": True,
        "protocol_version": dict(PROTOCOL_VERSION),
        "runtime": {
            "chatterbox_tts_version": "0.1.7" if not fixture else "fixture",
            "cuda_runtime_version": "12.4" if not fixture else "fixture",
            "python_version": platform.python_version(),
            "torch_version": "2.6.0+cu124" if not fixture else "fixture",
            "torchaudio_version": "2.6.0+cu124" if not fixture else "fixture",
            "worker_build_id": WORKER_ID if not fixture else "martlet-chatterbox-deterministic-fixture",
            "worker_build_revision": hashlib.sha1(source.read_bytes()).hexdigest() if source.is_file() else "0" * 40,
        },
        "worker_id": "martlet-chatterbox-deterministic-fixture" if fixture else WORKER_ID,
    }


def _write_config(config: dict[str, Any]) -> None:
    MODELS.mkdir(parents=True, exist_ok=True)
    temporary = CONFIG.with_name(CONFIG.name + ".partial")
    temporary.write_bytes(canonical_json_bytes(config))
    temporary.replace(CONFIG)


def provision(fixture: bool) -> None:
    if MODEL != "chatterbox-turbo":
        raise SystemExit("Only CHATTERBOX_MODEL=chatterbox-turbo is supported by this prototype host.")
    if fixture:
        _log("FIXTURE - NOT AI: provisioning deterministic synthetic tone engine (no torch, no GPU, no model).")
        files: dict[str, str] = {}
        for role in PINNED_FILES:
            relative = f"models/fixture/{role}.fixture"
            path = ROOT / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(f"FIXTURE - NOT AI: {role}\n", encoding="utf-8")
            files[role] = relative
        _write_config({"device": None, "engine": "fake", "files": files, "identity": _identity("fake"), "model": MODEL})
        _log("Worker configuration written (fixture).")
        return

    _log("Chatterbox-Turbo model license: MIT, https://huggingface.co/ResembleAI/chatterbox-turbo")
    model_dir = MODELS / MODEL
    files = {}
    for role, (filename, size, sha256, _artifact_id, _license_id) in PINNED_FILES.items():
        _download(BASE_URL + filename, model_dir / filename, size, sha256)
        files[role] = f"models/{MODEL}/{filename}"
    _write_config({"device": DEVICE, "engine": "chatterbox", "files": files, "identity": _identity("chatterbox"), "model": MODEL})
    _log(f"Worker configuration written ({MODEL}, live Chatterbox Turbo engine).")


@dataclass(frozen=True)
class RequestIds:
    session_id: str
    turn_id: str
    request_id: str
    parent_request_id: str | None

    @classmethod
    def parse(cls, source: Any) -> "RequestIds":
        value = require_object(source, "ids")
        return cls(
            session_id=require_string(value.get("session_id"), "ids.session_id"),
            turn_id=require_string(value.get("turn_id"), "ids.turn_id"),
            request_id=require_string(value.get("request_id"), "ids.request_id"),
            parent_request_id=None if value.get("parent_request_id") is None else require_string(value.get("parent_request_id"), "ids.parent_request_id"),
        )

    def wire(self) -> dict[str, Any]:
        return {"parent_request_id": self.parent_request_id, "request_id": self.request_id, "session_id": self.session_id, "turn_id": self.turn_id}


@dataclass(frozen=True)
class Chunk:
    index: int
    chunk_id: str
    text: str


@dataclass(frozen=True)
class Reference:
    preset_id: str
    reference_revision: str
    audio_sha256: str
    transcript: str
    transcript_revision: str
    audio: bytes
    duration_seconds: float


@dataclass(frozen=True)
class SynthesisRequest:
    ids: RequestIds
    deadline_utc: datetime
    reference: Reference
    chunks: tuple[Chunk, ...]


def _parse_wave(audio: bytes) -> tuple[int, int, int, float]:
    require(44 <= len(audio) <= MAX_BODY_BYTES, "reference_invalid", "The reference must be a bounded PCM WAV file.")
    require(audio[0:4] == b"RIFF" and audio[8:12] == b"WAVE", "reference_invalid", "The reference is not a RIFF/WAVE file.")
    require(struct.unpack_from("<I", audio, 4)[0] + 8 == len(audio), "reference_invalid", "The reference RIFF length is invalid.")
    offset = 12
    channels = sample_rate = 0
    data_bytes = 0
    usable = False
    while offset < len(audio):
        require(len(audio) - offset >= 8, "reference_invalid", "The reference contains a truncated chunk header.")
        chunk_id = audio[offset : offset + 4]
        chunk_bytes = struct.unpack_from("<I", audio, offset + 4)[0]
        payload = offset + 8
        require(chunk_bytes <= len(audio) - payload, "reference_invalid", "The reference contains a truncated chunk.")
        end = payload + chunk_bytes
        if chunk_id == b"fmt ":
            require(channels == 0 and chunk_bytes >= 16, "reference_invalid", "The reference must contain one PCM format chunk.")
            format_tag, channels, sample_rate, byte_rate, block_align, bits = struct.unpack_from("<HHIIHH", audio, payload)
            require(format_tag == 1 and bits == 16 and channels >= 1, "reference_invalid", "The reference must be PCM16 WAV audio.")
            require(byte_rate == sample_rate * channels * 2 and block_align == channels * 2, "reference_invalid", "The reference PCM format is inconsistent.")
        elif chunk_id == b"data":
            require(data_bytes == 0 and chunk_bytes > 0 and chunk_bytes % 2 == 0, "reference_invalid", "The reference must contain one aligned data chunk.")
            data_bytes = chunk_bytes
            for sample_offset in range(payload, end, 2):
                if struct.unpack_from("<h", audio, sample_offset)[0] != 0:
                    usable = True
                    break
        offset = end + (chunk_bytes & 1)
        require(offset <= len(audio), "reference_invalid", "The reference chunk padding is invalid.")
    require(sample_rate > 0 and channels > 0 and data_bytes > 0 and usable, "reference_invalid", "The reference is missing usable PCM samples.")
    samples_per_channel = data_bytes // (channels * 2)
    return sample_rate, channels, samples_per_channel, samples_per_channel / sample_rate


def _parse_request(body: dict[str, Any]) -> SynthesisRequest:
    ids = RequestIds.parse(body.get("ids"))
    deadline = min(parse_utc(body.get("deadline_utc"), "deadline_utc"), datetime.now(timezone.utc) + timedelta(seconds=REQUEST_SECONDS - 1))
    ref = require_object(body.get("reference"), "reference")
    try:
        audio = base64.b64decode(require_string(ref.get("audio_base64"), "reference.audio_base64"), validate=True)
    except (binascii.Error, ValueError):
        fail("reference_invalid", "The reference audio is not valid base64.")
    _rate, _channels, _samples, duration = _parse_wave(audio)
    if duration <= 5.0:
        fail("reference_too_short", "Chatterbox needs a voice recording longer than 5 seconds.", action_id="chatterbox.use-longer-reference")
    chunks_source = body.get("chunks")
    require(type(chunks_source) is list and 0 < len(chunks_source) <= 16, "invalid_request", "chunks must be a non-empty bounded array.")
    chunks = []
    for item in chunks_source:
        chunk = require_object(item, "chunks[]")
        index = chunk.get("index")
        require(type(index) is int and index >= 0, "invalid_request", "chunk.index must be a non-negative integer.")
        text = require_string(chunk.get("text"), "chunk.text")
        require(text.strip() != "" and len(text.encode("utf-8")) <= 4096, "invalid_request", "chunk.text is blank or too large.")
        chunks.append(Chunk(index=index, chunk_id=require_string(chunk.get("chunk_id"), "chunk.chunk_id"), text=text))
    return SynthesisRequest(
        ids=ids,
        deadline_utc=deadline,
        reference=Reference(
            preset_id=require_string(ref.get("preset_id"), "reference.preset_id"),
            reference_revision=require_string(ref.get("reference_revision"), "reference.reference_revision"),
            audio_sha256=require_string(ref.get("audio_sha256"), "reference.audio_sha256"),
            transcript=require_string(ref.get("transcript"), "reference.transcript"),
            transcript_revision=require_string(ref.get("transcript_revision"), "reference.transcript_revision"),
            audio=audio,
            duration_seconds=duration,
        ),
        chunks=tuple(sorted(chunks, key=lambda chunk: chunk.index)),
    )


def _error_wire(error: ContractError) -> dict[str, Any]:
    return {"action_id": error.action_id, "code": error.code, "retryable": error.retryable, "stage": error.stage, "summary": error.summary}


def _make_frame(sequence: int, chunk_index: int, sample_offset: int, pcm: bytes) -> dict[str, Any]:
    return {
        "chunk_index": chunk_index,
        "data_base64": base64.b64encode(pcm).decode("ascii"),
        "format": {"bits_per_sample": 16, "channels": 1, "encoding": "signed16_little_endian", "sample_rate": 24_000},
        "sample_count": len(pcm) // 2,
        "sample_offset": sample_offset,
        "sequence": sequence,
    }


def _make_event(
    *,
    kind: str,
    sequence: int,
    ids: RequestIds,
    worker: dict[str, Any],
    reference_revision: str,
    frame: dict[str, Any] | None = None,
    chunk_index: int | None = None,
    final_sample_count: int | None = None,
    cancellation: str | None = None,
    error: dict[str, Any] | None = None,
) -> dict[str, Any]:
    return {
        "cancellation": cancellation,
        "chunk_index": chunk_index,
        "contract_id": CONTRACT_ID,
        "error": error,
        "final_sample_count": final_sample_count,
        "frame": frame,
        "ids": ids.wire(),
        "kind": kind,
        "protocol_version": dict(PROTOCOL_VERSION),
        "reference_revision": reference_revision,
        "sequence": sequence,
        "type": "event",
        "worker": worker,
    }


class Job:
    def __init__(self, request: SynthesisRequest, worker: dict[str, Any]) -> None:
        self.request = request
        self.worker = worker
        self.queue: queue.Queue[dict[str, Any] | None] = queue.Queue()
        self.lock = threading.Lock()
        self.cancel_requested = False
        self.terminal = False
        self.event_sequence = 0
        self.frame_sequence = 0
        self.sample_offset = 0
        self.deadline_monotonic = time.monotonic() + max(0.0, (request.deadline_utc - datetime.now(timezone.utc)).total_seconds())

    def request_cancel(self) -> None:
        with self.lock:
            self.cancel_requested = True
            if not self.terminal:
                self._canceled_locked()

    def started(self) -> None:
        with self.lock:
            self._emit_locked("started")

    def emit_pcm(self, chunk_index: int, pcm: bytes) -> bool:
        for offset in range(0, len(pcm), MAX_FRAME_BYTES):
            frame_bytes = pcm[offset : offset + MAX_FRAME_BYTES]
            with self.lock:
                if self.terminal or self.cancel_requested:
                    return False
                if self._deadline_expired():
                    self._fail_locked(self._deadline_error())
                    return False
                if self.sample_offset + len(frame_bytes) // 2 > MAX_SAMPLES:
                    self._fail_locked(ContractError("internal_failure", "The engine emitted more than 90 seconds of PCM.", stage="pcm_conversion"))
                    return False
                frame = _make_frame(self.frame_sequence, chunk_index, self.sample_offset, frame_bytes)
                self._emit_locked("audio_frame", frame=frame)
                self.frame_sequence += 1
                self.sample_offset += len(frame_bytes) // 2
        return True

    def chunk_completed(self, chunk_index: int) -> bool:
        with self.lock:
            if self.terminal or self.cancel_requested:
                return False
            if self._deadline_expired():
                self._fail_locked(self._deadline_error())
                return False
            self._emit_locked("chunk_completed", chunk_index=chunk_index, final_sample_count=self.sample_offset)
            return True

    def completed(self) -> None:
        with self.lock:
            if self.terminal:
                return
            if self.cancel_requested:
                self._canceled_locked()
                return
            if self._deadline_expired():
                self._fail_locked(self._deadline_error())
                return
            self._emit_locked("completed", final_sample_count=self.sample_offset)
            self.terminal = True
            self.queue.put(None)

    def failed(self, error: ContractError) -> None:
        with self.lock:
            if not self.terminal:
                self._fail_locked(error)

    def _deadline_expired(self) -> bool:
        return time.monotonic() >= self.deadline_monotonic or datetime.now(timezone.utc) >= self.request.deadline_utc

    @staticmethod
    def _deadline_error() -> ContractError:
        return ContractError("deadline_exceeded", "The synthesis deadline expired; late output was discarded.", stage="synthesis", action_id="chatterbox.retry-request")

    def _emit_locked(self, kind: str, **kwargs: Any) -> None:
        self.queue.put(
            _make_event(
                kind=kind,
                sequence=self.event_sequence,
                ids=self.request.ids,
                worker=self.worker,
                reference_revision=self.request.reference.reference_revision,
                **kwargs,
            )
        )
        self.event_sequence += 1

    def _fail_locked(self, error: ContractError) -> None:
        self._emit_locked("failed", error=_error_wire(error))
        self.terminal = True
        self.queue.put(None)

    def _canceled_locked(self) -> None:
        self._emit_locked("canceled", cancellation="discard_only", final_sample_count=self.sample_offset)
        self.terminal = True
        self.queue.put(None)


class EngineHost:
    def __init__(self) -> None:
        self.lock = threading.Lock()
        self.changed = threading.Condition(self.lock)
        self.state = "not_provisioned" if not CONFIG.is_file() else "cold"
        self.error: str | None = None
        self.identity: dict[str, Any] | None = None
        self.engine_kind: str | None = None
        self.model: Any = None
        self.active: Job | None = None
        self.loading = False

    def status(self) -> dict[str, Any]:
        with self.lock:
            return {"error": self.error, "ready": self.state == "ready", "state": self.state, "worker": self.identity}

    def start_loading(self) -> None:
        with self.lock:
            if self.loading or self.state in {"ready", "busy", "loading_model"}:
                return
            if not CONFIG.is_file():
                self.state, self.error = "not_provisioned", "Run the chatterbox role's provisioning step first."
                self.changed.notify_all()
                return
            self.loading = True
            self.state, self.error = "loading_model", None
            self.changed.notify_all()
        threading.Thread(target=self._load, name="chatterbox-load", daemon=True).start()

    def _load(self) -> None:
        try:
            config = json.loads(CONFIG.read_text(encoding="utf-8"))
            engine = config.get("engine")
            identity = config.get("identity")
            if engine == "fake" and os.environ.get("MARTLET_CHATTERBOX_FIXTURE_REAL_IDENTITY") == "1":
                # FIXTURE - NOT AI: reports real pinned model_weights identity so existing relays can be exercised.
                identity = _identity("fake", real_model_identity=True)
            if engine == "fake":
                model = FakeEngine()
            elif engine == "chatterbox":
                os.environ.setdefault("HF_HUB_OFFLINE", "1")
                os.environ.setdefault("TRANSFORMERS_OFFLINE", "1")
                os.environ.setdefault("HF_DATASETS_OFFLINE", "1")
                from chatterbox.tts_turbo import ChatterboxTurboTTS  # type: ignore

                model_dir = MODELS / str(config.get("model") or MODEL)
                model = ChatterboxTurboTTS.from_local(model_dir, str(config.get("device") or DEVICE))
            else:
                raise RuntimeError("Unknown Chatterbox engine configuration.")
        except Exception as exc:
            with self.lock:
                self.model = None
                self.engine_kind = None
                self.identity = None
                self.state = "failed"
                self.error = f"The Chatterbox worker could not load: {exc}"
                self.loading = False
                self.changed.notify_all()
            traceback.print_exc()
            return
        with self.lock:
            self.model = model
            self.engine_kind = engine
            self.identity = identity
            self.state, self.error = "ready", None
            self.loading = False
            self.changed.notify_all()

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
        deadline = time.monotonic() + seconds
        with self.lock:
            while (self.state != "ready" or self.active is not None) and self.state not in {"not_provisioned", "failed"}:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                self.changed.wait(remaining)

    def run(self, job: Job) -> None:
        failed_engine: str | None = None
        try:
            job.started()
            with self.lock:
                engine = self.model
                kind = self.engine_kind
            if engine is None or kind is None:
                job.failed(ContractError("model_not_ready", "The Chatterbox model is not ready.", stage="warmup", action_id="chatterbox.warm"))
                return
            reference_path: Path | None = None
            try:
                if kind == "fake":
                    for chunk in job.request.chunks:
                        pcm = engine.generate(chunk.text, job)
                        if not job.emit_pcm(chunk.index, pcm) or not job.chunk_completed(chunk.index):
                            return
                else:
                    reference_path = _write_private_reference(job.request.reference.audio)
                    for chunk in job.request.chunks:
                        if job.cancel_requested:
                            return
                        pcm = _generate_with_memory_retry(engine, chunk.text, reference_path)
                        if not job.emit_pcm(chunk.index, pcm) or not job.chunk_completed(chunk.index):
                            return
                job.completed()
            finally:
                if reference_path is not None:
                    reference_path.unlink(missing_ok=True)
        except ContractError as exc:
            job.failed(exc)
        except Exception as exc:
            traceback.print_exc()
            detail = _failure_detail(exc)
            if _out_of_memory(exc):
                # The model is intact after running out of graphics memory; reloading it would need even more. Keep it, free
                # what is cached and let the next reply try again.
                _free_gpu_memory()
                job.failed(ContractError("gpu_out_of_memory", "The graphics card ran out of memory while speaking; close other "
                    f"programs using it or move a job to another host ({detail}).", stage="synthesis",
                    action_id="chatterbox.free-gpu-memory", retryable=True))
            else:
                failed_engine = detail
                job.failed(ContractError("internal_failure", f"The Chatterbox engine failed while synthesizing this reply ({detail}).",
                    stage="synthesis", action_id="chatterbox.restart-worker"))
        finally:
            with self.lock:
                if self.active is job:
                    self.active = None
                if failed_engine is not None:
                    self.model = None
                    self.state = "failed"
                    self.error = f"The Chatterbox engine failed ({failed_engine}); the next reply will reload it."
                elif self.state == "busy":
                    self.state = "ready"
                self.changed.notify_all()


def _failure_detail(exc: BaseException) -> str:
    """The exception's type and first line, bounded: what the host's log shows for a failed reply. Never the reply text."""
    first = (str(exc).strip().splitlines() or [""])[0]
    text = f"{type(exc).__name__}: {first}" if first else type(exc).__name__
    text = "".join(ch if ch.isprintable() else " " for ch in text)
    return text if len(text) <= 240 else text[:240] + "..."


def _out_of_memory(exc: BaseException) -> bool:
    return type(exc).__name__ == "OutOfMemoryError" or "out of memory" in str(exc).lower()


def _free_gpu_memory() -> None:
    try:
        import gc

        gc.collect()
        import torch  # type: ignore

        if torch.cuda.is_available():
            torch.cuda.empty_cache()
    except Exception:
        pass


def _generate_with_memory_retry(model: Any, text: str, reference_path: Path) -> bytes:
    """One sentence; when the graphics card runs out of memory, free the cache once and try again."""
    try:
        return _real_generate_pcm(model, text, reference_path)
    except Exception as exc:
        if not _out_of_memory(exc):
            raise
        _log(f"Out of graphics memory ({_failure_detail(exc)}); freeing cached memory and trying once more.")
        _free_gpu_memory()
        return _real_generate_pcm(model, text, reference_path)


class FakeEngine:
    """FIXTURE - NOT AI deterministic tone for plumbing tests; never resembles speech."""

    def generate(self, text: str, job: Job) -> bytes:
        duration = min(3.0, max(0.08, len(text) * 0.06))
        total = min(MAX_SAMPLES - job.sample_offset, int(24_000 * duration))
        seed = int(hashlib.sha256(text.encode("utf-8")).hexdigest()[:8], 16)
        frequency = 220.0 + (seed % 80)
        out = bytearray(total * 2)
        start = time.monotonic()
        for i in range(total):
            value = int(math.sin(2 * math.pi * frequency * (i / 24_000.0)) * 0.18 * 32767)
            struct.pack_into("<h", out, i * 2, value)
            if i % 2_400 == 0:
                if job.cancel_requested:
                    break
                target = start + (i / 24_000.0) * 0.35
                if target > time.monotonic():
                    time.sleep(min(0.02, target - time.monotonic()))
        return bytes(out)


def _write_private_reference(audio: bytes) -> Path:
    directory = ROOT / "requests"
    directory.mkdir(parents=True, exist_ok=True)
    handle = tempfile.NamedTemporaryFile(prefix="reference-", suffix=".wav", dir=directory, delete=False)
    try:
        try:
            os.chmod(handle.name, 0o600)
        except OSError:
            pass
        handle.write(audio)
        return Path(handle.name)
    finally:
        handle.close()


def _real_generate_pcm(model: Any, text: str, reference_path: Path) -> bytes:
    wav = model.generate(text, audio_prompt_path=str(reference_path))
    sr = int(getattr(model, "sr", 24_000) or 24_000)
    try:
        import torch  # type: ignore
        import torchaudio.functional as AF  # type: ignore

        tensor = wav.detach().cpu().float().flatten() if hasattr(wav, "detach") else torch.tensor(wav, dtype=torch.float32).flatten()
        if sr != 24_000:
            tensor = AF.resample(tensor, sr, 24_000)
        tensor = torch.clamp(tensor, -1.0, 1.0)
        return (tensor * 32767.0).to(torch.int16).numpy().tobytes()
    except Exception:
        import numpy as np  # type: ignore

        array = wav.detach().cpu().numpy().reshape(-1) if hasattr(wav, "detach") else np.asarray(wav, dtype=np.float32).reshape(-1)
        if sr != 24_000 and array.size > 0:
            old = np.linspace(0.0, 1.0, num=array.size, endpoint=False)
            new_size = max(1, int(round(array.size * 24_000 / sr)))
            new = np.linspace(0.0, 1.0, num=new_size, endpoint=False)
            array = np.interp(new, old, array).astype(np.float32)
        return (np.clip(array, -1.0, 1.0) * 32767.0).astype("<i2").tobytes()


WORKER = EngineHost()


class Handler(http.server.BaseHTTPRequestHandler):
    server_version = "martlet-chatterbox-host"
    sys_version = ""

    def log_message(self, format: str, *args: Any) -> None:  # noqa: A002 - no request content in logs
        return

    def _json(self, status: int, value: dict[str, Any]) -> None:
        body = canonical_json_bytes(value)
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _body(self) -> dict[str, Any] | None:
        try:
            length = int(self.headers.get("Content-Length") or "0")
        except ValueError:
            length = 0
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
            WORKER.start_loading()
            status = WORKER.wait_settled(WARMUP_SECONDS + 10)
            self._json(200 if status["ready"] else 503, status)
        elif self.path == "/cancel":
            body = self._body()
            if body is None:
                return
            request_id = str(body.get("request_id") or "")
            with WORKER.lock:
                active = WORKER.active
            if active is not None and active.request.ids.request_id == request_id:
                active.request_cancel()
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
            deadline_utc = parse_utc(body.get("deadline_utc"), "deadline_utc")
            remaining = (deadline_utc - datetime.now(timezone.utc)).total_seconds()
        except (ContractError, TypeError, ValueError):
            self._json(400, {"error": "request.invalid"})
            return
        with WORKER.lock:
            restart = WORKER.state in {"failed", "not_provisioned", "cold"}
        if restart:
            WORKER.start_loading()
        WORKER.wait_admissible(min(BUSY_WAIT_SECONDS, max(0.0, remaining / 2)))
        with WORKER.lock:
            identity = WORKER.identity
            if WORKER.state != "ready" or identity is None or WORKER.active is not None:
                busy = WORKER.active is not None or WORKER.state == "busy"
                refusal = {"error": "worker.busy" if busy else "worker.unavailable", "state": WORKER.state}
                # Why the model isn't ready (it failed to load, or the last reply failed it); the relay logs it on the host.
                if WORKER.error and not busy:
                    refusal["detail"] = WORKER.error[:400]
                self._json(503, refusal)
                return
            try:
                request = _parse_request(body)
            except ContractError as exc:
                if exc.code in {"reference_too_short", "reference_invalid", "deadline_exceeded", "internal_failure", "model_not_ready"}:
                    request_ids = RequestIds.parse(body.get("ids"))
                    reference_revision = str((body.get("reference") or {}).get("reference_revision") or "")
                    fake_request = SynthesisRequest(request_ids, deadline_utc, Reference("", reference_revision, "", "", "", b"", 0.0), tuple())
                    job = Job(fake_request, identity)
                    self._stream_failed(job, exc)
                else:
                    self._json(400, {"error": "request.invalid"})
                return
            job = Job(request, identity)
            WORKER.active = job
            WORKER.state = "busy"
            WORKER.changed.notify_all()
        threading.Thread(target=WORKER.run, args=(job,), name="chatterbox-synthesize", daemon=True).start()
        self._stream_job(job)

    def _stream_failed(self, job: Job, error: ContractError) -> None:
        self.send_response(200)
        self.send_header("Content-Type", "application/x-ndjson")
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        job.started()
        job.failed(error)
        while True:
            event = job.queue.get()
            if event is None:
                break
            self.wfile.write(canonical_json_bytes(event) + b"\n")
            self.wfile.flush()

    def _stream_job(self, job: Job) -> None:
        deadline = time.monotonic() + REQUEST_SECONDS + 5
        try:
            self.send_response(200)
            self.send_header("Content-Type", "application/x-ndjson")
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            while True:
                try:
                    event = job.queue.get(timeout=max(0.1, deadline - time.monotonic()))
                except queue.Empty:
                    break
                if event is None:
                    break
                self.wfile.write(canonical_json_bytes(event) + b"\n")
                self.wfile.flush()
                if event.get("kind") in TERMINAL:
                    break
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            job.request_cancel()


def warm() -> None:
    _log("Loading the Chatterbox Turbo model (verifies pinned files first; can take a few minutes)...")
    conn = http.client.HTTPConnection("127.0.0.1", PORT, timeout=WARMUP_SECONDS + 30)
    try:
        conn.request("POST", "/warmup", body=b"{}", headers={"Content-Type": "application/json"})
        response = conn.getresponse()
        status = json.loads(response.read() or b"{}")
    finally:
        conn.close()
    if not status.get("ready"):
        raise SystemExit(f"The Chatterbox worker is not ready ({status.get('state')}): {status.get('error') or 'no detail'}")
    _log("The Chatterbox Turbo voice model is loaded and ready.")


def serve() -> None:
    server = http.server.ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    server.daemon_threads = True
    if CONFIG.is_file():
        WORKER.start_loading()
    _log(f"Martlet Chatterbox host service listening on 127.0.0.1:{PORT}")
    server.serve_forever()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="martlet-chatterbox")
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
