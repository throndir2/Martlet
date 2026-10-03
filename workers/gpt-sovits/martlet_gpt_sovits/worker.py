"""GPT-SoVITS worker process: the one pinned v2Pro model pair, one synthesis at a time, NDJSON over stdin/stdout.

The host front (host.py) spawns this process and is its only peer. Messages in: ``warmup``, ``synthesize`` (checked by
wire.check_request) and ``cancel``. Messages out: ``worker_state`` and martlet.f5.worker 1.0 ``event`` lines. Weights are
loaded exactly once, at warmup, from the pinned paths after every pinned file's size and SHA-256 match; there is no
message that names a path or switches weights. The worker reports ready only after GPT-SoVITS confirms it loaded both
halves of the pair (GPT and SoVITS v2Pro) from those paths.
"""

from __future__ import annotations

import base64
import hashlib
import json
import math
import os
import sys
import threading
import traceback
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable, Iterator

from . import pins, wire

ROOT = Path(os.environ.get("MARTLET_GPT_SOVITS_ROOT", "/opt/gpt-sovits"))
SOURCE = ROOT / "GPT-SoVITS"
MODELS = SOURCE / "GPT_SoVITS" / "pretrained_models"
CONFIG = MODELS / "martlet-worker.json"
REFERENCES = Path(os.environ.get("MARTLET_GPT_SOVITS_REFERENCES", "/tmp/martlet-gpt-sovits-references"))
DEBUG = os.environ.get("MARTLET_GPT_SOVITS_DEBUG") == "1"
WORKER_ID = "martlet-gpt-sovits-host"
FIXTURE_WORKER_ID = "martlet-gpt-sovits-fixture"


def sha256_file(path: Path) -> tuple[int, str]:
    digest, size = hashlib.sha256(), 0
    with path.open("rb") as stream:
        while block := stream.read(1_048_576):
            size += len(block)
            digest.update(block)
    return size, digest.hexdigest()


def _stage(path: Path, audio_base64: str) -> Path:
    """Writes a reference recording once (its name carries its revision) and marks it used."""
    if path.is_file():
        path.touch()
    else:
        staged = path.with_suffix(".partial")
        staged.write_bytes(base64.b64decode(audio_base64))
        staged.replace(path)
    return path


def _prune(keep: str) -> None:
    """Keeps the reference files of the four most recently used revisions (always ``keep``'s)."""
    revisions: dict[str, list[Path]] = {}
    for path in REFERENCES.glob("*.wav"):
        revisions.setdefault(path.name[:64], []).append(path)
    ordered = sorted(revisions.items(), key=lambda item: max(p.stat().st_mtime for p in item[1]))
    for revision, paths in ordered[:-4]:
        if revision != keep:
            for path in paths:
                path.unlink(missing_ok=True)


def identity(engine: str) -> dict[str, Any]:
    fixture = engine == "fixture"
    artifacts = [item.wire() for item in pins.ARTIFACTS]
    if fixture:
        artifacts = [dict(item, license_id="fixture-only", revision="0" * 40,
                          sha256=hashlib.sha256(f"fixture-{item['role']}".encode()).hexdigest()) for item in artifacts]
    return {
        "artifacts": artifacts,
        "cancellation": "discard_only",
        "contract_id": wire.CONTRACT_ID,
        "engine": "fixture" if fixture else "gpt-sovits",
        "evidence": "synthetic_fixture" if fixture else "live_worker",
        "incremental_within_chunk_synthesis": not fixture,
        "model_version": pins.MODEL_VERSION,
        "pcm_transport_streaming": True,
        "protocol_version": dict(wire.PROTOCOL_VERSION),
        "source": {"commit": pins.SOURCE_COMMIT, "repository": pins.SOURCE_REPOSITORY, "tag": pins.SOURCE_TAG},
        "worker_id": FIXTURE_WORKER_ID if fixture else WORKER_ID,
    }


class FixtureEngine:
    """FIXTURE - NOT AI: a deterministic tone per sentence, for plumbing checks without a GPU or model."""

    def load(self, report: Callable[[str], None]) -> None:
        report("loading_model")

    def stop(self) -> None:
        return

    def synthesize(self, reference: Path, transcript: str, prompt_lang: str, text: str, text_lang: str,
                   auxiliary: list[Path] | None = None) -> Iterator[bytes]:
        import array

        rate = pins.OUTPUT_SAMPLE_RATE
        for sentence in [part for part in text.replace("!", ".").replace("?", ".").split(".") if part.strip()]:
            count = max(rate // 5, min(rate * 4, len(sentence.strip()) * rate * 6 // 100))
            frequency = 220 if text_lang == "en" else 330
            samples = array.array("h", (int(6000 * math.sin(2 * math.pi * frequency * i / rate)) for i in range(count)))
            samples.extend([0] * (rate * 3 // 10))
            if sys.byteorder != "little":
                samples.byteswap()
            yield samples.tobytes()


class GptSovitsEngine:
    """The native GPT-SoVITS pipeline (GPT_SoVITS/TTS_infer_pack) with the pinned v2Pro pair."""

    def __init__(self, device: str) -> None:
        self.device = device
        self.tts: Any = None

    def load(self, report: Callable[[str], None]) -> None:
        report("verifying_artifacts")
        for item in pins.ARTIFACTS:
            path = MODELS / item.path
            if not path.is_file() or sha256_file(path) != (item.bytes, item.sha256):
                raise RuntimeError(f"{item.artifact_id} is missing or differs from its pin; run 'martlet-gpt-sovits provision'.")
        report("loading_model")
        # GPT-SoVITS resolves its own modules and pretrained_models relative to the source tree.
        os.chdir(SOURCE)
        for entry in (str(SOURCE), str(SOURCE / "GPT_SoVITS")):
            if entry not in sys.path:
                sys.path.append(entry)
        from TTS_infer_pack.TTS import TTS, TTS_Config  # noqa: PLC0415 - heavy import only in the live engine

        gpt = MODELS / pins.artifact("gpt_weights").path
        sovits = MODELS / pins.artifact("model_weights").path
        config = TTS_Config({"custom": {
            "device": self.device,
            "is_half": self.device.startswith("cuda"),
            "version": pins.MODEL_VERSION,
            "t2s_weights_path": str(gpt),
            "vits_weights_path": str(sovits),
            "bert_base_path": str(MODELS / "chinese-roberta-wwm-ext-large"),
            "cnhuhbert_base_path": str(MODELS / "chinese-hubert-base"),
        }})
        tts = TTS(config)
        # Both halves of the pair must be the pinned files, and GPT-SoVITS must have recognized SoVITS as v2Pro.
        if (Path(tts.configs.t2s_weights_path).resolve() != gpt.resolve() or
                Path(tts.configs.vits_weights_path).resolve() != sovits.resolve() or
                tts.configs.version != pins.MODEL_VERSION or not getattr(tts, "is_v2pro", False) or
                tts.configs.sampling_rate != pins.MODEL_SAMPLE_RATE):
            raise RuntimeError("GPT-SoVITS did not load the pinned v2Pro GPT and SoVITS weights.")
        # Load the text front ends and the resampler now, so the first reply does not wait for them.
        import importlib.util  # noqa: PLC0415

        import scipy.signal  # noqa: F401, PLC0415

        primes = [("Hello.", "en")]
        if importlib.util.find_spec("pyopenjtalk") is not None:  # always in the image; absent on Windows dev setups
            primes.append(("こんにちは。", "ja"))
        for sample, language in primes:
            tts.text_preprocessor.segment_and_extract_feature_for_text(sample, language, tts.configs.version)
        self.tts = tts

    def stop(self) -> None:
        if self.tts is not None:
            self.tts.stop()

    def synthesize(self, reference: Path, transcript: str, prompt_lang: str, text: str, text_lang: str,
                   auxiliary: list[Path] | None = None) -> Iterator[bytes]:
        tts = self.tts
        if tts.prompt_cache.get("prompt_lang") not in (None, prompt_lang):
            tts.prompt_cache["prompt_text"] = None
        tts.stop_flag = False
        inputs = {
            "text": text, "text_lang": text_lang, "ref_audio_path": str(reference),
            "aux_ref_audio_paths": [str(path) for path in auxiliary or []],
            "prompt_text": transcript, "prompt_lang": prompt_lang, "top_k": 5, "top_p": 1, "temperature": 1,
            "text_split_method": "cut5", "batch_size": 1, "batch_threshold": 0.75, "split_bucket": False,
            "speed_factor": 1.0, "fragment_interval": 0.3, "seed": -1, "parallel_infer": True,
            "repetition_penalty": 1.35, "sample_steps": 32, "super_sampling": False,
            # Streams one sentence at a time, so the first sentence plays while the rest are generated.
            "return_fragment": True,
        }
        for rate, audio in tts.run(inputs):
            # GPT-SoVITS yields a 16 kHz silence placeholder when stopped or failing; it is never speech.
            if rate != pins.MODEL_SAMPLE_RATE:
                continue
            yield wire.resample_to_output(audio, rate)


class Worker:
    def __init__(self, out: Any) -> None:
        self.out = out
        self.write_lock = threading.Lock()
        self.lock = threading.Lock()
        self.state = "cold"
        self.engine: Any = None
        self.identity: dict[str, Any] | None = None
        self.active: str | None = None
        self.canceled: set[str] = set()

    def send(self, message: dict[str, Any]) -> None:
        with self.write_lock:
            self.out.write(wire.canonical(message) + b"\n")
            self.out.flush()

    def report(self, state: str, error: str | None = None) -> None:
        with self.lock:
            self.state = state
        self.send({"error": {"summary": error} if error else None, "ready": state == "ready", "state": state,
                   "type": "worker_state", "worker": self.identity})

    def warmup(self) -> None:
        with self.lock:
            if self.state not in {"cold", "failed"}:
                busy = self.state
            else:
                busy = None
                self.state = "starting"
        if busy is not None:
            self.report(busy)
            return
        try:
            config = json.loads(CONFIG.read_bytes())
            engine_name = config["engine"]
            engine = FixtureEngine() if engine_name == "fixture" else GptSovitsEngine(config.get("device") or "cuda:0")
            self.identity = identity(engine_name)
            engine.load(self.report)
            self.engine = engine
            self.report("ready")
        except Exception as error:  # noqa: BLE001 - every load failure is reported, never raised
            if DEBUG:
                traceback.print_exc()
            summary = str(error) if isinstance(error, RuntimeError) else f"GPT-SoVITS failed to load ({type(error).__name__})."
            self.report("failed", summary[:300])

    def synthesize(self, message: dict[str, Any]) -> None:
        ids = message["ids"]
        revision = message["reference"]["reference_revision"]
        request_id = ids["request_id"]
        with self.lock:
            refused = None if self.state == "ready" and self.active is None else ("busy" if self.active else "model_not_ready")
            if refused is None:
                self.active = request_id
        if refused is not None:
            self.send(wire.make_event(kind="failed", sequence=0, ids=ids, worker=self.identity or identity("fixture"),
                                      reference_revision=revision, error=self._error(refused, "The voice model is not ready.")))
            return
        threading.Thread(target=self._run, args=(message,), name="gpt-sovits-synthesis", daemon=True).start()

    def _error(self, code: str, summary: str) -> dict[str, Any]:
        return {"action_id": None, "code": code, "retryable": code in {"busy", "model_not_ready"}, "stage": "synthesis",
                "summary": summary}

    def _run(self, message: dict[str, Any]) -> None:
        ids = message["ids"]
        request_id = ids["request_id"]
        reference = message["reference"]
        revision = reference["reference_revision"]
        deadline = wire.parse_utc(message["deadline_utc"])
        sequence = frame_sequence = offset = 0

        def event(kind: str, **fields: Any) -> None:
            nonlocal sequence
            self.send(wire.make_event(kind=kind, sequence=sequence, ids=ids, worker=self.identity,
                                      reference_revision=revision, **fields))
            sequence += 1

        try:
            event("started")
            REFERENCES.mkdir(parents=True, exist_ok=True)
            # GPT-SoVITS caches the reference by path, so each reference revision gets its own file; a voice made from several
            # recordings gets one for its prompt recording and one for each other recording (aux_ref_audio_paths).
            auxiliary = reference.get("auxiliary_base64") or []
            path = _stage(REFERENCES / (f"{revision}-p.wav" if auxiliary else f"{revision}.wav"), reference["audio_base64"])
            others = [_stage(REFERENCES / f"{revision}-a{index}.wav", data) for index, data in enumerate(auxiliary)]
            _prune(keep=revision)
            for chunk in message["chunks"]:
                text = chunk["text"]
                for pcm in self.engine.synthesize(path, reference["transcript"], reference["language"], text,
                                                  wire.text_language(text), others):
                    if request_id in self.canceled:
                        self.engine.stop()
                        event("canceled", final_sample_count=offset)
                        return
                    if datetime.now(timezone.utc) > deadline:
                        self.engine.stop()
                        event("failed", error=self._error("deadline_exceeded", "The reply took too long."))
                        return
                    for start in range(0, len(pcm), wire.MAX_FRAME_SAMPLES * 2):
                        piece = pcm[start:start + wire.MAX_FRAME_SAMPLES * 2]
                        if offset + len(piece) // 2 > wire.MAX_SAMPLES:
                            raise RuntimeError("The reply is longer than 90 seconds.")
                        event("audio_frame", frame=wire.make_frame(sequence=frame_sequence, chunk_index=chunk["index"],
                                                                   sample_offset=offset, pcm=piece))
                        frame_sequence += 1
                        offset += len(piece) // 2
                if request_id in self.canceled:
                    event("canceled", final_sample_count=offset)
                    return
                event("chunk_completed", chunk_index=chunk["index"], final_sample_count=offset)
            event("completed", final_sample_count=offset)
        except Exception as error:  # noqa: BLE001 - reported as a failed event
            if DEBUG:
                traceback.print_exc()
            event("failed", error=self._error("engine_failure", f"GPT-SoVITS failed ({type(error).__name__})."))
        finally:
            with self.lock:
                self.active = None
                self.canceled.discard(request_id)

    def cancel(self, request_id: str) -> None:
        with self.lock:
            if self.active == request_id:
                self.canceled.add(request_id)
        if self.engine is not None and self.active == request_id:
            self.engine.stop()


def main() -> int:
    # GPT-SoVITS prints progress (and the text it reads) to stdout; the protocol keeps its own copy of stdout and every
    # print goes to stderr, which the host discards unless MARTLET_GPT_SOVITS_DEBUG=1.
    protocol = os.fdopen(os.dup(1), "wb", buffering=0)
    os.dup2(2, 1)
    sys.stdout = sys.stderr
    worker = Worker(protocol)
    for line in sys.stdin.buffer:
        try:
            message = json.loads(line)
            kind = message.get("type")
            if kind == "warmup":
                # Loads in this thread, before reading on: loading native libraries while another thread blocks reading
                # stdin deadlocks on Windows, and the host sends nothing else until the worker reports ready.
                worker.warmup()
            elif kind == "synthesize":
                worker.synthesize(message)
            elif kind == "cancel":
                worker.cancel(str(message.get("request_id")))
        except (ValueError, KeyError, TypeError):
            worker.send({"type": "protocol_error"})
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
