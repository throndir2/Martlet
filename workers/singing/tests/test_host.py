"""Plumbing checks for the singing host service and worker with the FIXTURE - NOT AI engine (no model, no GPU)."""

from __future__ import annotations

import base64
import hashlib
import io
import json
import os
import signal
import socket
import subprocess
import sys
import tempfile
import time
import unittest
import urllib.request
import wave
from pathlib import Path

from martlet_singing import audio

WORKERS = Path(__file__).resolve().parents[1]
LYRICS = "[verse]\nMorning light is on the window\nCoffee steaming by the door\n\n[chorus]\nSing it with me, sing it slowly\n"


def reference_wav(seconds: float = 2.0, rate: int = 24_000) -> bytes:
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as writer:
        writer.setnchannels(1)
        writer.setsampwidth(2)
        writer.setframerate(rate)
        writer.writeframes(b"\x10\x00\xf0\xff" * int(seconds * rate / 2))
    return buffer.getvalue()


class LyricTests(unittest.TestCase):
    def test_sections_are_numbered_like_the_desktop(self) -> None:
        lines = audio.lyric_lines("Intro words\n[Verse]\nA\n\n[chorus]\nB\n[verse]\nC\n[Chorus]\nD\n[bridge 1]\nE\n[]\n")
        self.assertEqual(lines, [("", "Intro words"), ("verse", "A"), ("chorus", "B"), ("verse 2", "C"), ("chorus 2", "D"),
                                 ("bridge 1", "E")])


class HostTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.root = tempfile.TemporaryDirectory()
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", 0))
            cls.port = probe.getsockname()[1]
        cls.env = {**os.environ, "MARTLET_SINGING_ROOT": cls.root.name, "MARTLET_SINGING_PORT": str(cls.port),
                   "MARTLET_SINGING_IDLE_SECONDS": "3", "MARTLET_SINGING_FIXTURE_STAGE_SECONDS": "0.3",
                   "PYTHONPATH": str(WORKERS)}
        subprocess.run([sys.executable, "-m", "martlet_singing.host", "provision", "--fixture"], env=cls.env, check=True,
                       capture_output=True)
        cls.server = subprocess.Popen([sys.executable, "-m", "martlet_singing.host", "serve"], env=cls.env,
                                      stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        for _ in range(100):
            try:
                if cls.get("/status")["ready"]:
                    break
            except OSError:
                pass
            time.sleep(0.1)

    @classmethod
    def tearDownClass(cls) -> None:
        cls.server.kill()
        cls.server.wait()
        cls.root.cleanup()

    @classmethod
    def get(cls, path: str) -> dict:
        with urllib.request.urlopen(f"http://127.0.0.1:{cls.port}{path}", timeout=30) as response:
            return json.loads(response.read())

    @classmethod
    def post(cls, path: str, body: dict) -> dict:
        request = urllib.request.Request(f"http://127.0.0.1:{cls.port}{path}", data=json.dumps(body).encode(),
                                         headers={"Content-Type": "application/json"}, method="POST")
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.loads(response.read())

    def start(self, **overrides) -> dict:
        data = reference_wav()
        body = {"request_id": "0" * 8, "lyrics": LYRICS, "style": "acoustic pop", "duration_seconds": 15, "language": "en",
                "quality": "fast", "voice_match": "soulx", "voice_id": "a" * 64, "seed": 7,
                "reference": {"audio_base64": base64.b64encode(data).decode(), "audio_sha256": hashlib.sha256(data).hexdigest()}}
        body.update(overrides)
        return self.post("/jobs", body)

    def wait(self, job_id: str, seconds: float = 60) -> dict:
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            view = self.get(f"/jobs/{job_id}")
            if view["state"] in ("completed", "failed", "canceled"):
                return view
            time.sleep(0.1)
        self.fail("the song did not finish")

    def test_song_runs_through_every_stage_and_pages_aligned_tracks(self) -> None:
        status = self.get("/status")
        self.assertEqual(status["engine"], "fixture")
        self.assertEqual(status["worker"]["evidence"], "synthetic_fixture")
        started = self.start()
        self.assertIn(started["state"], ("queued", "running"))
        seen = set()
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            view = self.get(f"/jobs/{started['job_id']}")
            seen.add(view["stage"])
            if view["state"] == "completed":
                break
            time.sleep(0.05)
        self.assertEqual(view["state"], "completed")
        self.assertTrue({"writing_music", "separating", "matching_voice", "mixing", "aligning"} & seen)
        result = view["result"]
        self.assertTrue(result["engine"]["fixture"])
        self.assertEqual(result["frames"], 15 * 48_000)
        self.assertEqual([line["section"] for line in result["lyrics"]], ["verse", "verse", "chorus"])
        self.assertEqual(result["word_timing_source"], "fixture")
        self.assertEqual([w["text"] for w in result["words"][:3]], ["Morning", "light", "is"])
        self.assertTrue(all(0 <= w["line"] < len(result["lyrics"]) and w["end"] > w["start"] for w in result["words"]))
        self.assertEqual(result["beats_per_bar"], 4)
        self.assertTrue(set(result["downbeats"]) <= set(result["beats"]))
        self.assertIn("total", [t["stage"] for t in result["timings"]])
        lengths = {}
        for track, channels in audio.TRACKS.items():
            data = b""
            offset = 0
            while True:
                with urllib.request.urlopen(f"http://127.0.0.1:{self.port}/jobs/{started['job_id']}/tracks/{track}"
                                            f"?offset={offset}&frames=200000", timeout=30) as response:
                    self.assertEqual(response.headers["X-Song-Sample-Rate"], "48000")
                    self.assertEqual(response.headers["X-Song-Channels"], str(channels))
                    page = response.read()
                if not page:
                    break
                data += page
                offset += len(page) // (2 * channels)
            lengths[track] = len(data) // (2 * channels)
        self.assertEqual(set(lengths.values()), {15 * 48_000})

    def test_refusals_cancel_queue_and_idle_release(self) -> None:
        self.assertEqual(self.start(voice_match="vevosing")["error"]["code"], "singing.voice_match_unavailable")
        self.assertEqual(self.start(duration_seconds=500)["error"]["code"], "request.invalid")
        bad = self.start()
        wrong = self.post("/jobs", {"lyrics": LYRICS, "style": "x", "duration_seconds": 15, "language": "en",
                                    "quality": "fast", "voice_match": "soulx", "voice_id": "a",
                                    "reference": {"audio_base64": base64.b64encode(b"nope").decode(), "audio_sha256": "0"}})
        self.assertEqual(wrong["error"]["code"], "request.invalid")
        second = self.start()
        canceled = self.post(f"/jobs/{second['job_id']}/cancel", {})
        self.assertIn(canceled["state"], ("canceled", "running", "queued"))
        self.assertEqual(self.wait(second["job_id"])["state"], "canceled")
        self.assertEqual(self.wait(bad["job_id"])["state"], "completed")
        self.assertEqual(self.get("/jobs/song-unknown")["state"], "unknown")
        # The worker exits after the idle period, releasing its memory; the next song starts it again.
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline and self.get("/status")["worker_running"]:
            time.sleep(0.2)
        self.assertFalse(self.get("/status")["worker_running"])
        self.assertEqual(self.wait(self.start()["job_id"])["state"], "completed")

    def test_a_killed_worker_fails_its_song_and_restarts(self) -> None:
        job = self.start()
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline and not self.get("/status")["worker_pid"]:
            time.sleep(0.05)
        pid = self.get("/status")["worker_pid"]
        os.kill(pid, signal.SIGTERM if os.name == "nt" else signal.SIGKILL)
        view = self.wait(job["job_id"])
        self.assertIn(view["state"], ("failed", "completed"))
        if view["state"] == "failed":
            self.assertIn("restarts", view["error"]["summary"])
        self.assertEqual(self.wait(self.start()["job_id"])["state"], "completed")


try:
    import librosa  # noqa: F401
    import numpy

    HAVE_AUDIO = True
except ImportError:
    HAVE_AUDIO = False


@unittest.skipUnless(HAVE_AUDIO, "numpy and librosa (the image has them)")
class TimingTests(unittest.TestCase):
    def test_grid_words_and_gate_on_a_click_track(self) -> None:
        from martlet_singing import timing

        rate = 48_000
        seconds = 20
        t = numpy.arange(seconds * rate) / rate
        beat = 60 / 96
        backing = numpy.zeros_like(t)
        for n in range(int(seconds / beat)):
            start = int((0.25 + n * beat) * rate)
            backing[start:start + 1200] += (1.0 if n % 4 == 0 else 0.4) * numpy.sin(2 * numpy.pi * 80 * t[:1200])
        grid = timing.beat_grid(backing, rate, 96.0, 4)
        self.assertAlmostEqual(grid["bpm"], 96.0, delta=1.0)
        self.assertAlmostEqual(grid["downbeats"][0] % (4 * beat), 0.25, delta=0.05)

        vocals = numpy.zeros_like(t)
        sung = [(2.0, 2.5), (2.6, 3.1), (3.2, 3.7)]
        for a, b in sung:
            vocals[int(a * rate):int(b * rate)] = 0.3 * numpy.sin(2 * numpy.pi * 220 * t[int(a * rate):int(b * rate)])
        noisy = vocals + 0.002 * numpy.sin(2 * numpy.pi * 997 * t)
        gated, bleed = timing.gate_vocals(noisy.astype(numpy.float32), vocals, rate)
        self.assertLess(numpy.abs(gated[int(10 * rate):]).max(), 1e-4)
        self.assertIsNotNone(bleed)
        lines = [("verse", "one two three")]
        timed = [{"start": 2.0, "end": 3.7, "text": "one two three", "section": "verse"}]
        sentences = [{"text": "one two three", "start": 1.95, "end": 3.7, "tokens": [
            {"text": "one", "start": 1.95, "end": 2.5}, {"text": " two", "start": 2.62, "end": 3.1},
            {"text": " thr", "start": 3.15, "end": 3.4}, {"text": "ee", "start": 3.4, "end": 3.7}]}]
        words, source, metric = timing.word_times(lines, sentences, timed, vocals.astype(numpy.float32), rate)
        self.assertEqual(source, "ace-step-alignment")
        self.assertEqual([w["text"] for w in words], ["one", "two", "three"])
        self.assertLess(abs(words[0]["start"] - 2.0), 0.06)
        self.assertLess(metric["median_onset_offset_ms"], 60)


if __name__ == "__main__":
    unittest.main()
