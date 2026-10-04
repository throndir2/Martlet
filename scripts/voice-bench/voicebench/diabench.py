"""Dia 1.6B (Nari Labs) on this PC, the way Martlet's dia host role speaks: Martlet's own worker code (workers/dia,
martlet_dia.worker.DiaEngine and martlet_dia.text) with the role's pinned source, weights and codec, verified by SHA-256.
Runs in its own environment (Install-VoiceBench.ps1 -Dia), never the dia host role, whose install asks for consent to
Nari Labs' terms. Only the starter voices (public domain and CMU ARCTIC) are used: Nari Labs forbids imitating real
people without their permission.

    python -m voicebench.diabench fetch
    python -m voicebench.diabench run --voice librivox-annie --repeat 3

Dia doesn't stream within a piece: a piece's first audio is the whole piece."""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
import urllib.request
from pathlib import Path

from .common import DATA, MODELS, REPO, GpuMemory, Results, log, ms, now, stats, table, write_wav

DIA = MODELS / "dia"
WORKER = REPO / "workers" / "dia"
# Chatterbox tags in the bench texts, as Dia's own cues (martlet_dia.pins.NONVERBAL_TAGS).
CUES = {"[sigh]": "(sighs)", "[laugh]": "(laughs)", "[chuckle]": "(chuckle)", "[gasp]": "(gasps)", "[cough]": "(coughs)"}


def pins():
    sys.path.insert(0, str(WORKER))
    from martlet_dia import pins as pinned

    return pinned


def _download(url: str, path: Path, size: int, sha256: str) -> None:
    if path.is_file() and path.stat().st_size == size:
        digest = hashlib.sha256()
        with path.open("rb") as handle:
            for block in iter(lambda: handle.read(1 << 22), b""):
                digest.update(block)
        if digest.hexdigest() == sha256:
            return
    path.parent.mkdir(parents=True, exist_ok=True)
    part = path.with_suffix(path.suffix + ".part")
    log(f"Downloading {url} ({size / 1e9:.2f} GB)")
    digest = hashlib.sha256()
    with urllib.request.urlopen(url, timeout=120) as response, part.open("wb") as out:
        for block in iter(lambda: response.read(1 << 22), b""):
            out.write(block)
            digest.update(block)
    if part.stat().st_size != size or digest.hexdigest() != sha256:
        part.unlink()
        raise SystemExit(f"{path.name} doesn't match its pinned size and SHA-256")
    part.replace(path)


def fetch() -> None:
    pinned = pins()
    for name, (size, sha256) in pinned.DIA_SOURCE_FILES.items():
        _download(pinned.DIA_SOURCE_URL + name, DIA / "src" / name, size, sha256)
    for role, (_id, url, _revision, size, sha256, _licence, relative) in pinned.PINNED_MODELS[pinned.DEFAULT_MODEL].items():
        _download(url, DIA / "models" / relative, size, sha256)
    log(f"Dia {pinned.DEFAULT_MODEL} (source {pinned.DIA_SOURCE_COMMIT[:7]}) is in {DIA}")


def run(voice_name: str, repeat: int) -> None:
    import numpy as np

    sys.path.insert(0, str(DIA / "src"))
    sys.path.insert(0, str(WORKER))
    from martlet_dia import audio as dia_audio
    from martlet_dia import text as dia_text
    from martlet_dia.worker import GENERATION, DiaEngine

    from .tts import voice

    pinned = pins()
    files = {role: str(DIA / "models" / spec[6]) for role, spec in pinned.PINNED_MODELS[pinned.DEFAULT_MODEL].items()}
    chosen = voice(voice_name)
    reference = chosen.audio
    # The starter voices are canonical PCM16 WAV: the worker takes raw PCM and its rate.
    rate = int.from_bytes(reference[24:28], "little")
    pcm = reference[44:]
    seconds = len(pcm) / 2 / rate
    texts = json.loads((DATA / "prompts.json").read_text(encoding="utf-8"))["tts"]
    results = Results("tts-dia", {"engine": "dia", "model": pinned.DEFAULT_MODEL, "source": pinned.DIA_SOURCE_COMMIT,
                                  "voice": chosen.name, "repeat": repeat, "sampling": GENERATION, "streaming": False})
    folder = results.folder
    with GpuMemory() as gpu:
        engine = DiaEngine(files, "cuda:0")
        started = now()
        engine.load()
        load = now() - started
        # Warm-up: the voice's prompt codes and the first CUDA kernels, as the role's warm step does.
        warm_prompts = dia_text.prompts(chosen.transcript, "Warming up.", seconds)
        started = now()
        engine.speak(pcm, rate, "bench", warm_prompts[0][0], warm_prompts[0][1])
        warm = now() - started
        for item in texts:
            spoken_text = item["text"]
            for tag, cue in CUES.items():
                spoken_text = spoken_text.replace(tag, cue)
            runs = []
            for attempt in range(repeat):
                pieces = dia_text.prompts(chosen.transcript, spoken_text, seconds)
                started = now()
                first = None
                audio = bytearray()
                for prompt, piece in pieces:
                    audio += engine.speak(pcm, rate, "bench", prompt, piece)
                    if first is None:
                        first = now() - started
                whole = now() - started
                speech = len(audio) / 2 / dia_audio.OUTPUT_RATE
                runs.append({"first_ms": ms(first), "whole_ms": ms(whole), "speech_s": round(speech, 2), "pieces": len(pieces)})
                if attempt == 0:
                    write_wav(folder / f"{item['id']}.wav", np.frombuffer(bytes(audio), dtype="<i2").astype(np.float32) / 32768.0,
                              dia_audio.OUTPUT_RATE)
                log(f"  {item['id']:9} first audio {ms(first)} ms, whole {ms(whole)} ms, {speech:.2f} s, {len(pieces)} piece(s)")
            first_stats = stats(r["first_ms"] for r in runs)
            whole_stats = stats(r["whole_ms"] for r in runs)
            speech = float(np.median([r["speech_s"] for r in runs]))
            results.rows.append({"text": item["id"], "spoken": spoken_text, "first_audio": first_stats, "done": whole_stats,
                                 "audio_s": round(speech, 2), "pieces": runs[0]["pieces"], "gpu_added_mb": None})
            results.details += [{"text": item["id"], **r} for r in runs]
    for row in results.rows:
        row["gpu_added_mb"] = gpu.added_mb
    rows = [[r["text"], r["first_audio"]["median"], r["first_audio"]["p90"], r["done"]["median"], r["audio_s"],
             round(r["done"]["median"] / 1000 / r["audio_s"], 2) if r["audio_s"] else None, r["pieces"]] for r in results.rows]
    results.markdown = (f"Dia {pinned.DEFAULT_MODEL} (Martlet's dia worker code, source {pinned.DIA_SOURCE_COMMIT[:7]}, float16, "
                        f"sampling {GENERATION}), voice `{chosen.name}`, {repeat} runs each after a warm-up; loaded in "
                        f"{load:.1f} s, warm-up {warm * 1000:.0f} ms; GPU memory added {gpu.added_mb} MB. Dia doesn't stream within "
                        f"a piece, so its first audio is the whole first piece. The audio is in {folder}.\n\n" +
                        table(["Text", "First audio ms", "p90", "Whole ms", "Speech s", "Real-time factor", "Pieces"], rows))
    print("\n" + results.markdown)
    log(f"\nSaved {results.save()}")


def main() -> None:
    parser = argparse.ArgumentParser(prog="voicebench.diabench")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("fetch")
    runner = sub.add_parser("run")
    runner.add_argument("--voice", default="librivox-annie")
    runner.add_argument("--repeat", type=int, default=3)
    args = parser.parse_args()
    if args.command == "fetch":
        fetch()
    else:
        run(args.voice, args.repeat)


if __name__ == "__main__":
    main()
