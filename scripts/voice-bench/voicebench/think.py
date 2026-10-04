"""Thinking models over OpenAI-compatible Chat Completions: the transcript as text, the recording itself (an omni model that
hears), or both (Martlet's 'Let Thinking hear my voice'). Times the first word and the first piece the voice can say."""

from __future__ import annotations

import base64
import json
import os
import subprocess
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable

from .common import DATA, HOME, MODELS, RESULTS, TOOLS, Clip, GpuMemory, Results, load_clips, log, ms, now, stats, table

OLLAMA = "http://127.0.0.1:11434"
OPENROUTER = "https://openrouter.ai/api/v1"

# Martlet's note when the recording goes with the transcript (PromptSettings.HeardVoice), and the omni equivalent without one.
HEARD_WITH_TRANSCRIPT = (
    "The user's message was spoken. Their recording is attached along with an automatic transcript, which can contain "
    "mistakes: listen to the recording for exactly what was said and how it was said (tone, emotion, emphasis, laughter, "
    "hesitation), and trust it over the transcript. Answer in text as usual, without mentioning the recording or transcript.")
HEARD_ONLY = (
    "The user's message was spoken and their recording is attached, with no transcript: listen to it for exactly what was "
    "said and how it was said (tone, emotion, emphasis, laughter, hesitation). Answer in text as usual, without mentioning "
    "the recording.")
TRANSCRIBE = "Transcribe this recording exactly, word for word. Reply with only the words that were spoken."

MODES = ("text", "audio", "audio+text")


@dataclass(frozen=True)
class GgufModel:
    repo: str
    model: str
    mmproj: str
    about: str
    size: str
    args: tuple[str, ...] = ()

    @property
    def folder(self) -> Path:
        return MODELS / "gguf" / self.repo.split("/")[-1]


# Omni and audio-input models llama.cpp's server runs with their audio encoders (mmproj). Sizes are model + encoder.
GGUF: dict[str, GgufModel] = {
    "qwen2.5-omni-3b": GgufModel("ggml-org/Qwen2.5-Omni-3B-GGUF", "Qwen2.5-Omni-3B-Q4_K_M.gguf", "mmproj-Qwen2.5-Omni-3B-Q8_0.gguf",
                                 "Qwen2.5-Omni 3B Q4_K_M: text, image, audio in", "3.4 GB"),
    "qwen2.5-omni-7b": GgufModel("ggml-org/Qwen2.5-Omni-7B-GGUF", "Qwen2.5-Omni-7B-Q4_K_M.gguf", "mmproj-Qwen2.5-Omni-7B-Q8_0.gguf",
                                 "Qwen2.5-Omni 7B Q4_K_M: text, image, audio in", "5.8 GB"),
    "qwen3-omni-30b-a3b": GgufModel("ggml-org/Qwen3-Omni-30B-A3B-Instruct-GGUF", "Qwen3-Omni-30B-A3B-Instruct-Q4_K_M.gguf",
                                    "mmproj-Qwen3-Omni-30B-A3B-Instruct-Q8_0.gguf",
                                    "Qwen3-Omni 30B-A3B Instruct Q4_K_M (MoE, 3B active): text, image, audio in; experts that "
                                    "don't fit stay in system memory", "18.5 GB"),
    "voxtral-mini-3b": GgufModel("ggml-org/Voxtral-Mini-3B-2507-GGUF", "Voxtral-Mini-3B-2507-Q4_K_M.gguf",
                                 "mmproj-Voxtral-Mini-3B-2507-Q8_0.gguf", "Mistral Voxtral Mini 3B Q4_K_M: text and audio in", "3.0 GB"),
    "gemma-4-e4b": GgufModel("ggml-org/gemma-4-E4B-it-GGUF", "gemma-4-E4B-it-Q4_0.gguf", "mmproj-gemma-4-E4B-it-Q8_0.gguf",
                             "Gemma 4 E4B Q4_0: text, image, audio in", "4.8 GB"),
    "gemma-4-e2b": GgufModel("ggml-org/gemma-4-E2B-it-GGUF", "gemma-4-E2B-it-Q8_0.gguf", "mmproj-gemma-4-E2B-it-Q8_0.gguf",
                             "Gemma 4 E2B Q8_0: text, image, audio in", "5.2 GB"),
    # llama.cpp runs it but warns that quantized Qwen2-Audio answers poorly (ggml-org/llama.cpp#13760); measured anyway.
    "qwen2-audio-7b": GgufModel("mradermacher/Qwen2-Audio-7B-Instruct-GGUF", "Qwen2-Audio-7B-Instruct.Q4_K_M.gguf",
                                "Qwen2-Audio-7B-Instruct.mmproj-Q8_0.gguf",
                                "Qwen2-Audio 7B Instruct Q4_K_M: text and audio in (llama.cpp warns quantized quality is poor)",
                                "5.1 GB"),
}

# Models that hear but that llama.cpp can't give audio (their GGUFs are text or vision only): PyTorch through
# voicebench.hfserve, each in its own environment because each pins its own transformers.
HF_MODELS: dict[str, dict[str, str]] = {
    "phi-4-multimodal": {"repo": "microsoft/Phi-4-multimodal-instruct", "venv": "venv-phi4mm", "size": "11.2 GB bf16, about 4 GB 4-bit",
                         "about": "Microsoft Phi-4-multimodal 5.6B: text, image, audio in (MIT); 4-bit on the GPU"},
    "minicpm-o-2.6": {"repo": "openbmb/MiniCPM-o-2_6", "venv": "venv-minicpmo", "size": "17 GB bf16, about 6 GB 4-bit",
                      "about": "OpenBMB MiniCPM-o 2.6 8B: text, image, audio in (and its own speech out, unused); 4-bit on the GPU"},
}


@dataclass
class Target:
    spec: str
    base_url: str
    model: str
    reasoning: str  # effort | openrouter | template | none
    kind: str  # ollama | llamacpp | cloud | url
    key: str | None = None
    gguf: GgufModel | None = None

    @property
    def label(self) -> str:
        return self.spec


def parse_target(spec: str, llama_port: int = 8090) -> Target:
    """ollama:MODEL, llamacpp:NAME (see GGUF), hf:NAME (see HF_MODELS), openrouter:MODEL, openai:MODEL or URL#MODEL for any
    Chat Completions server."""
    kind, _, rest = spec.partition(":")
    if kind == "ollama":
        return Target(spec, f"{OLLAMA}/v1", rest, "effort", "ollama")
    if kind == "llamacpp":
        if rest not in GGUF:
            raise SystemExit(f"Unknown llama.cpp model '{rest}'. Known: {', '.join(GGUF)}")
        return Target(spec, f"http://127.0.0.1:{llama_port}/v1", rest, "template", "llamacpp", gguf=GGUF[rest])
    if kind == "hf":
        if rest not in HF_MODELS:
            raise SystemExit(f"Unknown PyTorch model '{rest}'. Known: {', '.join(HF_MODELS)}")
        return Target(spec, f"http://127.0.0.1:{llama_port + 1}/v1", rest, "none", "hf")
    if kind == "openrouter":
        return Target(spec, OPENROUTER, rest, "openrouter", "cloud", key=os.environ.get("OPENROUTER_API_KEY"))
    if kind == "openai":
        return Target(spec, "https://api.openai.com/v1", rest, "effort", "cloud", key=os.environ.get("OPENAI_API_KEY"))
    if spec.startswith("http") and "#" in spec:
        url, _, model = spec.partition("#")
        return Target(spec, url.rstrip("/"), model, "template", "url", key=os.environ.get("VOICEBENCH_API_KEY"))
    raise SystemExit(f"Can't read the Thinking target '{spec}' (ollama:MODEL, llamacpp:NAME, openrouter:MODEL, openai:MODEL or URL#MODEL)")


# ---------------------------------------------------------------- local servers


def llama_server_exe() -> Path:
    if os.environ.get("LLAMA_SERVER"):
        return Path(os.environ["LLAMA_SERVER"])
    found = sorted(TOOLS.glob("llama.cpp-*/llama-server.exe"))
    if not found:
        raise SystemExit("llama.cpp isn't installed for the bench: run Install-VoiceBench.ps1 -LlamaCpp, or set LLAMA_SERVER.")
    return found[-1]


def ensure_gguf(model: GgufModel) -> tuple[Path, Path]:
    from huggingface_hub import hf_hub_download

    paths = []
    for name in (model.model, model.mmproj):
        path = model.folder / name
        if not path.is_file():
            log(f"Downloading {model.repo}/{name}")
            hf_hub_download(model.repo, name, local_dir=str(model.folder))
        paths.append(path)
    return paths[0], paths[1]


class LocalServer:
    """Starts what a target needs on this PC and frees the graphics card afterwards: llama.cpp's server for a GGUF model, or
    Ollama with the model pulled and every other model unloaded."""

    def __init__(self, target: Target, pull: bool = False, context: int = 8192, bits: int = 4) -> None:
        self.target, self.pull, self.context, self.bits = target, pull, context, bits
        self.process: subprocess.Popen[bytes] | None = None
        self.load_ms: float | None = None

    def __enter__(self) -> "LocalServer":
        import httpx

        if self.target.kind == "hf":
            spec = HF_MODELS[self.target.model]
            python = HOME / spec["venv"] / "Scripts" / "python.exe"
            if not python.is_file():
                raise SystemExit(f"{self.target.model} isn't installed: run Install-VoiceBench.ps1 -HfModels {self.target.model}")
            unload_ollama()
            port = self.target.base_url.split(":")[-1].split("/")[0]
            log_dir = RESULTS / "logs"
            log_dir.mkdir(parents=True, exist_ok=True)
            self.log_file = open(log_dir / f"hfserve-{self.target.model}.log", "wb")
            started = now()
            self.process = subprocess.Popen([str(python), "-m", "voicebench.hfserve", "--model", self.target.model, "--port", port,
                                             "--bits", str(self.bits)], stdout=self.log_file, stderr=subprocess.STDOUT,
                                            cwd=Path(__file__).resolve().parent.parent)
            self._wait(f"http://127.0.0.1:{port}/health", started, 1800)
            return self
        if self.target.kind == "ollama":
            tags = httpx.get(f"{OLLAMA}/api/tags", timeout=10).json().get("models", [])
            names = {m["name"] for m in tags} | {m["model"] for m in tags}
            if self.target.model not in names and f"{self.target.model}:latest" not in names:
                if not self.pull:
                    raise SystemExit(f"Ollama doesn't have {self.target.model}; pass --pull to download it.")
                subprocess.run(["ollama", "pull", self.target.model], check=True)
            unload_ollama(except_model=self.target.model)
        elif self.target.kind == "llamacpp":
            assert self.target.gguf is not None
            model, mmproj = ensure_gguf(self.target.gguf)
            port = self.target.base_url.split(":")[-1].split("/")[0]
            unload_ollama()
            log_dir = RESULTS / "logs"
            log_dir.mkdir(parents=True, exist_ok=True)
            self.log_file = open(log_dir / f"llama-server-{self.target.model}.log", "wb")
            command = [str(llama_server_exe()), "-m", str(model), "--mmproj", str(mmproj), "--host", "127.0.0.1", "--port", port,
                       "-c", str(self.context), "-np", "1", "-fa", "on", "-ctk", "q8_0", "-ctv", "q8_0", "--reasoning", "off",
                       "--fit", "on", "--no-webui", *self.target.gguf.args]
            started = now()
            self.process = subprocess.Popen(command, stdout=self.log_file, stderr=subprocess.STDOUT)
            self._wait(f"http://127.0.0.1:{port}/health", started, 900)
        return self

    def _wait(self, url: str, started: float, seconds: int) -> None:
        import httpx

        assert self.process is not None
        deadline = time.time() + seconds
        while time.time() < deadline:
            if self.process.poll() is not None:
                self.log_file.close()
                raise SystemExit(f"The model server exited ({self.process.returncode}); see {self.log_file.name}")
            try:
                if httpx.get(url, timeout=2).status_code == 200:
                    self.load_ms = ms(now() - started)
                    log(f"   model server ready in {self.load_ms / 1000:.1f} s")
                    return
            except httpx.HTTPError:
                pass
            time.sleep(0.5)
        self.__exit__()
        raise SystemExit(f"The model server didn't become ready; see {self.log_file.name}")

    def __exit__(self, *_: Any) -> None:
        if self.process is not None:
            self.process.terminate()
            try:
                self.process.wait(30)
            except subprocess.TimeoutExpired:
                self.process.kill()
            self.log_file.close()
        elif self.target.kind == "ollama":
            unload_ollama()


def unload_ollama(except_model: str | None = None) -> None:
    import httpx

    try:
        loaded = httpx.get(f"{OLLAMA}/api/ps", timeout=5).json().get("models", [])
    except httpx.HTTPError:
        return
    for model in loaded:
        if model["name"] != except_model:
            httpx.post(f"{OLLAMA}/api/generate", json={"model": model["name"], "keep_alive": 0}, timeout=60)


# ---------------------------------------------------------------- streaming


class FirstPiece:
    """Martlet's speech segmenter, for the first piece only: a sentence (. ? ! then a space or a new line), or with nothing
    said yet a clause of 24+ characters ending in , ; or a dash."""

    def __init__(self) -> None:
        self.buffer = ""
        self.pending = False
        self.piece: str | None = None

    def push(self, delta: str) -> bool:
        if self.piece is not None:
            return False
        for c in delta:
            if self.pending and c.isspace():
                self.piece = self.buffer.strip()
                return True
            self.pending = False
            if c in "\r\n":
                if self.buffer.strip():
                    self.piece = self.buffer.strip()
                    return True
                continue
            self.buffer += c
            self.pending = c in ".?!" or (c in ",;\u2014\u2013" and len(self.buffer) >= 24)
        return False

    def finish(self) -> bool:
        if self.piece is None and self.buffer.strip():
            self.piece = self.buffer.strip()
            return True
        return False


@dataclass
class Reply:
    headers_ms: float | None = None
    first_reasoning_ms: float | None = None
    first_token_ms: float | None = None
    first_piece_ms: float | None = None
    done_ms: float | None = None
    text: str = ""
    piece: str | None = None
    chunks: int = 0
    error: str | None = None
    extra: dict[str, Any] = field(default_factory=dict)


class ChatClient:
    def __init__(self, target: Target, timeout: float = 180) -> None:
        import httpx

        if target.kind == "cloud" and not target.key:
            raise SystemExit(f"{target.spec} needs its API key in the environment (OPENROUTER_API_KEY or OPENAI_API_KEY).")
        headers = {"Authorization": f"Bearer {target.key}"} if target.key else {}
        self.target = target
        self.http = httpx.Client(timeout=timeout, headers=headers)

    def body(self, messages: list[dict[str, Any]], max_tokens: int, temperature: float) -> dict[str, Any]:
        body: dict[str, Any] = {"model": self.target.model, "messages": messages, "stream": True, "max_tokens": max_tokens,
                                "temperature": temperature}
        # Thinking steps Off, the way Martlet asks each server family (GenerationSupport.WriteReasoning).
        if self.target.reasoning == "effort":
            body["reasoning_effort"] = "none"
        elif self.target.reasoning == "openrouter":
            body["reasoning"] = {"effort": "none"}
            body["provider"] = {"allow_fallbacks": False}
        elif self.target.reasoning == "template":
            body["chat_template_kwargs"] = {"enable_thinking": False, "thinking": False}
        return body

    def stream(self, messages: list[dict[str, Any]], max_tokens: int = 200, temperature: float = 0.7,
               on_piece: Callable[[str, float], None] | None = None) -> Reply:
        reply = Reply()
        piece = FirstPiece()
        started = now()
        try:
            with self.http.stream("POST", f"{self.target.base_url}/chat/completions",
                                  json=self.body(messages, max_tokens, temperature)) as response:
                reply.headers_ms = ms(now() - started)
                if response.status_code >= 400:
                    reply.error = f"HTTP {response.status_code}: {response.read().decode('utf-8', 'replace')[:300]}"
                    return reply
                for line in response.iter_lines():
                    if not line.startswith("data:"):
                        continue
                    data = line[5:].strip()
                    if data == "[DONE]":
                        break
                    event = json.loads(data)
                    if event.get("error"):
                        reply.error = str(event["error"])[:300]
                        break
                    for choice in event.get("choices") or []:
                        delta = choice.get("delta") or {}
                        if (delta.get("reasoning") or delta.get("reasoning_content")) and reply.first_reasoning_ms is None:
                            reply.first_reasoning_ms = ms(now() - started)
                        content = delta.get("content")
                        if content:
                            reply.chunks += 1
                            if reply.first_token_ms is None:
                                reply.first_token_ms = ms(now() - started)
                            reply.text += content
                            if piece.push(content):
                                reply.first_piece_ms = ms(now() - started)
                                reply.piece = piece.piece
                                if on_piece:
                                    on_piece(piece.piece or "", now())
        except Exception as exc:  # noqa: BLE001 - reported per request
            reply.error = f"{type(exc).__name__}: {exc}"[:300]
            return reply
        reply.done_ms = ms(now() - started)
        if piece.finish():
            reply.first_piece_ms = reply.done_ms
            reply.piece = piece.piece
            if on_piece:
                on_piece(piece.piece or "", now())
        return reply


def system_prompt() -> str:
    return (DATA / "system.txt").read_text(encoding="utf-8").strip()


def audio_part(wav: bytes) -> dict[str, Any]:
    return {"type": "input_audio", "input_audio": {"data": base64.b64encode(wav).decode("ascii"), "format": "wav"}}


def messages_for(mode: str, transcript: str | None, wav: bytes | None) -> list[dict[str, Any]]:
    """The same system prompt for every mode (so prompt caches are reused); the per-message note closes the user's turn,
    as Martlet sends it."""
    system = {"role": "system", "content": system_prompt()}
    if mode == "text":
        return [system, {"role": "user", "content": transcript or ""}]
    if mode == "audio":
        return [system, {"role": "user", "content": [{"type": "text", "text": HEARD_ONLY}, audio_part(wav or b"")]}]
    if mode == "audio+text":
        return [system, {"role": "user", "content": [{"type": "text", "text": f"{transcript}\n\n{HEARD_WITH_TRANSCRIPT}"},
                                                     audio_part(wav or b"")]}]
    raise SystemExit(f"Unknown mode '{mode}' ({', '.join(MODES)})")


# ---------------------------------------------------------------- benchmarks


def benchmark(args: Any) -> Results:
    clips = load_clips(args.clips, args.limit)
    modes = [m.strip() for m in args.modes.split(",")]
    results = Results("think", {"clips": args.clips, "count": len(clips), "targets": args.targets, "modes": modes,
                                "repeat": args.repeat, "max_tokens": args.max_tokens})
    for spec in args.targets:
        target = parse_target(spec)
        if target.kind == "cloud" and not args.allow_cloud:
            results.notes.append(f"{spec}: NOT RUN (cloud and paid; pass --allow-cloud with a key to include it)")
            continue
        log(f"\n== {spec}")
        try:
            with GpuMemory() as gpu, LocalServer(target, pull=args.pull, context=args.context, bits=args.bits) as server:
                client = ChatClient(target)
                per_mode: dict[str, list[Reply]] = {m: [] for m in modes}
                for mode in modes:
                    warm = client.stream(messages_for(mode, clips[0].text, clips[0].wav), args.max_tokens)
                    if warm.error:
                        log(f"   {mode}: {warm.error}")
                for _ in range(args.repeat):
                    for clip in clips:
                        wav = clip.wav
                        for mode in modes:
                            reply = client.stream(messages_for(mode, clip.text, wav), args.max_tokens)
                            reply.extra = {"clip": clip.id, "seconds": round(clip.seconds, 2), "mode": mode}
                            per_mode[mode].append(reply)
                            log(f"   {clip.id} {mode:10} first piece {reply.first_piece_ms} ms"
                                + (f"  ERROR {reply.error}" if reply.error else f"  {(reply.piece or '')[:60]!r}"))
                for mode, replies in per_mode.items():
                    good = [r for r in replies if not r.error]
                    results.rows.append({
                        "target": spec, "mode": mode, "server_load_ms": server.load_ms, "gpu_added_mb": gpu.added_mb,
                        "headers": stats(r.headers_ms for r in good), "first_token": stats(r.first_token_ms for r in good),
                        "first_piece": stats(r.first_piece_ms for r in good), "done": stats(r.done_ms for r in good),
                        "errors": len(replies) - len(good), "first_error": next((r.error for r in replies if r.error), None)})
                    results.details += [{"target": spec, **r.extra, **{k: v for k, v in r.__dict__.items() if k != "extra"}}
                                        for r in replies]
        except SystemExit as exc:
            results.notes.append(f"{spec}: NOT RUN ({exc})")
    rows = [[r["target"], r["mode"], r["first_token"]["median"], r["first_token"]["p90"], r["first_piece"]["median"],
             r["first_piece"]["p90"], r["done"]["median"], r["errors"], r["gpu_added_mb"]] for r in results.rows]
    results.markdown = (f"Clip set `{args.clips}` ({len(clips)} clips x {args.repeat}). *text* sends the reference transcript, "
                        "*audio* only the recording (omni, no speech-to-text), *audio+text* both (Martlet's 'Let Thinking hear "
                        "my voice'). Times are from sending the request, after a warm-up request per mode; Thinking steps Off.\n\n"
                        + table(["Target", "Mode", "First word ms", "p90", "First piece ms", "p90", "Whole reply ms", "Errors",
                                 "GPU MB added"], rows))
    errors = [f"{r['target']} {r['mode']}: {r['first_error']}" for r in results.rows if r["first_error"]]
    results.notes += errors
    print("\n" + results.markdown)
    for note in results.notes:
        print("- " + note)
    log(f"\nSaved {results.save()}")
    return results


def omni_transcriber(spec: str, clips: list[Clip], args: Any) -> dict[str, Any]:
    """A model that hears, asked to transcribe: how well it hears (word error rate) and how long a transcript takes."""
    target = parse_target(spec)
    if target.kind == "cloud" and not getattr(args, "allow_cloud", False):
        raise SystemExit(f"{spec} is a paid cloud target; pass --allow-cloud")
    with GpuMemory() as gpu, LocalServer(target, pull=getattr(args, "pull", False), bits=getattr(args, "bits", 4)) as server:
        client = ChatClient(target)

        def ask(clip: Clip) -> Reply:
            return client.stream([{"role": "user", "content": [{"type": "text", "text": TRANSCRIBE}, audio_part(clip.wav)]}],
                                 max_tokens=300, temperature=0.0)

        started = now()
        ask(clips[0])
        first = now() - started
        rows = []
        for clip in clips:
            reply = ask(clip)
            rows.append({"clip": clip.id, "seconds": round(clip.seconds, 2), "latency_ms": reply.done_ms, "text": reply.text.strip(),
                         "reference": clip.text, "error": reply.error})
    return {"engine": f"omni:{spec}", "about": f"{spec} asked to transcribe", "device": "gpu" if target.kind != "cloud" else "cloud",
            "load_ms": server.load_ms or 0.0, "first_ms": ms(first), "gpu_added_mb": gpu.added_mb,
            "rows": [r for r in rows if r["latency_ms"] is not None]}
