"""Martlet Chatterbox host service: Chatterbox Turbo, Chatterbox Nano or the original Chatterbox.

Loopback HTTP front for Resemble AI's Chatterbox models (CHATTERBOX_MODEL: chatterbox-turbo, chatterbox-nano or
chatterbox-original; each host role runs one). It mirrors the F5 host's HTTP shape and NDJSON worker-event protocol so
Martlet's relay can consume it.

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
import re
import struct
import sys
import tempfile
import threading
import time
import traceback
import urllib.request
from collections import OrderedDict
from contextlib import contextmanager, nullcontext
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, NoReturn

ROOT = Path(os.environ.get("MARTLET_CHATTERBOX_ROOT", "/opt/martlet-chatterbox"))
MODELS = ROOT / "models"
CONFIG = MODELS / "worker-config.json"
MODEL = os.environ.get("CHATTERBOX_MODEL", "chatterbox-turbo")
# "auto" (the chatterbox-nano role): the NVIDIA GPU when the container has one, otherwise the CPU.
DEVICE = os.environ.get("MARTLET_CHATTERBOX_DEVICE", "cuda:0")
PORT = int(os.environ.get("MARTLET_CHATTERBOX_PORT", "50083"))
# On the CPU, PyTorch uses at most DEFAULT_CPU_THREADS threads, never more than the performance cores, and on Linux a hybrid
# Intel CPU's performance cores get one thread each (_cpu_plan). Chatterbox Nano on an i7-13700K (8 performance and 8
# efficiency cores), median real-time factor: 0.52 pinned to 8 performance cores, 0.64 with 8 threads on any core, 0.75 with
# PyTorch's own 16 threads (6 of 16 sentences slower than real time). MARTLET_CHATTERBOX_CPU_THREADS sets another count.
CPU_THREADS = os.environ.get("MARTLET_CHATTERBOX_CPU_THREADS", "")
DEFAULT_CPU_THREADS = 8
# The meanflow decoder's steps on the CPU (the library always asks for 2; the GPU keeps 2). With 1, Nano's mel took 0.23-0.32 s
# a piece instead of 0.40-0.60 s, with the same UTMOS, speaker similarity and word error rate on 20 paired takes.
CPU_DECODER_STEPS = max(1, int(os.environ.get("MARTLET_CHATTERBOX_CPU_DECODER_STEPS", "1")))
SYSFS_DEVICES = Path("/sys/devices")

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
# While nobody speaks, the model is run briefly this often (seconds; 0 turns it off): Windows moves an idle model out of
# graphics memory when other programs fill the card, and the next reply then waits for it (51 s once on a shared card).
IDLE_CHECK_SECONDS = float(os.environ.get("MARTLET_CHATTERBOX_IDLE_CHECK_SECONDS", "300"))
# A CUDA error such as a device-side assert or an illegal memory access breaks the process's graphics context for good;
# the service exits with this code so Docker (restart: unless-stopped) starts it again with a fresh one.
RESTART_EXIT_CODE = 75
RESTART_DELAY_SECONDS = 1.0
# The most speech tokens (25 a second) a piece may take: SPEECH_TOKENS_BASE plus so many per text token and per tag
# ([laugh]...). Measured on three starter voices (270 pieces, none ran away): a text token took 6.7 speech tokens (median),
# at most 11.5, and up to 16 in digit strings ("3.14159265358979"); a tag up to about 35 more. These allow about twice the
# most measured, so only a piece Turbo doesn't stop is cut.
SPEECH_TOKENS_BASE = int(os.environ.get("MARTLET_CHATTERBOX_TOKENS_BASE", "100"))
SPEECH_TOKENS_PER_TEXT_TOKEN = int(os.environ.get("MARTLET_CHATTERBOX_TOKENS_PER_TEXT_TOKEN", "25"))
SPEECH_TOKENS_PER_TAG = int(os.environ.get("MARTLET_CHATTERBOX_TOKENS_PER_TAG", "100"))
# The tokenizer's 19 tags (added_tokens.json, ids 50257-50275).
TAG_TOKEN_FIRST, TAG_TOKEN_LAST = 50257, 50275
# Turbo reads its [whispering] tag but doesn't whisper (measured on four starter voices: as voiced as without it, at the same
# level), and its vocoder takes the voice's pitch from the mel, not from a source it could be told to drop. So a sentence that
# starts with the tag is whispered here (Whisperer), this many dB below the voice's own level, before the watermark.
WHISPER_TAG = "[whispering]"
WHISPER_DB = float(os.environ.get("MARTLET_CHATTERBOX_WHISPER_DB", "-6"))
# How many speech tokens are drawn between asking the GPU whether the stop token came (the tokens are the same as asking
# after every one; measured on an RTX 4070, 11.3 -> 11.1 ms a token, the same 30 ms with another program using the card).
CHECK_EVERY = 4
# Chatterbox Turbo (huggingface.co/ResembleAI/chatterbox-turbo, MIT) at this revision.
REVISION = "749d1c1a46eb10492095d68fbcf55691ccf137cd"

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


@dataclass(frozen=True)
class PinnedModel:
    """One Chatterbox model a host role installs: its Hugging Face repository at one revision, and the files the service
    downloads, verifies and loads (role -> file name, bytes, SHA-256, artifact ID, licence). family "turbo" speaks with
    Turbo's GPT-2 decoder and reads the tags; "original" is the 500M Llama model with exaggeration and CFG weight."""

    key: str
    name: str
    repository: str
    revision: str
    family: str
    files: dict[str, tuple[str, int, str, str, str]]

    @property
    def base_url(self) -> str:
        return f"https://huggingface.co/{self.repository}/resolve/{self.revision}/"


def _renamed(files: dict[str, tuple[str, int, str, str, str]], key: str) -> dict[str, tuple[str, int, str, str, str]]:
    return {role: (name, size, sha256, artifact.replace("chatterbox-turbo", key), licence)
            for role, (name, size, sha256, artifact, licence) in files.items()}


# Chatterbox Nano (huggingface.co/ResembleAI/chatterbox-nano, MIT): Turbo's architecture with a GPT-2 small backbone. Its
# decoder, voice encoder and tokenizer files are byte for byte Turbo's; only T3 differs.
NANO_REVISION = "71ccd1d0081b430592cea481f4307e764e07bc64"
# The original 500M English Chatterbox (huggingface.co/ResembleAI/chatterbox, MIT): its own T3, decoder and tokenizer, and the
# same voice encoder. The repository's built-in voice (conds.pt) and multilingual weights are not downloaded.
ORIGINAL_REVISION = "5bb1f6ee58e50c3b8d408bc82a6d3740c2db6e18"
PINNED_MODELS: dict[str, PinnedModel] = {
    "chatterbox-turbo": PinnedModel("chatterbox-turbo", "Chatterbox Turbo", "ResembleAI/chatterbox-turbo", REVISION, "turbo",
                                    PINNED_FILES),
    "chatterbox-nano": PinnedModel("chatterbox-nano", "Chatterbox Nano", "ResembleAI/chatterbox-nano", NANO_REVISION, "turbo", {
        **_renamed(PINNED_FILES, "chatterbox-nano"),
        "model_weights": ("t3_nano_v1.safetensors", 869_899_204,
                          "72b110185087d945dbdf54dee4e333848e1811bdd5fd6cb16ceb8da50006f0c9", "chatterbox-nano", "MIT"),
    }),
    "chatterbox-original": PinnedModel("chatterbox-original", "Chatterbox Original", "ResembleAI/chatterbox", ORIGINAL_REVISION,
                                       "original", {
        "model_weights": ("t3_cfg.safetensors", 2_129_653_744,
                          "914cb1696f47527fe8852ca8f1fe1fa63cb34f76f9c715e84e067b744dd0da81", "chatterbox-original", "MIT"),
        "s3gen_weights": ("s3gen.safetensors", 1_056_484_620,
                          "2b78103c654207393955e4900aac14a12de8ef25f4b09424f1ef91941f161d4e", "chatterbox-original-s3gen", "MIT"),
        "voice_encoder_weights": ("ve.safetensors", 5_695_784,
                                  "f0921cab452fa278bc25cd23ffd59d36f816d7dc5181dd1bef9751a7fb61f63c",
                                  "chatterbox-original-voice-encoder", "MIT"),
        "tokenizer": ("tokenizer.json", 25_470, "d71e3a44eabb1784df9a68e9f95b251ecbf1a7af6a9f50835856b2ca9d8c14a5",
                      "chatterbox-original-tokenizer", "MIT"),
    }),
}


def _pinned(model: str | None = None) -> PinnedModel:
    """The pinned model CHATTERBOX_MODEL (or model) names; Turbo for a name this service doesn't install."""
    return PINNED_MODELS.get(model or MODEL, PINNED_MODELS["chatterbox-turbo"])


# Resemble's GPT-2 small configuration for Nano (chatterbox master, models/t3/llama_configs.py), which chatterbox-tts 0.1.7
# predates: loading Nano adds it to the library's table.
GPT2_SMALL_CONFIG: dict[str, Any] = {
    "activation_function": "gelu_new", "architectures": ["GPT2LMHeadModel"], "attn_pdrop": 0.1, "bos_token_id": 50256,
    "embd_pdrop": 0.1, "eos_token_id": 50256, "initializer_range": 0.02, "layer_norm_epsilon": 1e-05, "model_type": "gpt2",
    "n_ctx": 8196, "n_embd": 768, "hidden_size": 768, "n_head": 12, "n_layer": 12, "n_positions": 8196, "n_special": 0,
    "predict_special_tokens": True, "resid_pdrop": 0.1, "summary_activation": None, "summary_first_dropout": 0.1,
    "summary_proj_to_labels": True, "summary_type": "cls_index", "summary_use_proj": True,
    "task_specific_params": {"text-generation": {"do_sample": True, "max_length": 50}}, "vocab_size": 50276,
}

# The original model's two ways of speaking (Resemble's tips): general (exaggeration 0.5, CFG weight 0.5) for sentences without
# a tag, expressive (0.7, 0.3: more emotion, and a lower CFG weight for slower, more deliberate pacing) for a sentence that
# starts with [expressive]. The desktop may send other values (Companion › Voice); these are the ranges Resemble's app offers.
EXPRESSIVE_TAG = "[expressive]"
EXAGGERATION_RANGE = (0.25, 2.0)
CFG_WEIGHT_RANGE = (0.0, 1.0)
# The most speech tokens (25 a second) a piece of the original model may take: so many plus so many per text token (its
# tokenizer splits text into characters and short pieces), never more than its 1,000. Measured on CPU (docs/CHATTERBOX_VOICE.md).
ORIGINAL_TOKENS_BASE = int(os.environ.get("MARTLET_CHATTERBOX_ORIGINAL_TOKENS_BASE", "100"))
ORIGINAL_TOKENS_PER_TEXT_TOKEN = int(os.environ.get("MARTLET_CHATTERBOX_ORIGINAL_TOKENS_PER_TEXT_TOKEN", "10"))


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


def _artifact(role: str, size: int, sha256: str, artifact_id: str, license_id: str, revision: str = REVISION) -> dict[str, Any]:
    return {"artifact_id": artifact_id, "bytes": size, "license_id": license_id, "revision": revision, "role": role, "sha256": sha256}


def _real_artifacts(pinned: PinnedModel | None = None) -> list[dict[str, Any]]:
    pinned = pinned or _pinned()
    return [_artifact(role, size, sha256, artifact_id, license_id, pinned.revision)
            for role, (_name, size, sha256, artifact_id, license_id) in pinned.files.items()]


def _fixture_artifacts(real_model_identity: bool = False, pinned: PinnedModel | None = None) -> list[dict[str, Any]]:
    pinned = pinned or _pinned()
    artifacts: list[dict[str, Any]] = []
    for index, role in enumerate(pinned.files, 1):
        if real_model_identity and role == "model_weights":
            _name, size, sha256, artifact_id, license_id = pinned.files[role]
            artifacts.append(_artifact(role, size, sha256, artifact_id, license_id, pinned.revision))
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


def _identity(engine: str, *, real_model_identity: bool = False, pinned: PinnedModel | None = None) -> dict[str, Any]:
    fixture = engine == "fake"
    source = Path(__file__)
    return {
        "artifacts": _fixture_artifacts(real_model_identity, pinned) if fixture else _real_artifacts(pinned),
        "cancellation": "discard_only",
        "contract_id": CONTRACT_ID,
        "evidence": "synthetic_fixture" if fixture else "live_worker",
        "incremental_within_chunk_synthesis": False,
        "pcm_transport_streaming": True,
        "protocol_version": dict(PROTOCOL_VERSION),
        "runtime": {
            "chatterbox_tts_version": "0.1.7" if not fixture else "fixture",
            "cuda_runtime_version": "12.8" if not fixture else "fixture",
            "python_version": platform.python_version(),
            "torch_version": "2.8.0+cu128" if not fixture else "fixture",
            "torchaudio_version": "2.8.0+cu128" if not fixture else "fixture",
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
    pinned = PINNED_MODELS.get(MODEL)
    if pinned is None:
        raise SystemExit(f"CHATTERBOX_MODEL={MODEL} is not a model this service installs ({', '.join(PINNED_MODELS)}).")
    if fixture:
        _log("FIXTURE - NOT AI: provisioning deterministic synthetic tone engine (no torch, no GPU, no model).")
        files: dict[str, str] = {}
        for role in pinned.files:
            relative = f"models/fixture/{role}.fixture"
            path = ROOT / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(f"FIXTURE - NOT AI: {role}\n", encoding="utf-8")
            files[role] = relative
        _write_config({"device": None, "engine": "fake", "files": files, "identity": _identity("fake", pinned=pinned), "model": MODEL})
        _log("Worker configuration written (fixture).")
        return

    _log(f"{pinned.name} model license: MIT, https://huggingface.co/{pinned.repository}")
    model_dir = MODELS / MODEL
    files = {}
    for role, (filename, size, sha256, _artifact_id, _license_id) in pinned.files.items():
        _download(pinned.base_url + filename, model_dir / filename, size, sha256)
        files[role] = f"models/{MODEL}/{filename}"
    _write_config({"device": DEVICE, "engine": "chatterbox", "files": files, "identity": _identity("chatterbox", pinned=pinned),
                   "model": MODEL})
    _log(f"Worker configuration written ({MODEL}, live {pinned.name} engine).")


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
class VoiceStyle:
    """How the original model speaks, as (exaggeration, CFG weight): general for a sentence without a tag, expressive for one
    that starts with [expressive]. Turbo and Nano have neither setting."""

    general: tuple[float, float] = (0.5, 0.5)
    expressive: tuple[float, float] = (0.7, 0.3)

    def wire(self) -> dict[str, Any]:
        return {name: {"cfg_weight": cfg, "exaggeration": exaggeration}
                for name, (exaggeration, cfg) in (("expressive", self.expressive), ("general", self.general))}


DEFAULT_STYLE = VoiceStyle()


@dataclass(frozen=True)
class SynthesisRequest:
    ids: RequestIds
    deadline_utc: datetime
    reference: Reference
    chunks: tuple[Chunk, ...]
    style: VoiceStyle = DEFAULT_STYLE


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


def _parse_style(source: Any) -> VoiceStyle:
    """The request's style (optional: the desktop sends it only to the original model), each value within Resemble's ranges."""
    if source is None:
        return DEFAULT_STYLE
    style = require_object(source, "style")
    require(set(style) == {"general", "expressive"}, "invalid_request", "style must have general and expressive.")

    def pair(name: str) -> tuple[float, float]:
        value = require_object(style.get(name), f"style.{name}")
        require(set(value) == {"exaggeration", "cfg_weight"}, "invalid_request", f"style.{name} must have exaggeration and cfg_weight.")
        exaggeration, cfg = value.get("exaggeration"), value.get("cfg_weight")
        require(type(exaggeration) in (int, float) and type(cfg) in (int, float), "invalid_request", f"style.{name} values must be numbers.")
        require(EXAGGERATION_RANGE[0] <= exaggeration <= EXAGGERATION_RANGE[1] and CFG_WEIGHT_RANGE[0] <= cfg <= CFG_WEIGHT_RANGE[1],
                "invalid_request", f"style.{name} is out of range (exaggeration 0.25-2, CFG weight 0-1).")
        return float(exaggeration), float(cfg)

    return VoiceStyle(pair("general"), pair("expressive"))


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
        style=_parse_style(body.get("style")),
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


class IdleCheck:
    """The brief run of the model while nobody speaks (see EngineHost.idle_check); a reply that arrives stops it."""

    def __init__(self) -> None:
        self.cancel_requested = False


class EngineHost:
    def __init__(self) -> None:
        self.lock = threading.Lock()
        self.changed = threading.Condition(self.lock)
        self.state = "not_provisioned" if not CONFIG.is_file() else "cold"
        self.error: str | None = None
        self.identity: dict[str, Any] | None = None
        self.engine_kind: str | None = None
        self.model: Any = None
        self.fast: FastTurbo | None = None
        self.active: Job | None = None
        self.loading = False
        # Set once a model has loaded in this process: only then can a broken graphics context be this process's own.
        self.loaded_once = False
        # The graphics context broke: the service is about to exit so Docker starts it fresh; nothing loads meanwhile.
        self.restarting = False
        self.idle: IdleCheck | None = None
        self.last_activity = time.monotonic()
        self.idle_checks = 0
        self.idle_last_ms: int | None = None
        self.idle_fastest_ms: int | None = None
        # Sentences whispered since the service started (Whisperer): what /status reports as whisper.parts.
        self.whispered_parts = 0
        # The original model (OriginalVoice), its sentences said expressively since the service started, and the style the last
        # reply asked for (what /status reports as style).
        self.original: OriginalVoice | None = None
        self.expressive_parts = 0
        self.last_style: VoiceStyle | None = None
        # On the CPU: PyTorch's threads and the CPUs the service is pinned to (_use_cpu); None on a GPU.
        self.cpu: dict[str, Any] | None = None

    def status(self) -> dict[str, Any]:
        with self.lock:
            model = getattr(self.model, "device", None)
            status = {
                "cpu": self.cpu,
                # The meanflow decoder's steps a whole piece takes (Turbo and Nano; CPU_DECODER_STEPS on the CPU).
                "decoder_steps": None if self.fast is None else self.fast.decoder_steps,
                "device": None if self.model is None else str(model or DEVICE),
                "error": self.error,
                "idle_check": {"checks": self.idle_checks, "every_seconds": IDLE_CHECK_SECONDS, "fastest_ms": self.idle_fastest_ms,
                               "last_ms": self.idle_last_ms},
                "model": MODEL,
                "ready": self.state == "ready",
                "state": self.state,
                "whisper": {"level_db": WHISPER_DB, "parts": self.whispered_parts},
                "worker": self.identity,
            }
            if _pinned().family == "original":
                status["style"] = {"default": DEFAULT_STYLE.wire(), "expressive_parts": self.expressive_parts,
                                   "last": None if self.last_style is None else self.last_style.wire()}
            return status

    def start_loading(self) -> None:
        with self.lock:
            if self.loading or self.restarting or self.state in {"ready", "busy", "loading_model"}:
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
        # A model dropped after a failure must be gone from graphics memory before the next one loads, or the card needs
        # room for both.
        _free_gpu_memory()
        engine: str | None = None
        pinned: PinnedModel | None = None
        device = DEVICE
        cpu: dict[str, Any] | None = None
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
                # The original model's decoding loop draws a progress bar for every speech token; the log needs none.
                os.environ.setdefault("TQDM_DISABLE", "1")
                pinned = PINNED_MODELS.get(str(config.get("model") or MODEL))
                if pinned is None:
                    raise RuntimeError(f"The configured model {config.get('model')!r} is not one this service installs.")
                device = _device(str(config.get("device") or DEVICE))
                if problem := _gpu_problem(device):
                    raise RuntimeError(problem)
                if device.split(":")[0] == "cpu":
                    cpu = _use_cpu()
                model = _load_model(pinned, MODELS / pinned.key, device)
            else:
                raise RuntimeError("Unknown Chatterbox engine configuration.")
            # Faster speech for the same words and sound: kept voice conditionals and CUDA-graph decoding, warmed up now so
            # the first reply doesn't pay for it. The original model keeps its conditionals the same way (OriginalVoice).
            fast = (FastTurbo(model, graph=os.environ.get("MARTLET_CHATTERBOX_FAST", "1") != "0", name=pinned.name)
                    if pinned is not None and pinned.family == "turbo" else None)
            original = OriginalVoice(model) if pinned is not None and pinned.family == "original" else None
            if fast is not None:
                fast.warm()
            if original is not None:
                original.warm()
        except Exception as exc:
            with self.lock:
                self.model = None
                self.fast = None
                self.original = None
                self.cpu = None
                self.engine_kind = None
                self.identity = None
                self.state = "failed"
                self.error = f"The Chatterbox worker could not load: {exc}"
                self.loading = False
                reloading = self.loaded_once
                self.changed.notify_all()
            traceback.print_exc()
            # Reloading after a failed reply in a process whose graphics context broke can't work; a fresh process can.
            # (A fresh process that fails to load is not restarted: it would fail the same way.)
            if reloading and engine == "chatterbox" and (broken := _cuda_broken(device)):
                self._restart(broken)
            return
        with self.lock:
            self.model = model
            self.fast = fast
            self.original = original
            self.cpu = cpu
            self.engine_kind = engine
            self.identity = identity
            self.state, self.error = "ready", None
            self.loading = False
            self.loaded_once = True
            self.last_activity = time.monotonic()
            self.changed.notify_all()

    def _restart(self, why: str) -> None:
        """The graphics context is broken (why): say so, refuse new replies and exit so Docker starts the service fresh."""
        with self.lock:
            if self.restarting:
                return
            self.restarting = True
            self.state = "failed"
            self.error = f"The graphics card's CUDA context broke ({why}); the voice service is restarting."
            self.changed.notify_all()
        _log(f"The graphics card's CUDA context broke ({why}); exiting so Docker restarts the voice service with a fresh one.")
        # A moment for the failed reply's last event to reach the relay first.
        timer = threading.Timer(RESTART_DELAY_SECONDS, _restart_process)
        timer.daemon = True
        timer.start()

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
            # A reply stops an idle check at its next speech token instead of waiting for it.
            if self.idle is not None:
                self.idle.cancel_requested = True
            while ((self.state != "ready" or self.active is not None or self.idle is not None)
                   and self.state not in {"not_provisioned", "failed"}):
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                self.changed.wait(remaining)

    def run(self, job: Job) -> None:
        failed_engine: str | None = None
        broken: str | None = None
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
                with self.lock:
                    original = self.original
                if kind == "fake":
                    for chunk in job.request.chunks:
                        pcm = engine.generate(chunk.text, job)
                        if not job.emit_pcm(chunk.index, pcm) or not job.chunk_completed(chunk.index):
                            return
                elif original is not None:
                    if not self._speak_original(job, original):
                        return
                else:
                    with self.lock:
                        fast = self.fast
                    started = time.monotonic()
                    cached = False
                    if fast is not None:
                        # The reference's conditionals are computed once and kept, not for every sentence.
                        cached = fast.use_reference(job.request.reference.audio)
                    else:
                        reference_path = _write_private_reference(job.request.reference.audio)
                    samples = 0
                    first_audio: float | None = None
                    streaming = fast is not None and fast.streams
                    capped = 0
                    whispered_parts = 0
                    for chunk in job.request.chunks:
                        if job.cancel_requested:
                            return
                        text = _speakable(chunk.text)
                        if not text:
                            # Nothing Turbo should say (only quotation marks): an empty piece, not the library's stand-in sentence.
                            if not job.chunk_completed(chunk.index):
                                return
                            continue
                        # A sentence starting with [whispering] is whispered here: Turbo itself doesn't.
                        for part, whisper in _whisper_parts(text):
                            whispered_parts += whisper
                            spoken = False
                            if streaming:
                                # Spoken as it is made: the first audio leaves after about a dozen speech tokens.
                                try:
                                    for pcm in fast.stream(part, cancelled=lambda: job.cancel_requested, whisper=whisper):
                                        spoken = True
                                        first_audio = first_audio if first_audio is not None else time.monotonic() - started
                                        samples += len(pcm) // 2
                                        if not job.emit_pcm(chunk.index, pcm):
                                            return
                                except CacheTooSmall:
                                    pass
                                if job.cancel_requested:
                                    return
                            if not spoken:
                                pcm = _generate_with_memory_retry(engine, part, reference_path, whisper=whisper)
                                first_audio = first_audio if first_audio is not None else time.monotonic() - started
                                samples += len(pcm) // 2
                                if not job.emit_pcm(chunk.index, pcm):
                                    return
                            if fast is not None and fast.graph is not None and fast.graph.capped:
                                capped += 1
                        if not job.chunk_completed(chunk.index):
                            return
                    if whispered_parts:
                        with self.lock:
                            self.whispered_parts += whispered_parts
                    # Timing only, never the text: how long this reply's speech took to make.
                    _log(f"Made {samples / 24_000:.2f} s of speech in {(time.monotonic() - started) * 1000:.0f} ms, first audio after "
                         f"{(first_audio or 0) * 1000:.0f} ms ({'kept' if cached else 'new'} voice conditionals, "
                         f"{'CUDA graph' if fast is not None and fast.graph_ready else 'eager'} decoding"
                         f"{', streamed' if streaming else ''}"
                         f"{f', {whispered_parts} part(s) whispered' if whispered_parts else ''}"
                         f"{f', {capped} piece(s) stopped at their speech-token limit' if capped else ''}).")
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
                # A device-side assert or an illegal memory access leaves the graphics context unusable until the process
                # exits; reloading the model here would fail every reply until someone restarted the container.
                broken = _cuda_broken(str(getattr(self.model, "device", None) or DEVICE)) if kind == "chatterbox" else None
                job.failed(ContractError("internal_failure", f"The Chatterbox engine failed while synthesizing this reply ({detail})"
                    + ("; the voice service is restarting." if broken else "."), stage="synthesis", action_id="chatterbox.restart-worker"))
        finally:
            dropped: FastTurbo | OriginalVoice | None = None
            with self.lock:
                if self.active is job:
                    self.active = None
                self.last_activity = time.monotonic()
                if failed_engine is not None:
                    dropped = self.fast or self.original
                    self.model = None
                    self.fast = None
                    self.original = None
                    self.state = "failed"
                    self.error = f"The Chatterbox engine failed ({failed_engine}); the next reply will reload it."
                elif self.state == "busy":
                    self.state = "ready"
                self.changed.notify_all()
            if dropped is not None:
                # Its CUDA graph and the reference it keeps on the model would hold the old model in graphics memory.
                dropped.close()
            if broken is not None:
                self._restart(broken)

    def _speak_original(self, job: Job, original: "OriginalVoice") -> bool:
        """The original model: each part of each piece spoken whole, as the reply's style says (VoiceStyle: general, or
        expressive for a sentence that starts with [expressive]; [whispering] whispers it). False when the reply stopped."""
        started = time.monotonic()
        cached = original.use_reference(job.request.reference.audio)
        samples = 0
        first_audio: float | None = None
        whispered_parts = expressive_parts = capped = 0
        for chunk in job.request.chunks:
            if job.cancel_requested:
                return False
            text = _speakable(chunk.text)
            for part, expressive, whisper in _original_parts(text) if text else []:
                if job.cancel_requested:
                    return False
                whispered_parts += whisper
                expressive_parts += expressive
                exaggeration, cfg_weight = job.request.style.expressive if expressive else job.request.style.general
                pcm = original.speak(part, exaggeration, cfg_weight, whisper)
                capped += original.capped
                first_audio = first_audio if first_audio is not None else time.monotonic() - started
                samples += len(pcm) // 2
                if not job.emit_pcm(chunk.index, pcm):
                    return False
            if not job.chunk_completed(chunk.index):
                return False
        with self.lock:
            self.whispered_parts += whispered_parts
            self.expressive_parts += expressive_parts
            self.last_style = job.request.style
        # Timing only, never the text.
        _log(f"Made {samples / 24_000:.2f} s of speech in {(time.monotonic() - started) * 1000:.0f} ms, first audio after "
             f"{(first_audio or 0) * 1000:.0f} ms ({'kept' if cached else 'new'} voice conditionals, original model on "
             f"{original.device}{f', {expressive_parts} part(s) expressive' if expressive_parts else ''}"
             f"{f', {whispered_parts} part(s) whispered' if whispered_parts else ''}"
             f"{f', {capped} piece(s) stopped at their speech-token limit' if capped else ''}).")
        return True

    def idle_check(self) -> bool:
        """Runs the model briefly when nobody has spoken for IDLE_CHECK_SECONDS: speech tokens, the decoder, the vocoder and the
        watermark, on the warm-up's synthetic voice (FIXTURE - NOT a voice), output thrown away. That brings a model Windows
        moved out of graphics memory back before the next reply instead of during it, says in the log when it was slow
        (evidence of it), and finds a broken graphics context before a reply does. A reply stops it at its next token.
        True when it ran."""
        with self.lock:
            fast = self.fast
            device = str(getattr(self.model, "device", None) or DEVICE)
            # Only a graphics card's memory needs it (Windows moves an idle model out of it); on the CPU it would only cost time.
            if (fast is None or self.state != "ready" or self.active is not None or self.idle is not None or self.restarting
                    or self.engine_kind != "chatterbox" or not device.startswith("cuda")
                    or time.monotonic() - self.last_activity < IDLE_CHECK_SECONDS):
                return False
            idle = self.idle = IdleCheck()
        started = time.monotonic()
        failure: Exception | None = None
        try:
            fast.idle_pass(lambda: idle.cancel_requested)
        except Exception as exc:  # noqa: BLE001 - reported below; the model stays for the next reply
            failure = exc
        elapsed_ms = int((time.monotonic() - started) * 1000)
        fastest: int | None = None
        with self.lock:
            self.idle = None
            self.last_activity = time.monotonic()
            if failure is None and not idle.cancel_requested:
                fastest = self.idle_fastest_ms
                self.idle_checks += 1
                self.idle_last_ms = elapsed_ms
                self.idle_fastest_ms = elapsed_ms if fastest is None else min(fastest, elapsed_ms)
            self.changed.notify_all()
        if failure is not None:
            detail = _failure_detail(failure)
            if _out_of_memory(failure):
                _free_gpu_memory()
                _log(f"Idle check: the graphics card was out of memory ({detail}); other programs are using it.")
            elif broken := _cuda_broken(device):
                self._restart(broken)
            else:
                _log(f"Idle check failed ({detail}); the model stays loaded.")
        elif not idle.cancel_requested and elapsed_ms > (5_000 if fastest is None else max(2_000, 4 * fastest)):
            _log(f"Idle check took {elapsed_ms} ms{f' (fastest {fastest} ms)' if fastest is not None else ''}: the graphics card was "
                 "busy, or Windows had moved the voice model out of graphics memory while other programs used it; it is back now.")
        return True

    def idle_loop(self) -> None:
        while True:
            time.sleep(max(1.0, min(30.0, IDLE_CHECK_SECONDS / 10)))
            try:
                self.idle_check()
            except Exception:  # noqa: BLE001 - the idle check must never stop the service
                traceback.print_exc()


def _failure_detail(exc: BaseException) -> str:
    """The exception's type and first line, bounded: what the host's log shows for a failed reply. Never the reply text."""
    first = (str(exc).strip().splitlines() or [""])[0]
    text = f"{type(exc).__name__}: {first}" if first else type(exc).__name__
    text = "".join(ch if ch.isprintable() else " " for ch in text)
    return text if len(text) <= 240 else text[:240] + "..."


def _out_of_memory(exc: BaseException) -> bool:
    return type(exc).__name__ == "OutOfMemoryError" or "out of memory" in str(exc).lower()


def _cuda_broken(device: str) -> str | None:
    """Why this process can no longer use the graphics card (a sticky CUDA error such as a device-side assert, an illegal
    memory access or a launch failure poisons the context until the process exits), or None when a tiny operation still
    runs, the card is only out of memory, or there is no CUDA here."""
    try:
        import torch  # type: ignore
    except ImportError:
        return None
    if not str(device).startswith("cuda") or not torch.cuda.is_available():
        return None
    try:
        probe = torch.ones(1, device=device)
        probe.add_(1)
        torch.cuda.synchronize(probe.device)
        return None
    except Exception as exc:  # noqa: BLE001 - whatever stops a tiny operation means the context is gone
        return None if _out_of_memory(exc) else _failure_detail(exc)


def _restart_process() -> None:
    """Exits so Docker (restart: unless-stopped) starts the service again with a fresh CUDA context."""
    sys.stdout.flush()
    sys.stderr.flush()
    os._exit(RESTART_EXIT_CODE)


_DOUBLE_QUOTES = dict.fromkeys(map(ord, "\"\u201c\u201d\u201e\u00ab\u00bb"))


def _speakable(text: str) -> str:
    """A piece as Turbo should read it: without double quotation marks, which it can say as a breath or a sigh
    (resemble-ai/chatterbox#433), and with single spaces. Apostrophes stay."""
    return " ".join(text.translate(_DOUBLE_QUOTES).split())


_SENTENCE_END = re.compile(r"[.!?]+[\"')\]\u2019\u201d]*(?=\s|$)")
_WHISPER = re.compile(re.escape(WHISPER_TAG), re.IGNORECASE)
_WORD = re.compile(r"\w")


def _whisper_parts(text: str) -> list[tuple[str, bool]]:
    """A piece as (text, whispered) parts in order: from each [whispering] to the end of its sentence (. ! or ?, or the
    piece's end) whispered, as the Thinking prompt promises a tone; the rest in the voice. Neighbouring whispered sentences
    stay one part; a [whispering] with nothing after it in its sentence whispers nothing. The tag stays in the text."""
    parts: list[tuple[str, bool]] = []

    def add(part: str, whisper: bool) -> None:
        part = part.strip()
        if not part:
            return
        if whisper and not _WORD.search(_WHISPER.sub("", part)):
            whisper = False
        if parts and parts[-1][1] == whisper:
            parts[-1] = (f"{parts[-1][0]} {part}", whisper)
        else:
            parts.append((part, whisper))

    position = 0
    while tag := _WHISPER.search(text, position):
        add(text[position:tag.start()], False)
        end = _SENTENCE_END.search(text, tag.end())
        stop = end.end() if end else len(text)
        add(text[tag.start():stop], True)
        position = stop
    add(text[position:], False)
    return parts


_STYLE_TAGS = re.compile(r"\[(expressive|whispering)\]", re.IGNORECASE)


def _original_parts(text: str) -> list[tuple[str, bool, bool]]:
    """A piece for the original model as (text, expressive, whispered) parts in order: from each [expressive] or [whispering]
    to the end of its sentence (. ! or ?, or the piece's end) in that style, both when both start it, as the Thinking prompt
    promises a tone; the rest in the general voice. The original model has no tag tokens, so the tags are left out of what it
    reads. Neighbouring parts in the same style stay one part; a tag with no words after it in its sentence styles nothing."""
    parts: list[tuple[str, bool, bool]] = []

    def add(part: str, expressive: bool, whisper: bool) -> None:
        part = " ".join(_STYLE_TAGS.sub(" ", part).split())
        if not part:
            return
        if not _WORD.search(part):
            expressive = whisper = False
        if parts and parts[-1][1:] == (expressive, whisper):
            parts[-1] = (f"{parts[-1][0]} {part}", expressive, whisper)
        else:
            parts.append((part, expressive, whisper))

    position = 0
    while tag := _STYLE_TAGS.search(text, position):
        add(text[position:tag.start()], False, False)
        end = _SENTENCE_END.search(text, tag.end())
        stop = end.end() if end else len(text)
        styles = {found.group(1).lower() for found in _STYLE_TAGS.finditer(text, tag.start(), stop)}
        add(text[tag.start():stop], "expressive" in styles, "whispering" in styles)
        position = stop
    add(text[position:], False, False)
    return parts


class Whisperer:
    """A voice turned into a whisper as its audio arrives (float samples in, as many out): each 25 ms frame's spectral
    envelope (linear prediction, order 2 + 1 per kHz) shapes white noise instead of the voice's pulses, as breath through a
    whispering mouth does, high-passed at 300 Hz (where a whisper has no voice) and then set to the frame's own level
    WHISPER_DB lower; the frames are overlap-added under sine windows, so the noise's level stays even. What arrives is
    whispered in order whatever the chunks, the same as all at once; the last 25-40 ms waits for the next audio or
    finish()."""

    def __init__(self, rate: int = 24_000, seed: int = 0, gain_db: float | None = None) -> None:
        import numpy as np  # type: ignore
        from scipy.signal import butter  # type: ignore

        self.np = np
        self.order = 2 + rate // 1000
        self.frame = int(0.025 * rate) // 2 * 2
        self.hop = self.frame // 2
        hann = np.hanning(self.frame + 1)[:-1]
        self.analysis = hann
        self.analysis_energy = float(np.dot(hann, hann))
        self.synthesis = np.sqrt(hann)
        self.expand = 0.994 ** np.arange(self.order + 1)
        self.size = 1 << (2 * self.frame - 1).bit_length()
        self.gain = 10 ** ((WHISPER_DB if gain_db is None else gain_db) / 20)
        self.sos = butter(4, 300, "highpass", fs=rate, output="sos")
        self.rng = np.random.default_rng(seed)
        # Half a frame of silence first, so the first frame is centred on the first sample; its output is dropped.
        self.pending = np.zeros(self.hop)
        self.tail = np.zeros(self.hop)
        self.skip = self.hop
        self.received = 0
        self.sent = 0

    def feed(self, samples: Any) -> Any:
        """The whispered audio ready so far for these samples."""
        samples = self.np.asarray(samples, dtype=self.np.float64).reshape(-1)
        self.received += samples.size
        return self._push(samples)

    def finish(self) -> Any:
        """The rest of the whisper, so that as many samples came out as went in."""
        return self._push(self.np.zeros(self.frame + self.hop))

    def _push(self, samples: Any) -> Any:
        np = self.np
        self.pending = np.concatenate([self.pending, samples])
        ready = []
        while self.pending.size >= self.frame:
            ready.append(self._frame(self.pending[: self.frame]))
            self.pending = self.pending[self.hop:]
        if not ready:
            return np.zeros(0, dtype=np.float32)
        out = np.concatenate(ready)
        dropped = min(self.skip, out.size)
        self.skip -= dropped
        out = out[dropped:][: max(0, self.received - self.sent)]
        self.sent += out.size
        return np.clip(out, -1.0, 1.0).astype(np.float32)

    def _frame(self, x: Any) -> Any:
        """The finished first half of this frame's slot: the last frame's second half plus this frame's first."""
        np = self.np
        windowed = x * self.analysis
        power = float(np.dot(windowed, windowed)) / self.analysis_energy
        y = np.zeros(self.frame)
        if power > 1e-10:
            from scipy.linalg import solve_toeplitz  # type: ignore
            from scipy.signal import lfilter, sosfilt  # type: ignore

            spectrum = np.fft.rfft(windowed, self.size)
            r = np.fft.irfft(spectrum.real ** 2 + spectrum.imag ** 2, self.size)[: self.order + 1]
            r[0] *= 1.0 + 1e-6
            try:
                a = np.concatenate([[1.0], solve_toeplitz(r[:-1], -r[1:])]) * self.expand
                noise = sosfilt(self.sos, lfilter([1.0], a, self.rng.standard_normal(self.frame)))
                level = float(np.dot(noise, noise)) / self.frame
                if math.isfinite(level) and level > 0:
                    y = noise * (math.sqrt(power / level) * self.gain) * self.synthesis
            except (np.linalg.LinAlgError, ValueError):
                pass
        y[: self.hop] += self.tail
        self.tail = y[self.hop:].copy()
        return y[: self.hop]


def whispered(samples: Any, rate: int = 24_000) -> Any:
    """A whole piece whispered (Whisperer)."""
    import numpy as np  # type: ignore

    whisperer = Whisperer(rate)
    return np.concatenate([whisperer.feed(samples), whisperer.finish()])


@contextmanager
def _whispering(model: Any) -> Any:
    """While inside, what model.generate makes is whispered: its Perth watermarker, which generate() runs on the finished
    speech, whispers the speech first, so the watermark is still applied to (and found in) what is sent."""
    marker = model.watermarker
    own = "apply_watermark" in vars(marker)
    original = marker.apply_watermark

    def apply(wav: Any, sample_rate: int, **kwargs: Any) -> Any:
        return original(whispered(wav, int(sample_rate)), sample_rate=sample_rate, **kwargs)

    marker.apply_watermark = apply
    try:
        yield
    finally:
        if own:
            marker.apply_watermark = original
        else:
            del marker.apply_watermark


def _free_gpu_memory() -> None:
    try:
        import gc

        gc.collect()
        import torch  # type: ignore

        if torch.cuda.is_available():
            torch.cuda.empty_cache()
    except Exception:
        pass


def _generate_with_memory_retry(model: Any, text: str, reference_path: Path | None, whisper: bool = False) -> bytes:
    """One sentence (whispered when whisper is set); when the graphics card runs out of memory, free the cache once and try
    again."""
    with _whispering(model) if whisper else nullcontext():
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


def _gpu_problem(device: str) -> str | None:
    """Why this image's PyTorch has no kernels for the graphics card, or None (CUDA only fails at the first kernel launch,
    with "no kernel image is available")."""
    import torch  # type: ignore

    if not device.startswith("cuda") or not torch.cuda.is_available():
        return None
    index = torch.device(device).index or 0
    major, minor = torch.cuda.get_device_capability(index)
    archs = list(torch.cuda.get_arch_list())

    def capability(arch: str) -> tuple[int, int]:
        digits = arch.split("_", 1)[1]
        return int(digits[:-1]), int(digits[-1])

    # A cubin runs on later minor revisions of its major architecture; PTX is compiled for any later one.
    if any(capability(a)[0] == major and capability(a)[1] <= minor for a in archs if a.startswith("sm_")):
        return None
    if any(capability(a) <= (major, minor) for a in archs if a.startswith("compute_")):
        return None
    return (
        f"This graphics card ({torch.cuda.get_device_name(index)}, compute capability {major}.{minor}) is not supported by "
        f"PyTorch {torch.__version__} in this image, which has kernels for {', '.join(archs)}. Chatterbox needs an NVIDIA GPU "
        "with compute capability 7.0 or newer (GeForce GTX 16 / RTX 20 series or newer)."
    )


def _device(device: str) -> str:
    """The device to load on: "auto" is the NVIDIA GPU when this container has one (and PyTorch can use it), else the CPU."""
    if device != "auto":
        return device
    import torch  # type: ignore

    return "cuda:0" if torch.cuda.is_available() else "cpu"


def _cpu_list(text: str) -> list[int]:
    """Linux's CPU list format ("0-7,16,18-19") as numbers."""
    cpus: list[int] = []
    for part in text.strip().split(","):
        if part.strip():
            first, _, last = part.strip().partition("-")
            cpus.extend(range(int(first), int(last or first) + 1))
    return cpus


def _performance_cores(sysfs: Path = SYSFS_DEVICES) -> list[int]:
    """The first logical CPU of each performance core of a hybrid Intel CPU, from the CPUs Linux lists in
    /sys/devices/cpu_core/cpus. Empty for any other CPU, or where Linux doesn't say (Windows, most virtual machines)."""
    try:
        listed = _cpu_list((sysfs / "cpu_core" / "cpus").read_text(encoding="ascii"))
    except (OSError, ValueError):
        return []
    cores: dict[str, int] = {}
    for cpu in listed:
        try:
            siblings = (sysfs / "system" / "cpu" / f"cpu{cpu}" / "topology" / "thread_siblings_list").read_text(encoding="ascii")
        except OSError:
            siblings = str(cpu)
        cores.setdefault(siblings.strip(), cpu)
    return sorted(cores.values())


def _cpu_plan(setting: str, physical_cores: int, performance: list[int], allowed: set[int] | None) -> tuple[int, list[int]]:
    """How many threads PyTorch uses on the CPU, and the logical CPUs the service is pinned to (empty: not pinned).
    setting is MARTLET_CHATTERBOX_CPU_THREADS (empty: DEFAULT_CPU_THREADS, never more than the performance cores, or the
    physical cores PyTorch counts when Linux doesn't name performance cores); allowed is the CPUs this process may use."""
    usable = [cpu for cpu in performance if allowed is None or cpu in allowed]
    if setting.strip():
        threads = int(setting)
        if threads < 1:
            raise ValueError(f"MARTLET_CHATTERBOX_CPU_THREADS must be 1 or more, not {setting!r}.")
    else:
        threads = max(1, min(DEFAULT_CPU_THREADS, len(usable) or physical_cores))
    return threads, usable[:threads] if threads <= len(usable) else []


def _use_cpu() -> dict[str, Any]:
    """Sets PyTorch's threads for the CPU and, on a hybrid Intel CPU under Linux, pins every thread of the service to the
    performance cores (_cpu_plan); threads started later (the replies', PyTorch's own) inherit it. Returns what /status
    reports as cpu."""
    import torch  # type: ignore

    allowed = set(os.sched_getaffinity(0)) if hasattr(os, "sched_getaffinity") else None
    threads, cores = _cpu_plan(CPU_THREADS, torch.get_num_threads(), _performance_cores(), allowed)
    torch.set_num_threads(threads)
    if cores:
        try:
            for task in os.listdir("/proc/self/task"):
                try:
                    os.sched_setaffinity(int(task), cores)
                except ProcessLookupError:
                    pass
        except OSError as exc:
            _log(f"Pinning to the performance cores failed ({exc}); the threads run on any core.")
            cores = []
    _log(f"On the CPU: {threads} threads" + (f", pinned to the performance cores (CPUs {_cpu_text(cores)})." if cores else "."))
    return {"pinned_cpus": cores, "threads": threads}


def _cpu_text(cpus: list[int]) -> str:
    return ",".join(str(cpu) for cpu in cpus)


def _load_model(pinned: PinnedModel, model_dir: Path, device: str) -> Any:
    """Loads the pinned model's verified files from model_dir, only from disk (never from_pretrained)."""
    if pinned.key == "chatterbox-turbo":
        from chatterbox.tts_turbo import ChatterboxTurboTTS  # type: ignore

        return ChatterboxTurboTTS.from_local(model_dir, device)
    if pinned.key == "chatterbox-nano":
        return _load_nano(model_dir, device)
    if pinned.key == "chatterbox-original":
        from chatterbox.tts import ChatterboxTTS  # type: ignore

        return ChatterboxTTS.from_local(model_dir, device)
    raise RuntimeError(f"No loader for {pinned.key}.")


def _load_nano(model_dir: Path, device: str) -> Any:
    """Chatterbox Nano as Resemble's own loader builds it (chatterbox master, ChatterboxTurboTTS.from_local(nano=True)): Turbo
    with the GPT-2 small backbone and t3_nano_v1.safetensors. chatterbox-tts 0.1.7 predates it, so the backbone's configuration
    is added to the library's table first."""
    from chatterbox.models.s3gen import S3Gen  # type: ignore
    from chatterbox.models.t3 import T3  # type: ignore
    from chatterbox.models.t3.llama_configs import LLAMA_CONFIGS  # type: ignore
    from chatterbox.models.t3.modules.t3_config import T3Config  # type: ignore
    from chatterbox.models.voice_encoder import VoiceEncoder  # type: ignore
    from chatterbox.tts_turbo import ChatterboxTurboTTS  # type: ignore
    from safetensors.torch import load_file  # type: ignore
    from transformers import AutoTokenizer  # type: ignore

    LLAMA_CONFIGS.setdefault("GPT2_small", GPT2_SMALL_CONFIG)
    ve = VoiceEncoder()
    ve.load_state_dict(load_file(model_dir / "ve.safetensors"))
    ve.to(device).eval()
    hp = T3Config(text_tokens_dict_size=50276)
    hp.llama_config_name = "GPT2_small"
    hp.speech_tokens_dict_size = 6563
    hp.input_pos_emb = None
    hp.speech_cond_prompt_len = 375
    hp.use_perceiver_resampler = False
    hp.emotion_adv = False
    t3 = T3(hp)
    state = load_file(model_dir / "t3_nano_v1.safetensors")
    if "model" in state.keys():
        state = state["model"][0]
    t3.load_state_dict(state)
    del t3.tfmr.wte
    t3.to(device).eval()
    s3gen = S3Gen(meanflow=True)
    s3gen.load_state_dict(load_file(model_dir / "s3gen_meanflow.safetensors"), strict=True)
    s3gen.to(device).eval()
    tokenizer = AutoTokenizer.from_pretrained(model_dir)
    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token
    return ChatterboxTurboTTS(t3, s3gen, ve, tokenizer, device)


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


def _real_generate_pcm(model: Any, text: str, reference_path: Path | None) -> bytes:
    # Without a path the model speaks with the voice conditionals FastTurbo kept for this reply's reference.
    wav = model.generate(text, audio_prompt_path=str(reference_path)) if reference_path is not None else model.generate(text)
    return _pcm16(wav, int(getattr(model, "sr", 24_000) or 24_000))


def _pcm16(wav: Any, sr: int) -> bytes:
    """Float speech (a tensor or an array, any shape) as 24 kHz mono PCM16, resampled when the model made another rate."""
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


class FastTurbo:
    """Makes Chatterbox Turbo answer sooner without changing what it says or how it sounds.

    - Voice conditionals (speaker embedding, prompt speech tokens, reference mel) are computed once per reference recording,
      keyed by its SHA-256, instead of for every sentence: about 140 ms a sentence, and several seconds the first time after
      a start (librosa/numba warm-up), which warm() now pays while the model loads.
    - T3's token-by-token decoding replays one captured CUDA graph per token over a static KV cache instead of launching
      hundreds of small kernels each step: about 6 ms a token instead of 17-25 ms on an RTX 5080 under Docker/WSL2, with the
      same logits and the library's own sampling. A sentence it can't decode that way (no CUDA, a capture failure, a prompt
      too long for the cache) uses the library's own loop.
    """

    CACHED_REFERENCES = 2
    # The model's name for the log (Chatterbox Turbo or Chatterbox Nano, which shares Turbo's decoding).
    name = "Chatterbox Turbo"

    def __init__(self, model: Any, *, graph: bool = True, name: str = "Chatterbox Turbo") -> None:
        self.model = model
        self.name = name
        self.conditionals: OrderedDict[str, Any] = OrderedDict()
        self.graph: _T3Graph | None = None
        self.graph_error: str | None = None
        # The warm-up's synthetic conditionals (FIXTURE - NOT a voice), kept for the idle check.
        self.warm_conds: Any = None
        self._original = model.t3.inference_turbo
        if graph:
            try:
                self.graph = _T3Graph(model.t3, model.device)
            except Exception as exc:  # noqa: BLE001 - any failure only means the library's own decoding
                self.graph_error = _failure_detail(exc)
        # Decoding goes through _inference_turbo with or without the graph, so a piece that never stops ends at its budget on
        # the CPU too.
        model.t3.inference_turbo = self._inference_turbo
        # The library's generate() always asks the meanflow decoder for 2 steps; on the CPU a whole piece takes
        # CPU_DECODER_STEPS. The streamed path (GPU only) passes its own.
        self.decoder_steps = 2
        if str(model.device).split(":")[0] == "cpu" and CPU_DECODER_STEPS != 2:
            self.decoder_steps = CPU_DECODER_STEPS
            inference = model.s3gen.inference

            def decode(*args: Any, **kwargs: Any) -> Any:
                kwargs["n_cfm_timesteps"] = self.decoder_steps
                return inference(*args, **kwargs)

            model.s3gen.inference = decode

    @property
    def graph_ready(self) -> bool:
        return self.graph is not None and self.graph.captured

    def close(self) -> None:
        """Lets go of the model: puts T3's own decoding and S3Gen's own inference back (the patched methods held this object,
        and through it the model, in a reference cycle that kept it in graphics memory until a full garbage collection) and
        drops the CUDA graph and the kept conditionals."""
        self.model.t3.__dict__.pop("inference_turbo", None)
        self.model.s3gen.__dict__.pop("inference", None)
        self.graph = None
        self.conditionals.clear()
        self.warm_conds = None

    @property
    def streams(self) -> bool:
        """Whether each piece is spoken as it is made (first audio after about a dozen speech tokens) rather than whole."""
        return self.graph_ready and os.environ.get("MARTLET_CHATTERBOX_STREAM", "1") != "0"

    def stream(self, text: str, cancelled: Any = None, whisper: bool = False) -> Any:
        """One piece of speech as 24 kHz mono PCM16 chunks, each watermarked, the first after about 12 speech tokens (half a
        second of speech) and then 25, 50 and 100 more at a time. Each chunk decodes every token so far with the same noise
        and holds back the last 3 tokens' frames until their lookahead is known; the vocoder carries its source and an 8-frame
        mel overlap across chunks, and the 160 ms where chunks meet is crossfaded (CosyVoice 2's streaming scheme, which
        S3Gen comes from). Measured against a whole-piece decode: the same length, less difference than two whole decodes
        with different noise, no larger sample jumps at the seams, and the watermark still detected. With whisper the
        speech is whispered (Whisperer) before it is watermarked."""
        import numpy as np  # type: ignore
        import torch  # type: ignore
        from chatterbox.models.s3gen.const import S3GEN_SIL  # type: ignore
        from chatterbox.models.s3gen.s3gen import S3Token2Mel  # type: ignore
        from chatterbox.tts_turbo import punc_norm  # type: ignore

        model, s3 = self.model, self.model.s3gen
        conds = model.conds
        tokens_in = model.tokenizer(punc_norm(text), return_tensors="pt", padding=True, truncation=True).input_ids.to(model.device)
        prompt = conds.gen["prompt_token"].shape[1]
        overlap = 8 * 480
        window = torch.from_numpy(np.hamming(2 * overlap)).float()
        trim = s3.trim_fade.float().cpu()
        whisperer = Whisperer(int(model.sr)) if whisper else None
        with torch.inference_mode():
            noise = torch.randn(1, 80, 2 * (prompt + 1100), device=model.device, dtype=s3.dtype)

            def mel(tokens: Any, last: bool) -> Any:
                # The library's finalize=False trims the encoder output but not its mask, so decode everything and hold back the
                # last 3 tokens' frames until the next chunk brings their lookahead.
                count = tokens.shape[1]
                out = S3Token2Mel.forward(s3, tokens, ref_wav=None, ref_sr=None, ref_dict=conds.gen, n_cfm_timesteps=2,
                                          finalize=True, noised_mels=noise[:, :, :2 * (prompt + count)])
                return out if last else out[:, :, :2 * (count - 3)]

            emitted, cache, first = 0, None, True
            for tokens, last in self.graph.chunks(conds.t3, tokens_in, cancelled):
                # A stopped reply frees the model now, not after decoding audio nobody will hear.
                if cancelled is not None and cancelled():
                    return
                tokens = tokens[:, tokens[0] < 6561]
                if last:
                    tokens = torch.cat([tokens, torch.full((1, 3), S3GEN_SIL, device=tokens.device, dtype=tokens.dtype)], dim=1)
                if not last and tokens.shape[1] <= 3:
                    continue
                spec = mel(tokens, last)
                fresh = spec[:, :, emitted:]
                emitted = spec.shape[2]
                if cache is not None:
                    fresh = torch.cat([cache["mel"], fresh], dim=2)
                wav, source = s3.hift_inference(fresh.to(dtype=s3.dtype), cache["source"] if cache is not None else None)
                wav = wav.float().cpu()
                if cache is not None:
                    wav[..., :overlap] = wav[..., :overlap] * window[:overlap] + cache["speech"][..., -overlap:] * window[overlap:]
                if not last:
                    cache = {"mel": fresh[:, :, -8:], "source": source[:, :, -overlap:], "speech": wav[:, -overlap:].clone()}
                    wav = wav[:, :-overlap]
                if first:
                    wav[:, :len(trim)] *= trim
                    first = False
                samples = wav[0].numpy()
                if whisperer is not None:
                    samples = whisperer.feed(samples)
                    if last:
                        samples = np.concatenate([samples, whisperer.finish()])
                if samples.size:
                    marked = model.watermarker.apply_watermark(samples, sample_rate=model.sr)
                    yield (np.clip(np.asarray(marked, dtype=np.float32), -1.0, 1.0) * 32767.0).astype("<i2").tobytes()
                if last or (cancelled is not None and cancelled()):
                    return

    def use_reference(self, audio: bytes) -> bool:
        """Sets the model's voice conditionals for this reference; True when they were already kept."""
        key = hashlib.sha256(audio).hexdigest()
        kept = self.conditionals.get(key)
        if kept is not None:
            self.conditionals.move_to_end(key)
            self.model.conds = kept
            return True
        path = _write_private_reference(audio)
        try:
            # What model.generate(text, audio_prompt_path=...) does for every sentence, with its defaults.
            self.model.prepare_conditionals(str(path), exaggeration=0.0, norm_loudness=True)
        finally:
            path.unlink(missing_ok=True)
        self.conditionals[key] = self.model.conds
        while len(self.conditionals) > self.CACHED_REFERENCES:
            self.conditionals.popitem(last=False)
        return False

    def _inference_turbo(self, t3_cond: Any, text_tokens: Any, temperature: float = 0.8, top_k: int = 1000, top_p: float = 0.95,
                         repetition_penalty: float = 1.2, max_gen_len: int = 1000) -> Any:
        graph = self.graph
        if graph is not None:
            try:
                tokens = graph.generate(t3_cond, text_tokens, temperature, top_k, top_p, repetition_penalty, max_gen_len)
                if tokens is not None:
                    return tokens
            except Exception as exc:  # noqa: BLE001 - a broken graph is dropped; the library's loop speaks instead
                if _out_of_memory(exc):
                    raise
                self.graph = None
                self.graph_error = _failure_detail(exc)
                _log(f"CUDA-graph decoding stopped ({self.graph_error}); using the library's own decoding.")
        return self._original(t3_cond, text_tokens, temperature=temperature, top_k=top_k, top_p=top_p,
                              repetition_penalty=repetition_penalty, max_gen_len=min(max_gen_len, _speech_budget(text_tokens)))

    def warm(self) -> None:
        """Pays the first reply's one-time costs now: the conditionals' librosa/numba warm-up, the CUDA graph capture, the
        decoder's first run, a streamed piece long enough for several chunks (the vocoder's carried-over source and the
        growing decodes a reply uses) and the Perth watermarker's first call (on the CPU: about 0.8 s the first time, 20 ms
        after, which the first reply's first audio used to wait for), on a synthetic signal (FIXTURE - NOT a voice). Its
        conditionals stay for the idle check (idle_pass), out of the voices' cache. Never raises."""
        started = time.monotonic()
        try:
            import torch  # type: ignore

            key = self._warm_reference()
            text = self.model.tokenizer("Warm up.", return_tensors="pt").input_ids.to(self.model.device)
            with torch.inference_mode():
                tokens = self.model.t3.inference_turbo(t3_cond=self.model.conds.t3, text_tokens=text, max_gen_len=24)
                tokens = tokens[tokens < 6561]
                if tokens.numel() > 0:
                    self.model.s3gen.inference(speech_tokens=tokens.to(self.model.device), ref_dict=self.model.conds.gen,
                                               n_cfm_timesteps=2)
            self._warm_watermarker()
            if self.streams:
                for _ in self.stream("Warming up the voice, so that the first reply starts right away."):
                    pass
            self.warm_conds = self.conditionals.pop(key, None)
            self.model.conds = None
            decoding = "CUDA graph" if self.graph_ready else f"eager ({self.graph_error or 'graph off'})"
            _log(f"{self.name} warmed up in {(time.monotonic() - started) * 1000:.0f} ms on {self.model.device}; decoding: {decoding}.")
        except Exception as exc:  # noqa: BLE001 - warming up is best effort
            _log(f"Warming {self.name} up failed ({_failure_detail(exc)}); the first reply may be slower.")

    def idle_pass(self, cancelled: Any) -> None:
        """One short piece on the warm-up's synthetic voice (FIXTURE - NOT a voice), thrown away: every part of the model a
        reply uses runs once. Stops at the next speech token once cancelled() says a reply is waiting."""
        conds = self.warm_conds or self.model.conds
        if conds is None:
            return
        self.model.conds = conds
        if self.streams:
            for _ in self.stream("Okay.", cancelled=cancelled):
                if cancelled():
                    return
        elif not cancelled():
            self.model.generate("Okay.")

    def _warm_watermarker(self) -> None:
        # FIXTURE - NOT a voice: one second of a quiet 220 Hz tone, watermarked once and thrown away.
        import numpy as np  # type: ignore

        rate = int(self.model.sr)
        tone = (0.05 * np.sin(2 * np.pi * 220 * np.arange(rate) / rate)).astype(np.float32)
        self.model.watermarker.apply_watermark(tone, sample_rate=rate)

    def _warm_reference(self) -> str:
        audio = _synthetic_reference()
        self.use_reference(audio)
        return hashlib.sha256(audio).hexdigest()


def _synthetic_reference() -> bytes:
    """FIXTURE - NOT a voice: six seconds of a gliding harmonic tone as a 24 kHz WAV, only to run the conditioning code once."""
    rate, seconds = 24_000, 6.0
    frames = bytearray()
    for i in range(int(rate * seconds)):
        t = i / rate
        pitch = 140 + 40 * math.sin(2 * math.pi * 0.5 * t)
        envelope = 0.5 + 0.5 * math.sin(2 * math.pi * 3 * t)
        value = sum(math.sin(2 * math.pi * pitch * k * t) / k for k in (1, 2, 3)) * 0.2 * envelope
        frames += struct.pack("<h", int(max(-1.0, min(1.0, value)) * 32767))
    header = b"RIFF" + struct.pack("<I", 36 + len(frames)) + b"WAVEfmt " + struct.pack("<IHHIIHH", 16, 1, 1, rate, rate * 2, 2, 16)
    return header + b"data" + struct.pack("<I", len(frames)) + bytes(frames)


def _speech_budget(text_tokens: Any) -> int:
    """The most speech tokens (25 a second) a Turbo or Nano piece of these text tokens may take: well beyond what its words
    need (MARTLET_CHATTERBOX_TOKENS_*), so a piece the model doesn't stop (it has no stop detector of its own and would
    otherwise babble or hiss up to 1,000 tokens, 40 s) ends instead, and never more than 1,000."""
    tags = int(((text_tokens >= TAG_TOKEN_FIRST) & (text_tokens <= TAG_TOKEN_LAST)).sum())
    words = int(text_tokens.shape[1]) - tags
    return min(1000, SPEECH_TOKENS_BASE + SPEECH_TOKENS_PER_TEXT_TOKEN * words + SPEECH_TOKENS_PER_TAG * tags)


def _original_budget(text_tokens: int) -> int:
    """The most speech tokens a piece of the original model with this many text tokens may take (ORIGINAL_TOKENS_*), at most
    the 1,000 its own generate() allows."""
    return min(1000, ORIGINAL_TOKENS_BASE + ORIGINAL_TOKENS_PER_TEXT_TOKEN * text_tokens)


def _original_generate(model: Any, text: str, exaggeration: float, cfg_weight: float) -> tuple[Any, bool]:
    """What ChatterboxTTS.generate does with the kept voice conditionals, with this exaggeration and CFG weight and at most
    _original_budget speech tokens. chatterbox-tts 0.1.7's T3 always decodes two rows (with and without the text, for CFG),
    so the text goes in twice whatever the weight; at weight 0 the second row changes nothing. Returns the watermarked float
    speech and whether it stopped at the budget instead of its stop token."""
    import torch  # type: ignore
    import torch.nn.functional as F  # type: ignore
    from chatterbox.models.s3tokenizer import drop_invalid_tokens  # type: ignore
    from chatterbox.models.t3.modules.cond_enc import T3Cond  # type: ignore
    from chatterbox.tts import punc_norm  # type: ignore

    conds = model.conds
    if float(conds.t3.emotion_adv[0, 0, 0]) != exaggeration:
        kept = conds.t3
        conds.t3 = T3Cond(speaker_emb=kept.speaker_emb, cond_prompt_speech_tokens=kept.cond_prompt_speech_tokens,
                          emotion_adv=exaggeration * torch.ones(1, 1, 1)).to(device=model.device)
    tokens = model.tokenizer.text_to_tokens(punc_norm(text)).to(model.device)
    budget = _original_budget(int(tokens.shape[-1]))
    tokens = torch.cat([tokens, tokens], dim=0)
    tokens = F.pad(tokens, (1, 0), value=model.t3.hp.start_text_token)
    tokens = F.pad(tokens, (0, 1), value=model.t3.hp.stop_text_token)
    with torch.inference_mode():
        speech = model.t3.inference(t3_cond=conds.t3, text_tokens=tokens, max_new_tokens=budget, temperature=0.8,
                                    cfg_weight=cfg_weight, repetition_penalty=1.2, min_p=0.05, top_p=1.0)[0]
        capped = speech.numel() >= budget and int(speech[-1]) != model.t3.hp.stop_speech_token
        speech = drop_invalid_tokens(speech)
        speech = speech[speech < 6561].to(model.device)
        wav, _ = model.s3gen.inference(speech_tokens=speech, ref_dict=conds.gen)
        wav = wav.squeeze(0).detach().cpu().numpy()
        return model.watermarker.apply_watermark(wav, sample_rate=model.sr), capped


class OriginalVoice:
    """The original 500M Chatterbox (ChatterboxTTS): each part spoken whole, with its style's exaggeration and CFG weight
    (_original_generate). The reference's voice conditionals are computed once per recording and kept, as FastTurbo keeps
    Turbo's; only the exaggeration in them changes from part to part. Every part carries the Perth watermark. A stopped
    reply stops after the part it is in (its decoding loop has no way in)."""

    CACHED_REFERENCES = 2
    # Whether the last part stopped at its speech-token budget rather than at the stop token.
    capped = False

    def __init__(self, model: Any) -> None:
        self.model = model
        self.conditionals: OrderedDict[str, Any] = OrderedDict()

    @property
    def device(self) -> str:
        return str(getattr(self.model, "device", None) or DEVICE)

    def close(self) -> None:
        self.conditionals.clear()

    def use_reference(self, audio: bytes) -> bool:
        """Sets the model's voice conditionals for this reference; True when they were already kept."""
        key = hashlib.sha256(audio).hexdigest()
        kept = self.conditionals.get(key)
        if kept is not None:
            self.conditionals.move_to_end(key)
            self.model.conds = kept
            return True
        path = _write_private_reference(audio)
        try:
            self.model.prepare_conditionals(str(path), exaggeration=DEFAULT_STYLE.general[0])
        finally:
            path.unlink(missing_ok=True)
        self.conditionals[key] = self.model.conds
        while len(self.conditionals) > self.CACHED_REFERENCES:
            self.conditionals.popitem(last=False)
        return False

    def speak(self, text: str, exaggeration: float, cfg_weight: float, whisper: bool = False) -> bytes:
        """One part as 24 kHz mono PCM16 (whispered before the watermark when whisper is set); when the graphics card runs out
        of memory, the cache is freed once and the part tried again."""
        with _whispering(self.model) if whisper else nullcontext():
            try:
                return self._speak(text, exaggeration, cfg_weight)
            except Exception as exc:
                if not _out_of_memory(exc):
                    raise
                _log(f"Out of graphics memory ({_failure_detail(exc)}); freeing cached memory and trying once more.")
                _free_gpu_memory()
                return self._speak(text, exaggeration, cfg_weight)

    def _speak(self, text: str, exaggeration: float, cfg_weight: float) -> bytes:
        wav, self.capped = _original_generate(self.model, text, exaggeration, cfg_weight)
        return _pcm16(wav, int(getattr(self.model, "sr", 24_000) or 24_000))

    def warm(self) -> None:
        """Pays the first reply's one-time costs now (the conditioning code's librosa/numba warm-up, the first decode and the
        watermarker's first call) on a synthetic signal (FIXTURE - NOT a voice), whose conditionals are not kept. Never raises."""
        started = time.monotonic()
        try:
            audio = _synthetic_reference()
            self.use_reference(audio)
            self.speak("Warm up.", *DEFAULT_STYLE.general)
            self.conditionals.pop(hashlib.sha256(audio).hexdigest(), None)
            self.model.conds = None
            _log(f"Chatterbox Original warmed up in {(time.monotonic() - started) * 1000:.0f} ms on {self.device}.")
        except Exception as exc:  # noqa: BLE001 - warming up is best effort
            _log(f"Warming Chatterbox Original up failed ({_failure_detail(exc)}); the first reply may be slower.")


class _T3Graph:
    """T3 (GPT-2 medium) decoding as one CUDA graph per token over a static KV cache, sampling exactly as
    T3.inference_turbo does (temperature, top-k, top-p, repetition penalty, the same stop token)."""

    MAX_TOKENS = int(os.environ.get("MARTLET_CHATTERBOX_GRAPH_TOKENS", "2048"))

    def __init__(self, t3: Any, device: str) -> None:
        import torch  # type: ignore
        from transformers import StaticCache  # type: ignore

        if not str(device).startswith("cuda") or not torch.cuda.is_available():
            raise RuntimeError("CUDA graphs need an NVIDIA GPU")
        self.torch = torch
        self.t3 = t3
        self.device = device
        self.cache = StaticCache(config=t3.cfg, max_cache_len=self.MAX_TOKENS)
        weight = t3.speech_emb.weight
        self.x = torch.zeros(1, 1, weight.shape[1], device=weight.device, dtype=weight.dtype)
        self.position = torch.zeros(1, dtype=torch.long, device=weight.device)
        self.out: Any = None
        self.cuda_graph: Any = None
        # Whether the last piece decoded stopped at its speech-token budget rather than at the stop token.
        self.capped = False

    @property
    def captured(self) -> bool:
        return self.cuda_graph is not None

    def _prefill(self, embeds: Any) -> Any:
        torch = self.torch
        self.cache.reset()
        length = embeds.shape[1]
        out = self.t3.tfmr(inputs_embeds=embeds, past_key_values=self.cache, use_cache=True,
                           cache_position=torch.arange(length, device=embeds.device))
        return self.t3.speech_head(out[0][:, -1:])

    def _step(self) -> Any:
        out = self.t3.tfmr(inputs_embeds=self.x, past_key_values=self.cache, use_cache=True, cache_position=self.position)
        return self.t3.speech_head(out[0])

    def _capture(self, length: int) -> None:
        torch = self.torch
        self.position.fill_(length)
        side = torch.cuda.Stream(device=self.x.device)
        side.wait_stream(torch.cuda.current_stream())
        with torch.cuda.stream(side):
            for _ in range(3):
                self.out = self._step()
        torch.cuda.current_stream().wait_stream(side)
        graph = torch.cuda.CUDAGraph()
        with torch.cuda.graph(graph):
            self.out = self._step()
        self.cuda_graph = graph

    def generate(self, t3_cond: Any, text_tokens: Any, temperature: float, top_k: int, top_p: float, repetition_penalty: float,
                 max_gen_len: int) -> Any:
        """The speech tokens, or None when this prompt doesn't fit the static cache (the caller decodes it as before)."""
        torch = self.torch
        from transformers.generation.logits_process import (  # type: ignore
            LogitsProcessorList, RepetitionPenaltyLogitsProcessor, TemperatureLogitsWarper, TopKLogitsWarper, TopPLogitsWarper)

        t3 = self.t3
        processors = LogitsProcessorList()
        if temperature > 0 and temperature != 1.0:
            processors.append(TemperatureLogitsWarper(temperature))
        if top_k > 0:
            processors.append(TopKLogitsWarper(top_k))
        if top_p < 1.0:
            processors.append(TopPLogitsWarper(top_p))
        if repetition_penalty != 1.0:
            processors.append(RepetitionPenaltyLogitsProcessor(repetition_penalty))
        with torch.inference_mode():
            start = t3.hp.start_speech_token * torch.ones_like(text_tokens[:, :1])
            embeds, _ = t3.prepare_input_embeds(t3_cond=t3_cond, text_tokens=text_tokens, speech_tokens=start, cfg_weight=0.0)
            length = embeds.shape[1]
            if embeds.shape[0] != 1 or length + max_gen_len + 1 > self.MAX_TOKENS:
                return None
            tokens = None
            for tokens, _last in self._decode(embeds, start, processors, min(max_gen_len, self.budget(text_tokens)), None, None):
                pass
            return tokens

    def chunks(self, t3_cond: Any, text_tokens: Any, cancelled: Any = None, first: int = 12, largest: int = 100) -> Any:
        """The speech tokens so far, as (tokens, last) once there are first + 3 (the decoder's lookahead), then 25, 50 and
        largest more at a time, and once more with all of them at the end; sampled exactly as generate() does."""
        torch = self.torch
        from transformers.generation.logits_process import (  # type: ignore
            LogitsProcessorList, RepetitionPenaltyLogitsProcessor, TemperatureLogitsWarper, TopKLogitsWarper, TopPLogitsWarper)

        t3 = self.t3
        processors = LogitsProcessorList([TemperatureLogitsWarper(0.8), TopKLogitsWarper(1000), TopPLogitsWarper(0.95),
                                          RepetitionPenaltyLogitsProcessor(1.2)])
        with torch.inference_mode():
            start = t3.hp.start_speech_token * torch.ones_like(text_tokens[:, :1])
            embeds, _ = t3.prepare_input_embeds(t3_cond=t3_cond, text_tokens=text_tokens, speech_tokens=start, cfg_weight=0.0)
            room = self.MAX_TOKENS - embeds.shape[1] - 1
            if embeds.shape[0] != 1 or room < 100:
                raise CacheTooSmall()
            yield from self._decode(embeds, start, processors, min(room, self.budget(text_tokens)), cancelled, (first + 3, largest))

    def budget(self, text_tokens: Any) -> int:
        """The most speech tokens this piece may take (_speech_budget)."""
        return _speech_budget(text_tokens)

    def _decode(self, embeds: Any, start: Any, processors: Any, budget: int, cancelled: Any, schedule: tuple[int, int] | None) -> Any:
        """Samples up to budget speech tokens after the prompt embeds, as T3.inference_turbo does (the same processors, the
        same multinomial draws in the same order, the same stop token), yielding (tokens so far, last): at each target of
        schedule (first, largest: first, then 25, 50 and largest more at a time) and once at the end. The GPU is asked
        whether the stop token came only every CHECK_EVERY tokens (and at each target), not after every token, so the
        processor queues the next tokens while the card works; tokens drawn after the stop are discarded. A step whose
        probabilities are not finite (every logit -inf, which the library stops on) draws the stop token without asking
        the GPU."""
        torch = self.torch
        import torch.nn.functional as F  # type: ignore

        t3 = self.t3
        stop = t3.hp.stop_speech_token
        self.capped = False
        length = embeds.shape[1]
        if self.cuda_graph is None:
            self._prefill(embeds)
            self._capture(length)
        logits = self._prefill(embeds)
        ids = torch.empty(1, budget + 1, dtype=torch.long, device=logits.device)
        next_token = torch.multinomial(F.softmax(processors(start, logits[:, -1, :]), dim=-1), num_samples=1)
        ids[:, :1] = next_token
        only_stop = torch.zeros(1, logits.shape[-1], device=logits.device, dtype=torch.float32)
        only_stop[0, stop] = 1.0
        # The first token is kept whatever it is, as the library does; from the second on, the stop token ends the piece.
        count, checked = 1, 1
        target, step = (schedule[0], 25) if schedule is not None else (None, 0)
        for i in range(budget):
            # Checked every token: a reply stopped mid-chunk (up to 100 tokens, seconds on a busy card) would otherwise keep
            # the model busy and the next reply would be refused with worker.busy.
            if cancelled is not None and cancelled():
                return
            self.x.copy_(t3.speech_emb(next_token))
            self.position.fill_(length + i)
            self.cuda_graph.replay()
            probs = F.softmax(processors(ids[:, :count], self.out[:, -1, :]), dim=-1)
            probs = torch.where(torch.isfinite(probs).all(), probs, only_stop)
            next_token = torch.multinomial(probs, num_samples=1)
            ids[:, count:count + 1] = next_token
            count += 1
            due = target is not None and count >= target
            if due or count - checked >= CHECK_EVERY or i == budget - 1:
                hits = (ids[0, checked:count] == stop).nonzero()
                if hits.numel() > 0:
                    yield ids[:, :checked + int(hits[0, 0])], True
                    return
                checked = count
                if due:
                    yield ids[:, :count], False
                    target, step = target + step, min(step * 2, schedule[1])
        self.capped = True
        yield ids[:, :count], True


class CacheTooSmall(Exception):
    """A piece too long for the CUDA graph's static KV cache; it is spoken whole with the library's own decoding."""


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
            if WORKER.state != "ready" or identity is None or WORKER.active is not None or WORKER.idle is not None:
                busy = WORKER.active is not None or WORKER.idle is not None or WORKER.state == "busy"
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
    name = _pinned().name
    _log(f"Loading the {name} model (verifies pinned files first; can take a few minutes)...")
    conn = http.client.HTTPConnection("127.0.0.1", PORT, timeout=WARMUP_SECONDS + 30)
    try:
        conn.request("POST", "/warmup", body=b"{}", headers={"Content-Type": "application/json"})
        response = conn.getresponse()
        status = json.loads(response.read() or b"{}")
    finally:
        conn.close()
    if not status.get("ready"):
        raise SystemExit(f"The Chatterbox worker is not ready ({status.get('state')}): {status.get('error') or 'no detail'}")
    _log(f"The {name} voice model is loaded and ready on {status.get('device') or 'its device'}.")


def serve() -> None:
    server = http.server.ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    server.daemon_threads = True
    if CONFIG.is_file():
        WORKER.start_loading()
    if IDLE_CHECK_SECONDS > 0:
        threading.Thread(target=WORKER.idle_loop, name="chatterbox-idle", daemon=True).start()
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
