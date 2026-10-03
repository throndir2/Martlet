"""Martlet Dia worker process: one model, one synthesis at a time, JSON lines on stdin/stdout.

Started by the host service (martlet_dia.host) as ``python -m martlet_dia.worker --config <worker-config.json>``.
Nothing is downloaded: the model, its configuration and the DAC codec are loaded from the provisioned paths in the
config, and HF_HUB_OFFLINE is set by the image.

stdin:  {"type": "warmup"} | {"type": "synthesize", ...} | {"type": "cancel", "request_id"}
stdout: {"type": "state", "state", "ready", "error"} | {"type": "event", ...martlet.f5.worker-shaped event...}
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import math
import os
import queue
import sys
import threading
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, BinaryIO

from martlet_dia import audio, text

# Dia.generate defaults at the pinned commit. The voice_clone example's hotter settings (cfg 4.0, temperature 1.8) ran on
# for 15 s of noise after a reply that started with "(sighs)"; these spoke it with the sigh.
GENERATION = {"cfg_scale": 3.0, "temperature": 1.2, "top_p": 0.95, "cfg_filter_top_k": 45}
PIECE_GAP_SECONDS = 0.12


class SynthesisError(Exception):
    def __init__(self, code: str, summary: str) -> None:
        super().__init__(summary)
        self.code = code
        self.summary = summary


class FixtureEngine:
    """FIXTURE - NOT AI: a deterministic tone per piece, for plumbing checks without the model or a GPU."""

    def load(self) -> None:
        return

    def speak(self, reference_pcm: bytes, reference_rate: int, revision: str, prompt: str, piece: str) -> bytes:
        import array

        count = min(audio.OUTPUT_RATE * 4, max(2_400, 1_200 * len(piece)))
        pitch = 180 + int(hashlib.sha256(revision.encode()).hexdigest()[:2], 16)
        samples = array.array("h", (int(6000 * math.sin(2 * math.pi * pitch * i / audio.OUTPUT_RATE)) for i in range(count)))
        if sys.byteorder != "little":
            samples.byteswap()
        return samples.tobytes()


class DiaEngine:
    def __init__(self, files: dict[str, str], device: str) -> None:
        self.files = files
        self.device_name = device
        self.dia: Any = None
        self.prompts: dict[str, Any] = {}

    def load(self) -> None:
        import dac  # descript-audio-codec
        import torch
        from dia.model import Dia

        for role, path in self.files.items():
            if not Path(path).is_file():
                raise SynthesisError("model_not_ready", f"The {role} file is missing; run the dia role's provisioning step.")
        device = torch.device(self.device_name)
        dtype = "float16" if device.type == "cuda" else "float32"
        model = Dia.from_local(self.files["model_configuration"], self.files["model_weights"], compute_dtype=dtype,
                               device=device, load_dac=False)
        codec = dac.DAC.load(self.files["codec_weights"]).to(device)
        codec.eval()
        model.dac_model = codec
        model.load_dac = True
        self.dia = model
        self.torch = torch

    def _prompt_codes(self, pcm: bytes, rate: int, revision: str) -> Any:
        codes = self.prompts.get(revision)
        if codes is None:
            import numpy as np
            import torchaudio

            torch = self.torch
            samples = torch.from_numpy(np.frombuffer(pcm, dtype="<i2").astype(np.float32) / 32768.0).unsqueeze(0)
            if rate != 44_100:
                samples = torchaudio.functional.resample(samples, rate, 44_100)
            codes = self.dia._encode(samples.to(self.dia.device))
            self.prompts = {revision: codes}  # keep only the current voice
        return codes

    def speak(self, reference_pcm: bytes, reference_rate: int, revision: str, prompt: str, piece: str) -> bytes:
        import numpy as np
        import torchaudio

        torch = self.torch
        codes = self._prompt_codes(reference_pcm, reference_rate, revision)
        # The same voice and text give the same delivery.
        torch.manual_seed(int(hashlib.sha256(f"{revision}\n{prompt}".encode()).hexdigest()[:8], 16))
        limit = text.max_tokens(len(reference_pcm) / 2 / reference_rate, piece)
        output = self.dia.generate(prompt, audio_prompt=codes, max_tokens=limit, use_torch_compile=False,
                                   verbose=False, **GENERATION)
        if output is None or len(output) == 0:
            return b""
        waveform = torch.from_numpy(np.asarray(output, dtype=np.float32)).unsqueeze(0)
        resampled = torchaudio.functional.resample(waveform, 44_100, audio.OUTPUT_RATE).squeeze(0).clamp(-1.0, 1.0)
        return (resampled.numpy() * 32767.0).round().astype("<i2").tobytes()


class Worker:
    def __init__(self, config: dict[str, Any], output: BinaryIO) -> None:
        self.identity = config["worker"]
        self.engine = FixtureEngine() if config["engine"] == "fixture" else DiaEngine(config["files"], config["device"])
        self.output = output
        self.lock = threading.Lock()
        self.canceled: set[str] = set()
        self.inbox: queue.Queue[dict[str, Any] | None] = queue.Queue()
        self.loaded = False

    def emit(self, message: dict[str, Any]) -> None:
        with self.lock:
            self.output.write(json.dumps(message, separators=(",", ":"), sort_keys=True).encode("utf-8") + b"\n")
            self.output.flush()

    def state(self, state: str, error: str | None = None) -> None:
        self.emit({"type": "state", "state": state, "ready": state == "ready", "error": error})

    def read(self, stream: BinaryIO) -> None:
        for line in stream:
            try:
                message = json.loads(line)
            except ValueError:
                continue
            if not isinstance(message, dict):
                continue
            if message.get("type") == "cancel":
                with self.lock:
                    self.canceled.add(str(message.get("request_id")))
            else:
                self.inbox.put(message)
        self.inbox.put(None)

    def warm(self) -> None:
        if self.loaded:
            self.state("ready")
            return
        self.state("loading_model")
        try:
            self.engine.load()
        except SynthesisError as error:
            self.state("failed", error.summary)
            return
        except Exception as error:  # noqa: BLE001 - reported to the owner, the worker stays up for a retry
            self.state("failed", f"Dia could not load: {type(error).__name__}: {error}"[:300])
            return
        self.loaded = True
        self.state("ready")

    def run(self) -> None:
        while (message := self.inbox.get()) is not None:
            if message.get("type") == "warmup":
                self.warm()
            elif message.get("type") == "synthesize":
                self.state("busy")
                try:
                    self.synthesize(message)
                finally:
                    self.state("ready" if self.loaded else "failed")

    def synthesize(self, request: dict[str, Any]) -> None:
        ids = request["ids"]
        request_id = ids["request_id"]
        reference = request["reference"]
        revision = reference["reference_revision"]
        base = {"type": "event", "ids": ids, "reference_revision": revision}
        sequence = 0
        offset = 0
        self.emit({**base, "kind": "started", "worker": self.identity})

        def failed(code: str, summary: str) -> None:
            self.emit({**base, "kind": "failed", "error": {"code": code, "summary": summary}, "final_sample_count": offset})

        if not self.loaded:
            failed("model_not_ready", "The Dia model is not loaded.")
            return
        try:
            deadline = datetime.fromisoformat(request["deadline_utc"].replace("Z", "+00:00"))
            data = base64.b64decode(reference["audio_base64"], validate=True)
            if hashlib.sha256(data).hexdigest() != reference["audio_sha256"]:
                raise audio.AudioError("reference digest")
            pcm, rate = audio.parse_reference(data)
            seconds = len(pcm) / 2 / rate
            chunks = sorted(request["chunks"], key=lambda chunk: chunk["index"])
            plans = [(chunk["index"], text.prompts(reference["transcript"], chunk["text"], seconds)) for chunk in chunks]
        except text.PromptError as error:
            failed("reference_too_long", str(error))
            return
        except (KeyError, TypeError, ValueError, audio.AudioError):
            failed("invalid_request", "The synthesis request is not valid.")
            return
        for index, pieces in plans:
            pcm_out = b""
            for number, (prompt, piece) in enumerate(pieces):
                with self.lock:
                    stop = request_id in self.canceled
                if stop:
                    self.emit({**base, "kind": "canceled", "final_sample_count": offset})
                    return
                if datetime.now(timezone.utc) >= deadline:
                    failed("deadline_exceeded", "The reply took longer than its deadline.")
                    return
                try:
                    spoken = self.engine.speak(pcm, rate, revision, prompt, piece)
                except Exception as error:  # noqa: BLE001 - one failed reply must not stop the worker
                    failed("synthesis_failed", f"Dia failed: {type(error).__name__}"[:200])
                    return
                if number and spoken:
                    pcm_out += b"\x00\x00" * int(audio.OUTPUT_RATE * PIECE_GAP_SECONDS)
                pcm_out += spoken
            for frame in audio.frames(pcm_out):
                count = len(frame) // 2
                if offset + count > audio.OUTPUT_RATE * 90:
                    failed("output_too_long", "The reply is longer than 90 seconds of speech.")
                    return
                self.emit({**base, "kind": "audio_frame", "frame": {
                    "chunk_index": index, "data_base64": base64.b64encode(frame).decode("ascii"),
                    "sample_count": count, "sample_offset": offset, "sequence": sequence}})
                sequence += 1
                offset += count
            self.emit({**base, "kind": "chunk_completed", "chunk_index": index, "final_sample_count": offset})
        with self.lock:
            self.canceled.discard(request_id)
        self.emit({**base, "kind": "completed", "final_sample_count": offset})


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="martlet-dia-worker")
    parser.add_argument("--config", type=Path, required=True)
    args = parser.parse_args(argv)
    # Only protocol lines go to the host: anything the libraries print goes to stderr.
    protocol = os.fdopen(os.dup(sys.stdout.fileno()), "wb")
    os.dup2(sys.stderr.fileno(), sys.stdout.fileno())
    worker = Worker(json.loads(args.config.read_bytes()), protocol)
    threading.Thread(target=worker.read, args=(sys.stdin.buffer,), name="dia-worker-stdin", daemon=True).start()
    worker.state("cold")
    worker.run()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
