"""A minimal OpenAI-compatible Chat Completions server (streaming) for models that hear but that llama.cpp can't give audio:
Phi-4-multimodal and MiniCPM-o 2.6, through PyTorch and the model's own remote code. Loopback only, one request at a time.

    python -m voicebench.hfserve --model phi-4-multimodal --port 8091 [--bits 4]

Takes the same request voicebench (and Martlet) sends: a system message, then user content parts text, input_audio (WAV,
base64) and image_url (data URL)."""

from __future__ import annotations

import argparse
import base64
import io
import json
import threading
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any, Iterator

import numpy as np

REPOS = {"phi-4-multimodal": "microsoft/Phi-4-multimodal-instruct", "minicpm-o-2.6": "openbmb/MiniCPM-o-2_6"}


def decode_wav(data: str) -> np.ndarray:
    import soundfile

    samples, rate = soundfile.read(io.BytesIO(base64.b64decode(data)), dtype="float32", always_2d=True)
    samples = samples.mean(axis=1)
    if rate != 16_000:
        import soxr

        samples = soxr.resample(samples, rate, 16_000)
    return np.ascontiguousarray(samples, dtype=np.float32)


def decode_image(url: str) -> Any:
    from PIL import Image

    return Image.open(io.BytesIO(base64.b64decode(url.split(",", 1)[1]))).convert("RGB")


def parse(messages: list[dict[str, Any]]) -> tuple[str, list[tuple[str, list[Any]]]]:
    """(system text, [(role, [str | audio ndarray | PIL image, ...])])."""
    system = ""
    turns: list[tuple[str, list[Any]]] = []
    for message in messages:
        content = message.get("content")
        parts: list[Any] = []
        if isinstance(content, str):
            parts.append(content)
        else:
            for part in content or []:
                kind = part.get("type")
                if kind == "text":
                    parts.append(part["text"])
                elif kind == "input_audio":
                    parts.append(decode_wav(part["input_audio"]["data"]))
                elif kind == "image_url":
                    parts.append(decode_image(part["image_url"]["url"]))
        if message["role"] == "system":
            system += "".join(p for p in parts if isinstance(p, str))
        else:
            turns.append((message["role"], parts))
    return system, turns


def quantization(bits: int, skip: list[str] | None = None) -> Any:
    if bits >= 16:
        return None
    import torch
    from transformers import BitsAndBytesConfig

    if bits == 8:
        return BitsAndBytesConfig(load_in_8bit=True, llm_int8_skip_modules=skip)
    return BitsAndBytesConfig(load_in_4bit=True, bnb_4bit_quant_type="nf4", bnb_4bit_compute_dtype=torch.bfloat16,
                              llm_int8_skip_modules=skip)


class Phi4Multimodal:
    """microsoft/Phi-4-multimodal-instruct: <|system|>..<|end|><|user|><|audio_1|><|image_1|>text<|end|><|assistant|>."""

    def __init__(self, bits: int) -> None:
        import torch
        from transformers import AutoModelForCausalLM, AutoProcessor, GenerationConfig

        repo = REPOS["phi-4-multimodal"]
        self.torch = torch
        self.processor = AutoProcessor.from_pretrained(repo, trust_remote_code=True)
        # Only the language model's base weights are quantized: its speech and vision LoRA adapters (switched per request,
        # which needs float weights) and the audio and image encoders (embed_tokens_extend) stay bf16.
        self.model = AutoModelForCausalLM.from_pretrained(
            repo, trust_remote_code=True, torch_dtype=torch.bfloat16, _attn_implementation="sdpa", device_map="cuda",
            quantization_config=quantization(bits, ["lora_A", "lora_B", "embed_tokens_extend", "lm_head"])).eval()
        self.generation = GenerationConfig.from_pretrained(repo)

    def stream(self, system: str, turns: list[tuple[str, list[Any]]], max_tokens: int, temperature: float) -> Iterator[str]:
        from transformers import TextIteratorStreamer

        prompt = f"<|system|>{system}<|end|>" if system else ""
        audios, images = [], []
        for role, parts in turns:
            tags, text = "", ""
            for part in parts:
                if isinstance(part, str):
                    text += part
                elif isinstance(part, np.ndarray):
                    audios.append((part, 16_000))
                    tags += f"<|audio_{len(audios)}|>"
                else:
                    images.append(part)
                    tags += f"<|image_{len(images)}|>"
            prompt += f"<|{'user' if role == 'user' else 'assistant'}|>{tags}{text}<|end|>"
        prompt += "<|assistant|>"
        inputs = self.processor(text=prompt, audios=audios or None, images=images or None, return_tensors="pt").to("cuda")
        streamer = TextIteratorStreamer(self.processor.tokenizer, skip_prompt=True, skip_special_tokens=True, timeout=120)
        kwargs = dict(**inputs, max_new_tokens=max_tokens, generation_config=self.generation, streamer=streamer,
                      num_logits_to_keep=1, do_sample=temperature > 0, **({"temperature": temperature} if temperature > 0 else {}))
        failure: list[BaseException] = []

        def generate() -> None:
            try:
                self.model.generate(**kwargs)
            except BaseException as exc:  # noqa: BLE001 - re-raised on the request's thread
                failure.append(exc)
                streamer.end()

        thread = threading.Thread(target=generate, daemon=True)
        thread.start()
        yield from streamer
        thread.join()
        if failure:
            raise failure[0]


class MiniCpmO:
    """openbmb/MiniCPM-o-2_6 through its own chat(): content lists mixing text, 16 kHz audio arrays and images. Its own speech
    output (TTS) isn't loaded."""

    def __init__(self, bits: int) -> None:
        import torch
        from unittest import mock

        from transformers import AutoModel, AutoTokenizer
        from transformers.dynamic_module_utils import get_imports

        def without_flash_attn(filename: str) -> list[str]:
            # Its remote code lists flash_attn, which has no Windows wheel; the model runs with sdpa attention instead.
            return [name for name in get_imports(filename) if name != "flash_attn"]

        repo = REPOS["minicpm-o-2.6"]
        self.torch = torch
        with mock.patch("transformers.dynamic_module_utils.get_imports", without_flash_attn):
            self.tokenizer = AutoTokenizer.from_pretrained(repo, trust_remote_code=True)
            self.model = AutoModel.from_pretrained(repo, trust_remote_code=True, attn_implementation="sdpa", torch_dtype=torch.bfloat16,
                                                   init_vision=True, init_audio=True, init_tts=False, device_map="cuda",
                                                   quantization_config=quantization(bits, ["vpm", "apm", "resampler", "audio_projection_layer",
                                                                                           "lm_head"])).eval()

    def stream(self, system: str, turns: list[tuple[str, list[Any]]], max_tokens: int, temperature: float) -> Iterator[str]:
        msgs = ([{"role": "system", "content": [system]}] if system else []) + [{"role": role, "content": parts} for role, parts in turns]
        result = self.model.chat(msgs=msgs, tokenizer=self.tokenizer, sampling=temperature > 0, temperature=max(temperature, 0.01),
                                 max_new_tokens=max_tokens, stream=True, use_tts_template=False, generate_audio=False,
                                 omni_input=False, max_slice_nums=1)
        if isinstance(result, str):
            yield result
            return
        for piece in result:
            yield piece if isinstance(piece, str) else str(getattr(piece, "text", piece))


ENGINES = {"phi-4-multimodal": Phi4Multimodal, "minicpm-o-2.6": MiniCpmO}


def serve(name: str, port: int, bits: int) -> None:
    started = time.perf_counter()
    engine = ENGINES[name](bits)
    print(f"{name} loaded in {time.perf_counter() - started:.1f} s ({bits}-bit); http://127.0.0.1:{port}", flush=True)
    lock = threading.Lock()

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_: Any) -> None:
            return

        def do_GET(self) -> None:  # noqa: N802
            body = b'{"status":"ok"}'
            self.send_response(200 if self.path in ("/health", "/v1/models") else 404)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_POST(self) -> None:  # noqa: N802
            if self.path != "/v1/chat/completions":
                self.send_error(404)
                return
            request = json.loads(self.rfile.read(int(self.headers.get("Content-Length") or 0)))
            system, turns = parse(request.get("messages") or [])
            ident = f"chatcmpl-{uuid.uuid4().hex[:12]}"
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.send_header("Cache-Control", "no-store")
            self.end_headers()

            def send(delta: dict[str, Any], finish: str | None = None) -> None:
                event = {"id": ident, "object": "chat.completion.chunk", "model": name,
                         "choices": [{"index": 0, "delta": delta, "finish_reason": finish}]}
                self.wfile.write(f"data: {json.dumps(event)}\n\n".encode())
                self.wfile.flush()

            with lock:
                try:
                    send({"role": "assistant"})
                    with engine.torch.inference_mode():
                        for text in engine.stream(system, turns, int(request.get("max_tokens") or 256),
                                                  float(request.get("temperature", 0.7))):
                            if text:
                                send({"content": text})
                    send({}, "stop")
                except Exception as exc:  # noqa: BLE001 - reported to the client
                    self.wfile.write(f"data: {json.dumps({'error': {'message': f'{type(exc).__name__}: {exc}'}})}\n\n".encode())
                self.wfile.write(b"data: [DONE]\n\n")
                self.wfile.flush()

    ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True, choices=list(ENGINES))
    parser.add_argument("--port", type=int, default=8091)
    parser.add_argument("--bits", type=int, default=4, choices=[4, 8, 16])
    args = parser.parse_args()
    serve(args.model, args.port, args.bits)
