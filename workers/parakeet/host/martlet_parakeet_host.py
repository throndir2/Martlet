"""Martlet Parakeet host service: the ``parakeet`` engine of the ``stt`` host role (the bot's hearing).

It runs inside the ``stt`` role container next to the Martlet gateway, whose speech-to-text relay (Martlet.Gateway.Stt)
is its only client. It answers whisper.cpp's server contract, so the relay treats both engines alike: ``POST /inference``
with a multipart ``file`` field holding one utterance as a 16-bit PCM mono WAV returns ``{"text": "..."}``. The audio is
transcribed in memory and never written to disk.

The NVIDIA Parakeet model chosen in ``STT_MODEL`` is the same one Martlet's desktop runs on its own processor
(src/Martlet.Sherpa/ParakeetModels.cs): its sherpa-onnx export is downloaded once from Hugging Face at a pinned revision,
each file checked for its exact size and SHA-256, and runs on the CPU through sherpa-onnx's offline transducer (NeMo TDT,
greedy search, 80 features). Transcriptions are serialized; the model is loaded and warmed before the port opens.

Commands:
  serve      download the chosen model when needed, load it and listen on 127.0.0.1:$MARTLET_PARAKEET_PORT (default 8178);
             with MARTLET_PREPARE=1 it only downloads the model and exits (martlet-host's prepare step)
  provision  download and verify the chosen model, then exit

HTTP (127.0.0.1 only):
  GET  /status     {"engine", "model", "name", "languages", "revision", "state", "ready", "threads", "runtime"}
  POST /inference  multipart/form-data with "file" (16-bit PCM mono WAV, 8-48 kHz, at most 60 s) -> {"text"}
"""

from __future__ import annotations

import argparse
import hashlib
import http.server
import importlib.metadata
import io
import json
import os
import re
import sys
import threading
import time
import urllib.request
import wave
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable

ROOT = Path(os.environ.get("MARTLET_PARAKEET_ROOT", "/opt/martlet-parakeet"))
MODELS = ROOT / "models"
PORT = int(os.environ.get("MARTLET_PARAKEET_PORT", "8178"))
SAMPLE_RATE = 16_000
MAX_SECONDS = 60
MAX_BODY_BYTES = 8 * 1024 * 1024
DOWNLOAD_ATTEMPTS = 3
DOWNLOAD_TIMEOUT_SECONDS = 60
CHUNK_BYTES = 1 << 20
CC_BY = "License: Creative Commons Attribution 4.0 International (CC BY 4.0), https://creativecommons.org/licenses/by/4.0/\n"


@dataclass(frozen=True)
class ModelFile:
    name: str
    size: int
    sha256: str


@dataclass(frozen=True)
class Model:
    id: str
    name: str
    languages: str
    repository: str
    revision: str
    encoder: ModelFile
    decoder: ModelFile
    joiner: ModelFile
    tokens: ModelFile
    notice_file: str
    notice: str

    @property
    def files(self) -> tuple[ModelFile, ...]:
        return (self.encoder, self.decoder, self.joiner, self.tokens)

    def url(self, file: ModelFile) -> str:
        return f"https://huggingface.co/{self.repository}/resolve/{self.revision}/{file.name}"


# The desktop's ParakeetModels, file for file (a .NET test keeps the two the same), fastest first.
MODEL_LIST = (
    Model(
        "parakeet-tdt-110m-en", "Parakeet TDT 110M", "English",
        "csukuangfj/sherpa-onnx-nemo-parakeet_tdt_transducer_110m-en-36000", "e9bea5a06247dc3f55319ff23d34b0328f2f5ddf",
        ModelFile("encoder.onnx", 456_050_698, "db260f1073c654c37dd65006885d1ee98ff16c22463b1ef992bbcabc29780a3f"),
        ModelFile("decoder.onnx", 15_753_086, "3da156bde41a04c94ef783e0bd92928e9974e08645b976a22d0c3e1063510249"),
        ModelFile("joiner.onnx", 5_596_854, "b603765c0724a0768c378a23326dabbeb9cfea932d260e4fcc14384fa5fd5aff"),
        ModelFile("tokens.txt", 9_953, "450e56bd2f036fe5b6aa821865838cc5aa9d8b0106134ce9a9ba0664abe6cd10"),
        "Parakeet-TDT-110M-NOTICE.txt",
        "NVIDIA Parakeet TDT-CTC 110M (its TDT transducer branch), https://huggingface.co/nvidia/parakeet-tdt_ctc-110m\n" + CC_BY +
        "Exported to ONNX by the sherpa-onnx project (Apache-2.0): "
        "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet_tdt_transducer_110m-en-36000 "
        "(revision e9bea5a06247dc3f55319ff23d34b0328f2f5ddf)\n"),
    Model(
        "parakeet-tdt-0.6b-v2-int8", "Parakeet TDT 0.6B v2", "English",
        "csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8", "1ab9323565ddb038682214b292f588070a538ce2",
        ModelFile("encoder.int8.onnx", 652_184_296, "a32b12d17bbbc309d0686fbbcc2987b5e9b8333a7da83fa6b089f0a2acd651ab"),
        ModelFile("decoder.int8.onnx", 7_257_753, "b6bb64963457237b900e496ee9994b59294526439fbcc1fecf705b31a15c6b4e"),
        ModelFile("joiner.int8.onnx", 1_739_080, "7946164367946e7f9f29a122407c3252b680dbae9a51343eb2488d057c3c43d2"),
        ModelFile("tokens.txt", 9_384, "ec182b70dd42113aff6c5372c75cac58c952443eb22322f57bbd7f53977d497d"),
        "Parakeet-TDT-0.6B-v2-NOTICE.txt",
        "NVIDIA Parakeet TDT 0.6B v2, https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2\n" + CC_BY +
        "Converted to int8 ONNX by the sherpa-onnx project (Apache-2.0): "
        "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8 (revision 1ab9323565ddb038682214b292f588070a538ce2)\n"),
    Model(
        "parakeet-tdt-0.6b-v3-int8", "Parakeet TDT 0.6B v3", "25 European languages",
        "csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8", "2bda32ec70b097a55adaa07d9a7173915b43cc78",
        ModelFile("encoder.int8.onnx", 652_184_281, "acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247"),
        ModelFile("decoder.int8.onnx", 11_845_275, "179e50c43d1a9de79c8a24149a2f9bac6eb5981823f2a2ed88d655b24248db4e"),
        ModelFile("joiner.int8.onnx", 6_355_277, "3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3"),
        ModelFile("tokens.txt", 93_939, "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d"),
        "Parakeet-NOTICE.txt",
        "NVIDIA Parakeet TDT 0.6B v3, https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3\n" + CC_BY +
        "Converted to int8 ONNX by the sherpa-onnx project (Apache-2.0): "
        "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8 (revision 2bda32ec70b097a55adaa07d9a7173915b43cc78)\n"),
)
MODELS_BY_ID = {model.id: model for model in MODEL_LIST}


def say(text: str) -> None:
    print(text, flush=True)


def model_named(model_id: str) -> Model:
    model = MODELS_BY_ID.get(model_id)
    if model is None:
        raise SystemExit(f"Unknown Parakeet model: {model_id!r}. Choose one of: {', '.join(MODELS_BY_ID)}.")
    return model


def threads() -> int:
    """ONNX Runtime threads: four is about 1.4 times faster than two on a short turn (Martlet's desktop measurement)."""
    configured = os.environ.get("PARAKEET_THREADS", "")
    if configured.isdigit() and 0 < int(configured) <= 32:
        return int(configured)
    return max(1, min(4, os.cpu_count() or 1))


# ---------------------------------------------------------------------------------------------------- pinned downloads


def _verified(path: Path, file: ModelFile) -> bool:
    """The file is there with its pinned size and the SHA-256 recorded when it was downloaded and checked."""
    marker = path.with_name(path.name + ".sha256")
    try:
        return path.stat().st_size == file.size and marker.read_text(encoding="ascii").strip() == file.sha256
    except OSError:
        return False


def _download(url: str, target: Path, file: ModelFile, opener: Callable[..., Any]) -> None:
    part = target.with_name(target.name + ".part")
    digest = hashlib.sha256()
    size = 0
    try:
        with opener(urllib.request.Request(url, headers={"User-Agent": "Martlet-Parakeet-Host"}),
                    timeout=DOWNLOAD_TIMEOUT_SECONDS) as response, part.open("wb") as out:
            while True:
                block = response.read(CHUNK_BYTES)
                if not block:
                    break
                size += len(block)
                if size > file.size:
                    raise ValueError(f"{file.name} is larger than its pinned {file.size} bytes")
                digest.update(block)
                out.write(block)
        if size != file.size:
            raise ValueError(f"{file.name} has {size} bytes, not its pinned {file.size}")
        if digest.hexdigest() != file.sha256:
            raise ValueError(f"{file.name} does not match its pinned SHA-256")
        os.replace(part, target)
    finally:
        part.unlink(missing_ok=True)
    target.with_name(target.name + ".sha256").write_text(file.sha256 + "\n", encoding="ascii")


def ensure_model(model: Model, root: Path = MODELS, opener: Callable[..., Any] = urllib.request.urlopen,
                 report: Callable[[str], None] = say) -> Path:
    """Downloads the model's missing files into ``root/<id>`` (pinned revision, exact size and SHA-256) and writes its
    NOTICE beside the models. Files already checked are kept, so a re-add or restart downloads nothing."""
    folder = root / model.id
    folder.mkdir(parents=True, exist_ok=True)
    missing = [file for file in model.files if not _verified(folder / file.name, file)]
    if missing:
        total = sum(file.size for file in missing)
        report(f"Downloading {model.name} ({model.languages}, {total / 1_000_000:.0f} MB, once)...")
    for file in missing:
        for attempt in range(1, DOWNLOAD_ATTEMPTS + 1):
            try:
                _download(model.url(file), folder / file.name, file, opener)
                break
            except (OSError, ValueError) as error:
                if attempt == DOWNLOAD_ATTEMPTS:
                    raise SystemExit(f"Couldn't download {model.name}'s {file.name}: {error}") from error
                report(f"{file.name}: {error}; trying again ({attempt + 1} of {DOWNLOAD_ATTEMPTS})...")
                time.sleep(2 * attempt)
    (root / model.notice_file).write_text(model.notice, encoding="utf-8")
    return folder


# ---------------------------------------------------------------------------------------------------- requests


class BadRequest(Exception):
    """The request isn't one utterance this service can transcribe; the message says why (never the audio)."""


_BOUNDARY = re.compile(r'boundary="?([^";]+)"?', re.IGNORECASE)
_NAME = re.compile(r'(?:^|;)\s*name="?([^";]*)"?', re.IGNORECASE)


def file_field(content_type: str, body: bytes, field: str = "file") -> bytes:
    """The bytes of the multipart/form-data field ``field`` (the relay's ``file``), whatever else the form holds."""
    if not content_type.lower().startswith("multipart/form-data"):
        raise BadRequest("send multipart/form-data")
    match = _BOUNDARY.search(content_type)
    if match is None:
        raise BadRequest("the form has no boundary")
    boundary = b"--" + match.group(1).encode("latin-1")
    for part in body.split(boundary)[1:]:
        if part.startswith(b"--"):
            break
        head, separator, data = part.partition(b"\r\n\r\n")
        if not separator:
            continue
        for line in head.decode("latin-1").split("\r\n"):
            key, _, value = line.partition(":")
            if key.strip().lower() == "content-disposition":
                name = _NAME.search(value)
                if name is not None and name.group(1) == field:
                    return data[:-2] if data.endswith(b"\r\n") else data
    raise BadRequest(f"the form has no {field} field")


def read_wave(data: bytes) -> tuple[int, bytes]:
    """The sample rate and 16-bit PCM samples of a mono WAV of at most a minute."""
    try:
        with wave.open(io.BytesIO(data), "rb") as reader:
            channels, width, rate, frames = reader.getnchannels(), reader.getsampwidth(), reader.getframerate(), reader.getnframes()
            if channels != 1 or width != 2:
                raise BadRequest("send a 16-bit PCM mono WAV")
            if not 8_000 <= rate <= 48_000:
                raise BadRequest("send audio sampled at 8 to 48 kHz")
            if frames > rate * MAX_SECONDS:
                raise BadRequest(f"send at most {MAX_SECONDS} seconds at once")
            return rate, reader.readframes(frames)
    except (wave.Error, EOFError) as error:
        raise BadRequest("send a 16-bit PCM mono WAV") from error


# ---------------------------------------------------------------------------------------------------- the model


class Transcriber:
    """One Parakeet model in sherpa-onnx, loaded once; transcriptions run one at a time."""

    def __init__(self, model: Model, folder: Path, thread_count: int) -> None:
        import numpy  # noqa: PLC0415 - the tests run without the runtime
        import sherpa_onnx  # noqa: PLC0415

        self._numpy = numpy
        self._lock = threading.Lock()
        self._recognizer = sherpa_onnx.OfflineRecognizer.from_transducer(
            encoder=str(folder / model.encoder.name), decoder=str(folder / model.decoder.name),
            joiner=str(folder / model.joiner.name), tokens=str(folder / model.tokens.name),
            num_threads=thread_count, sample_rate=SAMPLE_RATE, feature_dim=80, decoding_method="greedy_search",
            model_type="nemo_transducer", provider="cpu")
        # Warm up with a few seconds of faint noise (silence takes a shorter path), so the first utterance isn't slower.
        noise = numpy.random.default_rng(0).normal(0, 0.05, SAMPLE_RATE * 3).clip(-1, 1)
        self.transcribe(SAMPLE_RATE, (noise * 32767).astype("<i2").tobytes())

    def transcribe(self, rate: int, pcm: bytes) -> str:
        samples = self._numpy.frombuffer(pcm, dtype="<i2").astype(self._numpy.float32) / 32768.0
        with self._lock:
            stream = self._recognizer.create_stream()
            stream.accept_waveform(rate, samples)
            self._recognizer.decode_stream(stream)
            return stream.result.text.strip()

    @staticmethod
    def runtime() -> dict[str, str]:
        versions = {}
        for package in ("sherpa-onnx", "sherpa-onnx-core", "numpy"):
            try:
                versions[package] = importlib.metadata.version(package)
            except importlib.metadata.PackageNotFoundError:
                pass
        return versions


class Service:
    """What the HTTP handler answers with: the loaded transcriber and its status."""

    def __init__(self, transcriber: Any, model: Model, thread_count: int, runtime: dict[str, str]) -> None:
        self.transcriber = transcriber
        self.model = model
        self.threads = thread_count
        self.runtime = runtime

    def status(self) -> dict[str, Any]:
        return {"engine": "parakeet", "model": self.model.id, "name": self.model.name, "languages": self.model.languages,
                "revision": self.model.revision, "state": "ready", "ready": True, "threads": self.threads, "runtime": self.runtime}

    def inference(self, content_type: str, body: bytes) -> dict[str, str]:
        rate, pcm = read_wave(file_field(content_type, body))
        return {"text": self.transcriber.transcribe(rate, pcm)}


class Handler(http.server.BaseHTTPRequestHandler):
    server_version = "MartletParakeet/1"
    service: Service

    def log_message(self, format: str, *args: Any) -> None:  # noqa: A002 - no request content in logs
        pass

    def _send(self, status: int, payload: dict[str, Any]) -> None:
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self) -> None:  # noqa: N802
        if self.path == "/status":
            self._send(200, self.service.status())
        else:
            self._send(404, {"error": "not found"})

    def do_POST(self) -> None:  # noqa: N802
        if self.path != "/inference":
            self._send(404, {"error": "not found"})
            return
        try:
            length = int(self.headers.get("Content-Length", ""))
        except ValueError:
            self.close_connection = True
            self._send(411, {"error": "send Content-Length"})
            return
        if not 0 < length <= MAX_BODY_BYTES:
            self.close_connection = True
            self._send(413, {"error": f"send at most {MAX_BODY_BYTES} bytes"})
            return
        body = self.rfile.read(length)
        try:
            self._send(200, self.service.inference(self.headers.get("Content-Type", ""), body))
        except BadRequest as error:
            self._send(400, {"error": str(error)})
        except Exception as error:  # noqa: BLE001 - the relay only needs to know it failed
            say(f"Transcription failed: {type(error).__name__}")
            self._send(500, {"error": "transcription failed"})


def serve(service: Service, port: int = PORT) -> http.server.ThreadingHTTPServer:
    handler = type("BoundHandler", (Handler,), {"service": service})
    return http.server.ThreadingHTTPServer(("127.0.0.1", port), handler)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Martlet Parakeet speech-to-text host service")
    parser.add_argument("command", choices=("serve", "provision"), nargs="?", default="serve")
    args = parser.parse_args(argv)
    model = model_named(os.environ.get("STT_MODEL", MODEL_LIST[0].id))
    folder = ensure_model(model)
    if args.command == "provision" or os.environ.get("MARTLET_PREPARE") == "1":
        say(f"The {model.name} model is ready.")
        return 0
    count = threads()
    say(f"Loading {model.name} ({model.languages}) on the CPU with {count} threads...")
    started = time.monotonic()
    service = Service(Transcriber(model, folder, count), model, count, Transcriber.runtime())
    server = serve(service)
    say(f"{model.name} loaded in {time.monotonic() - started:.1f} s; listening on 127.0.0.1:{PORT}.")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
