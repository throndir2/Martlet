"""The shared worker host module: pinned downloads, the HTTP handler's request checks and the model process owner."""

from __future__ import annotations

import hashlib
import http.client
import http.server
import io
import json
import queue
import sys
import tempfile
import threading
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import martlet_worker_host as shared  # noqa: E402

CONTENT = b"pinned model bytes"
SHA256 = hashlib.sha256(CONTENT).hexdigest()


def opener(data: bytes, calls: list[str] | None = None):
    def open_url(request, timeout):  # noqa: ARG001 - urlopen's signature
        if calls is not None:
            calls.append(request.full_url)
        return io.BytesIO(data)
    return open_url


class DownloadTests(unittest.TestCase):
    def setUp(self) -> None:
        self.folder = tempfile.TemporaryDirectory()
        self.root = Path(self.folder.name)
        self.target = self.root / "models" / "model.bin"

    def tearDown(self) -> None:
        self.folder.cleanup()

    def test_sha256_file_gives_size_and_digest(self) -> None:
        path = self.root / "file.bin"
        path.write_bytes(CONTENT)
        self.assertEqual((len(CONTENT), SHA256), shared.sha256_file(path))
        self.assertTrue(shared.is_pinned(path, len(CONTENT), SHA256))
        self.assertFalse(shared.is_pinned(path, len(CONTENT) + 1, SHA256))
        self.assertFalse(shared.is_pinned(self.root / "missing.bin", len(CONTENT), SHA256))

    def test_installs_the_pinned_file(self) -> None:
        shared.download("https://example.test/model.bin", self.target, len(CONTENT), SHA256, agent="test",
                        opener=opener(CONTENT))
        self.assertEqual(CONTENT, self.target.read_bytes())
        self.assertEqual(["model.bin"], [p.name for p in self.target.parent.iterdir()])

    def test_a_sha256_mismatch_installs_nothing(self) -> None:
        changed = b"pinned model bytez"
        with self.assertRaises(shared.DownloadError) as error:
            shared.download("https://example.test/model.bin", self.target, len(changed), SHA256, agent="test",
                            opener=opener(changed))
        self.assertIn("does not match its pinned SHA-256", str(error.exception))
        self.assertEqual([], list(self.target.parent.iterdir()))

    def test_a_larger_or_shorter_file_installs_nothing(self) -> None:
        with self.assertRaises(shared.DownloadError) as error:
            shared.download("https://example.test/u", self.target, len(CONTENT) - 1, SHA256, agent="test", opener=opener(CONTENT))
        self.assertIn("larger than its pinned", str(error.exception))
        with self.assertRaises(shared.DownloadError) as error:
            shared.download("https://example.test/u", self.target, len(CONTENT) + 1, SHA256, agent="test", opener=opener(CONTENT),
                            label="models/model.bin")
        self.assertIn("models/model.bin has", str(error.exception))
        self.assertEqual([], list(self.target.parent.iterdir()))

    def test_fetch_pinned_keeps_a_verified_file_and_stops_on_a_mismatch(self) -> None:
        calls: list[str] = []
        shared.fetch_pinned("https://example.test/a", self.target, len(CONTENT), SHA256, agent="test",
                            opener=opener(CONTENT, calls))
        shared.fetch_pinned("https://example.test/a", self.target, len(CONTENT), SHA256, agent="test",
                            opener=opener(CONTENT, calls))
        self.assertEqual(1, len(calls))
        # A changed file on disk is downloaded again; a download that is not the pinned file ends the command.
        self.target.write_bytes(b"something else entirely")
        with self.assertRaises(SystemExit) as stopped:
            shared.fetch_pinned("https://example.test/a", self.target, len(CONTENT), SHA256, agent="test",
                                opener=opener(b"x" * len(CONTENT), calls))
        self.assertIn("nothing was installed", str(stopped.exception))
        self.assertFalse(self.target.exists())


class _Echo(shared.JsonHandler):
    server_version = "martlet-test-host"
    max_body_bytes = 64

    def do_POST(self) -> None:  # noqa: N802
        body = self._body()
        if body is not None:
            self._json(200, {"echo": body})


class _Shaped(_Echo):
    def refusal(self, code: str, summary: str) -> dict:
        return {"error": {"code": code, "summary": summary}}


class HandlerTests(unittest.TestCase):
    def serve(self, handler: type) -> int:
        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        return server.server_address[1]

    def post(self, port: int, body: bytes, length: str | None) -> tuple[int, dict]:
        connection = http.client.HTTPConnection("127.0.0.1", port, timeout=10)
        try:
            connection.putrequest("POST", "/", skip_accept_encoding=True)
            if length is not None:
                connection.putheader("Content-Length", length)
            connection.endheaders()
            if body:
                connection.send(body)
            response = connection.getresponse()
            return response.status, json.loads(response.read())
        finally:
            connection.close()

    def test_a_json_object_within_the_limit_is_read(self) -> None:
        port = self.serve(_Echo)
        self.assertEqual((200, {"echo": {"a": 1}}), self.post(port, b'{"a":1}', "7"))

    def test_content_length_cases(self) -> None:
        port = self.serve(_Echo)
        self.assertEqual((411, {"error": "request.length_required"}), self.post(port, b"", None))
        self.assertEqual((411, {"error": "request.length_required"}), self.post(port, b"", " "))
        for bad in ("abc", "-5", "1e3", "7.0", "\u00b2"):
            self.assertEqual((400, {"error": "request.invalid"}), self.post(port, b'{"a":1}', bad), bad)
        self.assertEqual((400, {"error": "request.invalid"}), self.post(port, b"", "0"))
        self.assertEqual((413, {"error": "request.too_large"}), self.post(port, b"", "65"))
        self.assertEqual((400, {"error": "request.invalid"}), self.post(port, b"[1,2]", "5"))
        self.assertEqual((400, {"error": "request.invalid"}), self.post(port, b"{nope", "5"))

    def test_the_default_limit_is_six_mebibytes(self) -> None:
        self.assertEqual(6 * 1024 * 1024, shared.JsonHandler.max_body_bytes)

    def test_a_host_can_shape_its_errors(self) -> None:
        port = self.serve(_Shaped)
        status, body = self.post(port, b"", "x")
        self.assertEqual((400, "request.invalid"), (status, body["error"]["code"]))
        self.assertTrue(body["error"]["summary"])


class _Pipe:
    def __init__(self, lines: list[bytes]) -> None:
        self.lines = lines
        self.written = bytearray()

    def __iter__(self):
        return iter(self.lines)

    def write(self, data: bytes) -> None:
        self.written += data

    def flush(self) -> None:
        pass


class _Process:
    def __init__(self, lines: list[bytes]) -> None:
        self.stdout = _Pipe(lines)
        self.stdin = _Pipe([])

    def poll(self) -> None:
        return None


class ModelProcessTests(unittest.TestCase):
    def test_reads_states_and_events_then_fails_the_waiting_reply_when_the_process_ends(self) -> None:
        owner = shared.ModelProcess("cold")
        process = _Process([
            b"not json\n",
            json.dumps({"type": "worker_state", "ready": True}).encode() + b"\n",
            json.dumps({"type": "event", "ids": {"request_id": "r1"}, "kind": "audio_frame"}).encode() + b"\n",
            json.dumps({"type": "event", "ids": {"request_id": "other"}, "kind": "audio_frame"}).encode() + b"\n",
        ])
        owner.process = process  # type: ignore[assignment]
        events: queue.Queue = queue.Queue()
        owner.pending["r1"] = events
        self.assertTrue(owner.send({"type": "warmup"}))
        self.assertEqual(b'{"type":"warmup"}\n', bytes(process.stdin.written))
        owner._read(process)  # type: ignore[arg-type]
        self.assertEqual("audio_frame", events.get_nowait()["kind"])
        self.assertIsNone(events.get_nowait())
        self.assertTrue(events.empty())
        self.assertEqual(("failed", shared.ModelProcess.stopped_error), (owner.state, owner.error))
        self.assertEqual("failed", owner.wait_settled(0)["state"])

    def test_send_without_a_process_fails(self) -> None:
        self.assertFalse(shared.ModelProcess("cold").send({"type": "warmup"}))


if __name__ == "__main__":
    unittest.main()
