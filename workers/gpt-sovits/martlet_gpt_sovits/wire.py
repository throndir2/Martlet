"""Wire helpers shared by the GPT-SoVITS host front and its worker: request checks, reference WAV inspection, language
choice, 24 kHz conversion and the martlet.f5.worker 1.0 event shape Martlet's reference-voice gateway relay reads."""

from __future__ import annotations

import base64
import hashlib
import json
import re
import struct
from datetime import datetime, timezone
from typing import Any

from . import pins

# Martlet's reference-voice speech stream (the same events the F5 worker emits; Martlet.Gateway.F5 relays both).
CONTRACT_ID = "martlet.f5.worker"
PROTOCOL_VERSION = {"major": 1, "minor": 0}
MAX_TEXT_CHUNKS = 16
MAX_CHUNK_CHARACTERS = 2_048
MAX_TRANSCRIPT_CHARACTERS = 4_096
MAX_FRAME_SAMPLES = 4_800
MAX_SAMPLES = pins.OUTPUT_SAMPLE_RATE * 90
MAX_AUDIO_FILE_BYTES = 4 * 1_024 * 1_024
MAX_CLIPS = 10
REFERENCE_SAMPLE_RATES = (16_000, 22_050, 24_000, 44_100, 48_000)
_HEX64 = re.compile(r"^[0-9a-f]{64}$")
_UUID = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")
_KANA = re.compile(r"[\u3041-\u3096\u30a1-\u30fa\u30fc\uff66-\uff9d]")
_HAN = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff]")


class RequestError(ValueError):
    """A request Martlet's relay should never send; answered with 400 and never forwarded to GPT-SoVITS."""


def canonical(value: Any) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def parse_utc(text: Any) -> datetime:
    if not isinstance(text, str) or not text.endswith("Z"):
        raise RequestError("deadline_utc must be UTC")
    try:
        return datetime.fromisoformat(text[:-1]).replace(tzinfo=timezone.utc)
    except ValueError as error:
        raise RequestError("deadline_utc is invalid") from error


def format_utc(value: datetime) -> str:
    return value.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def wave_duration_ms(audio: bytes) -> int:
    """Duration of a mono PCM16 RIFF/WAVE at a reference sample rate; raises RequestError otherwise."""
    rate, data, _ = wave_data(audio)
    return (len(data) // 2) * 1000 // rate


def wave_data(audio: bytes) -> tuple[int, bytes, int]:
    """(sample rate, PCM16 data, data offset) of a mono PCM16 RIFF/WAVE at a reference sample rate; raises RequestError."""
    if len(audio) < 44 or len(audio) > MAX_AUDIO_FILE_BYTES or audio[:4] != b"RIFF" or audio[8:12] != b"WAVE":
        raise RequestError("reference is not a WAV file")
    offset, rate, data, start = 12, None, None, 0
    while offset + 8 <= len(audio):
        chunk, size = audio[offset:offset + 4], struct.unpack_from("<I", audio, offset + 4)[0]
        body = offset + 8
        if body + size > len(audio):
            raise RequestError("reference WAV is truncated")
        if chunk == b"fmt ":
            fmt, channels, rate, _, align, bits = struct.unpack_from("<HHIIHH", audio, body)
            if fmt != 1 or channels != 1 or bits != 16 or align != 2 or rate not in REFERENCE_SAMPLE_RATES:
                raise RequestError("reference must be mono 16-bit PCM")
        elif chunk == b"data":
            data, start = audio[body:body + size - (size & 1)], body
        offset = body + size + (size & 1)
    if rate is None or not data:
        raise RequestError("reference WAV has no audio")
    return rate, data, start


def wave_bytes(rate: int, data: bytes) -> bytes:
    """A mono PCM16 RIFF/WAVE of ``data`` at ``rate``."""
    return (b"RIFF" + struct.pack("<I", 36 + len(data)) + b"WAVEfmt " +
            struct.pack("<IHHIIHH", 16, 1, 1, rate, rate * 2, 2, 16) + b"data" + struct.pack("<I", len(data)) + data)


def split_clips(audio: bytes, clips: Any) -> list[dict[str, Any]]:
    """A voice made from several recordings: each one (``audio_base64`` WAV, ``transcript``, ``duration_ms``) cut from the
    joined recording at the sample ranges Martlet's relay sent, in order and not overlapping; raises RequestError."""
    if not isinstance(clips, list) or not 2 <= len(clips) <= MAX_CLIPS:
        raise RequestError("reference clips are invalid")
    rate, data, _ = wave_data(audio)
    total, end, parts = len(data) // 2, 0, []
    for clip in clips:
        start, count, transcript = clip["start_sample"], clip["sample_count"], clip["transcript"]
        if (type(start) is not int or type(count) is not int or start < end or count <= 0 or start + count > total or
                not isinstance(transcript, str) or not transcript.strip() or len(transcript) > MAX_TRANSCRIPT_CHARACTERS):
            raise RequestError("reference clip is invalid")
        parts.append({"audio_base64": base64.b64encode(wave_bytes(rate, data[start * 2:(start + count) * 2])).decode("ascii"),
                      "duration_ms": -(-count * 1000 // rate), "transcript": transcript})
        end = start + count
    return parts


def reference_language(transcript: str) -> str:
    """The language GPT-SoVITS reads a reference transcript in: Japanese when it has kana or kanji, else English."""
    return "ja" if _KANA.search(transcript) or _HAN.search(transcript) else "en"


def text_language(text: str) -> str:
    """GPT-SoVITS text_lang for a reply chunk: "ja" (Japanese with English words) when it has kana or kanji, else "en"."""
    return "ja" if _KANA.search(text) or _HAN.search(text) else "en"


def check_request(body: dict[str, Any]) -> dict[str, Any]:
    """Validates the relay's synthesis body and returns the worker's synthesize message (audio kept base64)."""
    try:
        ids = body["ids"]
        reference = body["reference"]
        chunks = body["chunks"]
        for name in ("session_id", "turn_id", "request_id"):
            if not isinstance(ids[name], str) or not _UUID.match(ids[name]):
                raise RequestError(f"{name} is invalid")
        parent = ids.get("parent_request_id")
        if parent is not None and (not isinstance(parent, str) or not _UUID.match(parent)):
            raise RequestError("parent_request_id is invalid")
        deadline = parse_utc(body["deadline_utc"])
        audio = base64.b64decode(reference["audio_base64"], validate=True)
        if hashlib.sha256(audio).hexdigest() != reference["audio_sha256"]:
            raise RequestError("reference audio does not match its SHA-256")
        transcript = reference["transcript"]
        if not isinstance(transcript, str) or not transcript.strip() or len(transcript) > MAX_TRANSCRIPT_CHARACTERS:
            raise RequestError("reference transcript is required")
        if hashlib.sha256(transcript.encode("utf-8")).hexdigest() != reference["transcript_revision"]:
            raise RequestError("reference transcript does not match its revision")
        revision = reference["reference_revision"]
        if not isinstance(revision, str) or not _HEX64.match(revision):
            raise RequestError("reference_revision is invalid")
        language = reference.get("language") or reference_language(transcript)
        if language not in pins.REFERENCE_LANGUAGES:
            raise RequestError("reference language is not supported")
        # A voice made from several recordings: the first 3-10 second one is the prompt (its words and language) and the
        # others add their tone (aux_ref_audio_paths). Without one that fits, the joined recording is the reference.
        prompt, auxiliary = None, []
        if reference.get("clips") is not None:
            clips = split_clips(audio, reference["clips"])
            fitting = [clip for clip in clips
                       if pins.MIN_REFERENCE_MILLISECONDS <= clip["duration_ms"] <= pins.MAX_REFERENCE_MILLISECONDS]
            if fitting:
                prompt = fitting[0]
                auxiliary = [clip["audio_base64"] for clip in clips if clip is not prompt]
                language = reference_language(prompt["transcript"])
        if prompt is None:
            duration = wave_duration_ms(audio)
            if not pins.MIN_REFERENCE_MILLISECONDS <= duration <= pins.MAX_REFERENCE_MILLISECONDS:
                raise RequestError("GPT-SoVITS needs a 3 to 10 second reference recording")
        if not isinstance(chunks, list) or not 0 < len(chunks) <= MAX_TEXT_CHUNKS:
            raise RequestError("chunks are invalid")
        checked = []
        for index, chunk in enumerate(chunks):
            text = chunk["text"]
            if chunk["index"] != index or not isinstance(text, str) or not text.strip() or len(text) > MAX_CHUNK_CHARACTERS:
                raise RequestError("chunk is invalid")
            checked.append({"chunk_id": str(chunk["chunk_id"]), "index": index, "text": text})
    except (KeyError, TypeError, ValueError, struct.error) as error:
        if isinstance(error, RequestError):
            raise
        raise RequestError("request is invalid") from error
    return {
        "chunks": checked,
        "deadline_utc": format_utc(deadline),
        "ids": {"parent_request_id": parent, "request_id": ids["request_id"], "session_id": ids["session_id"],
                "turn_id": ids["turn_id"]},
        "reference": {
            "audio_base64": prompt["audio_base64"] if prompt else reference["audio_base64"],
            "auxiliary_base64": auxiliary,
            "language": language,
            "preset_id": str(reference.get("preset_id", "")),
            "reference_revision": revision,
            "transcript": prompt["transcript"] if prompt else transcript,
        },
        "type": "synthesize",
    }


def resample_to_output(samples: Any, rate: int) -> bytes:
    """int16 mono samples at ``rate`` -> 24 kHz signed 16-bit little-endian bytes (polyphase, as v2Pro writes 32 kHz)."""
    import numpy as np

    data = np.asarray(samples, dtype=np.int16)
    if rate != pins.OUTPUT_SAMPLE_RATE:
        from math import gcd

        from scipy.signal import resample_poly

        divisor = gcd(pins.OUTPUT_SAMPLE_RATE, rate)
        converted = resample_poly(data.astype(np.float32), pins.OUTPUT_SAMPLE_RATE // divisor, rate // divisor)
        data = np.clip(np.rint(converted), -32768, 32767).astype(np.int16)
    return data.astype("<i2").tobytes()


def make_event(*, kind: str, sequence: int, ids: dict[str, Any], worker: dict[str, Any], reference_revision: str,
               frame: dict[str, Any] | None = None, chunk_index: int | None = None,
               final_sample_count: int | None = None, error: dict[str, Any] | None = None) -> dict[str, Any]:
    return {
        "cancellation": "discard_only" if kind == "canceled" else None,
        "chunk_index": chunk_index,
        "contract_id": CONTRACT_ID,
        "error": error,
        "final_sample_count": final_sample_count,
        "frame": frame,
        "ids": ids,
        "kind": kind,
        "protocol_version": dict(PROTOCOL_VERSION),
        "reference_revision": reference_revision,
        "sequence": sequence,
        "type": "event",
        "worker": worker,
    }


def make_frame(*, sequence: int, chunk_index: int, sample_offset: int, pcm: bytes) -> dict[str, Any]:
    if not pcm or len(pcm) % 2 or len(pcm) > MAX_FRAME_SAMPLES * 2:
        raise ValueError("invalid PCM frame")
    return {
        "chunk_index": chunk_index,
        "data_base64": base64.b64encode(pcm).decode("ascii"),
        "format": {"bits_per_sample": 16, "channels": 1, "encoding": "signed16_little_endian",
                   "sample_rate": pins.OUTPUT_SAMPLE_RATE},
        "sample_count": len(pcm) // 2,
        "sample_offset": sample_offset,
        "sequence": sequence,
    }
