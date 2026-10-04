"""Speech-to-text engines and the speech-to-text benchmark (speed and accuracy, one engine per process)."""

from __future__ import annotations

import json
import os
import shutil
import site
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.request
from pathlib import Path
from typing import Any

import numpy as np

from .common import (MARTLET_DATA, MODELS, SAMPLE_RATE, Clip, GpuMemory, Results, load_clips, log, ms, now, stats, table,
                     wav_bytes, wer)

SHERPA_RELEASE = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/{}.tar.bz2"
# Martlet's own choice for Parakeet on this PC (ParakeetEngine): a quarter of the logical processors, 2 to 4.
MARTLET_THREADS = max(2, min(4, (os.cpu_count() or 8) // 4))

ENGINES: dict[str, dict[str, Any]] = {
    # On this PC's processor through sherpa-onnx, the runtime Martlet already ships.
    "parakeet-v3": {"kind": "sherpa-transducer", "archive": "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8",
                    "martlet": "parakeet-tdt-0.6b-v3-int8", "size": "0.67 GB",
                    "about": "Martlet's on-device model today: Parakeet TDT 0.6B v3 int8, 25 languages, CPU"},
    "parakeet-v2-en": {"kind": "sherpa-transducer", "archive": "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8", "size": "0.66 GB",
                       "about": "Parakeet TDT 0.6B v2 int8, English only, CPU"},
    "parakeet-unified-en": {"kind": "sherpa-transducer", "archive": "sherpa-onnx-nemo-parakeet-unified-en-0.6b-int8-non-streaming",
                            "size": "0.68 GB", "about": "Parakeet unified 0.6B int8 (English, offline mode), CPU"},
    "parakeet-110m-en": {"kind": "sherpa-transducer", "archive": "sherpa-onnx-nemo-parakeet_tdt_transducer_110m-en-36000-int8",
                         "size": "0.13 GB", "about": "Parakeet TDT 110M int8, English only, CPU"},
    "moonshine-base": {"kind": "sherpa-moonshine", "archive": "sherpa-onnx-moonshine-base-en-int8", "size": "0.29 GB",
                       "about": "Moonshine base int8, English only, CPU"},
    "moonshine-tiny": {"kind": "sherpa-moonshine", "archive": "sherpa-onnx-moonshine-tiny-en-int8", "size": "0.12 GB",
                       "about": "Moonshine tiny int8, English only, CPU"},
    "moonshine2-base": {"kind": "sherpa-moonshine", "archive": "sherpa-onnx-moonshine-base-en-quantized-2026-02-27",
                        "size": "0.13 GB", "about": "Moonshine v2 base (2026-02-27) quantized, English only, CPU (fails in "
                                                    "sherpa-onnx 1.13.8: shape errors on most clips)"},
    "qwen3-asr-0.6b": {"kind": "sherpa-qwen3", "archive": "sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25", "size": "0.98 GB",
                       "about": "Qwen3-ASR 0.6B int8 (2026-03), 52 languages, CPU"},
    # faster-whisper (CTranslate2) on the NVIDIA GPU, float16 unless noted.
    "fw-base.en": {"kind": "faster-whisper", "model": "base.en", "size": "0.15 GB", "about": "Whisper base.en, faster-whisper, GPU"},
    "fw-small.en": {"kind": "faster-whisper", "model": "small.en", "size": "0.48 GB", "about": "Whisper small.en, faster-whisper, GPU"},
    "fw-distil-large-v3": {"kind": "faster-whisper", "model": "distil-large-v3", "size": "1.5 GB",
                           "about": "Distil-Whisper large-v3, faster-whisper, GPU"},
    "fw-large-v3-turbo": {"kind": "faster-whisper", "model": "large-v3-turbo", "size": "1.6 GB",
                          "about": "Whisper large-v3-turbo, faster-whisper, GPU"},
    "fw-large-v3": {"kind": "faster-whisper", "model": "large-v3", "size": "3.1 GB", "about": "Whisper large-v3, faster-whisper, GPU"},
    "fw-large-v3-int8": {"kind": "faster-whisper", "model": "large-v3", "compute": "int8_float16", "size": "1.6 GB in memory",
                         "about": "Whisper large-v3, faster-whisper int8 weights, GPU (half the memory)"},
    "fw-small.en-cpu": {"kind": "faster-whisper", "model": "small.en", "device": "cpu", "compute": "int8", "size": "0.48 GB",
                        "about": "Whisper small.en, faster-whisper int8, CPU"},
    "fw-large-v3-turbo-cpu": {"kind": "faster-whisper", "model": "large-v3-turbo", "device": "cpu", "compute": "int8",
                              "size": "1.6 GB", "about": "Whisper large-v3-turbo, faster-whisper int8, CPU"},
    # The reference implementation of Whisper large (PyTorch, Hugging Face transformers) on the GPU.
    "hf-whisper-large-v3": {"kind": "transformers", "model": "openai/whisper-large-v3", "size": "3.1 GB",
                            "about": "Whisper large-v3, PyTorch transformers fp16, GPU"},
    "hf-whisper-large-v3-turbo": {"kind": "transformers", "model": "openai/whisper-large-v3-turbo", "size": "1.6 GB",
                                  "about": "Whisper large-v3-turbo, PyTorch transformers fp16, GPU"},
    # Martlet's host speech-to-text role: whisper.cpp's server in Docker (deploy/host/roles/stt).
    "whispercpp-role": {"kind": "whispercpp", "role": True, "size": "role",
                        "about": "Martlet's running host speech-to-text role (whisper.cpp, CUDA, its chosen model), via voicebench relay"},
    "whispercpp-large-v3-turbo": {"kind": "whispercpp", "model": "large-v3-turbo", "size": "1.6 GB",
                                  "about": "Martlet's host speech-to-text role today: whisper.cpp large-v3-turbo, CUDA, Docker"},
    "whispercpp-small": {"kind": "whispercpp", "model": "small", "size": "0.47 GB", "about": "whisper.cpp small, CUDA, Docker"},
    # Cloud (paid): only with --allow-cloud and a key.
    "openai-mini-transcribe": {"kind": "openai", "model": "gpt-4o-mini-transcribe", "cloud": True, "size": "cloud",
                               "about": "OpenAI gpt-4o-mini-transcribe (cloud, paid)"},
}

DEFAULT_ENGINES = ["parakeet-v3", "parakeet-v2-en", "moonshine-base", "fw-small.en", "fw-distil-large-v3", "fw-large-v3-turbo",
                   "fw-large-v3", "hf-whisper-large-v3", "whispercpp-large-v3-turbo"]


# ---------------------------------------------------------------- engines


class Engine:
    device = "cpu"

    def __init__(self, name: str, spec: dict[str, Any], beam: int = 1) -> None:
        self.name, self.spec, self.beam = name, spec, beam

    def load(self) -> None: ...

    def transcribe(self, samples: np.ndarray) -> str:
        raise NotImplementedError

    def close(self) -> None: ...


def _download_sherpa(archive: str) -> Path:
    root = MODELS / "sherpa"
    folder = root / archive
    if folder.is_dir():
        return folder
    root.mkdir(parents=True, exist_ok=True)
    url = SHERPA_RELEASE.format(archive)
    log(f"Downloading {url}")
    with tempfile.TemporaryDirectory(dir=root) as temp:
        target = Path(temp) / "model.tar.bz2"
        urllib.request.urlretrieve(url, target)
        with tarfile.open(target, "r:bz2") as tar:
            tar.extractall(temp, filter="data")
        extracted = next(p for p in Path(temp).iterdir() if p.is_dir())
        shutil.move(str(extracted), folder)
    return folder


def _one(folder: Path, *patterns: str) -> str:
    for pattern in patterns:
        found = sorted(folder.rglob(pattern))
        if found:
            return str(found[0])
    raise SystemExit(f"{folder} has no {' or '.join(patterns)}")


class Sherpa(Engine):
    def load(self) -> None:
        import sherpa_onnx

        martlet = self.spec.get("martlet")
        folder = MARTLET_DATA / "speech" / "models" / martlet if martlet else None
        if folder is None or not folder.is_dir():
            folder = _download_sherpa(self.spec["archive"])
        else:
            log(f"Using Martlet's own {martlet} from {folder}")
        threads = MARTLET_THREADS
        if self.spec["kind"] == "sherpa-transducer":
            self.recognizer = sherpa_onnx.OfflineRecognizer.from_transducer(
                encoder=_one(folder, "encoder.int8.onnx", "encoder*.onnx"), decoder=_one(folder, "decoder.int8.onnx", "decoder*.onnx"),
                joiner=_one(folder, "joiner.int8.onnx", "joiner*.onnx"), tokens=_one(folder, "tokens.txt"), num_threads=threads,
                sample_rate=SAMPLE_RATE, feature_dim=80, decoding_method="greedy_search", model_type="nemo_transducer",
                provider="cpu")
        elif self.spec["kind"] == "sherpa-qwen3":
            self.recognizer = sherpa_onnx.OfflineRecognizer.from_qwen3_asr(
                conv_frontend=_one(folder, "conv_frontend.onnx"), encoder=_one(folder, "encoder*.onnx"),
                decoder=_one(folder, "decoder*.onnx"), tokenizer=str(folder / "tokenizer"), num_threads=threads, max_new_tokens=256)
        elif any(folder.rglob("preprocess*.onnx")):
            self.recognizer = sherpa_onnx.OfflineRecognizer.from_moonshine(
                preprocessor=_one(folder, "preprocess*.onnx"), encoder=_one(folder, "encode*.onnx"),
                uncached_decoder=_one(folder, "uncached_decode*.onnx"), cached_decoder=_one(folder, "cached_decode*.onnx"),
                tokens=_one(folder, "tokens.txt"), num_threads=threads)
        else:
            # Moonshine v2 exports (.ort): an encoder and one merged decoder.
            self.recognizer = sherpa_onnx.OfflineRecognizer.from_moonshine_v2(
                encoder=_one(folder, "encoder*.onnx", "encoder*.ort"), decoder=_one(folder, "*decoder*.onnx", "*decoder*.ort"),
                tokens=_one(folder, "tokens.txt"), num_threads=threads)
        self.threads = threads

    def transcribe(self, samples: np.ndarray) -> str:
        stream = self.recognizer.create_stream()
        stream.accept_waveform(SAMPLE_RATE, samples)
        self.recognizer.decode_stream(stream)
        return stream.result.text.strip()


def add_cuda_dlls() -> None:
    """faster-whisper's CTranslate2 needs cuBLAS and cuDNN, which the nvidia-* wheels install beside it on Windows."""
    for base in site.getsitepackages():
        for sub in ("nvidia/cublas/bin", "nvidia/cudnn/bin", "nvidia/cuda_nvrtc/bin"):
            path = Path(base) / sub
            if path.is_dir():
                os.add_dll_directory(str(path)) if hasattr(os, "add_dll_directory") else None
                os.environ["PATH"] = str(path) + os.pathsep + os.environ.get("PATH", "")


class FasterWhisper(Engine):
    def load(self) -> None:
        add_cuda_dlls()
        from faster_whisper import WhisperModel

        self.device = self.spec.get("device", "cuda")
        compute = self.spec.get("compute", "float16" if self.device == "cuda" else "int8")
        self.model = WhisperModel(self.spec["model"], device=self.device, compute_type=compute,
                                  download_root=str(MODELS / "faster-whisper"), cpu_threads=MARTLET_THREADS)

    def transcribe(self, samples: np.ndarray) -> str:
        segments, _ = self.model.transcribe(samples, language="en", beam_size=self.beam, vad_filter=False,
                                            condition_on_previous_text=False, without_timestamps=True)
        return "".join(segment.text for segment in segments).strip()


class Transformers(Engine):
    device = "cuda"

    def load(self) -> None:
        import torch
        import transformers
        from transformers import AutoModelForSpeechSeq2Seq, AutoProcessor

        transformers.logging.set_verbosity_error()
        self.torch = torch
        self.processor = AutoProcessor.from_pretrained(self.spec["model"])
        self.model = AutoModelForSpeechSeq2Seq.from_pretrained(self.spec["model"], dtype=torch.float16,
                                                               attn_implementation="sdpa").to("cuda").eval()

    def transcribe(self, samples: np.ndarray) -> str:
        long = len(samples) > 30 * SAMPLE_RATE
        inputs = self.processor(samples, sampling_rate=SAMPLE_RATE, return_tensors="pt", return_attention_mask=True,
                                **({"truncation": False, "padding": "longest"} if long else {}))
        inputs = inputs.to("cuda", self.torch.float16)
        with self.torch.inference_mode():
            ids = self.model.generate(**inputs, language="en", task="transcribe", num_beams=self.beam, max_new_tokens=440,
                                      **({"return_timestamps": True} if long else {}))
        return self.processor.batch_decode(ids, skip_special_tokens=True)[0].strip()


WHISPERCPP_CONTAINER = "martlet-bench-whispercpp"


class WhisperCpp(Engine):
    """Martlet's host role image (ghcr.io/ggml-org/whisper.cpp CUDA) with its models volume, on a published port, run the way
    the role runs it (-l auto, at most 8 threads, --suppress-nst)."""

    device = "cuda"
    port = 8189

    def load(self) -> None:
        import httpx

        self.client = httpx.Client(timeout=120)
        if self.spec.get("role"):
            self.port = 8188
            try:
                self.client.get(f"http://127.0.0.1:{self.port}/")
            except httpx.HTTPError:
                raise SystemExit("The speech-to-text role isn't reachable: run voicebench relay start first.") from None
            self.owned = False
            return
        self.owned = True
        images = subprocess.run(["docker", "images", "--format", "{{.Repository}}:{{.Tag}}"], capture_output=True, text=True,
                                check=True).stdout.split()
        image = next((i for i in images if "whisper.cpp" in i and "cuda" in i), None)
        if image is None:
            raise SystemExit("No whisper.cpp CUDA image: add Martlet's speech-to-text host role on this PC first.")
        model = self.spec["model"]
        listing = subprocess.run(["docker", "run", "--rm", "-v", "martlet-stt-models:/models:ro", "--entrypoint", "ls", image,
                                  "/models"], capture_output=True, text=True).stdout.split()
        if f"ggml-{model}.bin" not in listing:
            raise SystemExit(f"The martlet-stt-models volume has no ggml-{model}.bin (it has {listing}). Choose that model in "
                             "the host's speech-to-text role first.")
        subprocess.run(["docker", "rm", "-f", WHISPERCPP_CONTAINER], capture_output=True)
        subprocess.run(["docker", "run", "-d", "--rm", "--name", WHISPERCPP_CONTAINER, "--gpus", "all", "-p",
                        f"127.0.0.1:{self.port}:{self.port}", "-v", "martlet-stt-models:/models:ro", "--entrypoint",
                        "/app/build/bin/whisper-server", image, "--host", "0.0.0.0", "--port", str(self.port), "-m",
                        f"/models/ggml-{model}.bin", "-l", "auto", "-t", str(min(os.cpu_count() or 8, 8)), "--suppress-nst"],
                       check=True, capture_output=True)
        deadline = time.time() + 180
        while time.time() < deadline:
            try:
                if self.client.get(f"http://127.0.0.1:{self.port}/").status_code == 200:
                    return
            except httpx.HTTPError:
                pass
            time.sleep(0.5)
        raise SystemExit("whisper.cpp's server didn't start; see: docker logs " + WHISPERCPP_CONTAINER)

    def transcribe(self, samples: np.ndarray) -> str:
        response = self.client.post(f"http://127.0.0.1:{self.port}/inference", files={"file": ("speech.wav", wav_bytes(samples), "audio/wav")},
                                    data={"temperature": "0.0", "response_format": "json"})
        response.raise_for_status()
        return response.json().get("text", "").strip()

    def close(self) -> None:
        if self.owned:
            subprocess.run(["docker", "stop", WHISPERCPP_CONTAINER], capture_output=True)


class OpenAiCloud(Engine):
    device = "cloud"

    def load(self) -> None:
        import httpx

        key = os.environ.get("OPENAI_API_KEY")
        if not key:
            raise SystemExit("Set OPENAI_API_KEY for the OpenAI transcription engine.")
        self.client = httpx.Client(timeout=60, headers={"Authorization": f"Bearer {key}"})

    def transcribe(self, samples: np.ndarray) -> str:
        response = self.client.post("https://api.openai.com/v1/audio/transcriptions",
                                    files={"file": ("speech.wav", wav_bytes(samples), "audio/wav")},
                                    data={"model": self.spec["model"], "response_format": "json"})
        response.raise_for_status()
        return response.json().get("text", "").strip()


KINDS = {"sherpa-transducer": Sherpa, "sherpa-moonshine": Sherpa, "sherpa-qwen3": Sherpa, "faster-whisper": FasterWhisper,
         "transformers": Transformers, "whispercpp": WhisperCpp, "openai": OpenAiCloud}


def create(name: str, beam: int = 1) -> Engine:
    if name not in ENGINES:
        raise SystemExit(f"Unknown speech-to-text engine '{name}'. Known: {', '.join(ENGINES)}")
    spec = ENGINES[name]
    return KINDS[spec["kind"]](name, spec, beam)


# ---------------------------------------------------------------- benchmark


def run_engine(name: str, clips: list[Clip], beam: int) -> dict[str, Any]:
    """Loads one engine, warms it on the first clip, then times every clip. Runs in its own process (see worker_main)."""
    engine = create(name, beam)
    with GpuMemory() as gpu:
        started = now()
        engine.load()
        load = now() - started
        started = now()
        engine.transcribe(clips[0].samples)
        first = now() - started
        rows = []
        for clip in clips:
            samples = clip.samples
            started = now()
            text = engine.transcribe(samples)
            rows.append({"clip": clip.id, "seconds": round(clip.seconds, 2), "latency_ms": ms(now() - started), "text": text,
                         "reference": clip.text})
    engine.close()
    return {"engine": name, "about": ENGINES[name]["about"], "device": getattr(engine, "device", "cpu"), "load_ms": ms(load),
            "first_ms": ms(first), "gpu_added_mb": gpu.added_mb, "rows": rows}


def worker_main(args: Any) -> None:
    clips = load_clips(args.clips, args.limit)
    result = run_engine(args.engine, clips, args.beam)
    Path(args.out).write_text(json.dumps(result), encoding="utf-8")


def summarize(result: dict[str, Any]) -> dict[str, Any]:
    rows = result["rows"]
    latencies = [r["latency_ms"] for r in rows]
    per_second = [r["latency_ms"] / max(r["seconds"], 0.1) for r in rows]
    short = [r["latency_ms"] for r in rows if r["seconds"] <= 6]
    return {"engine": result["engine"], "about": result["about"], "device": result["device"], "load_s": round(result["load_ms"] / 1000, 1),
            "first_ms": result["first_ms"], "latency": stats(latencies), "short_latency": stats(short),
            "ms_per_speech_s": stats(per_second)["median"], "wer": wer([r["reference"] for r in rows], [r["text"] for r in rows]),
            "gpu_added_mb": result["gpu_added_mb"], "clips": len(rows)}


def benchmark(args: Any) -> Results:
    clips = load_clips(args.clips, args.limit)
    engines = args.engines or DEFAULT_ENGINES
    results = Results("stt", {"clips": args.clips, "count": len(clips), "speech_seconds": round(sum(c.seconds for c in clips), 1),
                              "engines": engines, "beam": args.beam, "threads": MARTLET_THREADS})
    from .think import omni_transcriber

    for name in engines:
        log(f"\n== {name}")
        if name.startswith("omni:"):
            result = omni_transcriber(name[5:], clips, args)
        elif ENGINES.get(name, {}).get("cloud") and not args.allow_cloud:
            results.notes.append(f"{name}: NOT RUN (cloud and paid; pass --allow-cloud with a key to include it)")
            continue
        else:
            with tempfile.TemporaryDirectory() as temp:
                out = Path(temp) / "result.json"
                command = [sys.executable, "-m", "voicebench", "stt-worker", "--engine", name, "--clips", args.clips, "--beam",
                           str(args.beam), "--out", str(out)] + (["--limit", str(args.limit)] if args.limit else [])
                process = subprocess.run(command, cwd=Path(__file__).resolve().parent.parent)
                if process.returncode != 0 or not out.is_file():
                    results.notes.append(f"{name}: FAILED (exit {process.returncode}; see the output above)")
                    continue
                result = json.loads(out.read_text(encoding="utf-8"))
        summary = summarize(result)
        results.rows.append(summary)
        results.details.append(result)
        log(f"   median {summary['latency']['median']} ms, WER {summary['wer']}%")
    rows = [[s["engine"], s["device"], s["load_s"], s["first_ms"], s["latency"]["median"], s["latency"]["p90"],
             s["short_latency"]["median"], s["ms_per_speech_s"], s["wer"], s["gpu_added_mb"]] for s in results.rows]
    results.markdown = (f"Clip set `{args.clips}`: {len(clips)} clips, {results.settings['speech_seconds']} s of speech. Latency is "
                        "from handing over the whole utterance to the finished text, after one warm-up call.\n\n" +
                        table(["Engine", "Device", "Load s", "1st call ms", "Median ms", "p90 ms", "Median ms (clips <= 6 s)",
                               "ms per s of speech", "WER %", "GPU MB added"], rows))
    print("\n" + results.markdown)
    for note in results.notes:
        print("- " + note)
    log(f"\nSaved {results.save()}")
    return results
