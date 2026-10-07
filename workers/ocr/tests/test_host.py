"""HTTP contract tests for the Martlet OCR host service with a stand-in engine."""

from __future__ import annotations

import http.client
import json
import sys
import threading
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "host"))

import martlet_ocr_host as host  # noqa: E402


class FakeImage:
    shape = (40, 100, 3)


class FakeEngine:
    def __init__(self) -> None:
        self.calls = 0

    def __call__(self, image):  # noqa: ANN001 - stand-in image
        self.calls += 1
        return [
            ([[50, 20], [90, 20], [90, 30], [50, 30]], "bottom", 0.87654),
            ([[5, 3], [30, 3], [30, 12], [5, 12]], "top", 0.991),
            ([[40, 3], [60, 3], [60, 12], [40, 12]], "", 0.5),
        ]


def fake_decoder(data: bytes, media_type: str):
    if data == b"bad":
        raise host.BadRequest("bad image")
    return FakeImage()


class ServiceTests(unittest.TestCase):
    def test_status_reports_loading_or_ready(self) -> None:
        self.assertEqual("loading", host.Service(thread_count=2).status()["state"])
        ready = host.Service(FakeEngine(), fake_decoder, "1.4.4", 3).status()
        self.assertEqual(("ready", "rapidocr", "1.4.4", 3),
                         (ready["state"], ready["engine"], ready["engine_version"], ready["threads"]))

    def test_read_sorts_lines_and_converts_boxes(self) -> None:
        engine = FakeEngine()
        payload = host.Service(engine, fake_decoder).read(b"png", "image/png")
        self.assertEqual((100, 40, 1), (payload["width"], payload["height"], engine.calls))
        self.assertEqual(["top", "bottom"], [line["text"] for line in payload["lines"]])
        self.assertEqual([5, 3, 25, 9], payload["lines"][0]["box"])
        self.assertEqual(0.877, payload["lines"][1]["score"])


class HttpTests(unittest.TestCase):
    def setUp(self) -> None:
        self.engine = FakeEngine()
        self.service = host.Service(self.engine, fake_decoder, thread_count=4)
        self.server = host.serve(self.service, port=0)
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
            payload = json.loads(response.read())
            return response.status, payload
        finally:
            connection.close()

    def test_listens_only_on_loopback(self) -> None:
        self.assertEqual("127.0.0.1", self.server.server_address[0])

    def test_status_and_read_contract(self) -> None:
        status, payload = self.request("GET", "/status")
        self.assertEqual(200, status)
        self.assertEqual("ready", payload["state"])
        status, payload = self.request("POST", "/read", b"image", {"Content-Type": "image/png"})
        self.assertEqual(200, status)
        self.assertEqual(["top", "bottom"], [line["text"] for line in payload["lines"]])
        self.assertEqual((100, 40), (payload["width"], payload["height"]))

    def test_refuses_bad_requests(self) -> None:
        self.assertEqual(400, self.request("POST", "/read", b"image", {"Content-Type": "text/plain"})[0])
        self.assertEqual(400, self.request("POST", "/read", b"bad", {"Content-Type": "image/png"})[0])
        self.assertEqual(404, self.request("POST", "/load", b"x", {"Content-Type": "image/png"})[0])
        self.assertEqual(404, self.request("GET", "/")[0])
        status, _ = self.request("POST", "/read", b"", {"Content-Type": "image/png", "Content-Length": str(host.MAX_IMAGE_BYTES + 1)})
        self.assertEqual(400, status)

    def test_loading_read_returns_503(self) -> None:
        self.server.shutdown()
        self.server.server_close()
        self.service = host.Service(decoder=fake_decoder)
        self.server = host.serve(self.service, port=0)
        self.port = self.server.server_address[1]
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.assertEqual(503, self.request("POST", "/read", b"image", {"Content-Type": "image/png"})[0])


if __name__ == "__main__":
    unittest.main()
