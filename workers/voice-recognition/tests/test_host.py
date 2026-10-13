"""Checks of the voice recognition host service without sherpa-onnx or the models: the port of SpeakerEngine's steps with stand-in
segmentation and voiceprints, the audio and request checks, the pinned downloads and the HTTP contract. The real models are
compared with the desktop's engine by tests/Martlet.Desktop.Tests/VoiceRecognitionHostTests.cs."""

from __future__ import annotations

import hashlib
import http.client
import io
import json
import math
import struct
import sys
import tempfile
import threading
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "host"))

import martlet_voice_recognition_host as host  # noqa: E402

CASES = json.loads((Path(__file__).resolve().parent / "clean_cases.json").read_text(encoding="utf-8"))
RATE = host.SAMPLE_RATE


def unit(*values: float) -> list[float]:
    """A 256-number vector starting with ``values``, normalized as the engine does."""
    return list(host.normalize(list(values) + [0.0] * (host.DIMENSION - len(values))))


VOICE_A = unit(1.0, 0.2, 0.1)
VOICE_B = unit(0.1, 1.0, 0.3)
VOICE_C = unit(0.0, 0.1, 1.0)


def audio(seconds: float, regions: list[tuple[float, float, float]]) -> host.array:
    """Silence with each (start, end, level) region filled with that level: the stand-in voice model tells voices apart by it."""
    samples = [0.0] * int(seconds * RATE)
    for start, end, level in regions:
        for i in range(int(start * RATE), min(len(samples), int(end * RATE))):
            samples[i] = level
    return host.float32(samples)


def embed_by_level(samples) -> list[float] | None:
    if not len(samples):
        return None
    level = sum(samples) / len(samples)
    if level == 0:
        return None
    return VOICE_A if level < 0.15 else VOICE_B if level < 0.25 else VOICE_C


class FakeEngine:
    def __init__(self, turns=(), vector=None) -> None:
        self.turns = list(turns)
        self.vector = vector if vector is not None else VOICE_A
        self.calls: list[int] = []

    def segment(self, samples, duration):
        self.calls.append(len(samples))
        return self.turns

    def embed(self, samples):
        return host.float32(self.vector)


class PortTests(unittest.TestCase):
    def test_clean_matches_the_desktop_cases(self) -> None:
        for case in CASES["clean"]:
            with self.subTest(case["name"]):
                turns = [(float(a), float(b), int(c)) for a, b, c in case["turns"]]
                got = host.clean(turns, case["speaker"])
                self.assertEqual(len(case["expected"]), len(got))
                for (start, end), (want_start, want_end) in zip(got, case["expected"]):
                    self.assertAlmostEqual(want_start, start, places=9)
                    self.assertAlmostEqual(want_end, end, places=9)

    def test_rounds_as_dotnet_math_round(self) -> None:
        for value, expected in CASES["round"]:
            self.assertEqual(expected, host.dotnet_round2(value), value)
        self.assertNotEqual(round(1.115, 2), host.dotnet_round2(1.115))

    def test_two_voices_talking_over_each_other(self) -> None:
        samples = audio(10, [(0.0, 3.5, 0.1), (3.5, 4.0, 0.15), (4.0, 9.0, 0.2)])
        turns = [(0.0, 4.0, 0), (3.5, 9.0, 1)]
        result = host.analyze(samples, lambda s, d: turns, embed_by_level)
        self.assertEqual(2, result["voices"])
        self.assertTrue(result["overlap"])
        self.assertIsNone(result["whole"])
        # Speech counts each turn: the half second both talk is in both.
        self.assertEqual(9.5, result["speech_seconds"])
        # Most clean speech first: the second voice's 4.12-8.88, then the first's 0.12-3.38.
        first, second = result["speakers"]
        self.assertEqual((1, 4.76, 4.12, 8.88), (first["local"], first["clean_seconds"], first["start"], first["end"]))
        self.assertEqual((0, 3.26, 0.12, 3.38), (second["local"], second["clean_seconds"], second["start"], second["end"]))
        self.assertEqual(VOICE_B, list(first["voiceprint"]))
        self.assertEqual(VOICE_A, list(second["voiceprint"]))

    def test_short_overlap_is_not_talking_over(self) -> None:
        samples = audio(8, [(0.0, 4.0, 0.1), (4.0, 8.0, 0.2)])
        result = host.analyze(samples, lambda s, d: [(0.0, 4.1, 0), (3.95, 8.0, 1)], embed_by_level)
        self.assertFalse(result["overlap"])
        self.assertEqual(2, len(result["speakers"]))

    def test_a_voice_needs_two_seconds_of_clean_speech_or_the_whole_utterance_is_used(self) -> None:
        samples = audio(2.5, [(0.0, 2.0, 0.1)])
        result = host.analyze(samples, lambda s, d: [(0.0, 2.0, 0)], embed_by_level)
        self.assertEqual([], result["speakers"])
        self.assertEqual(1, result["voices"])
        self.assertEqual(VOICE_A, list(result["whole"]))
        self.assertEqual(2.0, result["speech_seconds"])

    def test_a_short_utterance_has_no_voiceprint_at_all(self) -> None:
        result = host.analyze(audio(0.5, [(0.0, 0.5, 0.1)]), lambda s, d: [], embed_by_level)
        self.assertEqual({"speakers": [], "voices": 0, "overlap": False, "whole": None, "speech_seconds": 0.0}, result)
        self.assertIsNone(host.voiceprint(audio(0.99, [(0.0, 0.99, 0.1)]), embed_by_level))
        self.assertEqual(VOICE_A, list(host.voiceprint(audio(1.0, [(0.0, 1.0, 0.1)]), embed_by_level)))

    def test_fragments_that_disagree_never_reach_the_voice_list(self) -> None:
        # One cluster with two clean stretches that sound like different people (cosine below 0.45).
        samples = audio(12, [(0.0, 5.0, 0.1), (7.0, 12.0, 0.3)])
        turns = [(0.0, 5.0, 0), (5.0, 7.0, 1), (7.0, 12.0, 0)]
        self.assertLess(host.cosine(VOICE_A, VOICE_C), host.CONSISTENCY)
        result = host.analyze(samples, lambda s, d: turns, embed_by_level)
        self.assertEqual([], result["speakers"])
        self.assertIsNotNone(result["whole"])

    def test_a_voice_averages_up_to_three_stretches_of_at_most_eight_seconds(self) -> None:
        vectors = iter([unit(1.0, 0.0), unit(1.0, 0.5), unit(1.0, 1.0)])
        turns = [(0.0, 20.0, 0), (20.0, 20.5, 1), (20.5, 23.0, 0), (23.0, 23.5, 1), (23.5, 25.5, 0), (25.5, 26.0, 1),
                 (26.0, 27.8, 0)]
        result = host.analyze(audio(28, [(0.0, 28.0, 0.1)]), lambda s, d: turns, lambda s: next(vectors))
        voice = next(s for s in result["speakers"] if s["local"] == 0)
        # The 20 s stretch counts 8 s; the three longest stretches are used, not the fourth (1.8 s, 1.56 s after trimming).
        self.assertEqual(8.0 + 2.26 + 1.76, voice["clean_seconds"])
        self.assertEqual((0.12, 25.38), (voice["start"], voice["end"]))
        total = host.float32([0.0] * host.DIMENSION)
        for vector in (unit(1.0, 0.0), unit(1.0, 0.5), unit(1.0, 1.0)):
            for i in range(host.DIMENSION):
                total[i] += vector[i]
        self.assertEqual(list(host.normalize(total)), list(voice["voiceprint"]))

    def test_a_stretch_without_a_voiceprint_is_skipped(self) -> None:
        result = host.analyze(audio(6, [(0.0, 6.0, 0.1)]), lambda s, d: [(0.0, 6.0, 0)], lambda s: None)
        self.assertEqual([], result["speakers"])
        self.assertIsNone(result["whole"])

    def test_at_most_a_minute(self) -> None:
        with self.assertRaises(ValueError):
            host.analyze(host.float32([0.0] * (60 * RATE + 1)), lambda s, d: [], embed_by_level)
        with self.assertRaises(ValueError):
            host.voiceprint(host.float32([0.0] * (60 * RATE + 1)), embed_by_level)

    def test_normalizes_like_the_desktop(self) -> None:
        vector = host.normalize([3.0, 4.0] + [0.0] * 254)
        self.assertEqual(struct.unpack("<f", struct.pack("<f", 0.6))[0], vector[0])
        self.assertEqual(struct.unpack("<f", struct.pack("<f", 0.8))[0], vector[1])
        self.assertEqual([0.0] * 3, list(host.normalize([0.0, 0.0, 0.0])))

    def test_voiceprints_are_written_as_the_shortest_float_text(self) -> None:
        for value in (0.1, -0.0625, 1e-7, 0.33333334, 0.987654321, -1.0):
            single = struct.unpack("<f", struct.pack("<f", value))[0]
            written = json.loads(json.dumps(host.json_vector([single])))[0]
            self.assertEqual(struct.pack("<f", single), struct.pack("<f", written))
            self.assertLessEqual(len(repr(written)), 15)
        self.assertEqual([0.1], host.json_vector(host.float32([0.1])))
        self.assertIsNone(host.json_vector(None))


class AudioTests(unittest.TestCase):
    def test_reads_pcm16_as_the_desktop_does(self) -> None:
        pcm = struct.pack("<4h", -32768, 16384, 0, 1)
        self.assertEqual([-1.0, 0.5, 0.0, 1 / 32768], list(host.samples_of(pcm)))

    def test_refuses_what_is_not_up_to_a_minute_of_pcm16(self) -> None:
        for bad in (b"", b"\x01", b"\x00" * (host.MAX_PCM_BYTES + 2)):
            with self.assertRaises(host.BadRequest):
                host.samples_of(bad)
        self.assertEqual(60 * RATE, len(host.samples_of(b"\x00" * host.MAX_PCM_BYTES)))

    def test_reads_raw_and_wave_files(self) -> None:
        with tempfile.TemporaryDirectory() as folder:
            raw, wave_file = Path(folder, "a.pcm"), Path(folder, "a.wav")
            raw.write_bytes(b"\x01\x00\x02\x00")
            wave_file.write_bytes(b"RIFF" + bytes(40) + b"\x01\x00\x02\x00")
            self.assertEqual(b"\x01\x00\x02\x00", host.read_pcm_file(raw))
            self.assertEqual(b"\x01\x00\x02\x00", host.read_pcm_file(wave_file))


class ModelTests(unittest.TestCase):
    def setUp(self) -> None:
        self.folder = tempfile.TemporaryDirectory()
        self.root = Path(self.folder.name)
        self.content = {"wespeaker_en_voxceleb_resnet34_LM.onnx": b"voice", "pyannote-segmentation-3-0/model.onnx": b"turns",
                        "pyannote-segmentation-3-0/LICENSE": b"MIT"}
        self.files = tuple(host.ModelFile(path, len(data), hashlib.sha256(data).hexdigest(), f"https://example.test/{path}")
                           for path, data in self.content.items())
        self.requests: list[str] = []

    def tearDown(self) -> None:
        self.folder.cleanup()

    def opener(self, served: dict[str, bytes]):
        def open_url(request, timeout):  # noqa: ARG001 - urlopen's signature
            self.requests.append(request.full_url)
            return io.BytesIO(served[request.full_url.removeprefix("https://example.test/")])
        return open_url

    def test_the_desktops_pinned_files_in_the_desktops_layout(self) -> None:
        self.assertEqual(["wespeaker_en_voxceleb_resnet34_LM.onnx", "pyannote-segmentation-3-0/model.onnx",
                          "pyannote-segmentation-3-0/LICENSE"], [f.path for f in host.MODEL_FILES])
        for file in host.MODEL_FILES:
            self.assertRegex(file.sha256, "^[0-9a-f]{64}$")
            self.assertTrue(file.url.startswith("https://"))
        self.assertIn("/resolve/9403a6902bb58e3d5ae8c7e77c3422de279db2e0/", host.SEGMENTATION_MODEL.url)

    def test_downloads_once_and_checks_every_file(self) -> None:
        with mock.patch.object(host, "MODEL_FILES", self.files):
            host.fetch_models(self.root, self.opener(self.content), report=lambda _: None, pause=lambda _: None)
            self.assertEqual(3, len(self.requests))
            self.assertEqual(b"turns", (self.root / "pyannote-segmentation-3-0" / "model.onnx").read_bytes())
            host.check_models(self.root)
            host.fetch_models(self.root, self.opener(self.content), report=lambda _: None, pause=lambda _: None)
            self.assertEqual(3, len(self.requests))
            (self.root / "pyannote-segmentation-3-0" / "LICENSE").write_bytes(b"MIX")
            with self.assertRaises(SystemExit):
                host.check_models(self.root)

    def test_a_changed_file_is_never_kept(self) -> None:
        served = dict(self.content, **{"pyannote-segmentation-3-0/model.onnx": b"turnz"})
        with mock.patch.object(host, "MODEL_FILES", self.files), self.assertRaises(SystemExit) as stopped:
            host.fetch_models(self.root, self.opener(served), report=lambda _: None, pause=lambda _: None)
        self.assertIn("SHA-256", str(stopped.exception))
        self.assertFalse((self.root / "pyannote-segmentation-3-0" / "model.onnx").exists())
        self.assertFalse((self.root / "pyannote-segmentation-3-0" / "model.onnx.part").exists())


class ServiceTests(unittest.TestCase):
    def test_runs_on_cuda_only_when_it_works_here(self) -> None:
        loaded: list[str] = []

        def loader(models, provider, count):
            loaded.append(provider)
            return FakeEngine()

        for accelerator, works, expected in (("gpu", True, "cuda"), ("gpu", False, "cpu"), ("cpu", True, "cpu")):
            service = host.Service(accelerator=accelerator, provider="cuda" if accelerator == "gpu" else "cpu")
            self.assertEqual("loading", service.status()["state"])
            with mock.patch.object(host, "say"):
                service.load(Path("."), loader=loader, probe=lambda: (works, "no device"), checker=lambda _: None)
            self.assertEqual(("ready", expected), (service.status()["state"], service.status()["provider"]))
        self.assertEqual(["cuda", "cpu", "cpu"], loaded)

    def test_a_cuda_load_failure_falls_back_to_the_processor(self) -> None:
        def loader(models, provider, count):
            if provider == "cuda":
                raise RuntimeError("CUDA failure 100")
            return FakeEngine()

        service = host.Service(accelerator="gpu", provider="cuda")
        with mock.patch.object(host, "say"):
            service.load(Path("."), loader=loader, probe=lambda: (True, ""), checker=lambda _: None)
        self.assertEqual("cpu", service.status()["provider"])
        self.assertTrue(service.ready)

    def test_status_names_the_model(self) -> None:
        status = host.Service(FakeEngine(), thread_count=4).status()
        self.assertEqual({"state": "ready", "provider": "cpu", "model": "wespeaker-en-voxceleb-resnet34-lm"},
                         {key: status[key] for key in ("state", "provider", "model")})

    def test_waits_until_ready(self) -> None:
        answers = iter([OSError("refused"), {"state": "loading"}, {"state": "ready", "provider": "cuda"}])

        def opener(url, timeout):  # noqa: ARG001
            item = next(answers)
            if isinstance(item, Exception):
                raise item
            return io.BytesIO(json.dumps(item).encode())

        with mock.patch.object(host, "say"):
            self.assertTrue(host.wait_ready(port=1, minutes=1, opener=opener, pause=lambda _: None))
            self.assertFalse(host.wait_ready(port=1, minutes=0, opener=lambda url, timeout: io.BytesIO(b'{"state":"loading"}'),
                                             pause=lambda _: None))


class HttpTests(unittest.TestCase):
    def setUp(self) -> None:
        self.engine = FakeEngine(turns=[], vector=VOICE_B)
        self.service = host.Service(self.engine, thread_count=4, runtime={"sherpa-onnx": "1.13.8"})
        self.server = host.serve(self.service, port=0)
        self.port = self.server.server_address[1]
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def tearDown(self) -> None:
        self.server.shutdown()
        self.server.server_close()

    def request(self, method: str, path: str, body: bytes | None = None, content_type: str = "application/octet-stream"):
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=10)
        try:
            connection.request(method, path, body=body, headers={"Content-Type": content_type} if body is not None else {})
            response = connection.getresponse()
            return response.status, json.loads(response.read())
        finally:
            connection.close()

    def test_listens_only_on_loopback(self) -> None:
        self.assertEqual("127.0.0.1", self.server.server_address[0])

    def test_status(self) -> None:
        status, payload = self.request("GET", "/status")
        self.assertEqual((200, "ready", "cpu", "wespeaker-en-voxceleb-resnet34-lm"),
                         (status, payload["state"], payload["provider"], payload["model"]))

    def test_analyzes_the_relays_audio(self) -> None:
        status, payload = self.request("POST", "/analyze", bytes(2 * int(1.5 * RATE)))
        self.assertEqual(200, status)
        self.assertEqual({"speakers", "voices", "overlap", "whole", "speech_seconds", "milliseconds"}, set(payload))
        self.assertEqual(([], 0, False, 0.0), (payload["speakers"], payload["voices"], payload["overlap"], payload["speech_seconds"]))
        self.assertEqual(host.DIMENSION, len(payload["whole"]))
        self.assertAlmostEqual(1.0, math.sqrt(sum(x * x for x in payload["whole"])), places=5)
        self.assertEqual([int(1.5 * RATE)], self.engine.calls)

    def test_analysis_answers_each_heard_voice(self) -> None:
        self.engine.turns = [(0.0, 3.0, 0)]
        status, payload = self.request("POST", "/analyze", bytes(2 * 3 * RATE))
        self.assertEqual(200, status)
        voice = payload["speakers"][0]
        self.assertEqual({"local", "voiceprint", "clean_seconds", "start", "end"}, set(voice))
        self.assertEqual((0, 2.76, 0.12, 2.88), (voice["local"], voice["clean_seconds"], voice["start"], voice["end"]))
        self.assertEqual(host.json_vector(VOICE_B), voice["voiceprint"])
        self.assertIsNone(payload["whole"])

    def test_voiceprint(self) -> None:
        status, payload = self.request("POST", "/voiceprint", bytes(2 * RATE))
        self.assertEqual((200, host.json_vector(VOICE_B)), (status, payload["voiceprint"]))
        status, payload = self.request("POST", "/voiceprint", bytes(2 * RATE - 2))
        self.assertEqual((200, None), (status, payload["voiceprint"]))

    def test_refuses_what_is_not_audio(self) -> None:
        self.assertEqual(400, self.request("POST", "/analyze", b"{}", "application/json")[0])
        self.assertEqual(400, self.request("POST", "/analyze", b"\x00\x00\x00")[0])
        self.assertEqual(404, self.request("POST", "/load", b"\x00\x00")[0])
        self.assertEqual(404, self.request("GET", "/")[0])
        # More than a minute is refused from its Content-Length, before the body is read.
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=10)
        try:
            connection.putrequest("POST", "/analyze")
            connection.putheader("Content-Type", "application/octet-stream")
            connection.putheader("Content-Length", str(host.MAX_PCM_BYTES + 2))
            connection.endheaders()
            self.assertEqual(400, connection.getresponse().status)
        finally:
            connection.close()
        self.assertEqual([], self.engine.calls)

    def test_says_when_it_is_still_loading(self) -> None:
        self.service.engine = None
        self.assertEqual((503, {"error": "worker.loading"}), self.request("POST", "/analyze", b"\x00\x00"))
        self.assertEqual("loading", self.request("GET", "/status")[1]["state"])


if __name__ == "__main__":
    unittest.main()
