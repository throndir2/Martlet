from __future__ import annotations

import base64
import hashlib
import os
import tempfile
import threading
import time
import uuid
import zlib
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Callable

from martlet_perception_worker.contract import (
    ARTIFACT_ROLES,
    MAX_IMAGE_BYTES,
    ArtifactIdentity,
    ModelIdentity,
    ResourceRequirements,
    RuntimeIdentity,
    WorkerIdentity,
    WorkerLimits,
    format_utc,
)
from martlet_perception_worker.identity import (
    FIXTURE_BUILD_ID,
    FIXTURE_VERSION,
    WorkerConfig,
    compute_python_runtime_revision,
    compute_worker_build_revision,
    config_wire,
)
from martlet_perception_worker.jsonio import canonical_json_bytes


def png(
    width: int = 2,
    height: int = 2,
    *,
    color_type: int = 2,
    filter_type: int = 0,
    interlace: int = 0,
    compressed_suffix: bytes = b"",
    extra_chunk: tuple[bytes, bytes] | None = None,
) -> bytes:
    bytes_per_pixel = 3 if color_type == 2 else 4 if color_type == 6 else 1
    rows = bytearray()
    for row in range(height):
        rows.append(filter_type)
        for column in range(width * bytes_per_pixel):
            rows.append((row * 17 + column * 29) & 0xFF)
    header = (
        width.to_bytes(4, "big")
        + height.to_bytes(4, "big")
        + bytes((8, color_type, 0, 0, interlace))
    )
    chunks = [_chunk(b"IHDR", header)]
    if extra_chunk is not None:
        chunks.append(_chunk(*extra_chunk))
    chunks.extend(
        (
            _chunk(b"IDAT", zlib.compress(bytes(rows)) + compressed_suffix),
            _chunk(b"IEND", b""),
        )
    )
    return b"\x89PNG\r\n\x1a\n" + b"".join(chunks)


def _chunk(kind: bytes, data: bytes) -> bytes:
    return (
        len(data).to_bytes(4, "big")
        + kind
        + data
        + (zlib.crc32(data, zlib.crc32(kind)) & 0xFFFFFFFF).to_bytes(4, "big")
    )


def artifact(role: str, data: bytes) -> ArtifactIdentity:
    return ArtifactIdentity(
        role=role,
        artifact_id=f"fixture-{role}",
        revision=hashlib.sha1(data, usedforsecurity=False).hexdigest(),
        sha256=hashlib.sha256(data).hexdigest(),
        bytes=len(data),
        license_id="fixture-only",
    )


def identity(
    role: str,
    artifacts: tuple[ArtifactIdentity, ...],
    *,
    cpu_units: int = 2,
    gpu_memory_mib: int | None = None,
) -> WorkerIdentity:
    if gpu_memory_mib is None:
        gpu_memory_mib = 0 if role == "ocr" else 1_024
    weights = next(item for item in artifacts if item.role == "model_weights")
    return WorkerIdentity(
        worker_id=f"martlet-fixture-{role.replace('_', '-')}",
        evidence="synthetic_fixture",
        role=role,
        runtime=RuntimeIdentity(
            worker_build_id=FIXTURE_BUILD_ID,
            worker_build_revision=compute_worker_build_revision(),
            runtime_id="cpython",
            runtime_version=__import__("platform").python_version(),
            runtime_revision=compute_python_runtime_revision(),
            operating_system_id="fixture-os",
            operating_system_version=FIXTURE_VERSION,
        ),
        model=ModelIdentity(
            role=role,
            adapter_id=f"martlet-fixture-{role.replace('_', '-')}",
            adapter_version=FIXTURE_VERSION,
            model_id=f"fixture-{role.replace('_', '-')}",
            model_revision=weights.revision,
            model_sha256=weights.sha256,
            license_id="fixture-only",
        ),
        artifacts=artifacts,
        limits=WorkerLimits(
            maximum_input_bytes=MAX_IMAGE_BYTES,
            maximum_width=4_096,
            maximum_height=4_096,
            maximum_pixels=8_294_400,
            maximum_output_utf8_bytes=16 * 1_024,
            maximum_concurrency=1,
        ),
        resources=ResourceRequirements(
            cpu_units=cpu_units,
            gpu_memory_mib=gpu_memory_mib,
        ),
        cancellation="discard_only",
    )


class FixtureEnvironment:
    def __init__(
        self,
        role: str = "ocr",
        *,
        budget_cpu: int = 4,
        budget_gpu: int = 2_048,
        worker_cpu: int = 2,
        worker_gpu: int | None = None,
    ) -> None:
        self.role = role
        self.owner = tempfile.TemporaryDirectory(prefix="martlet-perception-test-")
        self.root = Path(self.owner.name)
        self.artifact_root = self.root / "artifacts"
        self.artifact_root.mkdir()
        roles = (
            ("runtime_image", "model_weights", "model_configuration")
            if role == "ocr"
            else ARTIFACT_ROLES
        )
        self.artifact_bytes = {
            name: f"fixture-{role}-{name}".encode("ascii") for name in roles
        }
        self.artifact_files: dict[str, str] = {}
        identities = []
        for index, name in enumerate(roles):
            relative = f"{index:02d}-{name}.bin"
            (self.artifact_root / relative).write_bytes(self.artifact_bytes[name])
            self.artifact_files[name] = relative
            identities.append(artifact(name, self.artifact_bytes[name]))
        self.worker = identity(
            role,
            tuple(identities),
            cpu_units=worker_cpu,
            gpu_memory_mib=worker_gpu,
        )
        self.config_path = self.root / "worker-config.json"
        self.config_path.write_bytes(
            config_wire(
                engine="fake",
                host_id="fixture-host",
                artifact_root=self.artifact_root,
                artifact_files=self.artifact_files,
                worker=self.worker,
                device=None,
                resource_budget=ResourceRequirements(
                    cpu_units=budget_cpu,
                    gpu_memory_mib=budget_gpu,
                ),
            )
        )
        self.config = WorkerConfig.load(self.config_path)

    def close(self) -> None:
        self.owner.cleanup()

    def __enter__(self) -> "FixtureEnvironment":
        return self

    def __exit__(self, *_: object) -> None:
        self.close()


class RecordingEmitter:
    def __init__(self) -> None:
        self.messages: list[dict[str, Any]] = []
        self.condition = threading.Condition()

    def __call__(self, message: dict[str, Any]) -> None:
        with self.condition:
            self.messages.append(message)
            self.condition.notify_all()

    def wait_for(
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
                    raise AssertionError(f"message not observed: {self.messages!r}")
                self.condition.wait(remaining)

    def all(self, message_type: str) -> list[dict[str, Any]]:
        with self.condition:
            return [
                item for item in self.messages if item.get("type") == message_type
            ]


def now() -> datetime:
    return datetime.now(timezone.utc)


def ids_wire() -> dict[str, Any]:
    return {
        "parent_request_id": None,
        "request_id": str(uuid.uuid4()),
        "session_id": str(uuid.uuid4()),
        "turn_id": str(uuid.uuid4()),
    }


def strict_policy() -> dict[str, bool]:
    return {
        "arbitrary_endpoint_allowed": False,
        "arbitrary_path_allowed": False,
        "artifact_download_allowed": False,
        "model_fallback_allowed": False,
        "persistent_image_allowed": False,
        "provider_fallback_allowed": False,
        "redirect_allowed": False,
        "screen_wide_fallback_allowed": False,
        "selected_window_only": True,
    }


def request_wire(
    environment: FixtureEnvironment,
    *,
    image: bytes | None = None,
    deadline_seconds: float = 2.0,
    captured_seconds_ago: float = 0.1,
    expected_worker: WorkerIdentity | None = None,
    task_role: str | None = None,
    question: str | None = None,
    reference: dict[str, Any] | None = None,
) -> dict[str, Any]:
    image = png() if image is None else image
    current = now()
    role = environment.role if task_role is None else task_role
    worker = environment.worker if expected_worker is None else expected_worker
    frame_id = str(uuid.uuid4())
    if reference is None:
        content = {
            "byte_count": len(image),
            "data_base64": base64.b64encode(image).decode("ascii"),
            "height": 2,
            "kind": "inline_png",
            "sha256": hashlib.sha256(image).hexdigest(),
            "width": 2,
        }
    else:
        content = {
            "byte_count": reference["byte_count"],
            "height": reference["height"],
            "kind": "ephemeral_gateway_reference",
            "reference_expires_at_utc": reference["expires_at_utc"],
            "reference_id": reference["reference_id"],
            "sha256": reference["sha256"],
            "width": reference["width"],
        }
    return {
        "action_id": str(uuid.uuid4()),
        "contract_id": "martlet.perception.worker",
        "deadline_utc": format_utc(
            current + timedelta(seconds=deadline_seconds)
        ),
        "destination_id": "fixture-destination",
        "epoch": 1,
        "expected_worker": worker.wire(),
        "frame": {
            "capture_epoch": 1,
            "captured_at_utc": format_utc(
                current - timedelta(seconds=captured_seconds_ago)
            ),
            "content": content,
            "frame_id": frame_id,
            "scope": "selected_window",
            "source": {
                "capture_permission_revision": "b" * 64,
                "selection_id": str(uuid.uuid4()),
                "source_revision": "a" * 64,
            },
        },
        "ids": ids_wire(),
        "maximum_frame_age_milliseconds": 5_000,
        "policy": strict_policy(),
        "protocol_version": {"major": 1, "minor": 0},
        "task": {
            "question": (
                None
                if role == "ocr"
                else "What is visible?" if question is None else question
            ),
            "role": role,
        },
        "type": "execute",
    }


def warmup_wire(
    environment: FixtureEnvironment,
    *,
    deadline_seconds: float = 2.0,
) -> dict[str, Any]:
    return {
        "contract_id": "martlet.perception.worker",
        "control_id": str(uuid.uuid4()),
        "deadline_utc": format_utc(now() + timedelta(seconds=deadline_seconds)),
        "expected_worker": environment.worker.wire(),
        "protocol_version": {"major": 1, "minor": 0},
        "type": "warmup",
    }


def status_wire() -> dict[str, Any]:
    return {
        "contract_id": "martlet.perception.worker",
        "control_id": str(uuid.uuid4()),
        "protocol_version": {"major": 1, "minor": 0},
        "type": "status",
    }


def cancel_wire(request: dict[str, Any]) -> dict[str, Any]:
    return {
        "action_id": request["action_id"],
        "contract_id": "martlet.perception.worker",
        "epoch": request["epoch"],
        "ids": request["ids"],
        "protocol_version": {"major": 1, "minor": 0},
        "type": "cancel",
    }


def offer_wire(
    image: bytes | None = None,
    *,
    expires_seconds: float = 2.0,
    reference_id: str = "frame-ref-001",
) -> dict[str, Any]:
    image = png() if image is None else image
    return {
        "byte_count": len(image),
        "contract_id": "martlet.perception.worker",
        "control_id": str(uuid.uuid4()),
        "data_base64": base64.b64encode(image).decode("ascii"),
        "expires_at_utc": format_utc(
            now() + timedelta(seconds=expires_seconds)
        ),
        "height": 2,
        "protocol_version": {"major": 1, "minor": 0},
        "reference_id": reference_id,
        "sha256": hashlib.sha256(image).hexdigest(),
        "type": "offer_frame",
        "width": 2,
    }


def reference_from_offer(offer: dict[str, Any]) -> dict[str, Any]:
    return {
        "byte_count": offer["byte_count"],
        "expires_at_utc": offer["expires_at_utc"],
        "height": offer["height"],
        "reference_id": offer["reference_id"],
        "sha256": offer["sha256"],
        "width": offer["width"],
    }


def warm_service(service: Any, emitter: RecordingEmitter, environment: FixtureEnvironment) -> None:
    service.handle(warmup_wire(environment))
    emitter.wait_for(
        lambda item: item.get("type") == "worker_state"
        and item.get("state") == "ready"
    )


def canonical_line(value: dict[str, Any]) -> bytes:
    return canonical_json_bytes(value) + b"\n"
