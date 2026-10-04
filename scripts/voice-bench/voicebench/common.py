"""Paths, audio, clip sets, statistics, GPU memory and result files shared by every benchmark."""

from __future__ import annotations

import io
import json
import os
import platform
import re
import socket
import statistics
import subprocess
import sys
import threading
import time
import wave
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Any, Iterable

import numpy as np

SAMPLE_RATE = 16_000
PACKAGE = Path(__file__).resolve().parent
BENCH = PACKAGE.parent
DATA = BENCH / "data"
REPO = BENCH.parent.parent
HOME = Path(os.environ.get("MARTLET_BENCH_HOME") or Path(os.environ.get("LOCALAPPDATA") or Path.home()) / "MartletBench")
CLIPS = HOME / "clips"
MODELS = HOME / "models"
RESULTS = HOME / "results"
TOOLS = HOME / "tools"
MARTLET_DATA = Path(os.environ.get("LOCALAPPDATA") or Path.home()) / "Martlet"


def log(message: str) -> None:
    print(message, file=sys.stderr, flush=True)


def now() -> float:
    return time.perf_counter()


def ms(seconds: float | None) -> float | None:
    return None if seconds is None else round(seconds * 1000.0, 1)


# ---------------------------------------------------------------- audio


def read_audio(path: Path | str) -> np.ndarray:
    """Any file soundfile reads, as 16 kHz mono float32."""
    import soundfile

    data, rate = soundfile.read(str(path), dtype="float32", always_2d=True)
    return to_16k(data.mean(axis=1), rate)


def decode_audio(blob: bytes) -> np.ndarray:
    import soundfile

    data, rate = soundfile.read(io.BytesIO(blob), dtype="float32", always_2d=True)
    return to_16k(data.mean(axis=1), rate)


def to_16k(samples: np.ndarray, rate: int) -> np.ndarray:
    samples = np.ascontiguousarray(samples, dtype=np.float32)
    if rate == SAMPLE_RATE:
        return samples
    import soxr

    return soxr.resample(samples, rate, SAMPLE_RATE).astype(np.float32)


def wav_bytes(samples: np.ndarray, rate: int = SAMPLE_RATE) -> bytes:
    pcm = (np.clip(samples, -1.0, 1.0) * 32767.0).astype("<i2").tobytes()
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as writer:
        writer.setnchannels(1)
        writer.setsampwidth(2)
        writer.setframerate(rate)
        writer.writeframes(pcm)
    return buffer.getvalue()


def write_wav(path: Path, samples: np.ndarray, rate: int = SAMPLE_RATE) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(wav_bytes(samples, rate))


# ---------------------------------------------------------------- clips


@dataclass
class Clip:
    id: str
    path: Path
    text: str
    seconds: float
    _samples: np.ndarray | None = None

    @property
    def samples(self) -> np.ndarray:
        if self._samples is None:
            self._samples = read_audio(self.path)
        return self._samples

    @property
    def wav(self) -> bytes:
        return wav_bytes(self.samples)


def clip_dir(name: str) -> Path:
    candidate = Path(name)
    return candidate if candidate.is_dir() else CLIPS / name


def load_clips(name: str, limit: int | None = None) -> list[Clip]:
    """A clip set: a folder of NAME.wav files, each with NAME.txt holding what was said (the reference)."""
    folder = clip_dir(name)
    if not folder.is_dir():
        raise SystemExit(f"No clip set '{name}' ({folder}). Make one with: voicebench clips fetch|synth|record")
    clips = []
    for wav in sorted(folder.glob("*.wav")):
        text_file = wav.with_suffix(".txt")
        text = text_file.read_text(encoding="utf-8").strip() if text_file.is_file() else ""
        with wave.open(str(wav), "rb") as reader:
            seconds = reader.getnframes() / float(reader.getframerate())
        clips.append(Clip(wav.stem, wav, text, seconds))
        if limit and len(clips) >= limit:
            break
    if not clips:
        raise SystemExit(f"The clip set '{name}' ({folder}) has no .wav files.")
    return clips


# ---------------------------------------------------------------- accuracy

_normalizer = None


def normalize(text: str) -> str:
    """Whisper's English normalizer (the Open ASR leaderboard's), so punctuation, case and spelled numbers don't count."""
    global _normalizer
    if _normalizer is None:
        try:
            from whisper_normalizer.english import EnglishTextNormalizer

            _normalizer = EnglishTextNormalizer()
        except ImportError:
            _normalizer = lambda value: re.sub(r"\s+", " ", re.sub(r"[^a-z0-9' ]", " ", value.lower())).strip()  # noqa: E731
    return _normalizer(text or "")


def wer(references: Iterable[str], hypotheses: Iterable[str]) -> float | None:
    """Corpus word error rate over every clip with a reference."""
    pairs = [(normalize(r), normalize(h)) for r, h in zip(references, hypotheses) if (r or "").strip()]
    pairs = [(r, h) for r, h in pairs if r]
    if not pairs:
        return None
    import jiwer

    return round(jiwer.wer([r for r, _ in pairs], [h for _, h in pairs]) * 100.0, 2)


# ---------------------------------------------------------------- statistics


def stats(values: Iterable[float | None]) -> dict[str, float | None]:
    data = sorted(v for v in values if v is not None)
    if not data:
        return {"n": 0, "median": None, "p90": None, "mean": None, "min": None, "max": None}
    p90 = data[min(len(data) - 1, int(round(0.9 * (len(data) - 1))))]
    return {
        "n": len(data),
        "median": round(statistics.median(data), 1),
        "p90": round(p90, 1),
        "mean": round(statistics.fmean(data), 1),
        "min": round(data[0], 1),
        "max": round(data[-1], 1),
    }


def fmt(value: Any, digits: int | None = None) -> str:
    if value is None:
        return "-"
    if isinstance(value, float):
        if digits is None:
            digits = 0 if abs(value) >= 100 else 1 if abs(value) >= 10 else 2
        return f"{value:,.{digits}f}"
    if isinstance(value, int):
        return f"{value:,}"
    return str(value)


def table(headers: list[str], rows: list[list[Any]]) -> str:
    lines = ["| " + " | ".join(headers) + " |", "| " + " | ".join("---" for _ in headers) + " |"]
    lines += ["| " + " | ".join(fmt(cell) if not isinstance(cell, str) else cell for cell in row) + " |" for row in rows]
    return "\n".join(lines)


# ---------------------------------------------------------------- GPU memory


class GpuMemory:
    """Samples the graphics card's used memory (all processes) every 100 ms; peak minus the idle baseline is what a
    benchmark added. Nothing when there is no NVIDIA card."""

    def __init__(self) -> None:
        self.baseline = self.used()
        self.peak = self.baseline
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None

    @staticmethod
    def used() -> float | None:
        try:
            import pynvml

            pynvml.nvmlInit()
            handle = pynvml.nvmlDeviceGetHandleByIndex(0)
            return pynvml.nvmlDeviceGetMemoryInfo(handle).used / 1048576.0
        except Exception:
            pass
        try:
            out = subprocess.run(["nvidia-smi", "--query-gpu=memory.used", "--format=csv,noheader,nounits"],
                                 capture_output=True, text=True, timeout=5)
            return float(out.stdout.split()[0])
        except Exception:
            return None

    def __enter__(self) -> "GpuMemory":
        def sample() -> None:
            while not self._stop.wait(0.1):
                value = self.used()
                if value is not None and (self.peak is None or value > self.peak):
                    self.peak = value

        self._thread = threading.Thread(target=sample, daemon=True)
        self._thread.start()
        return self

    def __exit__(self, *_: Any) -> None:
        self._stop.set()
        if self._thread:
            self._thread.join(1)

    @property
    def added_mb(self) -> float | None:
        if self.baseline is None or self.peak is None:
            return None
        return round(self.peak - self.baseline)


# ---------------------------------------------------------------- environment and results


def environment() -> dict[str, Any]:
    gpu = None
    try:
        out = subprocess.run(["nvidia-smi", "--query-gpu=name,memory.total,memory.used,driver_version", "--format=csv,noheader"],
                             capture_output=True, text=True, timeout=5)
        gpu = out.stdout.strip() or None
    except Exception:
        pass
    cpu = platform.processor()
    if sys.platform == "win32":
        try:
            out = subprocess.run(["powershell", "-NoProfile", "-Command", "(Get-CimInstance Win32_Processor).Name"],
                                 capture_output=True, text=True, timeout=15)
            cpu = out.stdout.strip() or cpu
        except Exception:
            pass
    return {"host": socket.gethostname(), "cpu": cpu, "cpu_threads": os.cpu_count(), "gpu": gpu,
            "python": platform.python_version(), "platform": platform.platform()}


class Results:
    """One run's rows, saved as JSON and Markdown under the bench home's results folder."""

    def __init__(self, command: str, settings: dict[str, Any]) -> None:
        self.command = command
        self.started = datetime.now()
        self.settings = settings
        self.rows: list[dict[str, Any]] = []
        self.details: list[dict[str, Any]] = []
        self.notes: list[str] = []
        self.markdown = ""
        RESULTS.mkdir(parents=True, exist_ok=True)
        self.stem = RESULTS / f"{self.started:%Y%m%d-%H%M%S}-{command}"

    @property
    def folder(self) -> Path:
        path = self.stem.with_name(self.stem.name + "-files")
        path.mkdir(parents=True, exist_ok=True)
        return path

    def save(self) -> Path:
        body = {"command": self.command, "started": self.started.isoformat(timespec="seconds"), "environment": environment(),
                "settings": self.settings, "summary": self.rows, "notes": self.notes, "details": self.details}
        path = self.stem.with_suffix(".json")
        path.write_text(json.dumps(body, indent=2, default=str), encoding="utf-8")
        env = body["environment"]
        header = (f"# voicebench {self.command}, {body['started']}\n\n{env['host']}: {env['gpu']}; {env['cpu']} "
                  f"({env['cpu_threads']} threads)\n\n")
        notes = "".join(f"- {note}\n" for note in self.notes)
        self.stem.with_suffix(".md").write_text(header + self.markdown + ("\n\n" + notes if notes else "") + "\n", encoding="utf-8")
        return path
