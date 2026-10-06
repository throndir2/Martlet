"""Plumbing checks for the Parakeet host service: the relay's form, WAV checks, pinned downloads and the HTTP contract,
with a stand-in transcriber (no sherpa-onnx, numpy or model needed)."""

from __future__ import annotations

import hashlib
import http.client
import io
import json
import sys
import tempfile
import threading
import unittest
import wave
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "host"))

import martlet_parakeet_host as host  # noqa: E402


def wav(seconds: float = 0.5, rate: int = 16_000, channels: int = 1, width: int = 2) -> bytes:
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as writer:
        writer.setnchannels(channels)
        writer.setsampwidth(width)
        writer.setframerate(rate)
        writer.writeframes(bytes(int(rate * seconds) * channels * width))
    return buffer.getvalue()


def form(audio: bytes, quoted: bool = False) -> tuple[str, bytes]:
    """The relay's request as .NET's MultipartFormDataContent writes it (unquoted names, a quoted boundary)."""
    boundary = "1b6c0f0e-5a0c-4d1e-9d3a-6f2b8c1d2e3f"
    name = '"file"' if quoted else "file"
    body = (
        f"--{boundary}\r\nContent-Type: audio/wav\r\n"
        f"Content-Disposition: form-data; name={name}; filename=utterance.wav; filename*=utf-8''utterance.wav\r\n\r\n"
    ).encode() + audio + (
        f"\r\n--{boundary}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Disposition: form-data; name=temperature\r\n\r\n0.0"
        f"\r\n--{boundary}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Disposition: form-data; name=response_format\r\n\r\njson"
        f"\r\n--{boundary}--\r\n"
    ).encode()
    return f'multipart/form-data; boundary="{boundary}"', body


class FakeTranscriber:
    def __init__(self) -> None:
        self.calls: list[tuple[int, int]] = []

    def transcribe(self, rate: int, pcm: bytes) -> str:
        self.calls.append((rate, len(pcm)))
        return "Hello there."


class FormAndWaveTests(unittest.TestCase):
    def test_reads_the_relays_file_field(self) -> None:
        audio = wav()
        for quoted in (False, True):
            content_type, body = form(audio, quoted)
            self.assertEqual(audio, host.file_field(content_type, body))

    def test_refuses_other_forms(self) -> None:
        content_type, body = form(wav())
        with self.assertRaises(host.BadRequest):
            host.file_field("application/json", body)
        with self.assertRaises(host.BadRequest):
            host.file_field("multipart/form-data", body)
        with self.assertRaises(host.BadRequest):
            host.file_field(content_type, body, field="audio")

    def test_takes_one_mono_16_bit_utterance_of_at_most_a_minute(self) -> None:
        rate, pcm = host.read_wave(wav(0.25))
        self.assertEqual((16_000, 8_000), (rate, len(pcm)))
        self.assertEqual(24_000, host.read_wave(wav(0.1, rate=24_000))[0])
        for bad in (wav(channels=2), wav(width=1), wav(rate=96_000), wav(61), b"RIFF not a wave", b""):
            with self.assertRaises(host.BadRequest):
                host.read_wave(bad)


class DownloadTests(unittest.TestCase):
    def setUp(self) -> None:
        self.folder = tempfile.TemporaryDirectory()
        self.root = Path(self.folder.name)
        self.content = {"a.onnx": b"encoder", "b.onnx": b"decoder", "c.onnx": b"joiner", "tokens.txt": b"<blk> 0\n"}
        files = [host.ModelFile(name, len(data), hashlib.sha256(data).hexdigest()) for name, data in self.content.items()]
        self.model = host.Model("parakeet-test", "Parakeet Test", "English", "owner/repo", "0123abcd", *files,
                                "Parakeet-Test-NOTICE.txt", "Test notice\n")
        self.requests: list[str] = []

    def tearDown(self) -> None:
        self.folder.cleanup()

    def opener(self, served: dict[str, bytes]):
        def open_url(request, timeout):  # noqa: ARG001 - urlopen's signature
            self.requests.append(request.full_url)
            return io.BytesIO(served[request.full_url.rsplit("/", 1)[1]])
        return open_url

    def test_downloads_once_at_the_pinned_revision_and_keeps_checked_files(self) -> None:
        folder = host.ensure_model(self.model, self.root, self.opener(self.content), report=lambda _: None)
        self.assertEqual(4, len(self.requests))
        self.assertTrue(all(url.startswith("https://huggingface.co/owner/repo/resolve/0123abcd/") for url in self.requests))
        self.assertEqual(b"encoder", (folder / "a.onnx").read_bytes())
        self.assertEqual("Test notice\n", (self.root / "Parakeet-Test-NOTICE.txt").read_text(encoding="utf-8"))
        self.assertFalse(any(path.suffix == ".part" for path in folder.iterdir()))
        host.ensure_model(self.model, self.root, self.opener(self.content), report=lambda _: None)
        self.assertEqual(4, len(self.requests))

    def test_a_changed_file_is_never_kept(self) -> None:
        served = dict(self.content, **{"b.onnx": b"decodex"})
        with mock.patch.object(host.time, "sleep"), self.assertRaises(SystemExit) as stopped:
            host.ensure_model(self.model, self.root, self.opener(served), report=lambda _: None)
        self.assertIn("SHA-256", str(stopped.exception))
        folder = self.root / "parakeet-test"
        self.assertFalse((folder / "b.onnx").exists())
        self.assertFalse((folder / "b.onnx.part").exists())
        served["b.onnx"] = b"decoder-and-more"
        with mock.patch.object(host.time, "sleep"), self.assertRaises(SystemExit) as stopped:
            host.ensure_model(self.model, self.root, self.opener(served), report=lambda _: None)
        self.assertIn("larger than its pinned", str(stopped.exception))

    def test_catalog_matches_the_desktops_models(self) -> None:
        self.assertEqual(["parakeet-tdt-110m-en", "parakeet-tdt-0.6b-v2-int8", "parakeet-tdt-0.6b-v3-int8"],
                         [model.id for model in host.MODEL_LIST])
        for model in host.MODEL_LIST:
            self.assertRegex(model.revision, "^[0-9a-f]{40}$")
            for file in model.files:
                self.assertRegex(file.sha256, "^[0-9a-f]{64}$")
                self.assertGreater(file.size, 0)
            self.assertIn("CC BY 4.0", model.notice)
        with self.assertRaises(SystemExit):
            host.model_named("large-v3-turbo")


class HttpTests(unittest.TestCase):
    def setUp(self) -> None:
        self.transcriber = FakeTranscriber()
        service = host.Service(self.transcriber, host.MODEL_LIST[0], 4, {"sherpa-onnx": "1.13.8"})
        self.server = host.serve(service, port=0)
        self.port = self.server.server_address[1]
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def tearDown(self) -> None:
        self.server.shutdown()
        self.server.server_close()

    def request(self, method: str, path: str, body: bytes | None = None, headers: dict[str, str] | None = None):
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=10)
        try:
            connection.request(method, path, body=body, headers=headers or {})
            response = connection.getresponse()
            return response.status, json.loads(response.read())
        finally:
            connection.close()

    def test_listens_only_on_loopback(self) -> None:
        self.assertEqual("127.0.0.1", self.server.server_address[0])

    def test_transcribes_the_relays_request(self) -> None:
        content_type, body = form(wav(1.0))
        status, payload = self.request("POST", "/inference", body, {"Content-Type": content_type})
        self.assertEqual((200, {"text": "Hello there."}), (status, payload))
        self.assertEqual([(16_000, 32_000)], self.transcriber.calls)

    def test_status_names_the_engine_and_model(self) -> None:
        status, payload = self.request("GET", "/status")
        self.assertEqual(200, status)
        self.assertEqual(("parakeet", "parakeet-tdt-110m-en", True, 4), (payload["engine"], payload["model"], payload["ready"], payload["threads"]))

    def test_refuses_what_is_not_one_utterance(self) -> None:
        content_type, body = form(wav(channels=2))
        self.assertEqual(400, self.request("POST", "/inference", body, {"Content-Type": content_type})[0])
        self.assertEqual(400, self.request("POST", "/inference", b"{}", {"Content-Type": "application/json"})[0])
        self.assertEqual(404, self.request("POST", "/load", b"x")[0])
        self.assertEqual(404, self.request("GET", "/")[0])
        status, _ = self.request("POST", "/inference", b"x", {"Content-Length": str(host.MAX_BODY_BYTES + 1)})
        self.assertEqual(413, status)
        self.assertEqual([], self.transcriber.calls)


if __name__ == "__main__":
    unittest.main()
