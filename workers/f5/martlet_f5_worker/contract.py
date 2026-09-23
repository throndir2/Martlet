from __future__ import annotations

import base64
import binascii
import hashlib
import hmac
import re
import struct
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any, Callable, Iterable, Mapping, NoReturn, Sequence

CONTRACT_ID = "martlet.f5.worker"
PROTOCOL_VERSION = {"major": 1, "minor": 0}

MAX_TEXT_CHUNKS = 16
MAX_CHUNK_CHARACTERS = 2_048
MAX_CHUNK_UTF8_BYTES = 4_096
MAX_TEXT_UTF8_BYTES = 16 * 1_024
MAX_FRAME_SAMPLES = 4_800
MAX_FRAME_BYTES = MAX_FRAME_SAMPLES * 2
MAX_EVENTS = 16_384
MAX_SAMPLES = 24_000 * 90
MAX_REQUEST_SECONDS = 90
MAX_CANCEL_SECONDS = 2
MAX_WARMUP_SECONDS = 300
MAX_AUDIO_FILE_BYTES = 4 * 1_024 * 1_024
MAX_TRANSCRIPT_CHARACTERS = 4_096
MAX_TRANSCRIPT_UTF8_BYTES = 8_192
MIN_REFERENCE_MILLISECONDS = 1_000
MAX_REFERENCE_MILLISECONDS = 30_000
MAX_ARTIFACT_BYTES = 16 * 1_024**4

ARTIFACT_ROLES = (
    "runtime_image",
    "model_weights",
    "vocabulary",
    "vocoder_weights",
    "vocoder_configuration",
)
CANCELLATION_CAPABILITIES = (
    "discard_only",
    "request_abort",
    "cooperative_compute_cancel",
)
EVIDENCE_KINDS = ("live_worker", "synthetic_fixture")
ERROR_CODES = (
    "invalid_request",
    "unsupported_version",
    "identity_mismatch",
    "reference_rejected",
    "busy",
    "model_not_ready",
    "deadline_exceeded",
    "gpu_out_of_memory",
    "internal_failure",
)

_IDENTIFIER = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]*$")
_LOWER_SHA1 = re.compile(r"^[0-9a-f]{40}$")
_LOWER_SHA256 = re.compile(r"^[0-9a-f]{64}$")
_EXACT_VERSION = re.compile(r"^[A-Za-z0-9.+-]{1,64}$")
_UTC_TIMESTAMP = re.compile(
    r"^(?P<date>\d{4}-\d{2}-\d{2})T(?P<time>\d{2}:\d{2}:\d{2})"
    r"(?P<fraction>\.\d{1,6})?Z$"
)


class ContractError(Exception):
    def __init__(
        self,
        code: str,
        summary: str,
        *,
        stage: str = "request_validation",
        action_id: str = "f5.correct-request",
        retryable: bool = False,
    ) -> None:
        super().__init__(summary)
        self.code = code
        self.stage = stage
        self.summary = summary
        self.action_id = action_id
        self.retryable = retryable


def fail(
    code: str,
    summary: str,
    *,
    stage: str = "request_validation",
    action_id: str = "f5.correct-request",
    retryable: bool = False,
) -> NoReturn:
    raise ContractError(
        code,
        summary,
        stage=stage,
        action_id=action_id,
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
) -> None:
    expected = frozenset(required)
    actual = frozenset(value)
    require(
        actual == expected,
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
    require(type(value) is int, "invalid_request", f"{name} must be an integer.")
    return value


def validate_identifier(value: Any, name: str, maximum: int = 64) -> str:
    text = require_string(value, name)
    require(
        len(text) <= maximum and _IDENTIFIER.fullmatch(text) is not None,
        "invalid_request",
        f"{name} is not a valid identifier.",
    )
    return text


def validate_sha1(value: Any, name: str) -> str:
    text = require_string(value, name)
    require(
        _LOWER_SHA1.fullmatch(text) is not None,
        "invalid_request",
        f"{name} must be a lowercase SHA-1 revision.",
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
        and text.lower() not in {"main", "latest", "unknown", "unpinned"},
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
        if ord(character) < 32 or ord(character) == 127:
            permitted = allow_newlines and character in "\r\n\t"
            require(permitted, "invalid_request", f"{name} contains a control character.")
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


def parse_utc(value: Any, name: str) -> datetime:
    text = require_string(value, name)
    match = _UTC_TIMESTAMP.fullmatch(text)
    require(match is not None, "invalid_request", f"{name} must be canonical UTC RFC 3339.")
    try:
        result = datetime.fromisoformat(text[:-1] + "+00:00")
    except ValueError:
        fail("invalid_request", f"{name} is not a valid UTC timestamp.")
    require(result.tzinfo == timezone.utc, "invalid_request", f"{name} must be UTC.")
    return result


def format_utc(value: datetime) -> str:
    return value.astimezone(timezone.utc).isoformat(timespec="microseconds").replace("+00:00", "Z")


def parse_contract_header(message: Mapping[str, Any]) -> None:
    require(
        message.get("contract_id") == CONTRACT_ID,
        "invalid_request",
        "contract_id must be martlet.f5.worker.",
    )
    version = require_object(message.get("protocol_version"), "protocol_version")
    require_exact_keys(version, ("major", "minor"), "protocol_version")
    major = require_int(version["major"], "protocol_version.major")
    minor = require_int(version["minor"], "protocol_version.minor")
    if major != 1 or minor != 0:
        fail(
            "unsupported_version",
            "Only martlet.f5.worker protocol version 1.0 is supported.",
            action_id="f5.use-worker-v1",
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
        return cls(
            session_id=validate_uuid(source["session_id"], "ids.session_id") or "",
            turn_id=validate_uuid(source["turn_id"], "ids.turn_id") or "",
            request_id=validate_uuid(source["request_id"], "ids.request_id") or "",
            parent_request_id=validate_uuid(
                source["parent_request_id"],
                "ids.parent_request_id",
                nullable=True,
            ),
        )

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
    f5_package_version: str
    f5_source_revision: str
    python_version: str
    torch_version: str
    torchaudio_version: str
    cuda_runtime_version: str

    @classmethod
    def parse(cls, value: Any) -> "RuntimeIdentity":
        source = require_object(value, "runtime")
        fields = (
            "worker_build_id",
            "worker_build_revision",
            "f5_package_version",
            "f5_source_revision",
            "python_version",
            "torch_version",
            "torchaudio_version",
            "cuda_runtime_version",
        )
        require_exact_keys(source, fields, "runtime")
        return cls(
            worker_build_id=validate_identifier(source["worker_build_id"], "worker_build_id"),
            worker_build_revision=validate_sha1(
                source["worker_build_revision"],
                "worker_build_revision",
            ),
            f5_package_version=validate_exact_version(
                source["f5_package_version"],
                "f5_package_version",
            ),
            f5_source_revision=validate_sha1(
                source["f5_source_revision"],
                "f5_source_revision",
            ),
            python_version=validate_exact_version(source["python_version"], "python_version"),
            torch_version=validate_exact_version(source["torch_version"], "torch_version"),
            torchaudio_version=validate_exact_version(
                source["torchaudio_version"],
                "torchaudio_version",
            ),
            cuda_runtime_version=validate_exact_version(
                source["cuda_runtime_version"],
                "cuda_runtime_version",
            ),
        )

    def wire(self) -> dict[str, Any]:
        return {
            "cuda_runtime_version": self.cuda_runtime_version,
            "f5_package_version": self.f5_package_version,
            "f5_source_revision": self.f5_source_revision,
            "python_version": self.python_version,
            "torch_version": self.torch_version,
            "torchaudio_version": self.torchaudio_version,
            "worker_build_id": self.worker_build_id,
            "worker_build_revision": self.worker_build_revision,
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
            revision=validate_sha1(source["revision"], f"artifacts[{index}].revision"),
            sha256=validate_sha256(source["sha256"], f"artifacts[{index}].sha256"),
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
class WorkerIdentity:
    worker_id: str
    evidence: str
    runtime: RuntimeIdentity
    artifacts: tuple[ArtifactIdentity, ...]
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
                "runtime",
                "artifacts",
                "cancellation",
                "pcm_transport_streaming",
                "incremental_within_chunk_synthesis",
            ),
            "worker",
        )
        parse_contract_header(source)
        evidence = require_string(source["evidence"], "worker.evidence")
        require(evidence in EVIDENCE_KINDS, "invalid_request", "worker.evidence is unsupported.")
        cancellation = require_string(source["cancellation"], "worker.cancellation")
        require(
            cancellation in CANCELLATION_CAPABILITIES,
            "invalid_request",
            "worker.cancellation is unsupported.",
        )
        require(
            require_bool(
                source["pcm_transport_streaming"],
                "worker.pcm_transport_streaming",
            ),
            "invalid_request",
            "PCM transport streaming must be enabled.",
        )
        require(
            not require_bool(
                source["incremental_within_chunk_synthesis"],
                "worker.incremental_within_chunk_synthesis",
            ),
            "invalid_request",
            "Incremental-within-chunk synthesis must be false.",
        )
        artifacts_source = require_array(source["artifacts"], "worker.artifacts")
        require(
            len(artifacts_source) == len(ARTIFACT_ROLES),
            "invalid_request",
            "worker.artifacts must contain exactly five artifacts.",
        )
        artifacts = tuple(
            sorted(
                (
                    ArtifactIdentity.parse(artifact, index)
                    for index, artifact in enumerate(artifacts_source)
                ),
                key=lambda artifact: ARTIFACT_ROLES.index(artifact.role),
            )
        )
        require(
            tuple(artifact.role for artifact in artifacts) == ARTIFACT_ROLES,
            "invalid_request",
            "worker.artifacts must contain each role exactly once.",
        )
        require(
            len({artifact.artifact_id for artifact in artifacts}) == len(artifacts),
            "invalid_request",
            "worker artifact IDs must be unique.",
        )
        return cls(
            worker_id=validate_identifier(source["worker_id"], "worker.worker_id"),
            evidence=evidence,
            runtime=RuntimeIdentity.parse(source["runtime"]),
            artifacts=artifacts,
            cancellation=cancellation,
        )

    def wire(self) -> dict[str, Any]:
        return {
            "artifacts": [artifact.wire() for artifact in self.artifacts],
            "cancellation": self.cancellation,
            "contract_id": CONTRACT_ID,
            "evidence": self.evidence,
            "incremental_within_chunk_synthesis": False,
            "pcm_transport_streaming": True,
            "protocol_version": dict(PROTOCOL_VERSION),
            "runtime": self.runtime.wire(),
            "worker_id": self.worker_id,
        }


def identities_match(left: WorkerIdentity, right: WorkerIdentity) -> bool:
    return hmac.compare_digest(canonical_identity(left), canonical_identity(right))


def canonical_identity(identity: WorkerIdentity) -> bytes:
    from .jsonio import canonical_json_bytes

    return canonical_json_bytes(identity.wire())


@dataclass(frozen=True)
class AudioFormat:
    sample_rate: int
    channels: int
    bits_per_sample: int
    sample_count: int
    data_bytes: int
    duration_milliseconds: int

    @classmethod
    def parse(cls, value: Any) -> "AudioFormat":
        source = require_object(value, "reference.source_format")
        fields = (
            "sample_rate",
            "channels",
            "bits_per_sample",
            "sample_count",
            "data_bytes",
            "duration_milliseconds",
        )
        require_exact_keys(source, fields, "reference.source_format")
        result = cls(
            sample_rate=require_int(source["sample_rate"], "source_format.sample_rate"),
            channels=require_int(source["channels"], "source_format.channels"),
            bits_per_sample=require_int(
                source["bits_per_sample"],
                "source_format.bits_per_sample",
            ),
            sample_count=require_int(source["sample_count"], "source_format.sample_count"),
            data_bytes=require_int(source["data_bytes"], "source_format.data_bytes"),
            duration_milliseconds=require_int(
                source["duration_milliseconds"],
                "source_format.duration_milliseconds",
            ),
        )
        result.validate()
        return result

    def validate(self) -> None:
        computed_duration = (
            (self.sample_count * 1_000 + self.sample_rate - 1) // self.sample_rate
            if self.sample_rate > 0 and self.sample_count > 0
            else -1
        )
        require(
            self.sample_rate in {16_000, 22_050, 24_000, 44_100, 48_000}
            and self.channels == 1
            and self.bits_per_sample == 16
            and 0 < self.sample_count <= (2**31 - 1) // 2
            and self.data_bytes == self.sample_count * 2
            and self.duration_milliseconds == computed_duration
            and MIN_REFERENCE_MILLISECONDS
            <= self.duration_milliseconds
            <= MAX_REFERENCE_MILLISECONDS,
            "reference_rejected",
            "The reference source format is invalid.",
        )

    def wire(self) -> dict[str, Any]:
        return {
            "bits_per_sample": self.bits_per_sample,
            "channels": self.channels,
            "data_bytes": self.data_bytes,
            "duration_milliseconds": self.duration_milliseconds,
            "sample_count": self.sample_count,
            "sample_rate": self.sample_rate,
        }


def parse_wave(audio: bytes | bytearray) -> AudioFormat:
    size = len(audio)
    require(
        44 <= size <= MAX_AUDIO_FILE_BYTES,
        "reference_rejected",
        "The reference must be a bounded PCM WAV file.",
    )
    require(
        audio[0:4] == b"RIFF" and audio[8:12] == b"WAVE",
        "reference_rejected",
        "The reference is not a RIFF/WAVE file.",
    )
    riff_bytes = struct.unpack_from("<I", audio, 4)[0]
    require(
        riff_bytes + 8 == size,
        "reference_rejected",
        "The reference RIFF length is invalid.",
    )
    sample_rate: int | None = None
    data_bytes: int | None = None
    usable_sample = False
    offset = 12
    while offset < size:
        require(
            size - offset >= 8,
            "reference_rejected",
            "The reference contains a truncated chunk header.",
        )
        chunk_id = bytes(audio[offset : offset + 4])
        chunk_bytes = struct.unpack_from("<I", audio, offset + 4)[0]
        payload = offset + 8
        require(
            chunk_bytes <= 2**31 - 1 and chunk_bytes <= size - payload,
            "reference_rejected",
            "The reference contains a truncated chunk.",
        )
        end = payload + chunk_bytes
        if chunk_id == b"fmt ":
            require(
                sample_rate is None and chunk_bytes == 16,
                "reference_rejected",
                "The reference must contain one canonical PCM format chunk.",
            )
            format_tag, channels, rate, byte_rate, block_align, bits = struct.unpack_from(
                "<HHIIHH",
                audio,
                payload,
            )
            require(
                format_tag == 1
                and channels == 1
                and bits == 16
                and rate in {16_000, 22_050, 24_000, 44_100, 48_000}
                and block_align == 2
                and byte_rate == rate * 2,
                "reference_rejected",
                "The reference PCM format is unsupported.",
            )
            sample_rate = rate
        elif chunk_id == b"data":
            require(
                data_bytes is None and chunk_bytes > 0 and chunk_bytes % 2 == 0,
                "reference_rejected",
                "The reference must contain one aligned data chunk.",
            )
            data_bytes = chunk_bytes
            for sample_offset in range(payload, end, 2):
                if struct.unpack_from("<h", audio, sample_offset)[0] != 0:
                    usable_sample = True
                    break
        offset = end + (chunk_bytes & 1)
        require(
            offset <= size,
            "reference_rejected",
            "The reference chunk padding is invalid.",
        )
    require(
        offset == size
        and sample_rate is not None
        and data_bytes is not None
        and usable_sample,
        "reference_rejected",
        "The reference is missing usable mono PCM samples.",
    )
    samples = data_bytes // 2
    duration = (samples * 1_000 + sample_rate - 1) // sample_rate
    result = AudioFormat(sample_rate, 1, 16, samples, data_bytes, duration)
    result.validate()
    return result


@dataclass
class ReferenceInput:
    preset_id: str
    reference_revision: str
    audio_sha256: str
    transcript: str
    transcript_revision: str
    source_format: AudioFormat
    audio: bytearray

    @classmethod
    def parse(cls, value: Any) -> "ReferenceInput":
        source = require_object(value, "reference")
        require_exact_keys(
            source,
            (
                "preset_id",
                "reference_revision",
                "audio_sha256",
                "transcript",
                "transcript_revision",
                "source_format",
                "audio_base64",
            ),
            "reference",
        )
        encoded = require_string(source["audio_base64"], "reference.audio_base64")
        require(
            len(encoded) <= ((MAX_AUDIO_FILE_BYTES + 2) // 3) * 4,
            "reference_rejected",
            "The encoded reference exceeds the audio bound.",
        )
        try:
            decoded = base64.b64decode(encoded, validate=True)
        except (binascii.Error, ValueError):
            fail("reference_rejected", "The reference audio is not canonical base64.")
        require(
            base64.b64encode(decoded).decode("ascii") == encoded,
            "reference_rejected",
            "The reference audio is not canonical base64.",
        )
        audio = bytearray(decoded)
        try:
            parsed_format = parse_wave(audio)
            declared_format = AudioFormat.parse(source["source_format"])
            require(
                parsed_format == declared_format,
                "reference_rejected",
                "The declared reference format does not match the WAV bytes.",
            )
            audio_sha256 = validate_sha256(source["audio_sha256"], "reference.audio_sha256")
            actual_audio_sha256 = hashlib.sha256(audio).hexdigest()
            transcript = validate_text(
                source["transcript"],
                "reference.transcript",
                MAX_TRANSCRIPT_CHARACTERS,
                MAX_TRANSCRIPT_UTF8_BYTES,
            )
            transcript_revision = validate_sha256(
                source["transcript_revision"],
                "reference.transcript_revision",
            )
            actual_transcript_revision = hashlib.sha256(
                transcript.encode("utf-8", "strict")
            ).hexdigest()
            reference_revision = validate_sha256(
                source["reference_revision"],
                "reference.reference_revision",
            )
            actual_reference_revision = hashlib.sha256(
                bytes.fromhex(actual_audio_sha256)
                + bytes.fromhex(actual_transcript_revision)
            ).hexdigest()
            require(
                hmac.compare_digest(audio_sha256, actual_audio_sha256)
                and hmac.compare_digest(
                    transcript_revision,
                    actual_transcript_revision,
                )
                and hmac.compare_digest(
                    reference_revision,
                    actual_reference_revision,
                ),
                "reference_rejected",
                "The reference bytes, transcript, or revisions do not agree.",
            )
            return cls(
                preset_id=validate_uuid(source["preset_id"], "reference.preset_id") or "",
                reference_revision=reference_revision,
                audio_sha256=audio_sha256,
                transcript=transcript,
                transcript_revision=transcript_revision,
                source_format=declared_format,
                audio=audio,
            )
        except Exception:
            wipe(audio)
            raise

    def wipe(self) -> None:
        wipe(self.audio)
        self.transcript = ""


@dataclass(frozen=True)
class TextChunk:
    index: int
    chunk_id: str
    text: str

    @classmethod
    def parse(cls, value: Any, expected_index: int) -> "TextChunk":
        source = require_object(value, f"chunks[{expected_index}]")
        require_exact_keys(source, ("index", "chunk_id", "text"), f"chunks[{expected_index}]")
        index = require_int(source["index"], f"chunks[{expected_index}].index")
        require(
            index == expected_index,
            "invalid_request",
            "Text chunk indexes must be contiguous and zero-based.",
        )
        return cls(
            index=index,
            chunk_id=validate_identifier(
                source["chunk_id"],
                f"chunks[{expected_index}].chunk_id",
            ),
            text=validate_text(
                source["text"],
                f"chunks[{expected_index}].text",
                MAX_CHUNK_CHARACTERS,
                MAX_CHUNK_UTF8_BYTES,
            ),
        )


@dataclass
class SynthesisRequest:
    action_id: str
    ids: RequestIds
    destination_id: str
    expected_worker: WorkerIdentity
    deadline_utc: datetime
    reference: ReferenceInput
    chunks: tuple[TextChunk, ...]

    @classmethod
    def parse(
        cls,
        message: Mapping[str, Any],
        *,
        now: datetime,
    ) -> "SynthesisRequest":
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
                "deadline_utc",
                "reference",
                "chunks",
                "policy",
                "output_format",
            ),
            "synthesize",
        )
        parse_contract_header(message)
        require(message["type"] == "synthesize", "invalid_request", "Invalid message type.")
        validate_policy(message["policy"])
        validate_output_format(message["output_format"])
        deadline = parse_utc(message["deadline_utc"], "deadline_utc")
        remaining = (deadline - now).total_seconds()
        require(
            0 < remaining <= MAX_REQUEST_SECONDS,
            "deadline_exceeded",
            "The synthesis deadline is expired or exceeds 90 seconds.",
        )
        chunks_source = require_array(message["chunks"], "chunks")
        require(
            0 < len(chunks_source) <= MAX_TEXT_CHUNKS,
            "invalid_request",
            "The text chunk count is outside the contract bound.",
        )
        chunks = tuple(
            TextChunk.parse(chunk, index) for index, chunk in enumerate(chunks_source)
        )
        require(
            len({chunk.chunk_id for chunk in chunks}) == len(chunks),
            "invalid_request",
            "Text chunk IDs must be unique.",
        )
        require(
            sum(len(chunk.text.encode("utf-8", "strict")) for chunk in chunks)
            <= MAX_TEXT_UTF8_BYTES,
            "invalid_request",
            "The request text exceeds the aggregate bound.",
        )
        reference = ReferenceInput.parse(message["reference"])
        try:
            return cls(
                action_id=validate_uuid(message["action_id"], "action_id") or "",
                ids=RequestIds.parse(message["ids"]),
                destination_id=validate_identifier(
                    message["destination_id"],
                    "destination_id",
                    128,
                ),
                expected_worker=WorkerIdentity.parse(message["expected_worker"]),
                deadline_utc=deadline,
                reference=reference,
                chunks=chunks,
            )
        except Exception:
            reference.wipe()
            raise

    def wipe(self) -> None:
        self.reference.wipe()


def validate_policy(value: Any) -> None:
    policy = require_object(value, "policy")
    require_exact_keys(
        policy,
        (
            "reference_transcript_supplied",
            "automatic_transcription_allowed",
            "artifact_download_allowed",
            "default_voice_allowed",
            "model_fallback_allowed",
            "provider_fallback_allowed",
        ),
        "policy",
    )
    expected = {
        "reference_transcript_supplied": True,
        "automatic_transcription_allowed": False,
        "artifact_download_allowed": False,
        "default_voice_allowed": False,
        "model_fallback_allowed": False,
        "provider_fallback_allowed": False,
    }
    require(
        all(require_bool(policy[key], f"policy.{key}") is expected[key] for key in expected),
        "invalid_request",
        "Only the strict preprovisioned execution policy is supported.",
    )


def validate_output_format(value: Any) -> None:
    output = require_object(value, "output_format")
    require_exact_keys(
        output,
        ("sample_rate", "channels", "bits_per_sample", "encoding"),
        "output_format",
    )
    require(
        require_int(output["sample_rate"], "output_format.sample_rate") == 24_000
        and require_int(output["channels"], "output_format.channels") == 1
        and require_int(output["bits_per_sample"], "output_format.bits_per_sample") == 16
        and require_string(output["encoding"], "output_format.encoding")
        == "signed16_little_endian",
        "invalid_request",
        "The output format must be mono 24 kHz signed PCM16 little-endian.",
    )


def wipe(value: bytearray) -> None:
    value[:] = b"\x00" * len(value)


def error_wire(error: ContractError) -> dict[str, Any]:
    require(
        error.code in ERROR_CODES,
        "internal_failure",
        "An internal error code was invalid.",
    )
    return {
        "action_id": error.action_id,
        "code": error.code,
        "retryable": error.retryable,
        "stage": error.stage,
        "summary": error.summary,
    }


def make_event(
    *,
    kind: str,
    sequence: int,
    ids: RequestIds,
    worker: WorkerIdentity,
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
        "worker": worker.wire(),
    }


def make_frame(
    *,
    sequence: int,
    chunk_index: int,
    sample_offset: int,
    pcm: bytes,
) -> dict[str, Any]:
    require(
        len(pcm) > 0 and len(pcm) % 2 == 0 and len(pcm) <= MAX_FRAME_BYTES,
        "internal_failure",
        "The engine emitted an invalid PCM frame.",
    )
    return {
        "chunk_index": chunk_index,
        "data_base64": base64.b64encode(pcm).decode("ascii"),
        "format": {
            "bits_per_sample": 16,
            "channels": 1,
            "encoding": "signed16_little_endian",
            "sample_rate": 24_000,
        },
        "sample_count": len(pcm) // 2,
        "sample_offset": sample_offset,
        "sequence": sequence,
    }
