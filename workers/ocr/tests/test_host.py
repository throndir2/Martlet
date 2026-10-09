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

    def test_status_names_the_ppocrv5_model_accelerator_and_providers(self) -> None:
        engine = FakeEngine()
        engine.providers = ["CUDAExecutionProvider", "CPUExecutionProvider"]
        settings = host.Settings("ppocrv5", "ppocrv5-server", "gpu")
        status = host.Service(engine, fake_decoder, thread_count=2, settings=settings).status()
        self.assertEqual(("ready", "ppocrv5", "3.10.0", "ppocrv5-server", "gpu"),
                         (status["state"], status["engine"], status["engine_version"], status["model"], status["accelerator"]))
        self.assertEqual(["CUDAExecutionProvider", "CPUExecutionProvider"], status["providers"])
        default = host.Service(FakeEngine(), fake_decoder).status()
        self.assertEqual(("rapidocr-ppocrv4", "cpu"), (default["model"], default["accelerator"]))
        self.assertNotIn("providers", default)

    def test_load_failure_is_reported_not_hidden(self) -> None:
        service = host.Service(decoder=fake_decoder, settings=host.Settings("ppocrv5", "ppocrv5-mobile", "cpu"))
        original = host.PpOcrV5Engine

        def broken(model: str, accelerator: str):  # noqa: ANN202 - stand-in loader
            raise RuntimeError("no CUDAExecutionProvider")

        host.PpOcrV5Engine = broken
        try:
            service.load()
        finally:
            host.PpOcrV5Engine = original
        status = service.status()
        self.assertEqual("failed", status["state"])
        self.assertIn("no CUDAExecutionProvider", status["error"])
        with self.assertRaisesRegex(host.NotReady, "failed to load"):
            service.read(b"png", "image/png")

    def test_reads_rapidocr3_output(self) -> None:
        class Output:
            def __init__(self, boxes, txts, scores) -> None:  # noqa: ANN001 - RapidOCROutput stand-in
                self.boxes, self.txts, self.scores = boxes, txts, scores

        found = Output([[[5, 3], [30, 3], [30, 12], [5, 12]]], ("top",), (0.9912,))
        payload = host.Service(lambda image: found, fake_decoder).read(b"png", "image/png")
        self.assertEqual([{"text": "top", "score": 0.991, "box": [5, 3, 25, 9]}], payload["lines"])
        empty = host.Service(lambda image: Output(None, None, None), fake_decoder).read(b"png", "image/png")
        self.assertEqual([], empty["lines"])

    def test_turns_full_width_characters_into_plain_ones(self) -> None:
        found = [[[[0, 0], [9, 0], [9, 9], [0, 9]], "HEALTH 87\uff0f100\uff01", 0.9]]
        payload = host.Service(lambda image: (found, None), fake_decoder).read(b"png", "image/png")
        self.assertEqual("HEALTH 87/100!", payload["lines"][0]["text"])


class SettingsTests(unittest.TestCase):
    def test_defaults_to_rapidocr_on_the_processor(self) -> None:
        settings = host.Settings.from_env({})
        self.assertEqual(("rapidocr", "rapidocr-ppocrv4", "cpu"), (settings.engine, settings.model, settings.accelerator))

    def test_reads_ppocrv5_model_and_accelerator(self) -> None:
        settings = host.Settings.from_env({"MARTLET_OCR_ENGINE": "ppocrv5", "OCR_MODEL": "ppocrv5-server",
                                           "MARTLET_OCR_ACCELERATOR": "GPU"})
        self.assertEqual(("ppocrv5", "ppocrv5-server", "gpu"), (settings.engine, settings.model, settings.accelerator))
        self.assertEqual("ppocrv5-mobile", host.Settings.from_env({"MARTLET_OCR_ENGINE": "ppocrv5"}).model)

    def test_refuses_unknown_values(self) -> None:
        for env in ({"MARTLET_OCR_ENGINE": "tesseract"},
                    {"MARTLET_OCR_ENGINE": "ppocrv5", "OCR_MODEL": "rapidocr-ppocrv4"},
                    {"MARTLET_OCR_ENGINE": "ppocrv5", "MARTLET_OCR_ACCELERATOR": "npu"}):
            with self.subTest(env=env), self.assertRaises(ValueError):
                host.Settings.from_env(env)
        with self.assertRaises(ValueError):
            host.Settings("rapidocr", "rapidocr-ppocrv4", "gpu")

    def test_pins_every_model_with_a_sha256(self) -> None:
        self.assertEqual({"ppocrv5-mobile", "ppocrv5-server"}, set(host.MODELS))
        for files in host.MODELS.values():
            self.assertEqual({"det", "rec"}, set(files))
            for name, sha in files.values():
                self.assertTrue(name.endswith(".onnx"))
                self.assertRegex(sha, "^[0-9a-f]{64}$")


class FetchModelsTests(unittest.TestCase):
    def setUp(self) -> None:
        import hashlib  # noqa: PLC0415
        import tempfile  # noqa: PLC0415

        self.content = {"det/a.onnx": b"detector", "rec/b.onnx": b"recognizer"}
        self.original = host.MODELS
        host.MODELS = {"ppocrv5-mobile": {
            "det": ("det/a.onnx", hashlib.sha256(b"detector").hexdigest()),
            "rec": ("rec/b.onnx", hashlib.sha256(b"recognizer").hexdigest()),
        }}
        self.folder = tempfile.TemporaryDirectory()
        self.requests: list[str] = []
        self.mirror: dict[str, bytes] = {}
        self.pauses: list[float] = []

    def tearDown(self) -> None:
        host.MODELS = self.original
        self.folder.cleanup()

    def opener(self, url: str):  # noqa: ANN201 - urlopen stand-in
        import io  # noqa: PLC0415

        self.requests.append(url)
        if url.startswith("https://down/"):
            raise OSError("certificate is not valid for this name")
        source, _, name = url.removeprefix("https://").partition("/")
        return io.BytesIO(self.mirror.get(name, self.content[name]) if source == "mirror" else self.content[name])

    def fetch(self, *sources: str) -> list[str]:
        return host.fetch_models(self.folder.name, sources or ("https://models/",), self.opener, self.pauses.append)

    def test_downloads_checks_and_keeps_models(self) -> None:
        self.assertEqual(["det/a.onnx", "rec/b.onnx"], self.fetch())
        self.assertEqual(b"recognizer", (Path(self.folder.name) / "rec" / "b.onnx").read_bytes())
        self.assertEqual([], self.fetch())
        self.assertEqual(2, len(self.requests))

    def test_refuses_a_model_that_does_not_match_its_sha256(self) -> None:
        self.content["rec/b.onnx"] = b"tampered"
        with self.assertRaises(SystemExit):
            self.fetch()
        self.assertFalse((Path(self.folder.name) / "rec" / "b.onnx").exists())
        self.assertFalse((Path(self.folder.name) / "rec" / "b.onnx.part").exists())

    def test_uses_the_next_source_when_one_fails_or_gives_other_bytes(self) -> None:
        self.content["rec/b.onnx"] = b"tampered"
        self.mirror["rec/b.onnx"] = b"recognizer"
        self.assertEqual(["det/a.onnx", "rec/b.onnx"], self.fetch("https://down/", "https://models/", "https://mirror/"))
        self.assertEqual(b"recognizer", (Path(self.folder.name) / "rec" / "b.onnx").read_bytes())
        self.assertEqual([5, 10, 5, 10], self.pauses)
        self.assertIn("https://mirror/rec/b.onnx", self.requests)

    def test_pins_two_sources_for_the_models(self) -> None:
        self.assertEqual(2, len(host.MODEL_SOURCES))
        for source in host.MODEL_SOURCES:
            self.assertTrue(source.startswith("https://") and source.endswith("/onnx/PP-OCRv5/"))


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
