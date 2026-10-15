"""Plumbing checks for the singing host service and worker with the FIXTURE - NOT AI engine (no model, no GPU)."""

from __future__ import annotations

import base64
import hashlib
import io
import json
import math
import os
import signal
import socket
import struct
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

    def music(self, **overrides) -> dict:
        body = {"kind": "music", "lyrics": LYRICS, "style": "acoustic pop", "duration_seconds": 15, "language": "en",
                "quality": "fast", "seed": 7}
        body.update(overrides)
        return self.post("/jobs", body)

    def match(self, frames: int, **overrides) -> dict:
        data = reference_wav()
        body = {"kind": "match", "voice_match": "soulx", "voice_id": "b" * 64, "input_frames": frames,
                "reference": {"audio_base64": base64.b64encode(data).decode(), "audio_sha256": hashlib.sha256(data).hexdigest()}}
        body.update(overrides)
        return self.post("/jobs", body)

    def separate(self, frames: int, channels: int, **overrides) -> dict:
        body = {"kind": "separate", "lyrics": LYRICS, "language": "en", "input_frames": frames, "input_channels": channels}
        body.update(overrides)
        return self.post("/jobs", body)

    def send(self, job_id: str, offset: int, pcm: bytes) -> dict:
        request = urllib.request.Request(f"http://127.0.0.1:{self.port}/jobs/{job_id}/input?offset={offset}", data=pcm,
                                         headers={"Content-Type": "application/octet-stream"}, method="POST")
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.loads(response.read())

    def track(self, job_id: str, track: str) -> tuple[bytes, int, int]:
        """A finished job's whole track, its channels and its total frames, fetched page by page."""
        data = b""
        offset = 0
        while True:
            with urllib.request.urlopen(f"http://127.0.0.1:{self.port}/jobs/{job_id}/tracks/{track}"
                                        f"?offset={offset}&frames=200000", timeout=30) as response:
                self.assertEqual(response.headers["X-Song-Sample-Rate"], "48000")
                channels = int(response.headers["X-Song-Channels"])
                total = int(response.headers["X-Song-Total-Frames"])
                page = response.read()
            if not page:
                return data, channels, total
            data += page
            offset += len(page) // (2 * channels)

    def test_status_lists_the_kinds(self) -> None:
        status = self.get("/status")
        self.assertEqual(status["kinds"], ["song", "music", "match", "separate"])
        self.assertEqual(status["music_models"], ["ace-step-v15", "ace-step-v15-xl"])
        self.assertEqual(self.post("/jobs", {"kind": "remix"})["error"]["code"], "request.invalid")

    def test_music_job_asks_for_a_music_model(self) -> None:
        self.assertEqual(self.music(model="ace-step-v2")["error"]["code"], "request.invalid")
        self.assertEqual(self.start(model=7)["error"]["code"], "request.invalid")
        xl = self.music(model="ace-step-v15-xl")
        standard = self.music(model=None)
        view = self.wait(xl["job_id"])
        self.assertEqual(view["state"], "completed")
        self.assertEqual(view["result"]["engine"]["model"], "ace-step-v15-xl")
        self.assertEqual(view["result"]["engine"]["generator"], "FIXTURE - NOT AI tone song")
        self.assertEqual(self.wait(standard["job_id"])["result"]["engine"]["model"], "ace-step-v15")

    def test_a_music_model_that_is_not_set_up_is_refused(self) -> None:
        # The running service reads its configuration for every request: drop XL from it, as "standard" provisions.
        config = Path(self.root.name) / "models" / "worker-config.json"
        original = config.read_bytes()
        try:
            changed = json.loads(original)
            changed["music_models"] = ["ace-step-v15"]
            config.write_text(json.dumps(changed), encoding="utf-8")
            self.assertEqual(self.get("/status")["music_models"], ["ace-step-v15"])
            refused = self.music(model="ace-step-v15-xl")
            self.assertEqual(refused["error"], {"code": "singing.model_unavailable",
                                                "summary": "ACE-Step 1.5 XL is not set up on this computer."})
            self.assertEqual(self.start(model="ace-step-v15-xl")["error"]["code"], "singing.model_unavailable")
            del changed["music_models"]
            config.write_text(json.dumps(changed), encoding="utf-8")
            self.assertEqual(self.get("/status")["music_models"], ["ace-step-v15"])
        finally:
            config.write_bytes(original)
        self.assertEqual(self.get("/status")["music_models"], ["ace-step-v15", "ace-step-v15-xl"])

    def test_separate_job_receives_a_stereo_song_and_returns_separated_tracks(self) -> None:
        frames = 96_000
        samples = []
        for n in range(frames):
            samples.append(int(8000 * math.sin(2 * math.pi * 220 * n / 48_000)))
            samples.append(int(-4000 * math.sin(2 * math.pi * 330 * n / 48_000)) + 7)
        pcm = struct.pack(f"<{2 * frames}h", *samples)
        self.assertEqual(self.separate(frames, 3)["error"]["code"], "request.invalid")
        self.assertEqual(self.separate(0, 2)["error"]["code"], "request.invalid")
        self.assertEqual(self.separate(frames, 2, lyrics="")["error"]["code"], "request.invalid")
        self.assertEqual(self.separate(frames, 2, bpm=500)["error"]["code"], "request.invalid")
        started = self.separate(frames, 2, bpm=90, key="A minor", voice_match="vevosing", model="nonsense")
        job_id = started["job_id"]
        self.assertEqual((started["kind"], started["state"], started["stage"]), ("separate", "receiving", "queued"))
        self.assertEqual((started["input_frames"], started["received_frames"], started["input_channels"]), (frames, 0, 2))
        self.assertEqual(self.send(job_id, 0, pcm[:6])["error"]["code"], "request.invalid")
        self.assertEqual(self.send(job_id, 0, pcm[:2])["error"]["code"], "request.invalid")
        self.assertEqual(self.send(job_id, 0, pcm + b"\0" * 4)["error"]["code"], "request.invalid")
        self.assertEqual(self.get(f"/jobs/{job_id}")["received_frames"], 0)
        first = self.send(job_id, 0, pcm[:4 * 40_000])
        self.assertEqual((first["state"], first["received_frames"]), ("receiving", 40_000))
        self.assertEqual(self.send(job_id, 0, pcm[4 * 40_000:])["error"]["code"], "request.invalid")
        second = self.send(job_id, 40_000, pcm[4 * 40_000:])
        self.assertEqual(second["received_frames"], frames)
        self.assertIn(second["state"], ("queued", "running", "completed"))
        view = self.wait(job_id)
        self.assertEqual(view["state"], "completed")
        self.assertEqual(view["kind"], "separate")
        result = view["result"]
        self.assertEqual(result["engine"], {"generator": None, "separator": "FIXTURE - NOT AI", "converter": None,
                                            "quality": None, "voice_match": None, "model": None, "fixture": True})
        self.assertEqual(result["frames"], frames)
        self.assertEqual((result["bpm"], result["key"], result["beats_per_bar"]), (90, "A minor", 4))
        self.assertEqual(result["beats"][:2], [0.0, round(60 / 90, 6)])
        self.assertEqual([line["section"] for line in result["lyrics"]], [s for s, _ in audio.lyric_lines(LYRICS)])
        self.assertLessEqual(result["lyrics"][-1]["end"], 0.9 * frames / 48_000)
        self.assertTrue(all(0 <= w["line"] < len(result["lyrics"]) and w["end"] > w["start"] for w in result["words"]))
        self.assertNotIn("matching_voice", [t["stage"] for t in result["timings"]])
        tracks = {}
        for track, channels in audio.TRACKS.items():
            data, sent_channels, total = self.track(job_id, track)
            self.assertEqual((sent_channels, total, len(data) // (2 * channels)), (channels, frames, frames))
            tracks[track] = struct.unpack(f"<{frames * channels}h", data)
        self.assertEqual(tracks["mix"], tuple(samples))
        for n in range(0, frames, 997):
            for channel in (0, 1):
                self.assertLessEqual(abs(tracks["vocals"][n] + tracks["backing"][2 * n + channel]
                                         - tracks["mix"][2 * n + channel]), 1)
        self.assertEqual(tracks["vocals"][1000], round((samples[2000] + samples[2001]) / 4))

    def test_separate_job_doubles_a_mono_song(self) -> None:
        frames = 24_000
        tone = [int(6000 * math.sin(2 * math.pi * 440 * n / 48_000)) for n in range(frames)]
        started = self.separate(frames, 1, lyrics="Only one line here\n")
        self.assertEqual(self.send(started["job_id"], 0, struct.pack(f"<{frames}h", *tone))["received_frames"], frames)
        view = self.wait(started["job_id"])
        self.assertEqual(view["state"], "completed")
        self.assertEqual(len(view["result"]["lyrics"]), 1)
        self.assertEqual(view["result"]["bpm"], 120)
        mix, channels, total = self.track(started["job_id"], "mix")
        self.assertEqual((channels, total), (2, frames))
        mix = struct.unpack(f"<{2 * frames}h", mix)
        self.assertEqual(mix[0::2], tuple(tone))
        self.assertEqual(mix[1::2], tuple(tone))

    def test_music_job_makes_separated_tracks_and_timing_without_a_voice(self) -> None:
        self.assertEqual(self.music(quality="best")["error"]["code"], "request.invalid")
        started = self.music(voice_match="nonsense")
        self.assertEqual(started["kind"], "music")
        self.assertNotIn("input_frames", started)
        view = self.wait(started["job_id"])
        self.assertEqual(view["state"], "completed")
        self.assertEqual(view["kind"], "music")
        result = view["result"]
        self.assertTrue(result["engine"]["fixture"])
        self.assertIsNone(result["engine"]["converter"])
        self.assertIsNone(result["engine"]["voice_match"])
        self.assertEqual(result["frames"], 15 * 48_000)
        self.assertEqual([line["section"] for line in result["lyrics"]], ["verse", "verse", "chorus"])
        self.assertTrue(result["words"])
        self.assertIn("total", [t["stage"] for t in result["timings"]])
        self.assertNotIn("matching_voice", [t["stage"] for t in result["timings"]])
        for track, channels in audio.TRACKS.items():
            data, sent_channels, total = self.track(started["job_id"], track)
            self.assertEqual((sent_channels, total, len(data) // (2 * channels)), (channels, 15 * 48_000, 15 * 48_000))

    def test_match_job_receives_vocals_in_pages_and_returns_only_vocals(self) -> None:
        frames = 96_000
        tone = [int(8000 * math.sin(2 * math.pi * 220 * n / 48_000)) if n < 48_000 or n >= 72_000 else 0
                for n in range(frames)]
        pcm = struct.pack(f"<{frames}h", *tone)
        self.assertEqual(self.match(0)["error"]["code"], "request.invalid")
        self.assertEqual(self.match(frames, voice_match="vevosing")["error"]["code"], "singing.voice_match_unavailable")
        self.assertEqual(self.match(frames, reference={"audio_base64": "", "audio_sha256": "0"})["error"]["code"],
                         "request.invalid")
        started = self.match(frames, lyrics=None)
        job_id = started["job_id"]
        self.assertEqual((started["kind"], started["state"], started["stage"], started["fraction"]),
                         ("match", "receiving", "queued", 0))
        self.assertEqual((started["input_frames"], started["received_frames"]), (frames, 0))
        self.assertNotIn("queue_position", started)
        self.assertEqual(self.send(job_id, 5, pcm[:1000])["error"]["code"], "request.invalid")
        self.assertEqual(self.send(job_id, 0, pcm[:3])["error"]["code"], "request.invalid")
        self.assertEqual(self.send(job_id, 0, pcm + b"\0\0")["error"]["code"], "request.invalid")
        self.assertEqual(self.get(f"/jobs/{job_id}")["received_frames"], 0)
        first = self.send(job_id, 0, pcm[:96_000])
        self.assertEqual((first["state"], first["received_frames"]), ("receiving", 48_000))
        self.assertEqual(self.send(job_id, 0, pcm[96_000:])["error"]["code"], "request.invalid")
        self.assertEqual(self.get(f"/jobs/{job_id}")["received_frames"], 48_000)
        second = self.send(job_id, 48_000, pcm[96_000:])
        self.assertEqual(second["received_frames"], frames)
        self.assertIn(second["state"], ("queued", "running", "completed"))
        self.assertEqual(self.send(job_id, frames, b"\0\0")["error"]["code"], "request.invalid")
        view = self.wait(job_id)
        self.assertEqual(view["state"], "completed")
        result = view["result"]
        self.assertEqual(result["frames"], frames)
        self.assertEqual(result["engine"]["converter"], "FIXTURE - NOT AI")
        self.assertEqual(result["engine"]["voice_match"], "soulx")
        self.assertIsNone(result["engine"]["generator"])
        data, channels, total = self.track(job_id, "vocals")
        self.assertEqual((channels, total, len(data) // 2), (1, frames, frames))
        vocals = struct.unpack(f"<{frames}h", data)
        self.assertEqual(vocals[1000], round(tone[1000] * 0.8))
        self.assertEqual(max(abs(v) for v in vocals[48_000:72_000]), 0)
        for track in ("mix", "backing"):
            refused = self.get(f"/jobs/{job_id}/tracks/{track}?offset=0&frames=1000")
            self.assertEqual(refused["error"], {"code": "request.invalid", "summary": "A voice match job has only vocals."})

    def test_receiving_jobs_count_as_waiting_and_can_be_canceled(self) -> None:
        receiving = [self.match(48_000)["job_id"] for _ in range(4)]
        self.assertEqual(self.start()["error"]["code"], "singing.busy")
        self.assertEqual(self.match(48_000)["error"]["code"], "singing.busy")
        for job_id in receiving:
            self.assertEqual(self.post(f"/jobs/{job_id}/cancel", {})["state"], "canceled")
        self.assertEqual(self.send(receiving[0], 0, b"\0\0" * 10)["error"]["code"], "request.invalid")
        song = self.start()["job_id"]
        self.assertEqual(self.send(song, 0, b"\0\0")["error"]["code"], "request.invalid")
        self.assertEqual(self.wait(song)["state"], "completed")
        self.assertEqual(self.get("/jobs/song-unknown")["state"], "unknown")

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
        self.assertEqual(view["kind"], "song")
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
