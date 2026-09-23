from __future__ import annotations

import base64
import hashlib
import platform
import struct
import tempfile
import threading
import time
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Callable

from martlet_f5_worker.contract import (
    ARTIFACT_ROLES,
    CONTRACT_ID,
    PROTOCOL_VERSION,
    WorkerIdentity,
)
from martlet_f5_worker.identity import (
    FIXTURE_BUILD_ID,
    FIXTURE_SOURCE_REVISION,
    FIXTURE_VERSION,
    WorkerConfig,
    compute_worker_build_revision,
    config_wire,
)
from martlet_f5_worker.jsonio import canonical_json_bytes


class OutputCollector:
    def __init__(self) -> None:
        self.messages: list[dict[str, Any]] = []
        self.condition = threading.Condition()

    def emit(self, message: dict[str, Any]) -> None:
        with self.condition:
            self.messages.append(message)
            self.condition.notify_all()

    def wait(
        self,
        predicate: Callable[[dict[str, Any]], bool],
        timeout: float = 3.0,
    ) -> dict[str, Any]:
        deadline = time.monotonic() + timeout
        with self.condition:
            while True:
                for message in self.messages:
                    if predicate(message):
                        return message
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise AssertionError(f"Timed out waiting for output: {self.messages!r}")
                self.condition.wait(remaining)


class WorkerFixture:
    def __init__(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="martlet-f5-tests-")
        self.root = Path(self.temp.name)
        self.artifact_root = self.root / "artifacts"
        self.artifact_root.mkdir()
        self.artifact_files: dict[str, str] = {}
        self.artifact_bytes: dict[str, bytes] = {}
        artifacts: list[dict[str, Any]] = []
        for index, role in enumerate(ARTIFACT_ROLES, 1):
            relative = f"{role}.fixture"
            content = (f"{role}-fixture-{index}").encode("ascii")
            (self.artifact_root / relative).write_bytes(content)
            self.artifact_files[role] = relative
            self.artifact_bytes[role] = content
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
        self.worker = WorkerIdentity.parse(
            {
                "artifacts": artifacts,
                "cancellation": "discard_only",
                "contract_id": CONTRACT_ID,
                "evidence": "synthetic_fixture",
                "incremental_within_chunk_synthesis": False,
                "pcm_transport_streaming": True,
                "protocol_version": dict(PROTOCOL_VERSION),
                "runtime": {
                    "cuda_runtime_version": FIXTURE_VERSION,
                    "f5_package_version": FIXTURE_VERSION,
                    "f5_source_revision": FIXTURE_SOURCE_REVISION,
                    "python_version": platform.python_version(),
                    "torch_version": FIXTURE_VERSION,
                    "torchaudio_version": FIXTURE_VERSION,
                    "worker_build_id": FIXTURE_BUILD_ID,
                    "worker_build_revision": compute_worker_build_revision(),
                },
                "worker_id": "martlet-f5-deterministic-fixture",
            }
        )
        self.config_path = self.root / "worker-config.json"
        self.write_config()
        self.config = WorkerConfig.load(self.config_path)

    def write_config(self) -> None:
        self.config_path.write_bytes(
            config_wire(
                engine="fake",
                artifact_root=self.artifact_root,
                artifact_files=self.artifact_files,
                worker=self.worker,
                device=None,
            )
        )

    def close(self) -> None:
        self.temp.cleanup()


def ids() -> dict[str, Any]:
    return {
        "parent_request_id": None,
        "request_id": str(uuid.uuid4()),
        "session_id": str(uuid.uuid4()),
        "turn_id": str(uuid.uuid4()),
    }


def wav_bytes(
    *,
    sample_rate: int = 24_000,
    duration_milliseconds: int = 1_000,
    seed: int = 1,
) -> bytes:
    sample_count = sample_rate * duration_milliseconds // 1_000
    data_bytes = sample_count * 2
    output = bytearray(44 + data_bytes)
    output[0:4] = b"RIFF"
    struct.pack_into("<I", output, 4, len(output) - 8)
    output[8:12] = b"WAVE"
    output[12:16] = b"fmt "
    struct.pack_into("<I", output, 16, 16)
    struct.pack_into(
        "<HHIIHH",
        output,
        20,
        1,
        1,
        sample_rate,
        sample_rate * 2,
        2,
        16,
    )
    output[36:40] = b"data"
    struct.pack_into("<I", output, 40, data_bytes)
    for index in range(sample_count):
        struct.pack_into("<h", output, 44 + index * 2, ((index + seed) % 127 + 1) * 64)
    return bytes(output)


def reference(
    *,
    audio: bytes | None = None,
    transcript: str = "A rights-cleared fixture reference.",
) -> dict[str, Any]:
    audio = audio or wav_bytes()
    audio_sha256 = hashlib.sha256(audio).hexdigest()
    transcript_revision = hashlib.sha256(transcript.encode("utf-8")).hexdigest()
    reference_revision = hashlib.sha256(
        bytes.fromhex(audio_sha256) + bytes.fromhex(transcript_revision)
    ).hexdigest()
    sample_rate = struct.unpack_from("<I", audio, 24)[0]
    data_bytes = struct.unpack_from("<I", audio, 40)[0]
    sample_count = data_bytes // 2
    return {
        "audio_base64": base64.b64encode(audio).decode("ascii"),
        "audio_sha256": audio_sha256,
        "preset_id": str(uuid.uuid4()),
        "reference_revision": reference_revision,
        "source_format": {
            "bits_per_sample": 16,
            "channels": 1,
            "data_bytes": data_bytes,
            "duration_milliseconds": (
                sample_count * 1_000 + sample_rate - 1
            )
            // sample_rate,
            "sample_count": sample_count,
            "sample_rate": sample_rate,
        },
        "transcript": transcript,
        "transcript_revision": transcript_revision,
    }


def synthesize_message(
    worker: WorkerIdentity,
    *,
    deadline_seconds: float = 2,
    chunks: list[dict[str, Any]] | None = None,
    reference_value: dict[str, Any] | None = None,
    request_ids: dict[str, Any] | None = None,
    action_id: str | None = None,
) -> dict[str, Any]:
    return {
        "action_id": action_id or str(uuid.uuid4()),
        "chunks": chunks
        or [{"chunk_id": "chunk-0", "index": 0, "text": "Fixture sentence."}],
        "contract_id": CONTRACT_ID,
        "deadline_utc": (
            datetime.now(timezone.utc) + timedelta(seconds=deadline_seconds)
        )
        .isoformat(timespec="microseconds")
        .replace("+00:00", "Z"),
        "destination_id": "fixture-local-gateway",
        "expected_worker": worker.wire(),
        "ids": request_ids or ids(),
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
        "reference": reference_value or reference(),
        "type": "synthesize",
    }


def warmup_message(worker: WorkerIdentity, *, seconds: float = 2) -> dict[str, Any]:
    return {
        "contract_id": CONTRACT_ID,
        "control_id": str(uuid.uuid4()),
        "deadline_utc": (
            datetime.now(timezone.utc) + timedelta(seconds=seconds)
        )
        .isoformat(timespec="microseconds")
        .replace("+00:00", "Z"),
        "expected_worker": worker.wire(),
        "protocol_version": dict(PROTOCOL_VERSION),
        "type": "warmup",
    }


def cancel_message(synthesis: dict[str, Any]) -> dict[str, Any]:
    return {
        "action_id": synthesis["action_id"],
        "contract_id": CONTRACT_ID,
        "destination_id": synthesis["destination_id"],
        "ids": synthesis["ids"],
        "protocol_version": dict(PROTOCOL_VERSION),
        "reference_revision": synthesis["reference"]["reference_revision"],
        "type": "cancel",
    }


def canonical_line(message: dict[str, Any]) -> bytes:
    return canonical_json_bytes(message) + b"\n"
