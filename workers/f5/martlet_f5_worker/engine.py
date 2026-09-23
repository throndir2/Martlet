from __future__ import annotations

import importlib
import contextlib
import io
import math
import os
import struct
import tempfile
import time
from abc import ABC, abstractmethod
from pathlib import Path
from threading import Event
from typing import Any, Iterator

from .contract import ContractError, ReferenceInput
from .identity import (
    RuntimeObservation,
    RuntimePins,
    WorkerConfig,
    verify_imported_module_origins,
)


class EngineFault(Exception):
    pass


class EngineOutOfMemory(EngineFault):
    pass


class SynthesisEngine(ABC):
    @abstractmethod
    def load(
        self,
        artifact_paths: dict[str, Path],
        deadline_monotonic: float,
        runtime_observation: RuntimeObservation | None = None,
    ) -> None:
        raise NotImplementedError

    @abstractmethod
    def synthesize(
        self,
        reference: ReferenceInput,
        text: str,
        cancel: Event,
    ) -> Iterator[bytes]:
        raise NotImplementedError

    @abstractmethod
    def close(self) -> None:
        raise NotImplementedError


class DeterministicFakeEngine(SynthesisEngine):
    """FIXTURE - NOT AI."""

    def __init__(
        self,
        *,
        sample_count: int = 997,
        fragment_samples: int = 173,
        delay_seconds: float = 0.0,
        fault: str | None = None,
    ) -> None:
        if not 1 <= sample_count <= 24_000:
            raise ValueError("sample_count")
        if not 1 <= fragment_samples <= 4_800:
            raise ValueError("fragment_samples")
        if not 0 <= delay_seconds <= 2:
            raise ValueError("delay_seconds")
        if fault not in {None, "internal", "oom"}:
            raise ValueError("fault")
        self.sample_count = sample_count
        self.fragment_samples = fragment_samples
        self.delay_seconds = delay_seconds
        self.fault = fault
        self.loaded = False
        self.closed = False

    def load(
        self,
        artifact_paths: dict[str, Path],
        deadline_monotonic: float,
        runtime_observation: RuntimeObservation | None = None,
    ) -> None:
        if time.monotonic() >= deadline_monotonic:
            raise ContractError(
                "deadline_exceeded",
                "Fake-engine warmup exceeded its deadline.",
                stage="warmup",
                action_id="f5.retry-warmup",
            )
        if set(artifact_paths) != {
            "runtime_image",
            "model_weights",
            "vocabulary",
            "vocoder_weights",
            "vocoder_configuration",
        }:
            raise EngineFault("missing fake artifacts")
        self.loaded = True
        self.closed = False

    def synthesize(
        self,
        reference: ReferenceInput,
        text: str,
        cancel: Event,
    ) -> Iterator[bytes]:
        if not self.loaded or self.closed:
            raise EngineFault("fake engine not ready")
        if self.fault == "oom":
            raise EngineOutOfMemory("configured fake OOM")
        if self.fault == "internal":
            raise EngineFault("configured fake fault")
        seed = __import__("hashlib").sha256(
            (reference.reference_revision + "\n" + text).encode("utf-8", "strict")
        ).digest()
        pcm = bytearray(self.sample_count * 2)
        try:
            for index in range(self.sample_count):
                value = (seed[index % len(seed)] - 128) * 64
                struct.pack_into("<h", pcm, index * 2, value)
            for offset in range(0, self.sample_count, self.fragment_samples):
                if cancel.wait(self.delay_seconds):
                    return
                end = min(self.sample_count, offset + self.fragment_samples)
                yield bytes(pcm[offset * 2 : end * 2])
        finally:
            pcm[:] = b"\x00" * len(pcm)

    def close(self) -> None:
        self.loaded = False
        self.closed = True


class F5ProductionEngine(SynthesisEngine):
    def __init__(self, config: WorkerConfig) -> None:
        if config.engine != "f5" or config.device is None:
            raise ValueError("Production engine requires an F5 config.")
        self.config = config
        self._model: Any = None
        self._torch: Any = None
        self._torchaudio: Any = None
        self._utils: Any = None

    def load(
        self,
        artifact_paths: dict[str, Path],
        deadline_monotonic: float,
        runtime_observation: RuntimeObservation | None = None,
    ) -> None:
        if time.monotonic() >= deadline_monotonic:
            raise ContractError(
                "deadline_exceeded",
                "F5 warmup exceeded its deadline before imports.",
                stage="warmup",
                action_id="f5.retry-warmup",
            )
        os.environ["HF_HUB_OFFLINE"] = "1"
        os.environ["TRANSFORMERS_OFFLINE"] = "1"
        os.environ["HF_DATASETS_OFFLINE"] = "1"
        pins = RuntimePins.load()
        if runtime_observation is None:
            raise ContractError(
                "identity_mismatch",
                "Production imports require a verified runtime inventory.",
                stage="runtime_verification",
                action_id="f5.reprovision-runtime-inventory",
            )
        with _silence_third_party_output():
            try:
                torch = importlib.import_module("torch")
                torchaudio = importlib.import_module("torchaudio")
                api = importlib.import_module("f5_tts.api")
                utils = importlib.import_module("f5_tts.infer.utils_infer")
                importlib.import_module("vocos")
            except ImportError as error:
                raise ContractError(
                    "model_not_ready",
                    "A pinned F5/Torch/Vocos module could not be imported.",
                    stage="runtime_import",
                    action_id="f5.install-pinned-runtime",
                ) from error
            verify_imported_module_origins(runtime_observation)
        observed_cuda = getattr(getattr(torch, "version", None), "cuda", None)
        if observed_cuda != pins.cuda_runtime_version:
            raise ContractError(
                "identity_mismatch",
                "The imported Torch CUDA runtime differs from runtime-pins.v1.json.",
                stage="runtime_verification",
                action_id="f5.install-pinned-runtime",
            )
        if time.monotonic() >= deadline_monotonic:
            raise ContractError(
                "deadline_exceeded",
                "F5 warmup exceeded its deadline before model load.",
                stage="warmup",
                action_id="f5.retry-warmup",
            )
        with _silence_third_party_output():
            try:
                model = api.F5TTS(
                    model="F5TTS_v1_Base",
                    ckpt_file=str(artifact_paths["model_weights"]),
                    vocab_file=str(artifact_paths["vocabulary"]),
                    ode_method="euler",
                    use_ema=True,
                    vocoder_local_path=str(artifact_paths["vocoder_weights"].parent),
                    device=self.config.device,
                    hf_cache_dir=None,
                )
            except Exception as error:
                self._raise_engine_error(error, "The pinned F5 model could not be loaded.")
        if (
            getattr(model, "target_sample_rate", None) != 24_000
            or getattr(model, "mel_spec_type", None) != "vocos"
        ):
            raise ContractError(
                "identity_mismatch",
                "The loaded F5 model is not the pinned 24 kHz Vocos configuration.",
                stage="model_load",
                action_id="f5.reprovision-local-artifacts",
            )
        self._model = model
        self._torch = torch
        self._torchaudio = torchaudio
        self._utils = utils

    def synthesize(
        self,
        reference: ReferenceInput,
        text: str,
        cancel: Event,
    ) -> Iterator[bytes]:
        if self._model is None:
            raise EngineFault("F5 engine is not loaded")
        if cancel.is_set():
            return
        with (
            _silence_third_party_output(),
            tempfile.TemporaryDirectory(prefix="martlet-f5-reference-") as directory,
        ):
            path = Path(directory) / "reference.wav"
            try:
                with path.open("xb") as output:
                    output.write(reference.audio)
                    output.flush()
                    os.fsync(output.fileno())
                audio, sample_rate = self._torchaudio.load(str(path))
                stream = self._utils.infer_batch_process(
                    (audio, sample_rate),
                    reference.transcript,
                    [text],
                    self._model.ema_model,
                    self._model.vocoder,
                    self._model.mel_spec_type,
                    progress=None,
                    target_rms=0.1,
                    cross_fade_duration=0.15,
                    nfe_step=32,
                    cfg_strength=2,
                    sway_sampling_coef=-1,
                    speed=1.0,
                    fix_duration=None,
                    device=self._model.device,
                    streaming=True,
                    chunk_size=4_800,
                )
                for waveform, output_rate in stream:
                    if cancel.is_set():
                        return
                    if output_rate != 24_000:
                        raise EngineFault("F5 emitted a non-24-kHz waveform")
                    yield _float_wave_to_pcm16(waveform)
            except EngineFault:
                raise
            except Exception as error:
                self._raise_engine_error(error, "F5 synthesis failed.")

    def close(self) -> None:
        model = self._model
        self._model = None
        self._utils = None
        self._torchaudio = None
        self._torch = None
        if model is not None:
            del model

    def _raise_engine_error(self, error: Exception, summary: str) -> None:
        torch = self._torch
        oom_type = getattr(torch, "OutOfMemoryError", ()) if torch is not None else ()
        cuda_oom_type = (
            getattr(getattr(torch, "cuda", None), "OutOfMemoryError", ())
            if torch is not None
            else ()
        )
        if (
            isinstance(error, MemoryError)
            or (isinstance(oom_type, type) and isinstance(error, oom_type))
            or (isinstance(cuda_oom_type, type) and isinstance(error, cuda_oom_type))
            or error.__class__.__name__ in {"OutOfMemoryError", "CUDAOutOfMemoryError"}
        ):
            raise EngineOutOfMemory(summary) from error
        raise EngineFault(summary) from error


def _float_wave_to_pcm16(waveform: Any) -> bytes:
    try:
        flat = waveform.reshape(-1)
    except AttributeError:
        flat = waveform
    samples = len(flat)
    if samples <= 0 or samples > 4_800:
        raise EngineFault("F5 emitted an empty or oversized waveform fragment")
    pcm = bytearray(samples * 2)
    for index, raw_value in enumerate(flat):
        value = float(raw_value)
        if not math.isfinite(value):
            raise EngineFault("F5 emitted a non-finite sample")
        value = max(-1.0, min(1.0, value))
        converted = -32_768 if value <= -1.0 else int(round(value * 32_767))
        struct.pack_into("<h", pcm, index * 2, converted)
    return bytes(pcm)


class _DiscardTextOutput(io.TextIOBase):
    @property
    def encoding(self) -> str:
        return "utf-8"

    def writable(self) -> bool:
        return True

    def write(self, value: str) -> int:
        return len(value)

    def flush(self) -> None:
        return None


def _silence_third_party_output() -> contextlib.AbstractContextManager[None]:
    sink = _DiscardTextOutput()
    return _RedirectThirdPartyOutput(sink)


class _RedirectThirdPartyOutput(contextlib.AbstractContextManager[None]):
    def __init__(self, sink: _DiscardTextOutput) -> None:
        self.stdout = contextlib.redirect_stdout(sink)
        self.stderr = contextlib.redirect_stderr(sink)

    def __enter__(self) -> None:
        self.stdout.__enter__()
        self.stderr.__enter__()
        return None

    def __exit__(self, exception_type, exception, traceback) -> bool:
        self.stderr.__exit__(exception_type, exception, traceback)
        return self.stdout.__exit__(exception_type, exception, traceback)
