"""Martlet voice recognition host service: the ``voice-recognition`` host role.

It runs inside the role's container next to the Martlet gateway, whose voice recognition relay
(Martlet.Gateway.VoiceRecognition) is its only client. It does what Martlet's desktop does on its own processor
(src/Martlet.Sherpa/SpeakerEngine.cs), with the same models and the same steps, so a voiceprint made here matches the shared
voice list exactly like one made on the desktop:

* pyannote segmentation 3.0 finds who spoke when (sherpa-onnx's offline speaker diarization: up to three people per ten
  seconds, clustered across longer audio with threshold 0.5);
* WeSpeaker ResNet34-LM (trained on VoxCeleb) makes a 256-number voiceprint from each person's clean stretches only (nobody
  else talking), so overlapping speech never blends two people into one voiceprint.

``analyze`` and ``voiceprint`` are line-for-line ports of ``SpeakerEngine.Analyze`` and ``SpeakerEngine.Voiceprint``: the same
thresholds, the same order of operations and the same float32 and double arithmetic (.NET's rounding included), so the host and
the desktop give the same answer for the same audio. Both models are the desktop build's pinned files (exact size and SHA-256),
baked into the image when it is built. On an NVIDIA graphics card (ACCELERATOR=gpu, ONNX Runtime's CUDA provider) the service
first checks in a child process that CUDA works and otherwise runs on the processor; ``/status`` says which.

Audio stays in memory: it is never written to disk or logged. Analyses run one at a time, as on the desktop.

Commands:
  serve        listen on 127.0.0.1:$MARTLET_VOICE_RECOGNITION_PORT (default 50091) at once and load the models in the background
  wait-ready   wait until the local service says it is ready (martlet-host's warm step); exit 1 when it doesn't get there
  fetch-models <dir>   download and check the pinned models into <dir> (the image build)
  selftest     load the models on the processor and analyze generated sound
  probe        load the models with the provider in --provider and analyze generated sound (the CUDA check)
  analyze-files [--models DIR] [--provider cpu|cuda] FILE...   analyze 16 kHz mono PCM16 files (raw, or WAV with a 44-byte
               header) and print one JSON answer per file (Martlet's parity test with the desktop engine)

HTTP (127.0.0.1 only):
  GET  /status      {"state":"ready"|"loading","provider":"cuda"|"cpu","model","segmentation","accelerator","threads","runtime"}
  POST /analyze     application/octet-stream: 16 kHz mono PCM16 little-endian, at most 60 s ->
                    {"speakers":[{"local","voiceprint","clean_seconds","start","end"}],"voices","overlap","whole","speech_seconds",
                     "milliseconds"}
  POST /voiceprint  the same body -> {"voiceprint": null or 256 numbers, "milliseconds"}
"""

from __future__ import annotations

import argparse
import http.server
import importlib.metadata
import json
import math
import os
import random
import struct
import subprocess
import sys
import threading
import time
import urllib.request
from array import array
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Sequence

try:
    import martlet_worker_host as shared
except ModuleNotFoundError:  # a checkout: the shared module is in workers/common (the image copies it beside this file)
    sys.path.append(str(Path(__file__).resolve().parents[2] / "common"))
    import martlet_worker_host as shared

ROOT = Path(os.environ.get("MARTLET_VOICE_RECOGNITION_ROOT", "/opt/martlet-voice-recognition"))
MODELS = Path(os.environ.get("MARTLET_VOICE_RECOGNITION_MODELS", str(ROOT / "models")))
PORT = int(os.environ.get("MARTLET_VOICE_RECOGNITION_PORT", "50091"))
ACCELERATOR = os.environ.get("MARTLET_VOICE_RECOGNITION_ACCELERATOR", "cpu")

# SpeakerEngine's constants.
SAMPLE_RATE = 16_000
MAXIMUM_SECONDS = 60
MINIMUM_CLEAN_SECONDS = 2.0
MINIMUM_WHOLE_SECONDS = 1.0
EDGE_TRIM = 0.12
MINIMUM_FRAGMENT = 1.5
MAXIMUM_FRAGMENT = 8.0
CONSISTENCY = 0.45
CLUSTER_THRESHOLD = 0.5
DIMENSION = 256
MAXIMUM_TURNS = 4096
MAX_PCM_BYTES = SAMPLE_RATE * MAXIMUM_SECONDS * 2

MODEL_ID = "wespeaker-en-voxceleb-resnet34-lm"
SEGMENTATION_ID = "pyannote-segmentation-3-0"
DOWNLOAD_ATTEMPTS = 3
DOWNLOAD_TIMEOUT_SECONDS = 60


@dataclass(frozen=True)
class ModelFile:
    path: str
    size: int
    sha256: str
    url: str


# The desktop build's voice-recognition folder, file for file (src/Martlet.Desktop/Martlet.Desktop.csproj VoiceModel items and
# packaging/windows/toolchain.json voiceModels; a .NET test keeps them the same).
SPEAKER_MODEL = ModelFile(
    "wespeaker_en_voxceleb_resnet34_LM.onnx", 26_530_550, "e9848563da86f263117134dfd7ad63c92355b37de492b55e325400c9d9c39012",
    "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_resnet34_LM.onnx")
SEGMENTATION_MODEL = ModelFile(
    "pyannote-segmentation-3-0/model.onnx", 5_992_913, "220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079",
    "https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/9403a6902bb58e3d5ae8c7e77c3422de279db2e0/model.onnx")
SEGMENTATION_LICENSE = ModelFile(
    "pyannote-segmentation-3-0/LICENSE", 1_061, "14d7016ad68e7394d6e6b78d96cc2ae431c905287b89674cfdf021e79e62b8ba",
    "https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/9403a6902bb58e3d5ae8c7e77c3422de279db2e0/LICENSE")
MODEL_FILES = (SPEAKER_MODEL, SEGMENTATION_MODEL, SEGMENTATION_LICENSE)


def say(text: str) -> None:
    print(text, flush=True)


def threads() -> int:
    configured = os.environ.get("VOICE_RECOGNITION_THREADS", "")
    if configured.isdigit() and 0 < int(configured) <= 32:
        return int(configured)
    return max(1, min(4, os.cpu_count() or 1))


# ---------------------------------------------------------------------------------------------------- pinned models


def sha256_file(path: Path) -> str:
    return shared.sha256_file(path)[1]


def verified(root: Path, file: ModelFile) -> bool:
    return shared.is_pinned(root / file.path, file.size, file.sha256)


def _download(file: ModelFile, target: Path, opener: Callable[..., Any]) -> None:
    shared.download(file.url, target, file.size, file.sha256, agent="Martlet-Voice-Recognition-Host", label=file.path,
                    opener=opener, timeout=DOWNLOAD_TIMEOUT_SECONDS)


def fetch_models(root: Path, opener: Callable[..., Any] = urllib.request.urlopen, report: Callable[[str], None] = say,
                 pause: Callable[[float], None] = time.sleep) -> None:
    """Downloads every missing model file into ``root`` (the desktop's voice-recognition layout), each checked for its exact
    size and SHA-256; a changed or cut download is deleted, never kept."""
    for file in MODEL_FILES:
        target = root / file.path
        if verified(root, file):
            continue
        target.parent.mkdir(parents=True, exist_ok=True)
        report(f"Downloading {file.path} ({file.size / 1_000_000:.1f} MB)...")
        for attempt in range(1, DOWNLOAD_ATTEMPTS + 1):
            try:
                _download(file, target, opener)
                break
            except (OSError, ValueError) as error:
                if attempt == DOWNLOAD_ATTEMPTS:
                    raise SystemExit(f"Couldn't download {file.path}: {error}") from error
                report(f"{file.path}: {error}; trying again ({attempt + 1} of {DOWNLOAD_ATTEMPTS})...")
                pause(2 * attempt)


def check_models(root: Path) -> None:
    for file in MODEL_FILES:
        if not verified(root, file):
            raise SystemExit(f"{root / file.path} is missing or is not the pinned file (SHA-256 {file.sha256}).")


# ---------------------------------------------------------------------------------------------------- SpeakerEngine's steps


def dotnet_round2(value: float) -> float:
    """.NET's Math.Round(value, 2): scale by 100, round half to even, scale back (not Python's exact-decimal round)."""
    if abs(value) >= 1e16:
        return value
    return float(round(value * 100.0)) / 100.0


def float32(values: Sequence[float]) -> array:
    """Values stored as C# floats: array('f') rounds each double to the nearest float32, as a (float) cast does. A float32 sum
    computed as a double and stored this way is the float32 sum itself (a double has more than twice a float's precision)."""
    return array("f", values)


def normalize(vector: Sequence[float]) -> array:
    """SpeakerEngine.Normalize: the norm as a double summed in order, each value divided as a double and stored as a float."""
    values = [float(x) for x in vector]
    total = 0.0
    for x in values:
        total += x * x
    norm = math.sqrt(total)
    if norm < 1e-8:
        return float32(values)
    return float32([x / norm for x in values])


def cosine(a: Any, b: Any) -> float:
    total = 0.0
    for x, y in zip(a, b):
        total += float(x) * float(y)
    return total


def clean(turns: Sequence[tuple[float, float, int]], speaker: int) -> list[tuple[float, float]]:
    """SpeakerEngine.Clean: a person's stretches with nobody else talking, trimmed at the edges, longest first."""
    merged: list[tuple[float, float]] = []
    for start, end, _ in sorted((t for t in turns if t[2] == speaker), key=lambda t: t[0]):
        if merged and merged[-1][1] >= start:
            merged[-1] = (merged[-1][0], max(merged[-1][1], end))
        else:
            merged.append((start, end))
    result: list[tuple[float, float]] = []
    for span in merged:
        spans = [span]
        for other_start, other_end, other_speaker in turns:
            if other_speaker == speaker:
                continue
            following: list[tuple[float, float]] = []
            for start, end in spans:
                if other_end <= start or other_start >= end:
                    following.append((start, end))
                else:
                    if other_start > start:
                        following.append((start, min(end, other_start)))
                    if other_end < end:
                        following.append((max(start, other_end), end))
            spans = following
        result.extend((start + EDGE_TRIM, end - EDGE_TRIM) for start, end in spans
                      if end - start - 2 * EDGE_TRIM >= MINIMUM_FRAGMENT)
    # OrderByDescending(length).ThenBy(start): both keys at once keeps it one stable sort.
    return sorted(result, key=lambda s: (-(s[1] - s[0]), s[0]))


def distinct(items: Sequence[Any]) -> list[Any]:
    seen: set[Any] = set()
    kept = []
    for item in items:
        if item not in seen:
            seen.add(item)
            kept.append(item)
    return kept


Segmenter = Callable[[Any, float], Sequence[tuple[float, float, int]]]
Embedder = Callable[[Any], Any]


def analyze(samples: Any, segment: Segmenter, embed: Embedder) -> dict[str, Any]:
    """SpeakerEngine.Analyze on float32 ``samples`` (16 kHz mono, at most a minute), with ``segment`` giving the sorted,
    distinct (start, end, speaker) turns and ``embed`` a normalized voiceprint or None."""
    count = len(samples)
    if count > MAXIMUM_SECONDS * SAMPLE_RATE:
        raise ValueError("At most a minute of audio can be analyzed at once.")
    duration = count / float(SAMPLE_RATE)
    turns = list(segment(samples, duration))
    speakers = []
    for local in sorted(set(t[2] for t in turns)):
        vectors = []
        seconds, first_at, last_at = 0.0, sys.float_info.max, 0.0
        for start, end in clean(turns, local)[:3]:
            first = int(math.ceil(start * SAMPLE_RATE))
            last = min(count, min(int(math.floor(end * SAMPLE_RATE)), first + int(MAXIMUM_FRAGMENT * SAMPLE_RATE)))
            if last - first < MINIMUM_FRAGMENT * SAMPLE_RATE:
                continue
            vector = embed(samples[first:last])
            if vector is None:
                continue
            vectors.append(vector)
            seconds += (last - first) / float(SAMPLE_RATE)
            first_at = min(first_at, first / float(SAMPLE_RATE))
            last_at = max(last_at, last / float(SAMPLE_RATE))
        # Too little to trust, or fragments that disagree (a mixed cluster) never reach the voice list.
        if seconds < MINIMUM_CLEAN_SECONDS or not vectors or any(cosine(a, b) < CONSISTENCY for a in vectors for b in vectors):
            continue
        average = float32([0.0] * len(vectors[0]))
        for vector in vectors:
            for i in range(len(average)):
                average[i] += vector[i]
        speakers.append({"local": local, "voiceprint": normalize(average), "clean_seconds": dotnet_round2(seconds),
                         "start": dotnet_round2(first_at), "end": dotnet_round2(last_at)})
    speech = 0.0
    for start, end in distinct([(t[0], t[1]) for t in turns]):
        speech += end - start
    overlap = any(a[2] != b[2] and min(a[1], b[1]) - max(a[0], b[0]) > 0.2 for a in turns for b in turns)
    whole = embed(samples) if not speakers and duration >= MINIMUM_WHOLE_SECONDS else None
    return {
        "speakers": sorted(speakers, key=lambda s: s["clean_seconds"], reverse=True),
        "voices": len(set(t[2] for t in turns)),
        "overlap": overlap,
        "whole": whole,
        "speech_seconds": dotnet_round2(min(speech, duration)),
    }


def voiceprint(samples: Any, embed: Embedder) -> Any:
    """SpeakerEngine.Voiceprint: one voiceprint of all of ``samples`` (no segmentation), or None when it is too short."""
    if len(samples) > MAXIMUM_SECONDS * SAMPLE_RATE:
        raise ValueError("At most a minute of audio can be analyzed at once.")
    return embed(samples) if len(samples) >= MINIMUM_WHOLE_SECONDS * SAMPLE_RATE else None


def shortest(value: float) -> float:
    """A float32 as the fewest digits that read back as the very same float32 (as .NET writes a float)."""
    exact = struct.pack("<f", value)
    for digits in range(1, 10):
        text = f"{value:.{digits}g}"
        if struct.pack("<f", float(text)) == exact:
            return float(text)
    return value


def json_vector(vector: Sequence[float] | None) -> list[float] | None:
    """A voiceprint as JSON numbers: each float32 by its shortest text, so the desktop reads back the very same floats."""
    return None if vector is None else [shortest(float(x)) for x in vector]


def answer(analysis: dict[str, Any]) -> dict[str, Any]:
    return dict(analysis, speakers=[dict(s, voiceprint=json_vector(s["voiceprint"])) for s in analysis["speakers"]],
                whole=json_vector(analysis["whole"]))


# ---------------------------------------------------------------------------------------------------- audio


class BadRequest(Exception):
    """The request isn't audio this service can analyze; the message says why (never the audio)."""


class NotReady(Exception):
    pass


def samples_of(pcm: bytes) -> array:
    """16 kHz mono PCM16 little-endian as floats, the desktop's Pcm.ToFloats (each sample / 32768, exact in a float)."""
    if not 0 < len(pcm) <= MAX_PCM_BYTES or len(pcm) % 2:
        raise BadRequest(f"send 2 to {MAX_PCM_BYTES} bytes of 16 kHz mono PCM16 (at most {MAXIMUM_SECONDS} s)")
    values = array("h", pcm)
    if sys.byteorder == "big":
        values.byteswap()
    return float32([v / 32768.0 for v in values])


def noise(seconds: float = 3.0) -> array:
    """Faint noise with a voiced buzz in the middle, so segmentation and the voice model both run while warming up."""
    rng = random.Random(7)
    count = int(SAMPLE_RATE * seconds)
    return float32([max(-1.0, min(1.0, rng.gauss(0, 0.05) +
                                  (0.3 * math.sin(2 * math.pi * 140 * i / SAMPLE_RATE) if abs(i / SAMPLE_RATE - seconds / 2) < seconds / 3 else 0.0)))
                    for i in range(count)])


# ---------------------------------------------------------------------------------------------------- the models


class Engine:
    """The two models in sherpa-onnx, configured as SpeakerEngine configures them."""

    def __init__(self, models: Path, provider: str, thread_count: int) -> None:
        import sherpa_onnx  # noqa: PLC0415 - the tests run the plumbing without the runtime

        speaker = str(models / SPEAKER_MODEL.path)
        self._extractor = sherpa_onnx.SpeakerEmbeddingExtractor(
            sherpa_onnx.SpeakerEmbeddingExtractorConfig(model=speaker, num_threads=thread_count, provider=provider))
        if self._extractor.dim != DIMENSION:
            raise RuntimeError("The voice model has an unexpected format.")
        # The C API's defaults the desktop leaves in place: window shift 0.1, min_duration_on 0.3, min_duration_off 0.5.
        config = sherpa_onnx.OfflineSpeakerDiarizationConfig(
            segmentation=sherpa_onnx.OfflineSpeakerSegmentationModelConfig(
                pyannote=sherpa_onnx.OfflineSpeakerSegmentationPyannoteModelConfig(model=str(models / SEGMENTATION_MODEL.path)),
                num_threads=thread_count, provider=provider),
            embedding=sherpa_onnx.SpeakerEmbeddingExtractorConfig(model=speaker, num_threads=thread_count, provider=provider),
            clustering=sherpa_onnx.FastClusteringConfig(num_clusters=-1, threshold=CLUSTER_THRESHOLD),
            min_duration_on=0.3, min_duration_off=0.5)
        if not config.validate():
            raise RuntimeError("The speaker segmentation configuration is invalid.")
        self._diarizer = sherpa_onnx.OfflineSpeakerDiarization(config)
        if self._diarizer.sample_rate != SAMPLE_RATE:
            raise RuntimeError("The speaker segmentation model has an unexpected format.")

    def segment(self, samples: Sequence[float], duration: float) -> list[tuple[float, float, int]]:
        if len(samples) == 0:
            return []
        segments = self._diarizer.process(list(samples)).sort_by_start_time()
        if len(segments) > MAXIMUM_TURNS:
            raise RuntimeError("Speaker segmentation returned too many turns.")
        turns = []
        for item in segments:
            start, end, speaker = float(item.start), float(item.end), int(item.speaker)
            if not math.isfinite(start) or not math.isfinite(end) or speaker < 0:
                continue
            first, last = max(0.0, start), min(duration, end)
            if last > first:
                turns.append((first, last, speaker))
        return distinct(turns)

    def embed(self, samples: Sequence[float]) -> array | None:
        stream = self._extractor.create_stream()
        stream.accept_waveform(SAMPLE_RATE, list(samples))
        stream.input_finished()
        if not self._extractor.is_ready(stream):
            return None
        vector = float32(self._extractor.compute(stream))[:DIMENSION]
        if len(vector) != DIMENSION or not all(math.isfinite(v) for v in vector) or not any(v != 0 for v in vector):
            return None
        return normalize(vector)

    @staticmethod
    def runtime() -> dict[str, str]:
        versions: dict[str, str] = {}
        for package in ("sherpa-onnx", "sherpa-onnx-core", "numpy"):
            try:
                versions[package] = importlib.metadata.version(package)
            except importlib.metadata.PackageNotFoundError:
                pass
        try:
            import sherpa_onnx  # noqa: PLC0415

            value = sherpa_onnx.onnxruntime_version
            versions["onnxruntime"] = str(value() if callable(value) else value)
        except (ImportError, AttributeError):
            pass
        return versions


def cuda_works(timeout: float = 300.0) -> tuple[bool, str]:
    """Whether the models run with ONNX Runtime's CUDA provider here: checked in a child process, so a missing graphics card or
    driver can't take the service down. sherpa-onnx falls back to the processor by itself when its build has no CUDA; that
    counts as no."""
    try:
        done = subprocess.run([sys.executable, os.path.abspath(__file__), "probe", "--provider", "cuda"], capture_output=True,
                              text=True, timeout=timeout, check=False)
    except (OSError, subprocess.TimeoutExpired) as error:
        return False, type(error).__name__
    output = done.stdout + done.stderr
    if done.returncode == 0 and "probe-ok cuda" in done.stdout and "Fallback to cpu" not in output:
        return True, ""
    lines = [line.strip() for line in output.splitlines() if line.strip()]
    return False, (lines[-1] if lines else f"exit code {done.returncode}")[:300]


# ---------------------------------------------------------------------------------------------------- the service


class Service:
    """What the HTTP handler answers with: the engine, loaded in the background, and its status."""

    def __init__(self, engine: Any = None, provider: str = "cpu", accelerator: str = "cpu", thread_count: int = 1,
                 runtime: dict[str, str] | None = None, clock: Callable[[], float] = time.perf_counter) -> None:
        self.engine = engine
        self.provider = provider
        self.accelerator = accelerator
        self.threads = thread_count
        self.runtime = runtime or {}
        self._clock = clock
        self._lock = threading.Lock()

    @property
    def ready(self) -> bool:
        return self.engine is not None

    def load(self, models: Path, loader: Callable[[Path, str, int], Any] = Engine,
             probe: Callable[[], tuple[bool, str]] = cuda_works, checker: Callable[[Path], None] = check_models) -> None:
        """Checks the pinned models, picks CUDA when it works here (ACCELERATOR=gpu) and the processor otherwise, loads and warms
        up. A failure on CUDA loads on the processor instead; a failure there leaves the service loading (the caller exits)."""
        checker(models)
        provider = "cpu"
        if self.accelerator == "gpu":
            self.provider = "cuda"
            works, why = probe()
            if works:
                provider = "cuda"
            else:
                say(f"CUDA isn't usable here ({why}); voice recognition runs on the processor.")
        self.provider = provider
        say(f"Loading the voice models on the {'NVIDIA graphics card' if provider == 'cuda' else 'processor'} "
            f"with {self.threads} threads...")
        started = time.monotonic()
        try:
            engine = loader(models, provider, self.threads)
        except Exception as error:  # noqa: BLE001 - CUDA can still fail here; the processor is the fallback
            if provider == "cpu":
                raise
            say(f"Loading on the graphics card failed ({type(error).__name__}); voice recognition runs on the processor.")
            self.provider = provider = "cpu"
            engine = loader(models, provider, self.threads)
        warm = noise()
        analyze(warm, engine.segment, engine.embed)
        self.engine = engine
        say(f"Voice models ready on {provider} in {time.monotonic() - started:.1f} s; listening on 127.0.0.1:{PORT}.")

    def status(self) -> dict[str, Any]:
        return {"state": "ready" if self.ready else "loading", "provider": self.provider, "model": MODEL_ID,
                "segmentation": SEGMENTATION_ID, "accelerator": self.accelerator, "threads": self.threads, "runtime": self.runtime}

    def _run(self, pcm: bytes, work: Callable[[Any, Any], Any]) -> tuple[Any, int]:
        if self.engine is None:
            raise NotReady()
        samples = samples_of(pcm)
        try:
            with self._lock:
                started = self._clock()
                result = work(samples, self.engine)
                elapsed = int(round((self._clock() - started) * 1000))
        finally:
            for i in range(len(samples)):
                samples[i] = 0.0
        return result, elapsed

    def analyze(self, pcm: bytes) -> dict[str, Any]:
        result, elapsed = self._run(pcm, lambda samples, engine: analyze(samples, engine.segment, engine.embed))
        return dict(answer(result), milliseconds=elapsed)

    def voiceprint(self, pcm: bytes) -> dict[str, Any]:
        result, elapsed = self._run(pcm, lambda samples, engine: voiceprint(samples, engine.embed))
        return {"voiceprint": json_vector(result), "milliseconds": elapsed}


class Handler(shared.JsonHandler):
    server_version = "MartletVoiceRecognition/1"
    service: Service

    def refusal(self, code: str, summary: str) -> dict[str, Any]:
        return {"error": code, "message": summary}

    def do_GET(self) -> None:  # noqa: N802
        if self.path == "/status":
            self._json(200, self.service.status())
        else:
            self._json(404, {"error": "request.not_found"})

    def do_POST(self) -> None:  # noqa: N802
        operations = {"/analyze": self.service.analyze, "/voiceprint": self.service.voiceprint}
        operation = operations.get(self.path)
        if operation is None:
            self._json(404, {"error": "request.not_found"})
            return
        media_type = (self.headers.get("Content-Type") or "").split(";", 1)[0].strip().lower()
        # The relay reads 400 as request.invalid, so audio longer than a minute is a 400 here too.
        length = self.content_length(MAX_PCM_BYTES, too_large=400)
        if length is None:
            return
        if media_type != "application/octet-stream" or not 0 < length or length % 2:
            self.refuse(400, "request.invalid", f"send application/octet-stream with 2 to {MAX_PCM_BYTES} bytes of 16 kHz mono PCM16")
            return
        body = bytearray(self.rfile.read(length))
        try:
            self._json(200, operation(bytes(body)))
        except BadRequest as error:
            self._json(400, {"error": "request.invalid", "message": str(error)})
        except NotReady:
            self._json(503, {"error": "worker.loading"})
        except Exception as error:  # noqa: BLE001 - the relay only needs to know it failed
            say(f"Voice recognition failed: {type(error).__name__}")
            self._json(500, {"error": "worker.failed"})
        finally:
            body[:] = bytes(len(body))


def serve(service: Service, port: int = PORT) -> http.server.ThreadingHTTPServer:
    handler = type("BoundHandler", (Handler,), {"service": service})
    return http.server.ThreadingHTTPServer(("127.0.0.1", port), handler)


def wait_ready(port: int = PORT, minutes: float = 10.0, opener: Callable[..., Any] = urllib.request.urlopen,
               pause: Callable[[float], None] = time.sleep) -> bool:
    """martlet-host's warm step: the role's port opens at once, the models take a little longer."""
    deadline = time.monotonic() + minutes * 60
    while True:
        try:
            with opener(f"http://127.0.0.1:{port}/status", timeout=10) as response:
                status = json.loads(response.read())
            if status.get("state") == "ready":
                say(f"Voice recognition is ready on {status.get('provider')}.")
                return True
        except (OSError, ValueError):
            pass
        if time.monotonic() >= deadline:
            return False
        pause(2)


def read_pcm_file(path: Path) -> bytes:
    data = path.read_bytes()
    return data[44:] if data[:4] == b"RIFF" else data


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Martlet voice recognition host service")
    parser.add_argument("command", choices=("serve", "wait-ready", "fetch-models", "selftest", "probe", "analyze-files"),
                        nargs="?", default="serve")
    parser.add_argument("paths", nargs="*")
    parser.add_argument("--models", default=str(MODELS))
    parser.add_argument("--provider", choices=("cpu", "cuda"), default="cpu")
    args = parser.parse_args(argv)
    models = Path(args.models)
    if args.command == "fetch-models":
        fetch_models(Path(args.paths[0]) if args.paths else models)
        say("The voice models are ready.")
        return 0
    if args.command == "wait-ready":
        return 0 if wait_ready() else 1
    if args.command in ("selftest", "probe"):
        check_models(models)
        engine = Engine(models, args.provider, threads())
        result = analyze(noise(), engine.segment, engine.embed)
        say(f"probe-ok {args.provider} voices={result['voices']}")
        return 0
    if args.command == "analyze-files":
        check_models(models)
        engine = Engine(models, args.provider, threads())
        for path in args.paths:
            samples = samples_of(read_pcm_file(Path(path)))
            started = time.perf_counter()
            result = answer(analyze(samples, engine.segment, engine.embed))
            result["milliseconds"] = int(round((time.perf_counter() - started) * 1000))
            result["voiceprint"] = json_vector(voiceprint(samples, engine.embed))
            print(json.dumps(result, separators=(",", ":")), flush=True)
        return 0
    count = threads()
    service = Service(accelerator=ACCELERATOR if ACCELERATOR in ("cpu", "gpu") else "cpu", thread_count=count,
                      provider="cuda" if ACCELERATOR == "gpu" else "cpu", runtime=Engine.runtime())
    server = serve(service)
    say(f"Listening on 127.0.0.1:{PORT}; loading the voice models...")

    def load() -> None:
        try:
            service.load(models)
        except BaseException as error:  # noqa: BLE001 - restart the container (restart: unless-stopped) rather than hang
            say(f"Voice recognition failed to load: {type(error).__name__}: {error}"[:400])
            os._exit(1)

    threading.Thread(target=load, daemon=True).start()
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
