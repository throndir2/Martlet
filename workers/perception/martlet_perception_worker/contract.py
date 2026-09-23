from __future__ import annotations

import base64
import binascii
import calendar
import functools
import hashlib
import hmac
import math
import re
import uuid
import zlib
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from typing import Any, Iterable, Mapping, NoReturn

from .jsonio import canonical_json_bytes

CONTRACT_ID = "martlet.perception.worker"
PROTOCOL_VERSION = {"major": 1, "minor": 0}
GATEWAY_REGISTRY_VERSION = "1.0"

MAX_IMAGE_BYTES = 4 * 1_024 * 1_024
MAX_IMAGE_WIDTH = 4_096
MAX_IMAGE_HEIGHT = 4_096
MAX_IMAGE_PIXELS = 8_294_400
MAX_QUESTION_CHARACTERS = 512
MAX_QUESTION_UTF8_BYTES = 1_024
MAX_OCR_REGIONS = 128
MAX_REGION_TEXT_CHARACTERS = 1_024
MAX_REGION_TEXT_UTF8_BYTES = 2_048
MAX_OUTPUT_UTF8_BYTES = 16 * 1_024
MAX_JOB_SECONDS = 15
MAX_FRAME_AGE_SECONDS = 30
MAX_CLOCK_SKEW_SECONDS = 2
MAX_CANCEL_SECONDS = 0.5
MAX_WARMUP_SECONDS = 300
MAX_ARTIFACT_BYTES = 16 * 1_024**4
MAX_OWNED_FRAMES = 4
MAX_OWNED_FRAME_BYTES = 8 * 1_024 * 1_024
MAX_ROUTE_EPOCH = 2_147_483_646

ROLES = ("ocr", "visual_question_answering")
EVIDENCE_KINDS = ("live_worker", "synthetic_fixture")
CANCELLATION_CAPABILITIES = (
    "discard_only",
    "request_abort",
    "cooperative_compute_cancel",
)
ARTIFACT_ROLES = (
    "runtime_image",
    "model_weights",
    "model_configuration",
    "tokenizer",
    "projector",
)
WORKER_ERROR_CODES = (
    "invalid_request",
    "invalid_image",
    "model_not_ready",
    "resource_exhausted",
    "deadline_exceeded",
    "canceled",
    "internal_failure",
)
PROTOCOL_ERROR_CODES = (
    *WORKER_ERROR_CODES,
    "unsupported_version",
    "identity_mismatch",
    "role_mismatch",
    "busy",
)

_IDENTIFIER = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]*$")
_LOWER_SHA1 = re.compile(r"^[0-9a-f]{40}$")
_LOWER_SHA256 = re.compile(r"^[0-9a-f]{64}$")
_EXACT_VERSION = re.compile(r"^[A-Za-z0-9.+-]{1,64}$")
_UTC_TIMESTAMP = re.compile(
    r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}\+00:00$"
)


class ContractError(Exception):
    def __init__(
        self,
        code: str,
        summary: str,
        *,
        stage: str = "request_validation",
        remedy_code: str = "perception.correct-request",
        retryable: bool = False,
    ) -> None:
        super().__init__(summary)
        if code not in PROTOCOL_ERROR_CODES:
            raise ValueError("unsupported contract error code")
        self.code = code
        self.summary = summary
        self.stage = stage
        self.remedy_code = remedy_code
        self.retryable = retryable


def fail(
    code: str,
    summary: str,
    *,
    stage: str = "request_validation",
    remedy_code: str = "perception.correct-request",
    retryable: bool = False,
) -> NoReturn:
    raise ContractError(
        code,
        summary,
        stage=stage,
        remedy_code=remedy_code,
        retryable=retryable,
    )


def require(condition: bool, code: str, summary: str) -> None:
    if not condition:
        fail(code, summary)


def require_object(value: Any, name: str) -> dict[str, Any]:
    require(type(value) is dict, "invalid_request", f"{name} must be an object.")
    return value


def require_array(value: Any, name: str) -> list[Any]:
    require(type(value) is list, "invalid_request", f"{name} must be an array.")
    return value


def require_exact_keys(
    value: Mapping[str, Any],
    required: Iterable[str],
    name: str,
    optional: Iterable[str] = (),
) -> None:
    required_keys = frozenset(required)
    optional_keys = frozenset(optional)
    actual = frozenset(value)
    require(
        required_keys <= actual and actual <= required_keys | optional_keys,
        "invalid_request",
        f"{name} has missing or unknown properties.",
    )


def require_string(value: Any, name: str) -> str:
    require(type(value) is str, "invalid_request", f"{name} must be a string.")
    try:
        value.encode("utf-8", "strict")
    except UnicodeEncodeError:
        fail("invalid_request", f"{name} must contain valid Unicode.")
    return value


def require_bool(value: Any, name: str) -> bool:
    require(type(value) is bool, "invalid_request", f"{name} must be a boolean.")
    return value


def require_int(value: Any, name: str) -> int:
    require(
        type(value) is int,
        "invalid_request",
        f"{name} must be an integer.",
    )
    return value


def validate_identifier(value: Any, name: str, maximum: int = 64) -> str:
    text = require_string(value, name)
    require(
        0 < len(text) <= maximum and _IDENTIFIER.fullmatch(text) is not None,
        "invalid_request",
        f"{name} is not a valid identifier.",
    )
    return text


def validate_sha1(value: Any, name: str) -> str:
    text = require_string(value, name)
    require(
        _LOWER_SHA1.fullmatch(text) is not None,
        "invalid_request",
        f"{name} must be a lowercase 40-hex revision.",
    )
    return text


def validate_sha256(value: Any, name: str) -> str:
    text = require_string(value, name)
    require(
        _LOWER_SHA256.fullmatch(text) is not None,
        "invalid_request",
        f"{name} must be a lowercase SHA-256 digest.",
    )
    return text


def validate_exact_version(value: Any, name: str) -> str:
    text = require_string(value, name)
    require(
        _EXACT_VERSION.fullmatch(text) is not None
        and text.lower()
        not in {"main", "latest", "unknown", "unpinned", "floating"},
        "invalid_request",
        f"{name} must be an exact version.",
    )
    return text


def utf16_length(value: str) -> int:
    return len(value.encode("utf-16-le", "strict")) // 2


def validate_text(
    value: Any,
    name: str,
    maximum_characters: int,
    maximum_utf8_bytes: int,
    *,
    allow_newlines: bool = True,
) -> str:
    text = require_string(value, name)
    encoded = text.encode("utf-8", "strict")
    require(
        text.strip() != ""
        and "\x00" not in text
        and utf16_length(text) <= maximum_characters
        and len(encoded) <= maximum_utf8_bytes,
        "invalid_request",
        f"{name} is blank or exceeds its bound.",
    )
    for character in text:
        if ord(character) < 32 or 127 <= ord(character) <= 159:
            allowed = allow_newlines and character in "\r\n\t"
            require(
                allowed,
                "invalid_request",
                f"{name} contains a control character.",
            )
    return text


def validate_uuid(value: Any, name: str, *, nullable: bool = False) -> str | None:
    if nullable and value is None:
        return None
    text = require_string(value, name)
    try:
        parsed = uuid.UUID(text)
    except (ValueError, AttributeError):
        fail("invalid_request", f"{name} must be a UUID.")
    require(
        parsed.int != 0 and str(parsed) == text,
        "invalid_request",
        f"{name} must be a canonical nonzero UUID.",
    )
    return text


@functools.total_ordering
@dataclass(frozen=True)
class UtcTimestamp:
    ticks: int

    @classmethod
    def from_datetime(cls, value: "datetime | UtcTimestamp") -> "UtcTimestamp":
        if isinstance(value, cls):
            return value
        require(
            value.utcoffset() == timedelta(0),
            "invalid_request",
            "A UTC timestamp was required.",
        )
        normalized = value.astimezone(timezone.utc)
        seconds = calendar.timegm(normalized.utctimetuple())
        return cls(seconds * 10_000_000 + normalized.microsecond * 10)

    def seconds_from(self, other: "UtcTimestamp | datetime") -> float:
        return (self.ticks - self._coerce(other)) / 10_000_000

    def __eq__(self, other: object) -> bool:
        if isinstance(other, UtcTimestamp):
            return self.ticks == other.ticks
        if isinstance(other, datetime):
            return self.ticks == self.from_datetime(other).ticks
        return NotImplemented

    def __lt__(self, other: object) -> bool:
        if isinstance(other, (UtcTimestamp, datetime)):
            return self.ticks < self._coerce(other)
        return NotImplemented

    def __add__(self, value: timedelta) -> "UtcTimestamp":
        if not isinstance(value, timedelta):
            return NotImplemented
        return UtcTimestamp(self.ticks + _timedelta_ticks(value))

    def __sub__(
        self,
        value: "UtcTimestamp | datetime | timedelta",
    ) -> "UtcTimestamp | timedelta":
        if isinstance(value, timedelta):
            return UtcTimestamp(self.ticks - _timedelta_ticks(value))
        if isinstance(value, (UtcTimestamp, datetime)):
            delta = self.ticks - self._coerce(value)
            return timedelta(microseconds=delta / 10)
        return NotImplemented

    def __rsub__(self, value: datetime) -> timedelta:
        if isinstance(value, datetime):
            delta = self.from_datetime(value).ticks - self.ticks
            return timedelta(microseconds=delta / 10)
        return NotImplemented

    @staticmethod
    def _coerce(value: "UtcTimestamp | datetime") -> int:
        return (
            value.ticks
            if isinstance(value, UtcTimestamp)
            else UtcTimestamp.from_datetime(value).ticks
        )


def _timedelta_ticks(value: timedelta) -> int:
    return (
        value.days * 86_400 * 10_000_000
        + value.seconds * 10_000_000
        + value.microseconds * 10
    )


def parse_utc(value: Any, name: str) -> UtcTimestamp:
    text = require_string(value, name)
    require(
        _UTC_TIMESTAMP.fullmatch(text) is not None,
        "invalid_request",
        f"{name} must be canonical UTC using the .NET round-trip shape.",
    )
    try:
        second = datetime.strptime(
            text[:19],
            "%Y-%m-%dT%H:%M:%S",
        ).replace(tzinfo=timezone.utc)
        fraction = int(text[20:27])
    except ValueError:
        fail("invalid_request", f"{name} is not a valid UTC timestamp.")
    return UtcTimestamp(
        calendar.timegm(second.utctimetuple()) * 10_000_000 + fraction
    )


def format_utc(value: datetime | UtcTimestamp) -> str:
    timestamp = (
        value if isinstance(value, UtcTimestamp) else UtcTimestamp.from_datetime(value)
    )
    seconds, fraction = divmod(timestamp.ticks, 10_000_000)
    normalized = datetime.fromtimestamp(seconds, timezone.utc)
    return (
        normalized.strftime("%Y-%m-%dT%H:%M:%S.")
        + f"{fraction:07d}+00:00"
    )


def parse_contract_header(message: Mapping[str, Any]) -> None:
    require(
        message.get("contract_id") == CONTRACT_ID,
        "invalid_request",
        f"contract_id must be {CONTRACT_ID}.",
    )
    version = require_object(message.get("protocol_version"), "protocol_version")
    require_exact_keys(version, ("major", "minor"), "protocol_version")
    major = require_int(version["major"], "protocol_version.major")
    minor = require_int(version["minor"], "protocol_version.minor")
    if major != 1 or minor != 0:
        fail(
            "unsupported_version",
            "Only martlet.perception.worker protocol version 1.0 is supported.",
            remedy_code="perception.use-worker-v1",
        )


@dataclass(frozen=True)
class RequestIds:
    session_id: str
    turn_id: str
    request_id: str
    parent_request_id: str | None

    @classmethod
    def parse(cls, value: Any) -> "RequestIds":
        source = require_object(value, "ids")
        require_exact_keys(
            source,
            ("session_id", "turn_id", "request_id", "parent_request_id"),
            "ids",
        )
        result = cls(
            session_id=validate_uuid(source["session_id"], "ids.session_id") or "",
            turn_id=validate_uuid(source["turn_id"], "ids.turn_id") or "",
            request_id=validate_uuid(source["request_id"], "ids.request_id") or "",
            parent_request_id=validate_uuid(
                source["parent_request_id"],
                "ids.parent_request_id",
                nullable=True,
            ),
        )
        require(
            result.parent_request_id != result.request_id,
            "invalid_request",
            "ids.parent_request_id must not equal ids.request_id.",
        )
        return result

    def wire(self) -> dict[str, Any]:
        return {
            "parent_request_id": self.parent_request_id,
            "request_id": self.request_id,
            "session_id": self.session_id,
            "turn_id": self.turn_id,
        }


@dataclass(frozen=True)
class RuntimeIdentity:
    worker_build_id: str
    worker_build_revision: str
    runtime_id: str
    runtime_version: str
    runtime_revision: str
    operating_system_id: str
    operating_system_version: str

    @classmethod
    def parse(cls, value: Any) -> "RuntimeIdentity":
        source = require_object(value, "runtime")
        fields = (
            "worker_build_id",
            "worker_build_revision",
            "runtime_id",
            "runtime_version",
            "runtime_revision",
            "operating_system_id",
            "operating_system_version",
        )
        require_exact_keys(source, fields, "runtime")
        return cls(
            worker_build_id=validate_identifier(
                source["worker_build_id"],
                "runtime.worker_build_id",
            ),
            worker_build_revision=validate_sha1(
                source["worker_build_revision"],
                "runtime.worker_build_revision",
            ),
            runtime_id=validate_identifier(
                source["runtime_id"],
                "runtime.runtime_id",
            ),
            runtime_version=validate_exact_version(
                source["runtime_version"],
                "runtime.runtime_version",
            ),
            runtime_revision=validate_sha1(
                source["runtime_revision"],
                "runtime.runtime_revision",
            ),
            operating_system_id=validate_identifier(
                source["operating_system_id"],
                "runtime.operating_system_id",
            ),
            operating_system_version=validate_exact_version(
                source["operating_system_version"],
                "runtime.operating_system_version",
            ),
        )

    def wire(self) -> dict[str, Any]:
        return {
            "operating_system_id": self.operating_system_id,
            "operating_system_version": self.operating_system_version,
            "runtime_id": self.runtime_id,
            "runtime_revision": self.runtime_revision,
            "runtime_version": self.runtime_version,
            "worker_build_id": self.worker_build_id,
            "worker_build_revision": self.worker_build_revision,
        }


@dataclass(frozen=True)
class ModelIdentity:
    role: str
    adapter_id: str
    adapter_version: str
    model_id: str
    model_revision: str
    model_sha256: str
    license_id: str

    @classmethod
    def parse(cls, value: Any) -> "ModelIdentity":
        source = require_object(value, "model")
        require_exact_keys(
            source,
            (
                "role",
                "adapter_id",
                "adapter_version",
                "model_id",
                "model_revision",
                "model_sha256",
                "license_id",
            ),
            "model",
        )
        role = require_string(source["role"], "model.role")
        require(role in ROLES, "invalid_request", "model.role is unsupported.")
        return cls(
            role=role,
            adapter_id=validate_identifier(
                source["adapter_id"],
                "model.adapter_id",
            ),
            adapter_version=validate_exact_version(
                source["adapter_version"],
                "model.adapter_version",
            ),
            model_id=validate_identifier(
                source["model_id"],
                "model.model_id",
                128,
            ),
            model_revision=validate_sha1(
                source["model_revision"],
                "model.model_revision",
            ),
            model_sha256=validate_sha256(
                source["model_sha256"],
                "model.model_sha256",
            ),
            license_id=validate_identifier(
                source["license_id"],
                "model.license_id",
            ),
        )

    def wire(self) -> dict[str, Any]:
        return {
            "adapter_id": self.adapter_id,
            "adapter_version": self.adapter_version,
            "license_id": self.license_id,
            "model_id": self.model_id,
            "model_revision": self.model_revision,
            "model_sha256": self.model_sha256,
            "role": self.role,
        }


@dataclass(frozen=True)
class ArtifactIdentity:
    role: str
    artifact_id: str
    revision: str
    sha256: str
    bytes: int
    license_id: str

    @classmethod
    def parse(cls, value: Any, index: int) -> "ArtifactIdentity":
        source = require_object(value, f"artifacts[{index}]")
        require_exact_keys(
            source,
            ("role", "artifact_id", "revision", "sha256", "bytes", "license_id"),
            f"artifacts[{index}]",
        )
        role = require_string(source["role"], f"artifacts[{index}].role")
        require(
            role in ARTIFACT_ROLES,
            "invalid_request",
            f"artifacts[{index}].role is unsupported.",
        )
        byte_count = require_int(source["bytes"], f"artifacts[{index}].bytes")
        require(
            0 < byte_count <= MAX_ARTIFACT_BYTES,
            "invalid_request",
            f"artifacts[{index}].bytes is outside the contract bound.",
        )
        return cls(
            role=role,
            artifact_id=validate_identifier(
                source["artifact_id"],
                f"artifacts[{index}].artifact_id",
            ),
            revision=validate_sha1(
                source["revision"],
                f"artifacts[{index}].revision",
            ),
            sha256=validate_sha256(
                source["sha256"],
                f"artifacts[{index}].sha256",
            ),
            bytes=byte_count,
            license_id=validate_identifier(
                source["license_id"],
                f"artifacts[{index}].license_id",
            ),
        )

    def wire(self) -> dict[str, Any]:
        return {
            "artifact_id": self.artifact_id,
            "bytes": self.bytes,
            "license_id": self.license_id,
            "revision": self.revision,
            "role": self.role,
            "sha256": self.sha256,
        }


@dataclass(frozen=True)
class WorkerLimits:
    maximum_input_bytes: int
    maximum_width: int
    maximum_height: int
    maximum_pixels: int
    maximum_output_utf8_bytes: int
    maximum_concurrency: int

    @classmethod
    def parse(cls, value: Any) -> "WorkerLimits":
        source = require_object(value, "limits")
        fields = (
            "maximum_input_bytes",
            "maximum_width",
            "maximum_height",
            "maximum_pixels",
            "maximum_output_utf8_bytes",
            "maximum_concurrency",
        )
        require_exact_keys(source, fields, "limits")
        result = cls(*(require_int(source[field], f"limits.{field}") for field in fields))
        require(
            0 < result.maximum_input_bytes <= MAX_IMAGE_BYTES
            and 0 < result.maximum_width <= MAX_IMAGE_WIDTH
            and 0 < result.maximum_height <= MAX_IMAGE_HEIGHT
            and 0 < result.maximum_pixels <= MAX_IMAGE_PIXELS
            and 0 < result.maximum_output_utf8_bytes <= MAX_OUTPUT_UTF8_BYTES
            and result.maximum_concurrency == 1,
            "invalid_request",
            "limits exceed the v1 bounds or concurrency is not exactly one.",
        )
        return result

    def wire(self) -> dict[str, Any]:
        return {
            "maximum_concurrency": self.maximum_concurrency,
            "maximum_height": self.maximum_height,
            "maximum_input_bytes": self.maximum_input_bytes,
            "maximum_output_utf8_bytes": self.maximum_output_utf8_bytes,
            "maximum_pixels": self.maximum_pixels,
            "maximum_width": self.maximum_width,
        }


@dataclass(frozen=True)
class ResourceRequirements:
    cpu_units: int
    gpu_memory_mib: int

    @classmethod
    def parse(cls, value: Any, name: str = "resources") -> "ResourceRequirements":
        source = require_object(value, name)
        require_exact_keys(source, ("cpu_units", "gpu_memory_mib"), name)
        result = cls(
            cpu_units=require_int(source["cpu_units"], f"{name}.cpu_units"),
            gpu_memory_mib=require_int(
                source["gpu_memory_mib"],
                f"{name}.gpu_memory_mib",
            ),
        )
        require(
            1 <= result.cpu_units <= 64
            and 0 <= result.gpu_memory_mib <= 1_048_576,
            "invalid_request",
            f"{name} is outside the v1 resource bound.",
        )
        return result

    def wire(self) -> dict[str, Any]:
        return {
            "cpu_units": self.cpu_units,
            "gpu_memory_mib": self.gpu_memory_mib,
        }


@dataclass(frozen=True)
class WorkerIdentity:
    worker_id: str
    evidence: str
    role: str
    runtime: RuntimeIdentity
    model: ModelIdentity
    artifacts: tuple[ArtifactIdentity, ...]
    limits: WorkerLimits
    resources: ResourceRequirements
    cancellation: str

    @classmethod
    def parse(cls, value: Any) -> "WorkerIdentity":
        source = require_object(value, "worker")
        require_exact_keys(
            source,
            (
                "contract_id",
                "protocol_version",
                "worker_id",
                "evidence",
                "role",
                "runtime",
                "model",
                "artifacts",
                "limits",
                "resources",
                "cancellation",
            ),
            "worker",
        )
        parse_contract_header(source)
        evidence = require_string(source["evidence"], "worker.evidence")
        require(
            evidence in EVIDENCE_KINDS,
            "invalid_request",
            "worker.evidence is unsupported.",
        )
        role = require_string(source["role"], "worker.role")
        require(role in ROLES, "invalid_request", "worker.role is unsupported.")
        cancellation = require_string(source["cancellation"], "worker.cancellation")
        require(
            cancellation in CANCELLATION_CAPABILITIES,
            "invalid_request",
            "worker.cancellation is unsupported.",
        )
        model = ModelIdentity.parse(source["model"])
        require(
            model.role == role,
            "role_mismatch",
            "The worker and model roles do not match.",
        )
        artifacts_source = require_array(source["artifacts"], "worker.artifacts")
        require(
            3 <= len(artifacts_source) <= 16,
            "invalid_request",
            "worker.artifacts must contain three through sixteen artifacts.",
        )
        artifacts = tuple(
            sorted(
                (
                    ArtifactIdentity.parse(item, index)
                    for index, item in enumerate(artifacts_source)
                ),
                key=lambda item: (ARTIFACT_ROLES.index(item.role), item.artifact_id),
            )
        )
        require(
            len({item.artifact_id for item in artifacts}) == len(artifacts)
            and sum(item.role == "runtime_image" for item in artifacts) == 1
            and any(item.role == "model_weights" for item in artifacts)
            and sum(item.role == "model_configuration" for item in artifacts) == 1,
            "invalid_request",
            "worker.artifacts do not contain the required unique identities.",
        )
        if role == "visual_question_answering":
            require(
                any(item.role == "tokenizer" for item in artifacts)
                and any(item.role == "projector" for item in artifacts),
                "invalid_request",
                "A VLM identity requires tokenizer and projector artifacts.",
            )
        return cls(
            worker_id=validate_identifier(
                source["worker_id"],
                "worker.worker_id",
                64,
            ),
            evidence=evidence,
            role=role,
            runtime=RuntimeIdentity.parse(source["runtime"]),
            model=model,
            artifacts=artifacts,
            limits=WorkerLimits.parse(source["limits"]),
            resources=ResourceRequirements.parse(source["resources"]),
            cancellation=cancellation,
        )

    def wire(self) -> dict[str, Any]:
        return {
            "artifacts": [item.wire() for item in self.artifacts],
            "cancellation": self.cancellation,
            "contract_id": CONTRACT_ID,
            "evidence": self.evidence,
            "limits": self.limits.wire(),
            "model": self.model.wire(),
            "protocol_version": dict(PROTOCOL_VERSION),
            "resources": self.resources.wire(),
            "role": self.role,
            "runtime": self.runtime.wire(),
            "worker_id": self.worker_id,
        }


def identities_match(left: WorkerIdentity, right: WorkerIdentity) -> bool:
    return hmac.compare_digest(
        canonical_json_bytes(left.wire()),
        canonical_json_bytes(right.wire()),
    )


def artifact_identity_sha256(identity: WorkerIdentity) -> str:
    evidence_names = {
        "live_worker": "LiveWorker",
        "synthetic_fixture": "SyntheticFixture",
    }
    role_names = {
        "ocr": "Ocr",
        "visual_question_answering": "VisualQuestionAnswering",
    }
    artifact_names = {
        "runtime_image": "RuntimeImage",
        "model_weights": "ModelWeights",
        "model_configuration": "ModelConfiguration",
        "tokenizer": "Tokenizer",
        "projector": "Projector",
    }
    cancellation_names = {
        "discard_only": "DiscardOnly",
        "request_abort": "RequestAbort",
        "cooperative_compute_cancel": "CooperativeComputeCancel",
    }
    fields = [
        CONTRACT_ID,
        "1.0",
        identity.worker_id,
        evidence_names[identity.evidence],
        role_names[identity.role],
        identity.runtime.worker_build_id,
        identity.runtime.worker_build_revision,
        identity.runtime.runtime_id,
        identity.runtime.runtime_version,
        identity.runtime.runtime_revision,
        identity.runtime.operating_system_id,
        identity.runtime.operating_system_version,
        identity.model.adapter_id,
        identity.model.adapter_version,
        identity.model.model_id,
        identity.model.model_revision,
        identity.model.model_sha256,
        identity.model.license_id,
        str(identity.limits.maximum_input_bytes),
        str(identity.limits.maximum_width),
        str(identity.limits.maximum_height),
        str(identity.limits.maximum_pixels),
        str(identity.limits.maximum_output_utf8_bytes),
        str(identity.limits.maximum_concurrency),
        str(identity.resources.cpu_units),
        str(identity.resources.gpu_memory_mib),
        cancellation_names[identity.cancellation],
    ]
    for artifact in identity.artifacts:
        fields.extend(
            (
                artifact_names[artifact.role],
                artifact.artifact_id,
                artifact.revision,
                artifact.sha256,
                str(artifact.bytes),
                artifact.license_id,
            )
        )
    return "sha256:" + hashlib.sha256("\n".join(fields).encode("utf-8")).hexdigest()


def inspect_png(data: bytes | bytearray) -> tuple[int, int]:
    compressed = bytearray()
    try:
        return _inspect_png(data, compressed)
    finally:
        compressed[:] = b"\x00" * len(compressed)


def _inspect_png(data: bytes | bytearray, compressed: bytearray) -> tuple[int, int]:
    if len(data) > MAX_IMAGE_BYTES:
        fail("invalid_image", "The PNG exceeds the maximum byte count.")
    require(
        len(data) >= 57 and data[:8] == b"\x89PNG\r\n\x1a\n",
        "invalid_image",
        "The frame is not a supported PNG.",
    )
    offset = 8
    chunk_count = 0
    saw_header = False
    saw_data = False
    data_ended = False
    saw_end = False
    width = 0
    height = 0
    bytes_per_pixel = 0
    while offset < len(data):
        chunk_count += 1
        require(
            chunk_count <= 256 and len(data) - offset >= 12,
            "invalid_image",
            "The PNG chunk structure is invalid.",
        )
        length = int.from_bytes(data[offset : offset + 4], "big")
        offset += 4
        require(
            len(data) - offset >= length + 8,
            "invalid_image",
            "The PNG chunk is truncated.",
        )
        chunk_type = bytes(data[offset : offset + 4])
        offset += 4
        payload = bytes(data[offset : offset + length])
        offset += length
        expected_crc = int.from_bytes(data[offset : offset + 4], "big")
        offset += 4
        actual_crc = zlib.crc32(payload, zlib.crc32(chunk_type)) & 0xFFFFFFFF
        require(
            actual_crc == expected_crc,
            "invalid_image",
            "The PNG chunk checksum is invalid.",
        )
        if chunk_type == b"IHDR":
            require(
                not saw_header and chunk_count == 1 and length == 13,
                "invalid_image",
                "The PNG header is invalid.",
            )
            width = int.from_bytes(payload[:4], "big")
            height = int.from_bytes(payload[4:8], "big")
            bit_depth = payload[8]
            color_type = payload[9]
            require(
                bit_depth == 8
                and color_type in (2, 6)
                and payload[10:] == b"\x00\x00\x00",
                "invalid_image",
                "Only non-interlaced 8-bit RGB or RGBA PNG is supported.",
            )
            _validate_dimensions(1, width, height)
            bytes_per_pixel = 3 if color_type == 2 else 4
            saw_header = True
        elif chunk_type == b"IDAT":
            require(
                saw_header and not data_ended and not saw_end,
                "invalid_image",
                "PNG data chunks must be contiguous.",
            )
            saw_data = True
            compressed.extend(payload)
        elif chunk_type == b"IEND":
            require(
                saw_header and saw_data and not saw_end and length == 0,
                "invalid_image",
                "The PNG end chunk is invalid.",
            )
            data_ended = True
            saw_end = True
            require(
                offset == len(data),
                "invalid_image",
                "The PNG has trailing bytes.",
            )
            break
        else:
            fail("invalid_image", "The PNG contains an unsupported chunk.")
    require(
        saw_header and saw_data and saw_end and compressed,
        "invalid_image",
        "The PNG is incomplete.",
    )
    try:
        decompressor = zlib.decompressobj()
        row_bytes = width * bytes_per_pixel + 1
        pending = compressed
        for _ in range(height):
            row = bytearray(decompressor.decompress(pending, row_bytes))
            try:
                require(
                    len(row) == row_bytes and row[0] <= 4,
                    "invalid_image",
                    "The PNG scanline size or filter is invalid.",
                )
            finally:
                row[:] = b"\x00" * len(row)
            pending = decompressor.unconsumed_tail
        extra = bytearray(decompressor.decompress(pending, 1))
        try:
            require(
                not extra
                and decompressor.eof
                and not decompressor.unused_data
                and not decompressor.unconsumed_tail,
                "invalid_image",
                "The PNG image data has the wrong bounded size.",
            )
        finally:
            extra[:] = b"\x00" * len(extra)
    except zlib.error:
        fail("invalid_image", "The PNG image data is invalid.")
    return width, height


def _validate_dimensions(byte_count: int, width: int, height: int) -> None:
    require(
        0 < byte_count <= MAX_IMAGE_BYTES
        and 0 < width <= MAX_IMAGE_WIDTH
        and 0 < height <= MAX_IMAGE_HEIGHT
        and width * height <= MAX_IMAGE_PIXELS,
        "invalid_image",
        "The frame dimensions or byte count exceed the v1 bound.",
    )


def _decode_base64(value: Any, name: str, maximum_bytes: int) -> bytearray:
    text = require_string(value, name)
    require(
        0 < len(text) <= ((maximum_bytes + 2) // 3) * 4,
        "invalid_image",
        f"{name} exceeds its bound.",
    )
    try:
        decoded = base64.b64decode(text, validate=True)
    except (binascii.Error, ValueError):
        fail("invalid_image", f"{name} is not canonical base64.")
    require(
        base64.b64encode(decoded).decode("ascii") == text
        and 0 < len(decoded) <= maximum_bytes,
        "invalid_image",
        f"{name} is not canonical bounded base64.",
    )
    return bytearray(decoded)


@dataclass
class FrameContent:
    kind: str
    sha256: str
    byte_count: int
    width: int
    height: int
    data: bytearray | None = None
    reference_id: str | None = None
    reference_expires_at_utc: UtcTimestamp | None = None

    @classmethod
    def parse(cls, value: Any) -> "FrameContent":
        source = require_object(value, "frame.content")
        kind = require_string(source.get("kind"), "frame.content.kind")
        if kind == "inline_png":
            require_exact_keys(
                source,
                ("kind", "data_base64", "sha256", "byte_count", "width", "height"),
                "frame.content",
            )
        elif kind == "ephemeral_gateway_reference":
            require_exact_keys(
                source,
                (
                    "kind",
                    "reference_id",
                    "sha256",
                    "byte_count",
                    "width",
                    "height",
                    "reference_expires_at_utc",
                ),
                "frame.content",
            )
        else:
            fail("invalid_image", "frame.content.kind is unsupported.")
        digest = validate_sha256(source["sha256"], "frame.content.sha256")
        byte_count = require_int(source["byte_count"], "frame.content.byte_count")
        width = require_int(source["width"], "frame.content.width")
        height = require_int(source["height"], "frame.content.height")
        _validate_dimensions(byte_count, width, height)
        if kind == "inline_png":
            data = _decode_base64(
                source["data_base64"],
                "frame.content.data_base64",
                MAX_IMAGE_BYTES,
            )
            try:
                observed_width, observed_height = inspect_png(data)
                require(
                    len(data) == byte_count
                    and hashlib.sha256(data).hexdigest() == digest
                    and observed_width == width
                    and observed_height == height,
                    "invalid_image",
                    "The PNG bytes do not match their declared identity.",
                )
            except Exception:
                data[:] = b"\x00" * len(data)
                raise
            return cls(kind, digest, byte_count, width, height, data=data)
        reference_id = validate_identifier(
            source["reference_id"],
            "frame.content.reference_id",
            96,
        )
        expiry = parse_utc(
            source["reference_expires_at_utc"],
            "frame.content.reference_expires_at_utc",
        )
        return cls(
            kind,
            digest,
            byte_count,
            width,
            height,
            reference_id=reference_id,
            reference_expires_at_utc=expiry,
        )

    def attach_owned_data(self, data: bytearray) -> None:
        require(
            self.kind == "ephemeral_gateway_reference" and self.data is None,
            "invalid_image",
            "The frame reference is not available for ownership transfer.",
        )
        self.data = data

    def wipe(self) -> None:
        if self.data is not None:
            self.data[:] = b"\x00" * len(self.data)
            self.data = None


@dataclass(frozen=True)
class FrameSource:
    selection_id: str
    source_revision: str
    capture_permission_revision: str

    @classmethod
    def parse(cls, value: Any) -> "FrameSource":
        source = require_object(value, "frame.source")
        require_exact_keys(
            source,
            ("selection_id", "source_revision", "capture_permission_revision"),
            "frame.source",
        )
        return cls(
            selection_id=validate_uuid(
                source["selection_id"],
                "frame.source.selection_id",
            )
            or "",
            source_revision=validate_sha256(
                source["source_revision"],
                "frame.source.source_revision",
            ),
            capture_permission_revision=validate_sha256(
                source["capture_permission_revision"],
                "frame.source.capture_permission_revision",
            ),
        )


@dataclass
class SelectedWindowFrame:
    frame_id: str
    capture_epoch: int
    source: FrameSource
    captured_at_utc: UtcTimestamp
    content: FrameContent

    @classmethod
    def parse(cls, value: Any) -> "SelectedWindowFrame":
        source = require_object(value, "frame")
        require_exact_keys(
            source,
            (
                "frame_id",
                "capture_epoch",
                "scope",
                "source",
                "captured_at_utc",
                "content",
            ),
            "frame",
        )
        require(
            source["scope"] == "selected_window",
            "invalid_request",
            "Only selected_window capture scope is supported.",
        )
        capture_epoch = require_int(source["capture_epoch"], "frame.capture_epoch")
        require(
            0 < capture_epoch <= MAX_ROUTE_EPOCH,
            "invalid_request",
            "frame.capture_epoch is outside the integrated H03b route bound.",
        )
        return cls(
            frame_id=validate_uuid(source["frame_id"], "frame.frame_id") or "",
            capture_epoch=capture_epoch,
            source=FrameSource.parse(source["source"]),
            captured_at_utc=parse_utc(
                source["captured_at_utc"],
                "frame.captured_at_utc",
            ),
            content=FrameContent.parse(source["content"]),
        )

    def wipe(self) -> None:
        self.content.wipe()


@dataclass(frozen=True)
class PerceptionTask:
    role: str
    question: str | None

    @classmethod
    def parse(cls, value: Any) -> "PerceptionTask":
        source = require_object(value, "task")
        require_exact_keys(source, ("role", "question"), "task")
        role = require_string(source["role"], "task.role")
        require(role in ROLES, "role_mismatch", "task.role is unsupported.")
        question = source["question"]
        if role == "ocr":
            require(
                question is None,
                "role_mismatch",
                "OCR requests must not contain a question.",
            )
            return cls(role, None)
        return cls(
            role,
            validate_text(
                question,
                "task.question",
                MAX_QUESTION_CHARACTERS,
                MAX_QUESTION_UTF8_BYTES,
                allow_newlines=False,
            ),
        )


def validate_policy(value: Any) -> None:
    source = require_object(value, "policy")
    expected = {
        "selected_window_only": True,
        "screen_wide_fallback_allowed": False,
        "artifact_download_allowed": False,
        "model_fallback_allowed": False,
        "provider_fallback_allowed": False,
        "redirect_allowed": False,
        "persistent_image_allowed": False,
        "arbitrary_endpoint_allowed": False,
        "arbitrary_path_allowed": False,
    }
    require_exact_keys(source, expected, "policy")
    require(
        all(
            require_bool(source[key], f"policy.{key}") is expected[key]
            for key in expected
        ),
        "invalid_request",
        "Only the strict selected-window offline policy is supported.",
    )


@dataclass
class PerceptionRequest:
    action_id: str
    ids: RequestIds
    destination_id: str
    expected_worker: WorkerIdentity
    frame: SelectedWindowFrame
    task: PerceptionTask
    epoch: int
    deadline_utc: UtcTimestamp
    maximum_frame_age: timedelta

    @classmethod
    def parse(
        cls,
        message: Mapping[str, Any],
        *,
        now: datetime,
    ) -> "PerceptionRequest":
        require_exact_keys(
            message,
            (
                "contract_id",
                "protocol_version",
                "type",
                "action_id",
                "ids",
                "destination_id",
                "expected_worker",
                "frame",
                "task",
                "epoch",
                "deadline_utc",
                "maximum_frame_age_milliseconds",
                "policy",
            ),
            "execute",
        )
        parse_contract_header(message)
        require(message["type"] == "execute", "invalid_request", "Invalid message type.")
        validate_policy(message["policy"])
        deadline = parse_utc(message["deadline_utc"], "deadline_utc")
        remaining = deadline.seconds_from(now)
        require(
            0 < remaining <= MAX_JOB_SECONDS,
            "deadline_exceeded",
            "The job deadline is expired or exceeds fifteen seconds.",
        )
        frame_age_ms = require_int(
            message["maximum_frame_age_milliseconds"],
            "maximum_frame_age_milliseconds",
        )
        require(
            0 < frame_age_ms <= MAX_FRAME_AGE_SECONDS * 1_000,
            "invalid_request",
            "maximum_frame_age_milliseconds exceeds the v1 bound.",
        )
        frame = SelectedWindowFrame.parse(message["frame"])
        try:
            task = PerceptionTask.parse(message["task"])
            worker = WorkerIdentity.parse(message["expected_worker"])
            epoch = require_int(message["epoch"], "epoch")
            require(
                epoch == frame.capture_epoch,
                "invalid_request",
                "The request epoch does not match the frame capture epoch.",
            )
            require(
                worker.role == task.role,
                "role_mismatch",
                "The requested role does not match the selected worker.",
            )
            require(
                frame.content.byte_count <= worker.limits.maximum_input_bytes
                and frame.content.width <= worker.limits.maximum_width
                and frame.content.height <= worker.limits.maximum_height
                and frame.content.width * frame.content.height
                <= worker.limits.maximum_pixels,
                "invalid_image",
                "The frame exceeds the selected worker limits.",
            )
            maximum_frame_age = timedelta(milliseconds=frame_age_ms)
            now_utc = UtcTimestamp.from_datetime(now)
            require(
                frame.captured_at_utc
                <= now_utc + timedelta(seconds=MAX_CLOCK_SKEW_SECONDS)
                and now_utc.ticks - frame.captured_at_utc.ticks
                < _timedelta_ticks(maximum_frame_age),
                "invalid_image",
                "The selected-window frame is stale or from the future.",
            )
            if frame.content.reference_expires_at_utc is not None:
                require(
                    frame.content.reference_expires_at_utc > now_utc,
                    "invalid_image",
                    "The ephemeral frame reference has expired.",
                )
            return cls(
                action_id=validate_uuid(message["action_id"], "action_id") or "",
                ids=RequestIds.parse(message["ids"]),
                destination_id=validate_identifier(
                    message["destination_id"],
                    "destination_id",
                    128,
                ),
                expected_worker=worker,
                frame=frame,
                task=task,
                epoch=epoch,
                deadline_utc=deadline,
                maximum_frame_age=maximum_frame_age,
            )
        except Exception:
            frame.wipe()
            raise

    def wipe(self) -> None:
        self.frame.wipe()
        self.task = PerceptionTask(self.task.role, None)


@dataclass
class OfferedFrame:
    control_id: str
    reference_id: str
    sha256: str
    byte_count: int
    width: int
    height: int
    expires_at_utc: UtcTimestamp
    data: bytearray

    @classmethod
    def parse(cls, message: Mapping[str, Any], *, now: datetime) -> "OfferedFrame":
        require_exact_keys(
            message,
            (
                "contract_id",
                "protocol_version",
                "type",
                "control_id",
                "reference_id",
                "sha256",
                "byte_count",
                "width",
                "height",
                "expires_at_utc",
                "data_base64",
            ),
            "offer_frame",
        )
        parse_contract_header(message)
        require(
            message["type"] == "offer_frame",
            "invalid_request",
            "Invalid message type.",
        )
        data = _decode_base64(
            message["data_base64"],
            "data_base64",
            MAX_IMAGE_BYTES,
        )
        try:
            width = require_int(message["width"], "width")
            height = require_int(message["height"], "height")
            byte_count = require_int(message["byte_count"], "byte_count")
            _validate_dimensions(byte_count, width, height)
            observed_width, observed_height = inspect_png(data)
            digest = validate_sha256(message["sha256"], "sha256")
            expiry = parse_utc(message["expires_at_utc"], "expires_at_utc")
            require(
                len(data) == byte_count
                and hashlib.sha256(data).hexdigest() == digest
                and observed_width == width
                and observed_height == height,
                "invalid_image",
                "The offered PNG does not match its declared identity.",
            )
            require(
                UtcTimestamp.from_datetime(now)
                < expiry
                <= UtcTimestamp.from_datetime(now)
                + timedelta(seconds=MAX_FRAME_AGE_SECONDS),
                "invalid_image",
                "The offered frame expiry is invalid.",
            )
            return cls(
                control_id=validate_uuid(message["control_id"], "control_id")
                or "",
                reference_id=validate_identifier(
                    message["reference_id"],
                    "reference_id",
                    96,
                ),
                sha256=digest,
                byte_count=byte_count,
                width=width,
                height=height,
                expires_at_utc=expiry,
                data=data,
            )
        except Exception:
            data[:] = b"\x00" * len(data)
            raise

    def wipe(self) -> None:
        self.data[:] = b"\x00" * len(self.data)


def validate_engine_output(
    value: Any,
    *,
    role: str,
    maximum_output_bytes: int,
) -> dict[str, Any]:
    source = require_object(value, "engine output")
    if role == "ocr":
        require_exact_keys(source, ("role", "ocr"), "engine output")
        require(source["role"] == role, "internal_failure", "The engine role changed.")
        ocr = require_object(source["ocr"], "engine output.ocr")
        require_exact_keys(
            ocr,
            ("regions",),
            "engine output.ocr",
            ("detected_language",),
        )
        regions_source = require_array(ocr["regions"], "engine output.ocr.regions")
        require(
            len(regions_source) <= MAX_OCR_REGIONS,
            "internal_failure",
            "The OCR engine returned too many regions.",
        )
        regions: list[dict[str, Any]] = []
        text_bytes = 0
        for index, item in enumerate(regions_source):
            region = require_object(item, f"engine output.ocr.regions[{index}]")
            require_exact_keys(
                region,
                ("index", "text", "bounds", "confidence"),
                f"engine output.ocr.regions[{index}]",
            )
            require(
                require_int(region["index"], "region.index") == index,
                "internal_failure",
                "OCR region indexes must be contiguous.",
            )
            text = validate_text(
                region["text"],
                "region.text",
                MAX_REGION_TEXT_CHARACTERS,
                MAX_REGION_TEXT_UTF8_BYTES,
            )
            text_bytes += len(text.encode("utf-8"))
            bounds = _validate_bounds(region["bounds"])
            confidence = _validate_confidence(region["confidence"])
            regions.append(
                {
                    "bounds": bounds,
                    "confidence": confidence,
                    "index": index,
                    "text": text,
                }
            )
        result: dict[str, Any] = {"regions": regions}
        if "detected_language" in ocr:
            result["detected_language"] = validate_identifier(
                ocr["detected_language"],
                "detected_language",
                16,
            )
            text_bytes += len(result["detected_language"].encode("utf-8"))
        require(
            text_bytes <= min(maximum_output_bytes, MAX_OUTPUT_UTF8_BYTES),
            "internal_failure",
            "The OCR text exceeds the selected worker output bound.",
        )
        return {"ocr": result, "role": role}
    require_exact_keys(source, ("role", "vlm"), "engine output")
    require(source["role"] == role, "internal_failure", "The engine role changed.")
    vlm = require_object(source["vlm"], "engine output.vlm")
    require_exact_keys(
        vlm,
        ("answer", "uncertainty", "confidence"),
        "engine output.vlm",
    )
    answer = validate_text(vlm["answer"], "vlm.answer", 4_096, 8_192)
    uncertainty = validate_text(
        vlm["uncertainty"],
        "vlm.uncertainty",
        512,
        1_024,
    )
    require(
        len(answer.encode("utf-8")) + len(uncertainty.encode("utf-8"))
        <= min(maximum_output_bytes, MAX_OUTPUT_UTF8_BYTES),
        "internal_failure",
        "The VLM text exceeds the selected worker output bound.",
    )
    return {
        "role": role,
        "vlm": {
            "answer": answer,
            "confidence": _validate_confidence(vlm["confidence"]),
            "uncertainty": uncertainty,
        },
    }


def _validate_bounds(value: Any) -> dict[str, int]:
    source = require_object(value, "bounds")
    require_exact_keys(source, ("left", "top", "right", "bottom"), "bounds")
    result = {
        key: require_int(source[key], f"bounds.{key}")
        for key in ("left", "top", "right", "bottom")
    }
    require(
        all(0 <= item <= 10_000 for item in result.values())
        and result["left"] < result["right"]
        and result["top"] < result["bottom"],
        "internal_failure",
        "The engine returned invalid normalized bounds.",
    )
    return result


def _validate_confidence(value: Any) -> dict[str, Any]:
    source = require_object(value, "confidence")
    require_exact_keys(source, ("kind",), "confidence", ("value",))
    kind = require_string(source["kind"], "confidence.kind")
    if kind == "unavailable":
        require(
            "value" not in source,
            "internal_failure",
            "Unavailable confidence must not contain a value.",
        )
        return {"kind": kind}
    require(
        kind in ("uncalibrated", "calibrated")
        and "value" in source
        and type(source["value"]) in (int, float)
        and type(source["value"]) is not bool
        and math.isfinite(float(source["value"]))
        and 0 <= float(source["value"]) <= 1,
        "internal_failure",
        "The engine returned invalid confidence.",
    )
    return {"kind": kind, "value": float(source["value"])}


def make_observation(
    request: PerceptionRequest,
    worker: WorkerIdentity,
    host_id: str,
    engine_output: Any,
    *,
    processed_at_utc: datetime,
) -> dict[str, Any]:
    output = validate_engine_output(
        engine_output,
        role=request.task.role,
        maximum_output_bytes=worker.limits.maximum_output_utf8_bytes,
    )
    expires_at = min(
        request.frame.captured_at_utc + request.maximum_frame_age,
        request.deadline_utc,
    )
    processed = UtcTimestamp.from_datetime(processed_at_utc)
    require(
        processed >= request.frame.captured_at_utc
        and expires_at > processed,
        "deadline_exceeded",
        "The observation expired before it could be published.",
    )
    provenance = {
        "capture_epoch": request.frame.capture_epoch,
        "capture_permission_revision": (
            request.frame.source.capture_permission_revision
        ),
        "captured_at_utc": format_utc(request.frame.captured_at_utc),
        "destination_id": request.destination_id,
        "evidence": worker.evidence,
        "frame_id": request.frame.frame_id,
        "frame_sha256": request.frame.content.sha256,
        "host_id": host_id,
        "model_id": worker.model.model_id,
        "model_revision": worker.model.model_revision,
        "model_sha256": worker.model.model_sha256,
        "processed_at_utc": format_utc(processed_at_utc),
        "role": worker.role,
        "selection_id": request.frame.source.selection_id,
        "source_revision": request.frame.source.source_revision,
        "worker_id": worker.worker_id,
    }
    observation: dict[str, Any] = {
        "expires_at_utc": format_utc(expires_at),
        "provenance": provenance,
        "role": worker.role,
    }
    observation["ocr" if worker.role == "ocr" else "vlm"] = output[
        "ocr" if worker.role == "ocr" else "vlm"
    ]
    require(
        len(canonical_json_bytes(observation))
        <= worker.limits.maximum_output_utf8_bytes,
        "internal_failure",
        "The canonical observation exceeds the H03b route output bound.",
    )
    return observation


def worker_error_wire(error: ContractError) -> dict[str, Any]:
    code = error.code if error.code in WORKER_ERROR_CODES else "invalid_request"
    return {
        "code": code,
        "remedy_code": error.remedy_code,
        "summary": error.summary,
    }


def protocol_error_wire(error: ContractError) -> dict[str, Any]:
    return {
        "code": error.code,
        "remedy_code": error.remedy_code,
        "retryable": error.retryable,
        "stage": error.stage,
        "summary": error.summary,
    }


def response_wire(
    request: PerceptionRequest,
    worker: WorkerIdentity,
    *,
    outcome: str,
    observation: dict[str, Any] | None = None,
    error: ContractError | None = None,
    compute_cancellation: str | None = None,
    worker_may_continue: bool = False,
) -> dict[str, Any]:
    require(
        outcome in ("completed", "canceled", "failed"),
        "internal_failure",
        "The worker outcome is invalid.",
    )
    return {
        "action_id": request.action_id,
        "compute_cancellation": compute_cancellation,
        "contract_id": CONTRACT_ID,
        "epoch": request.epoch,
        "error": worker_error_wire(error) if error is not None else None,
        "ids": request.ids.wire(),
        "observation": observation,
        "outcome": outcome,
        "protocol_version": dict(PROTOCOL_VERSION),
        "type": "response",
        "worker": worker.wire(),
        "worker_may_continue": worker_may_continue,
    }


_GATEWAY_FAILURES = {
    "worker.failed": (
        "The selected private worker failed the bounded operation.",
        "Inspect only the configured private worker using this trace ID; do not "
        "retry automatically or select another model.",
    ),
    "context.unavailable": (
        "The optional perception context worker is unavailable.",
        "Continue without optional context or repair host 2 deliberately; voice "
        "inference remains independent.",
    ),
    "job.deadline": (
        "The bounded inference job deadline expired.",
        "Treat all partial output as incomplete; retry only as a new explicitly "
        "authorized request after checking the private worker.",
    ),
}


def h03b_route_events(
    request: PerceptionRequest,
    response: Mapping[str, Any],
    *,
    trace_id: str,
) -> list[dict[str, Any]]:
    _validate_route_response(request, response)
    trace = validate_uuid(trace_id, "trace_id") or ""
    identity = request.expected_worker
    route_id = (
        "martlet.gateway.perception-ocr.v1"
        if identity.role == "ocr"
        else "martlet.gateway.perception-vlm.v1"
    )
    common = {
        "artifact_identity_sha256": artifact_identity_sha256(identity),
        "destination_id": request.destination_id,
        "epoch": request.epoch,
        "model_id": identity.model.model_id,
        "model_revision": identity.model.model_revision,
        "model_sha256": identity.model.model_sha256,
        "protocol_version": dict(PROTOCOL_VERSION),
        "registry_version": GATEWAY_REGISTRY_VERSION,
        "request_id": request.ids.request_id,
        "route_id": route_id,
        "session_id": request.ids.session_id,
        "trace_id": trace,
        "turn_id": request.ids.turn_id,
        "worker_id": identity.worker_id,
    }

    def event(kind: str, sequence: int, **extra: Any) -> dict[str, Any]:
        return {**common, **extra, "sequence": sequence, "type": kind}

    events = [event("started", 0)]
    outcome = response.get("outcome")
    if outcome == "completed":
        observation = require_object(response.get("observation"), "observation")
        events.append(
            event(
                "observation",
                1,
                data_base64=base64.b64encode(
                    canonical_json_bytes(observation)
                ).decode("ascii"),
                data_media_type="application/json",
            )
        )
        events.append(event("completed", 2))
        return events
    if outcome == "canceled":
        events.append(event("canceled", 1))
        return events
    error = require_object(response.get("error"), "error")
    worker_code = require_string(error.get("code"), "error.code")
    if worker_code == "deadline_exceeded":
        gateway_code = "job.deadline"
    elif worker_code in ("model_not_ready", "resource_exhausted"):
        gateway_code = "context.unavailable"
    else:
        gateway_code = "worker.failed"
    summary, remedy = _GATEWAY_FAILURES[gateway_code]
    events.append(
        event(
            "failed",
            1,
            code=gateway_code,
            remedy=remedy,
            summary=summary,
        )
    )
    return events


def _validate_route_response(
    request: PerceptionRequest,
    response: Mapping[str, Any],
) -> None:
    require_exact_keys(
        response,
        ("action_id", "compute_cancellation", "contract_id", "epoch", "error",
         "ids", "observation", "outcome", "protocol_version", "type", "worker",
         "worker_may_continue"),
        "response",
    )
    parse_contract_header(response)
    require(
        response["type"] == "response"
        and response["action_id"] == request.action_id
        and RequestIds.parse(response["ids"]) == request.ids
        and require_int(response["epoch"], "response.epoch") == request.epoch
        and identities_match(WorkerIdentity.parse(response["worker"]), request.expected_worker),
        "internal_failure",
        "The response does not belong to this exact request and worker.",
    )
    may_continue = require_bool(response["worker_may_continue"], "worker_may_continue")
    cancellation = response["compute_cancellation"]
    require(
        cancellation is None
        or (cancellation == request.expected_worker.cancellation and
            (cancellation == "cooperative_compute_cancel" or may_continue)),
        "internal_failure",
        "The response cancellation ownership is contradictory.",
    )
    outcome = response["outcome"]
    if outcome == "completed":
        require(
            response["error"] is None and cancellation is None and not may_continue,
            "internal_failure",
            "The completed response has contradictory terminal fields.",
        )
        observation = require_object(response["observation"], "observation")
        provenance = require_object(observation.get("provenance"), "provenance")
        processed = parse_utc(provenance.get("processed_at_utc"), "processed_at_utc")
        role = request.task.role
        output_key = "ocr" if role == "ocr" else "vlm"
        expected = make_observation(
            request, request.expected_worker,
            validate_identifier(provenance.get("host_id"), "host_id"),
            {"role": role, output_key: observation.get(output_key)},
            processed_at_utc=processed,
        )
        require(
            canonical_json_bytes(observation) == canonical_json_bytes(expected),
            "internal_failure",
            "The observation provenance or output differs from its request.",
        )
        return
    require(
        outcome in ("canceled", "failed") and response["observation"] is None,
        "internal_failure",
        "The response outcome or observation is invalid.",
    )
    if outcome == "canceled":
        require(
            response["error"] is None and cancellation is not None,
            "internal_failure",
            "The canceled response has contradictory terminal fields.",
        )
        return
    error = require_object(response["error"], "error")
    require_exact_keys(error, ("code", "summary", "remedy_code"), "error")
    require(error["code"] in WORKER_ERROR_CODES, "internal_failure", "Unknown worker error.")
    validate_text(error["summary"], "error.summary", 256, 512, allow_newlines=False)
    validate_identifier(error["remedy_code"], "error.remedy_code", 96)
