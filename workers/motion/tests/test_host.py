"""Tests for the motion host service: request checks, one motion at a time, freeing, the HTTP answers, pinned files and the
adapter from HY-Motion's output to the VRMA converter (the real martlet_motion.vrma). HY-Motion itself and torch are not
needed: a fake runtime stands in for the model (FIXTURE - NOT AI)."""

from __future__ import annotations

import hashlib
import json
import struct
import sys
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer
from pathlib import Path
from unittest import mock

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from martlet_motion import host, pins  # noqa: E402
from martlet_motion.generator import MotionError, Runtime, to_smplh, to_vrma  # noqa: E402


def hymotion_output(frames: int) -> dict:
    """HY-Motion's dict output shape for one seed: rot6d (1, F, 22, 6) at the identity, transl (1, F, 3) standing still."""
    rot6d = np.tile(np.array([1.0, 0.0, 0.0, 0.0, 1.0, 0.0]), (1, frames, 22, 1))
    transl = np.tile(np.array([0.0, 1.16, 0.0]), (1, frames, 1))
    return {"rot6d": rot6d, "transl": transl}


def construct(rot6d, transl):
    """Like HY-Motion's construct_smpl_data_dict for identity rotations: 52 joints of zero axis-angle, transl unchanged."""
    frames = rot6d.shape[0]
    return {"poses": np.zeros((frames, 156)), "trans": np.asarray(transl), "mocap_framerate": 30}


def json_chunk(glb: bytes) -> dict:
    length = struct.unpack_from("<I", glb, 12)[0]
    return json.loads(glb[20:20 + length])


class FakeRuntime:
    """Stands in for HY-Motion: loads on the first motion, can be made to fail."""

    def __init__(self, missing=None, fail=None):
        self.model = pins.LITE
        self.loaded = False
        self.load_milliseconds = 1234
        self.calls = []
        self._missing = missing or []
        self.fail = fail
        self.entered = threading.Event()
        self.release = threading.Event()
        self.release.set()

    def missing(self):
        return list(self._missing)

    def generate(self, prompt, seconds, seed):
        self.entered.set()
        self.release.wait(5)
        if self.fail:
            raise self.fail
        self.loaded = True
        self.calls.append((prompt, seconds, seed))
        return hymotion_output(round(seconds * 30))

    def free(self):
        was, self.loaded = self.loaded, False
        return was

    def gpu(self):
        return None


def service(runtime=None, idle=5.0, clock=None):
    return host.Service(runtime or FakeRuntime(), idle, make_vrma=lambda output: to_vrma(output, construct),
                        clock=clock or (lambda: 0.0))


class AdapterTests(unittest.TestCase):
    def test_hymotion_output_becomes_converter_input_and_a_vrma_clip(self):
        poses, trans = to_smplh(hymotion_output(60), construct)
        self.assertEqual(poses.shape, (60, 52, 3))
        self.assertEqual(trans.shape, (60, 3))
        self.assertAlmostEqual(trans[0, 1], 1.16)
        clip, frames = to_vrma(hymotion_output(60), construct)
        self.assertEqual(frames, 60)
        self.assertEqual(clip[:4], b"glTF")
        document = json_chunk(clip)
        self.assertIn("VRMC_vrm_animation", document["extensionsUsed"])
        self.assertEqual(document["extensions"]["VRMC_vrm_animation"]["specVersion"], "1.0")
        self.assertGreater(len(document["animations"][0]["channels"]), 20)

    def test_an_unexpected_shape_is_a_failure_not_a_clip(self):
        with self.assertRaises(MotionError) as caught:
            to_smplh(hymotion_output(10), lambda r, t: {"poses": np.zeros((10, 66)), "trans": np.asarray(t)})
        self.assertEqual(caught.exception.code, "motion.failed")


class RequestTests(unittest.TestCase):
    def test_a_good_request_and_its_defaults(self):
        self.assertEqual(host.parse_request({"prompt": " A person waves. ", "seconds": 3, "seed": 9}), ("A person waves.", 3.0, 9))
        prompt, seconds, seed = host.parse_request({"prompt": "A person jumps."})
        self.assertEqual((prompt, seconds), ("A person jumps.", 4.0))
        self.assertTrue(0 <= seed < 2**31)

    def test_bad_requests_are_refused(self):
        for body in ({}, [], {"prompt": "hi"}, {"prompt": "A person waves.", "extra": 1}, {"prompt": "12345"},
                     {"prompt": "A person\nwaves."}, {"prompt": "word " * 61}, {"prompt": "x" * 401},
                     {"prompt": "A person waves.", "seconds": 0.5}, {"prompt": "A person waves.", "seconds": 13},
                     {"prompt": "A person waves.", "seconds": True}, {"prompt": "A person waves.", "seconds": float("nan")},
                     {"prompt": "A person waves.", "seed": -1}, {"prompt": "A person waves.", "seed": 2**31},
                     {"prompt": "A person waves.", "seed": True}, {"prompt": "A person waves.", "seed": 1.5}):
            with self.subTest(body=body), self.assertRaises(MotionError) as caught:
                host.parse_request(body)
            self.assertEqual((caught.exception.code, caught.exception.status), ("request.invalid", 400))


class ServiceTests(unittest.TestCase):
    def test_generate_returns_the_clip_and_its_facts(self):
        runtime = FakeRuntime()
        clip, headers = service(runtime).generate({"prompt": "A person waves with the right hand.", "seconds": 2, "seed": 7})
        self.assertEqual(clip[:4], b"glTF")
        self.assertEqual(headers["X-Martlet-Motion-Frames"], "60")
        self.assertEqual(headers["X-Martlet-Motion-Fps"], "30")
        self.assertEqual(headers["X-Martlet-Motion-Seed"], "7")
        self.assertEqual(headers["X-Martlet-Motion-Load-Milliseconds"], "1234")
        self.assertEqual(runtime.calls, [("A person waves with the right hand.", 2.0, 7)])

    def test_one_motion_at_a_time_and_no_free_while_making_one(self):
        runtime = FakeRuntime()
        runtime.release.clear()
        motions = service(runtime)
        worker = threading.Thread(target=motions.generate, args=({"prompt": "A person walks forward.", "seconds": 1},))
        worker.start()
        self.assertTrue(runtime.entered.wait(5))
        for call in (lambda: motions.generate({"prompt": "A person sits down."}), motions.free):
            with self.assertRaises(MotionError) as caught:
                call()
            self.assertEqual((caught.exception.code, caught.exception.status), ("motion.busy", 409))
        self.assertTrue(motions.status()["busy"])
        runtime.release.set()
        worker.join(5)
        self.assertFalse(motions.status()["busy"])

    def test_free_and_idle_unload(self):
        now = [0.0]
        runtime = FakeRuntime()
        motions = service(runtime, idle=5, clock=lambda: now[0])
        self.assertEqual(motions.free(), {"state": "freed", "was_loaded": False})
        motions.generate({"prompt": "A person bows.", "seconds": 1})
        self.assertTrue(motions.status()["loaded"])
        now[0] = 4 * 60
        self.assertFalse(motions.idle_check())
        now[0] = 5 * 60 + 1
        self.assertTrue(motions.idle_check())
        self.assertFalse(runtime.loaded)
        self.assertEqual(motions.free(), {"state": "freed", "was_loaded": False})

    def test_status_says_what_is_missing(self):
        status = service(FakeRuntime(missing=["tencent/HY-Motion-1.0-Lite/latest.ckpt"])).status()
        self.assertEqual(status["state"], "not_provisioned")
        self.assertEqual(status["missing"], ["tencent/HY-Motion-1.0-Lite/latest.ckpt"])
        ready = service().status()
        self.assertEqual((ready["state"], ready["engine"], ready["model"], ready["fps"]), ("ready", "hy-motion", pins.LITE, 30))
        self.assertEqual((ready["minimum_seconds"], ready["maximum_seconds"]), (1.0, 12.0))
        self.assertNotIn("fixture", ready)

    def test_random_weights_load_without_files_and_say_they_are_a_fixture(self):
        class Inner(FakeRuntime):
            def load(self, weights=True, config_dir=None):
                self.loads.append((weights, config_dir))
                self.loaded = True

            def generate(self, prompt, seconds, seed, steps=None):
                self.steps = steps
                return hymotion_output(round(seconds * 30))

        inner = Inner(missing=["everything"])
        inner.loads = []
        fixture = host.RandomWeights(inner, 3)
        fixture.config_dir = Path("config")
        motions = service(fixture)
        self.assertEqual((motions.status()["state"], motions.status()["fixture"]), ("ready", "random weights - NOT AI"))
        clip, headers = motions.generate({"prompt": "A person waves.", "seconds": 1})
        self.assertEqual((clip[:4], headers["X-Martlet-Motion-Frames"], inner.steps), (b"glTF", "30", 3))
        self.assertEqual(inner.loads, [(False, Path("config"))])
        self.assertEqual(motions.free(), {"state": "freed", "was_loaded": True})


class HttpTests(unittest.TestCase):
    def setUp(self):
        self.runtime = FakeRuntime()
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), host.make_handler(service(self.runtime)))
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.base = f"http://127.0.0.1:{self.server.server_address[1]}/"

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()

    def call(self, path, body=None):
        data = None if body is None else json.dumps(body).encode()
        request = urllib.request.Request(self.base + path, data=data, method="GET" if body is None else "POST",
                                         headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=10) as response:
                return response.status, response.headers, response.read()
        except urllib.error.HTTPError as error:
            return error.code, error.headers, error.read()

    def test_status_generate_free_and_errors(self):
        status, headers, body = self.call("status")
        self.assertEqual((status, headers["Content-Type"]), (200, "application/json"))
        self.assertEqual(json.loads(body)["state"], "ready")
        status, headers, body = self.call("generate", {"prompt": "A person jumps twice.", "seconds": 1.5, "seed": 3})
        self.assertEqual((status, headers["Content-Type"]), (200, "model/gltf-binary"))
        self.assertEqual(body[:4], b"glTF")
        self.assertEqual(headers["X-Martlet-Motion-Frames"], "45")
        status, _, body = self.call("generate", {"prompt": "no"})
        self.assertEqual((status, json.loads(body)["error"]["code"]), (400, "request.invalid"))
        status, _, body = self.call("free", {})
        self.assertEqual((status, json.loads(body)), (200, {"state": "freed", "was_loaded": True}))
        status, _, _ = self.call("elsewhere", {})
        self.assertEqual(status, 404)

    def test_failures_answer_their_code(self):
        self.runtime.fail = MotionError("motion.out_of_memory", "The graphics card ran out of memory.", 507)
        status, _, body = self.call("generate", {"prompt": "A person runs.", "seconds": 2})
        self.assertEqual((status, json.loads(body)["error"]["code"]), (507, "motion.out_of_memory"))
        self.runtime.fail = RuntimeError("boom")
        status, _, body = self.call("generate", {"prompt": "A person runs.", "seconds": 2})
        self.assertEqual((status, json.loads(body)["error"]["code"]), (500, "motion.failed"))
        status, _, _ = self.call("status")
        self.assertEqual(status, 200)


class PinTests(unittest.TestCase):
    def test_every_model_file_is_pinned(self):
        for model in pins.models():
            files = pins.files(model)
            self.assertEqual(len({f.local for f in files}), len(files))
            for pinned in files:
                self.assertRegex(pinned.sha256, "^[0-9a-f]{64}$")
                self.assertRegex(pinned.revision, "^[0-9a-f]{40}$")
                self.assertGreater(pinned.size, 0)
                self.assertTrue(pinned.url.startswith("https://huggingface.co/") and pinned.revision in pinned.url)
            self.assertTrue(pins.checkpoint(model).path.endswith("latest.ckpt"))
            self.assertIn(pins.MODEL_DIRECTORIES[model] + "/latest.ckpt", {f.local for f in files})
        self.assertGreater(pins.total_bytes(pins.STANDARD), pins.total_bytes(pins.LITE))
        self.assertRegex(pins.CODE_COMMIT, "^[0-9a-f]{40}$")
        for asset in pins.CODE_ASSETS:
            self.assertRegex(asset.sha256, "^[0-9a-f]{64}$")
            self.assertIn(pins.CODE_COMMIT, asset.url)
        with self.assertRaises(ValueError):
            pins.files("hy-motion-2")

    def test_the_gateway_pins_the_same_checkpoints(self):
        relay = (Path(__file__).resolve().parents[3] / "src" / "Martlet.Gateway.Motion" / "MotionRelayWorker.cs").read_text("utf-8")
        for model in pins.models():
            self.assertIn(f'"{pins.checkpoint(model).sha256}"', relay)
        self.assertIn(f'"{pins.MODEL_REVISION}"', relay)

    def test_downloads_are_checked_by_size_and_sha256(self):
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder) / "source.bin"
            source.write_bytes(b"motion" * 100)
            digest = hashlib.sha256(source.read_bytes()).hexdigest()
            target = Path(folder) / "models" / "file.bin"
            host._download(source.as_uri(), target, 600, digest)
            self.assertEqual(target.read_bytes(), source.read_bytes())
            host._download(source.as_uri(), target, 600, digest)
            with self.assertRaises(SystemExit):
                host._download(source.as_uri(), Path(folder) / "bad.bin", 600, "0" * 64)
            self.assertFalse((Path(folder) / "bad.bin").exists())
            self.assertFalse((Path(folder) / "bad.bin.partial").exists())

    def test_runtime_lists_missing_files_by_size(self):
        small = pins.PinnedFile("r", "0" * 40, "a.bin", 3, "0" * 64, "MIT", "a/a.bin")
        asset = pins.CodeAsset("stats/Mean.npy", 2, "0" * 64)
        with tempfile.TemporaryDirectory() as folder, \
                mock.patch.object(pins, "MODEL_FILES", {pins.LITE: (small,)}), mock.patch.object(pins, "SHARED_FILES", ()), \
                mock.patch.object(pins, "CODE_ASSETS", (asset,)):
            runtime = Runtime(pins.LITE, Path(folder) / "code", Path(folder) / "ckpts")
            self.assertEqual(runtime.missing(), ["a/a.bin", "stats/Mean.npy"])
            (Path(folder) / "ckpts" / "a").mkdir(parents=True)
            (Path(folder) / "ckpts" / "a" / "a.bin").write_bytes(b"abc")
            (Path(folder) / "code" / "stats").mkdir(parents=True)
            (Path(folder) / "code" / "stats" / "Mean.npy").write_bytes(b"x")
            self.assertEqual(runtime.missing(), ["stats/Mean.npy"])
            with self.assertRaises(MotionError) as caught:
                runtime.load()
            self.assertEqual(caught.exception.code, "motion.unavailable")
        with self.assertRaises(MotionError):
            Runtime("hy-motion-2", Path("."), Path("."))


if __name__ == "__main__":
    unittest.main()
