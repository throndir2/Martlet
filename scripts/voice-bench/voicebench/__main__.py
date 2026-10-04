"""voicebench: compare speech-to-text, Thinking (text, heard audio or omni), voices and the whole turn before building them
into Martlet. Run through Invoke-VoiceBench.ps1 (or the bench venv's python -m voicebench from scripts/voice-bench)."""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path

from .common import CLIPS, HOME, MARTLET_DATA, RESULTS, TOOLS, environment, log


def default_prompts() -> str:
    for name in ("prompts-mine", "prompts-voice", "prompts-sapi"):
        if (CLIPS / name).is_dir():
            return name
    return "prompts-sapi"


def cmd_env(_: argparse.Namespace) -> None:
    print(json.dumps(environment(), indent=2))
    print(f"bench home: {HOME}")
    checks = {
        "Martlet's Parakeet v3": (MARTLET_DATA / "speech" / "models" / "parakeet-tdt-0.6b-v3-int8").is_dir(),
        "llama.cpp server": bool(sorted(TOOLS.glob("llama.cpp-*/llama-server.exe"))),
        "ollama": shutil.which("ollama") is not None,
        "docker": shutil.which("docker") is not None,
    }
    for module in ("sherpa_onnx", "faster_whisper", "torch", "transformers", "jiwer", "httpx"):
        try:
            __import__(module)
            checks[f"python: {module}"] = True
        except ImportError:
            checks[f"python: {module}"] = False
    for name, ok in checks.items():
        print(f"  {'ok ' if ok else '-- '} {name}")
    if shutil.which("ollama"):
        print(subprocess.run(["ollama", "list"], capture_output=True, text=True).stdout)


def cmd_models(args: argparse.Namespace) -> None:
    from .stt import ENGINES
    from .think import GGUF, HF_MODELS
    from .common import HOME

    if args.fetch:
        fetch(args.fetch)
        return
    print("Speech-to-text engines (voicebench stt --engines ...):")
    for name, spec in ENGINES.items():
        print(f"  {name:28} {spec['size']:>8}  {spec['about']}")
    print("\nModels that hear, on llama.cpp's server (voicebench think --targets llamacpp:NAME):")
    for name, model in GGUF.items():
        present = "downloaded" if (model.folder / model.model).is_file() else ""
        print(f"  {name:28} {model.size:>8}  {model.about} {present}")
    print("\nModels that hear through PyTorch (voicebench think --targets hf:NAME; Install-VoiceBench.ps1 -HfModels NAME):")
    for name, spec in HF_MODELS.items():
        present = "installed" if (HOME / spec["venv"] / "Scripts" / "python.exe").is_file() else ""
        print(f"  {name:28} {spec['size']}  {spec['about']} {present}")
    print("\nOllama: any tag works as ollama:TAG. Gemma 4 hears through Ollama 0.35+'s input_audio (e2b, e4b, 12b).")
    print("Cloud (paid, --allow-cloud): openrouter:MODEL (OPENROUTER_API_KEY), openai:MODEL (OPENAI_API_KEY).")


def fetch(names: list[str]) -> None:
    from .stt import ENGINES, _download_sherpa
    from .think import GGUF, ensure_gguf

    for name in names:
        if name in GGUF:
            ensure_gguf(GGUF[name])
        elif name in ENGINES and ENGINES[name]["kind"].startswith("sherpa"):
            _download_sherpa(ENGINES[name]["archive"])
        elif name in ENGINES and ENGINES[name]["kind"] == "faster-whisper":
            from faster_whisper.utils import download_model

            from .common import MODELS

            download_model(ENGINES[name]["model"], cache_dir=str(MODELS / "faster-whisper"))
        elif name in ENGINES and ENGINES[name]["kind"] == "transformers":
            from huggingface_hub import snapshot_download

            snapshot_download(ENGINES[name]["model"], allow_patterns=["*.json", "*.safetensors", "*.txt", "*.model"])
        elif name.startswith("ollama:"):
            subprocess.run(["ollama", "pull", name[7:]], check=True)
        else:
            raise SystemExit(f"Don't know how to fetch '{name}'")
        log(f"fetched {name}")


def cmd_clips(args: argparse.Namespace) -> None:
    from . import clips

    if args.action == "fetch":
        clips.fetch_librispeech()
    elif args.action == "synth":
        if args.engine == "sapi":
            clips.synth_sapi()
        else:
            clips.synth_voice(args.tts_url)
    elif args.action == "record":
        clips.record()
    elif args.action == "import":
        if not args.source or not args.name:
            raise SystemExit("clips import SOURCE_FOLDER --name SET")
        clips.import_folder(Path(args.source), args.name)
    clips.list_sets()


def cmd_chatterbox(args: argparse.Namespace) -> None:
    from . import tts

    if args.action == "start":
        tts.chatterbox_start(args.port, stream=not args.no_stream, graph=not args.no_graph)
    elif args.action == "stop":
        tts.chatterbox_stop()
    else:
        print(json.dumps(tts.MartletWorker(f"http://127.0.0.1:{args.port}").status(), indent=2))


def cmd_results(_: argparse.Namespace) -> None:
    for path in sorted(RESULTS.glob("*.md"))[-20:]:
        print(path)


def add_tts_options(parser: argparse.ArgumentParser, default_engine: str) -> None:
    from .tts import CHATTERBOX_PORT

    parser.add_argument("--tts-engine", default=default_engine, choices=["martlet-worker", "openai-speech", "none"],
                        help="martlet-worker: Chatterbox/F5/Dia worker protocol; openai-speech: /v1/audio/speech; none: assume")
    parser.add_argument("--tts-url", default=f"http://127.0.0.1:{CHATTERBOX_PORT}")
    parser.add_argument("--tts-model", default="tts-1", help="openai-speech model")
    parser.add_argument("--tts-voice-name", default="alloy", help="openai-speech voice")
    parser.add_argument("--voice", default="librivox-annie", help="reference voice: a starter voice name or a WAV over 5 s")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="voicebench", description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("env", help="what this PC has for the benchmarks").set_defaults(func=cmd_env)

    models = sub.add_parser("models", help="list the engines and models, or --fetch them ahead of a run")
    models.add_argument("--fetch", nargs="+", metavar="NAME")
    models.set_defaults(func=cmd_models)

    clips = sub.add_parser("clips", help="make clip sets: fetch (LibriSpeech), synth (prompts), record (your voice), import, list")
    clips.add_argument("action", choices=["fetch", "synth", "record", "import", "list"])
    clips.add_argument("source", nargs="?")
    clips.add_argument("--name")
    clips.add_argument("--engine", default="sapi", choices=["sapi", "voice"], help="synth: Windows voices or a Martlet voice worker")
    add_tts_options(clips, "martlet-worker")
    clips.set_defaults(func=cmd_clips)

    stt = sub.add_parser("stt", help="speech-to-text speed and accuracy (each engine in its own process)")
    stt.add_argument("--engines", nargs="+", help="engine names (voicebench models) or omni:TARGET; default: the main comparison")
    stt.add_argument("--clips", default="librispeech")
    stt.add_argument("--limit", type=int)
    stt.add_argument("--beam", type=int, default=1, help="Whisper beam size (1 = greedy, like Martlet's Parakeet)")
    stt.add_argument("--allow-cloud", action="store_true")
    stt.add_argument("--pull", action="store_true", help="let Ollama download a missing model")
    stt.add_argument("--bits", type=int, default=4, choices=[4, 8, 16], help="omni:hf: targets' weight precision")
    stt.set_defaults(func=lambda a: __import__("voicebench.stt", fromlist=["benchmark"]).benchmark(a))

    worker = sub.add_parser("stt-worker", help=argparse.SUPPRESS)
    worker.add_argument("--engine", required=True)
    worker.add_argument("--clips", required=True)
    worker.add_argument("--limit", type=int)
    worker.add_argument("--beam", type=int, default=1)
    worker.add_argument("--out", required=True)
    worker.set_defaults(func=lambda a: __import__("voicebench.stt", fromlist=["worker_main"]).worker_main(a))

    think = sub.add_parser("think", help="Thinking models: time to the first word and first speakable piece, text vs audio")
    think.add_argument("--targets", nargs="+", required=True, help="ollama:TAG, llamacpp:NAME, openrouter:MODEL, openai:MODEL, URL#MODEL")
    think.add_argument("--clips", default=None)
    think.add_argument("--limit", type=int)
    think.add_argument("--modes", nargs="+", default=["text,audio,audio+text"],
                       help="text, audio, audio+text (comma- or space-separated)")
    think.add_argument("--repeat", type=int, default=1)
    think.add_argument("--max-tokens", type=int, default=200)
    think.add_argument("--context", type=int, default=8192, help="llama.cpp context size")
    think.add_argument("--pull", action="store_true", help="let Ollama download a missing model")
    think.add_argument("--bits", type=int, default=4, choices=[4, 8, 16], help="hf: targets' weight precision")
    think.add_argument("--allow-cloud", action="store_true")
    think.set_defaults(func=lambda a: __import__("voicebench.think", fromlist=["benchmark"]).benchmark(a))

    tts = sub.add_parser("tts", help="voices: time to first audio and real-time factor, saving the audio")
    add_tts_options(tts, "martlet-worker")
    tts.add_argument("--repeat", type=int, default=3)
    tts.set_defaults(func=lambda a: __import__("voicebench.tts", fromlist=["benchmark"]).benchmark(a))

    pipeline = sub.add_parser("pipeline", help="the whole turn: end of speech to the voice's first audio, cascade vs hearing vs omni")
    pipeline.add_argument("--think", required=True)
    pipeline.add_argument("--stt", default="parakeet-v3")
    pipeline.add_argument("--modes", nargs="+", default=["cascade,hearing,omni"],
                          help="cascade, hearing, omni (comma- or space-separated)")
    pipeline.add_argument("--clips", default=None)
    pipeline.add_argument("--limit", type=int)
    pipeline.add_argument("--repeat", type=int, default=1)
    pipeline.add_argument("--beam", type=int, default=1)
    pipeline.add_argument("--max-tokens", type=int, default=200)
    pipeline.add_argument("--context", type=int, default=8192)
    pipeline.add_argument("--endpoint-ms", type=int, default=800, help="the end-of-speech pause Martlet waits (Companion › Listening)")
    pipeline.add_argument("--tts-assumed-ms", type=float, default=380.0,
                          help="with --tts-engine none: the voice's first audio (Chatterbox Turbo streaming measured 0.35-0.4 s)")
    pipeline.add_argument("--pull", action="store_true")
    pipeline.add_argument("--bits", type=int, default=4, choices=[4, 8, 16], help="hf: targets' weight precision")
    pipeline.add_argument("--allow-cloud", action="store_true")
    add_tts_options(pipeline, "martlet-worker")
    pipeline.set_defaults(func=lambda a: __import__("voicebench.pipeline", fromlist=["benchmark"]).benchmark(a))

    chatterbox = sub.add_parser("chatterbox", help="run Martlet's Chatterbox Turbo service from this checkout on this PC")
    chatterbox.add_argument("action", choices=["start", "stop", "status"])
    chatterbox.add_argument("--port", type=int, default=50093)
    chatterbox.add_argument("--no-stream", action="store_true", help="whole pieces, as before streaming")
    chatterbox.add_argument("--no-graph", action="store_true", help="the library's own decoding instead of the CUDA graph")
    chatterbox.set_defaults(func=cmd_chatterbox)

    sub.add_parser("results", help="the newest result files").set_defaults(func=cmd_results)

    relay = sub.add_parser("relay", help="reach the Martlet host roles running on this PC (Chatterbox, whisper.cpp...) from the bench")
    relay.add_argument("action", choices=["start", "stop"])
    relay.set_defaults(func=lambda a: __import__("voicebench.tts", fromlist=["relay_start"]).relay_start() if a.action == "start"
                       else __import__("voicebench.tts", fromlist=["relay_stop"]).relay_stop())

    args = parser.parse_args(argv)
    if isinstance(getattr(args, "modes", None), list):
        args.modes = ",".join(args.modes)
    if getattr(args, "clips", None) is None and args.command in {"think", "pipeline"}:
        args.clips = default_prompts()
    args.func(args)
    return 0


if __name__ == "__main__":
    sys.exit(main())
