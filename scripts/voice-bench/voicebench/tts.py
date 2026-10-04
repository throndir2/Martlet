"""Voices: time to the first audio of a cloned voice. Drives Martlet's own voice workers (Chatterbox Turbo, F5, Dia: the
NDJSON worker protocol) or any OpenAI-compatible /audio/speech server, and saves what they said for listening."""

from __future__ import annotations

import base64
import hashlib
import json
import subprocess
import time
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any

import numpy as np

from .common import BENCH, DATA, REPO, Results, log, ms, now, stats, table, to_16k, write_wav

STARTER_VOICES = REPO / "src" / "Martlet.F5" / "BundledVoices"
# The starter voices' words (src/Martlet.F5/BundledVoices/NOTICES.txt).
STARTER_TRANSCRIPTS = {
    "librivox-annie": "But am I talking too much? People are always telling me I do. Would you rather I didn't talk? If you say so, I'll stop.",
    "librivox-annie-anime": "But am I talking too much? People are always telling me I do. Would you rather I didn't talk? If you say so, "
                            "I'll stop.",
    "librivox-woollybee-anime": "You do get so attached to things like that, don't you? Is there a brook anywhere near Green Gables? I "
                                "forgot to ask Mrs. Spencer that.",
    "librivox-woollybee": "It isn't heavy. I've got all my worldly goods in it, but it isn't heavy. And if it isn't carried in just a "
                          "certain way the handle pulls out, so I'd better keep it because I know the exact knack of it.",
    "lj-speech": "Printing, then, for our purpose, may be considered as the art of making books by means of movable types.",
    "arctic-slt": "I came for information more out of curiosity than anything else. The ship should be in within a week or ten days.",
    "arctic-bdl": "I came for information more out of curiosity than anything else. The ship should be in within a week or ten days.",
}
CHATTERBOX_CONTAINER = "martlet-bench-chatterbox"
CHATTERBOX_PORT = 50093


@dataclass(frozen=True)
class Voice:
    name: str
    audio: bytes
    transcript: str

    @property
    def sha256(self) -> str:
        return hashlib.sha256(self.audio).hexdigest()


def starter_voices() -> list[Voice]:
    return [Voice(name, (STARTER_VOICES / f"{name}.wav").read_bytes(), text) for name, text in STARTER_TRANSCRIPTS.items()
            if (STARTER_VOICES / f"{name}.wav").is_file()]


def voice(name_or_path: str, transcript: str | None = None) -> Voice:
    path = Path(name_or_path)
    if path.is_file():
        return Voice(path.stem, path.read_bytes(), transcript or "")
    for candidate in starter_voices():
        if candidate.name == name_or_path:
            return candidate
    raise SystemExit(f"No voice '{name_or_path}': give a WAV path (over 5 s) or one of {', '.join(STARTER_TRANSCRIPTS)}")


@dataclass
class Speech:
    first_audio_ms: float | None = None
    done_ms: float | None = None
    rate: int = 24_000
    pcm: bytearray = field(default_factory=bytearray)
    error: str | None = None

    @property
    def audio_seconds(self) -> float:
        return len(self.pcm) / 2 / self.rate

    def samples(self) -> np.ndarray:
        return np.frombuffer(bytes(self.pcm), dtype="<i2").astype(np.float32) / 32768.0

    def samples_16k(self) -> np.ndarray:
        return to_16k(self.samples(), self.rate)


class MartletWorker:
    """Martlet's voice worker protocol (martlet.f5.worker v1: POST /synthesize, NDJSON events with base64 PCM frames), as the
    gateway relay speaks it to Chatterbox Turbo, F5 and Dia on a host."""

    def __init__(self, url: str = f"http://127.0.0.1:{CHATTERBOX_PORT}") -> None:
        import httpx

        self.url = url.rstrip("/")
        self.http = httpx.Client(timeout=httpx.Timeout(120, connect=10))

    def status(self) -> dict[str, Any]:
        return self.http.get(f"{self.url}/status").json()

    def warm(self) -> dict[str, Any]:
        response = self.http.post(f"{self.url}/warmup", json={}, timeout=900)
        return response.json()

    def synthesize(self, text: str, voice: Voice, deadline_s: float = 85) -> Speech:
        session = str(uuid.uuid4())
        # The canonical reference revisions (the f5 contract checks them; Chatterbox and Dia accept them too): the transcript's
        # SHA-256, then the SHA-256 of the audio's and the transcript's digests, and a preset UUID derived from the voice.
        transcript_revision = hashlib.sha256(voice.transcript.encode("utf-8")).hexdigest()
        reference_revision = hashlib.sha256(bytes.fromhex(voice.sha256) + bytes.fromhex(transcript_revision)).hexdigest()
        body = {
            "ids": {"session_id": session, "turn_id": str(uuid.uuid4()), "request_id": str(uuid.uuid4()), "parent_request_id": None},
            "deadline_utc": (datetime.now(timezone.utc) + timedelta(seconds=deadline_s)).strftime("%Y-%m-%dT%H:%M:%S.%fZ"),
            "reference": {"preset_id": str(uuid.uuid5(uuid.NAMESPACE_URL, f"martlet-bench:{voice.name}")),
                          "reference_revision": reference_revision, "audio_sha256": voice.sha256,
                          "transcript": voice.transcript, "transcript_revision": transcript_revision,
                          "audio_base64": base64.b64encode(voice.audio).decode("ascii")},
            "chunks": [{"index": 0, "chunk_id": "c0", "text": text}],
        }
        speech = Speech()
        started = now()
        with self.http.stream("POST", f"{self.url}/synthesize", json=body) as response:
            if response.status_code != 200:
                speech.error = f"HTTP {response.status_code}: {response.read().decode('utf-8', 'replace')[:300]}"
                return speech
            for line in response.iter_lines():
                if not line.strip():
                    continue
                event = json.loads(line)
                kind = event.get("kind")
                if kind == "audio_frame":
                    frame = event["frame"]
                    if speech.first_audio_ms is None:
                        speech.first_audio_ms = ms(now() - started)
                        speech.rate = frame["format"]["sample_rate"]
                    speech.pcm += base64.b64decode(frame["data_base64"])
                elif kind == "failed":
                    speech.error = json.dumps(event.get("error"))[:300]
                    break
                elif kind in {"completed", "canceled"}:
                    break
        speech.done_ms = ms(now() - started)
        if speech.first_audio_ms is None and not speech.error:
            speech.error = "no audio: the service ended the stream without any (a rejected request)"
        return speech


class OpenAiSpeech:
    """Any OpenAI-compatible /v1/audio/speech server that streams raw 24 kHz PCM (response_format pcm)."""

    def __init__(self, base_url: str, model: str, voice_name: str, key: str | None = None) -> None:
        import httpx

        self.base_url, self.model, self.voice_name = base_url.rstrip("/"), model, voice_name
        self.http = httpx.Client(timeout=120, headers={"Authorization": f"Bearer {key}"} if key else {})

    def synthesize(self, text: str, _voice: Voice | None = None) -> Speech:
        speech = Speech()
        started = now()
        with self.http.stream("POST", f"{self.base_url}/audio/speech", json={"model": self.model, "voice": self.voice_name,
                                                                              "input": text, "response_format": "pcm"}) as response:
            if response.status_code != 200:
                speech.error = f"HTTP {response.status_code}: {response.read().decode('utf-8', 'replace')[:300]}"
                return speech
            for chunk in response.iter_bytes():
                if chunk and speech.first_audio_ms is None:
                    speech.first_audio_ms = ms(now() - started)
                speech.pcm += chunk
        speech.done_ms = ms(now() - started)
        return speech


def make_engine(args: Any) -> MartletWorker | OpenAiSpeech:
    if args.tts_engine == "openai-speech":
        import os

        return OpenAiSpeech(args.tts_url, args.tts_model, args.tts_voice_name, os.environ.get("VOICEBENCH_TTS_KEY"))
    return MartletWorker(args.tts_url)


# ---------------------------------------------------------------- Chatterbox Turbo on this PC


def chatterbox_image() -> str:
    images = subprocess.run(["docker", "images", "--format", "{{.Repository}}:{{.Tag}}"], capture_output=True, text=True,
                            check=True).stdout.split()
    found = sorted((i for i in images if i.startswith("martlet-chatterbox:")), key=lambda i: int(i.split(":")[1]) if
                   i.split(":")[1].isdigit() else 0)
    if not found:
        raise SystemExit("No martlet-chatterbox image here: add the Chatterbox voice role on this PC once (Devices › this PC), "
                         "or build it: docker build -f workers/chatterbox/host/Dockerfile -t martlet-chatterbox:bench workers/chatterbox")
    return found[-1]


def chatterbox_start(port: int = CHATTERBOX_PORT, stream: bool = True, graph: bool = True) -> None:
    """Runs Martlet's Chatterbox Turbo service from this checkout (workers/chatterbox, mounted over the role image, which
    has the same pinned packages) on the role's model volume, published on 127.0.0.1:PORT through a small forwarder."""
    image = chatterbox_image()
    subprocess.run(["docker", "rm", "-f", CHATTERBOX_CONTAINER], capture_output=True)
    host_py = REPO / "workers" / "chatterbox" / "martlet_chatterbox_host.py"
    forward = BENCH / "voicebench" / "tcp_forward.py"
    command = ["docker", "run", "-d", "--name", CHATTERBOX_CONTAINER, "--gpus", "all", "-p", f"127.0.0.1:{port}:{port}",
               "-v", "martlet-chatterbox-models:/opt/martlet-chatterbox/models",
               "--mount", f"type=bind,source={host_py},target=/opt/martlet-chatterbox/host/martlet_chatterbox_host.py,readonly",
               "--mount", f"type=bind,source={forward},target=/opt/bench/tcp_forward.py,readonly",
               "-e", f"MARTLET_CHATTERBOX_STREAM={'1' if stream else '0'}", "-e", f"MARTLET_CHATTERBOX_FAST={'1' if graph else '0'}",
               "--entrypoint", "/bin/sh", image, "-c",
               f"python3.12 /opt/bench/tcp_forward.py 0.0.0.0:{port}=127.0.0.1:50083 & exec python3.12 /opt/martlet-chatterbox/host/martlet_chatterbox_host.py serve"]
    subprocess.run(command, check=True, capture_output=True)
    log(f"Started {CHATTERBOX_CONTAINER} from {image}; loading the model (a few minutes the first time)...")
    worker = MartletWorker(f"http://127.0.0.1:{port}")
    deadline = time.time() + 120
    while True:
        try:
            worker.status()
            break
        except Exception:
            if time.time() > deadline:
                raise SystemExit(f"The Chatterbox service didn't answer; see: docker logs {CHATTERBOX_CONTAINER}")
            time.sleep(1)
    started = now()
    status = worker.warm()
    if not status.get("ready"):
        raise SystemExit(f"Chatterbox isn't ready: {status}")
    log(f"Chatterbox Turbo ready in {now() - started:.1f} s on http://127.0.0.1:{port}")


def chatterbox_stop() -> None:
    subprocess.run(["docker", "rm", "-f", CHATTERBOX_CONTAINER], capture_output=True)
    log(f"Removed {CHATTERBOX_CONTAINER}")


# ---------------------------------------------------------------- the host roles already running on this PC

RELAY_CONTAINER = "martlet-bench-relay"
# Role services on the host's shared loopback, and the Windows loopback port the relay gives each.
ROLE_PORTS = {"chatterbox": (50083, CHATTERBOX_PORT), "stt": (8178, 8188), "f5": (50081, 50091), "dia": (50085, 50095)}


def _docker(*args: str, check: bool = True) -> str:
    return subprocess.run(["docker", *args], capture_output=True, text=True, check=check).stdout.strip()


def relay_start() -> dict[str, int]:
    """Exposes the Martlet host roles running on this PC (they listen only on martlet-host-net's loopback) on this PC's
    loopback, so the bench measures the installed services without loading a second copy of a model. Two hops: a forwarder
    inside the namespace on its bridge address, then a container publishing 127.0.0.1 ports to it."""
    owner = "martlet-host-net"
    if _docker("ps", "-q", "--filter", f"name=^{owner}$") == "":
        raise SystemExit(f"{owner} isn't running: no Martlet host roles on this PC.")
    address = _docker("inspect", owner, "--format", "{{range .NetworkSettings.Networks}}{{.IPAddress}} {{end}}").split()[0]
    running = {name: ports for name, ports in ROLE_PORTS.items()
               if _docker("ps", "-q", "--filter", f"name=^martlet-{name}-{name}-1$")}
    if not running:
        raise SystemExit("No voice or speech-to-text role is running on this PC.")
    script = (BENCH / "voicebench" / "tcp_forward.py").read_text(encoding="utf-8")
    # Any role container shares the namespace; pick one with Python (the voice roles have it).
    inside = next((f"martlet-{n}-{n}-1" for n in running if n != "stt"), None)
    if inside is None:
        raise SystemExit("The relay needs a running voice role (Python) in the namespace.")
    pairs = [f"{address}:{inner + 10_000}=127.0.0.1:{inner}" for inner, _ in running.values()]
    _kill_forwarders(inside)
    subprocess.run(["docker", "exec", "-d", inside, "python3.12", "-c", f"# martlet-bench-forward\n{script}", *pairs], check=True)
    image = _docker("inspect", inside, "--format", "{{.Config.Image}}")
    subprocess.run(["docker", "rm", "-f", RELAY_CONTAINER], capture_output=True)
    publish = [arg for _, outer in running.values() for arg in ("-p", f"127.0.0.1:{outer}:{outer}")]
    outer_pairs = [f"0.0.0.0:{outer}={address}:{inner + 10_000}" for inner, outer in running.values()]
    subprocess.run(["docker", "run", "-d", "--rm", "--name", RELAY_CONTAINER, *publish, "--entrypoint", "python3.12", image, "-c",
                    script, *outer_pairs], check=True, capture_output=True)
    ports = {name: outer for name, (_, outer) in running.items()}
    for name, outer in ports.items():
        log(f"  {name}: http://127.0.0.1:{outer}")
    return ports


def _kill_forwarders(container: str) -> None:
    # The slim images have no pkill: find the forwarder by its marker in /proc.
    killer = ("import os,signal\nfor p in os.listdir('/proc'):\n if p.isdigit():\n  try:\n"
              "   c=open(f'/proc/{p}/cmdline','rb').read()\n  except OSError:\n   continue\n"
              "  if b'martlet-bench-forward' in c and int(p)!=os.getpid(): os.kill(int(p),signal.SIGTERM)")
    subprocess.run(["docker", "exec", container, "python3.12", "-c", killer], capture_output=True)


def relay_stop() -> None:
    for name in ROLE_PORTS:
        if name != "stt":
            _kill_forwarders(f"martlet-{name}-{name}-1")
    subprocess.run(["docker", "rm", "-f", RELAY_CONTAINER], capture_output=True)
    log("Removed the relay")


# ---------------------------------------------------------------- benchmark


def benchmark(args: Any) -> Results:
    texts = json.loads((DATA / "prompts.json").read_text(encoding="utf-8"))["tts"]
    chosen = voice(args.voice)
    engine = make_engine(args)
    results = Results("tts", {"engine": args.tts_engine, "url": args.tts_url, "voice": chosen.name, "repeat": args.repeat})
    folder = results.folder
    engine.synthesize("Warming up.", chosen)
    for item in texts:
        runs = []
        for attempt in range(args.repeat):
            speech = engine.synthesize(item["text"], chosen)
            runs.append(speech)
            if attempt == 0 and not speech.error:
                write_wav(folder / f"{item['id']}.wav", speech.samples(), speech.rate)
            log(f"  {item['id']:9} first audio {speech.first_audio_ms} ms, whole {speech.done_ms} ms, {speech.audio_seconds:.2f} s"
                + (f"  ERROR {speech.error}" if speech.error else ""))
        good = [s for s in runs if not s.error]
        results.rows.append({"text": item["id"], "first_audio": stats(s.first_audio_ms for s in good),
                             "done": stats(s.done_ms for s in good), "audio_s": round(float(np.median([s.audio_seconds for s in good])), 2)
                             if good else None, "errors": len(runs) - len(good)})
    rows = [[r["text"], r["first_audio"]["median"], r["first_audio"]["p90"], r["done"]["median"], r["audio_s"],
             round(r["done"]["median"] / 1000 / r["audio_s"], 2) if r["audio_s"] and r["done"]["median"] else None, r["errors"]]
            for r in results.rows]
    results.markdown = (f"{args.tts_engine} at {args.tts_url}, voice `{chosen.name}`, {args.repeat} runs each after a warm-up. "
                        f"The audio is in {folder}.\n\n" +
                        table(["Text", "First audio ms", "p90", "Whole piece ms", "Speech s", "Real-time factor", "Errors"], rows))
    print("\n" + results.markdown)
    log(f"\nSaved {results.save()}")
    return results
