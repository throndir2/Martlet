"""Plumbing checks for the Dia host service and worker with the FIXTURE - NOT AI engine (no model, no GPU)."""

from __future__ import annotations

import base64
import hashlib
import io
import json
import os
import socket
import subprocess
import sys
import tempfile
import time
import unittest
import urllib.error
import urllib.request
import uuid
import wave
from datetime import datetime, timedelta, timezone
from pathlib import Path

from martlet_dia import text

WORKERS = Path(__file__).resolve().parents[1]


def reference_wav(seconds: float = 2.0, rate: int = 24_000) -> bytes:
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as writer:
        writer.setnchannels(1)
        writer.setsampwidth(2)
        writer.setframerate(rate)
        writer.writeframes(b"\x10\x00\xf0\xff" * int(seconds * rate / 2))
    return buffer.getvalue()


class PromptTests(unittest.TestCase):
    def test_single_speaker_and_tags_unchanged(self) -> None:
        prompts = text.prompts("[S1] Hello there.", "[S2] Oh really? (laughs) [S1] Fine (sighs).", 6.0)
        self.assertEqual(prompts, [("[S1] Hello there. Oh really? (laughs) Fine (sighs).", "Oh really? (laughs) Fine (sighs).")])
        self.assertLess(text.max_tokens(6.0, prompts[0][1]), 3072)

    def test_long_reply_splits_at_sentences(self) -> None:
        reply = " ".join(f"Sentence number {i} is here." for i in range(30))
        prompts = text.prompts("Ref.", reply, 8.0)
        self.assertGreater(len(prompts), 1)
        self.assertEqual(" ".join(piece for _, piece in prompts), reply)
        self.assertTrue(all(len(p.encode()) < text.MAX_TEXT_BYTES and p.endswith(piece) for p, piece in prompts))

    def test_too_long_reference_is_refused(self) -> None:
        with self.assertRaises(text.PromptError):
            text.prompts("x", "hello", 34.0)


class HostTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.root = tempfile.TemporaryDirectory()
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", 0))
            cls.port = probe.getsockname()[1]
        cls.env = {**os.environ, "MARTLET_DIA_ROOT": cls.root.name, "MARTLET_DIA_PORT": str(cls.port),
                   "PYTHONPATH": str(WORKERS)}
        subprocess.run([sys.executable, "-m", "martlet_dia.host", "provision", "--fixture"], env=cls.env, check=True,
                       capture_output=True)
        cls.server = subprocess.Popen([sys.executable, "-m", "martlet_dia.host", "serve"], env=cls.env,
                                      stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        for _ in range(100):
            try:
                if cls.status()["ready"]:
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
    def status(cls) -> dict:
        with urllib.request.urlopen(f"http://127.0.0.1:{cls.port}/status", timeout=5) as response:
            return json.loads(response.read())

    def synthesize(self, chunks: list[str]) -> list[dict]:
        audio = reference_wav()
        request_id = str(uuid.uuid4())
        body = {
            "ids": {"session_id": str(uuid.uuid4()), "turn_id": str(uuid.uuid4()), "request_id": request_id,
                    "parent_request_id": None},
            "deadline_utc": (datetime.now(timezone.utc) + timedelta(seconds=60)).strftime("%Y-%m-%dT%H:%M:%S.%fZ"),
            "reference": {"preset_id": str(uuid.uuid4()), "reference_revision": "rev-1",
                          "audio_sha256": hashlib.sha256(audio).hexdigest(), "transcript": "A short reference.",
                          "transcript_revision": "t-1", "audio_base64": base64.b64encode(audio).decode()},
            "chunks": [{"index": i, "chunk_id": f"segment-{i}", "text": t} for i, t in enumerate(chunks)],
        }
        request = urllib.request.Request(f"http://127.0.0.1:{self.port}/synthesize", data=json.dumps(body).encode(),
                                         headers={"Content-Type": "application/json"}, method="POST")
        with urllib.request.urlopen(request, timeout=60) as response:
            self.assertEqual(response.headers["Content-Type"], "application/x-ndjson")
            events = [json.loads(line) for line in response.read().splitlines()]
        self.assertTrue(all(e["ids"]["request_id"] == request_id and e["reference_revision"] == "rev-1" for e in events))
        return events

    def test_status_lists_nonverbal_tags(self) -> None:
        status = self.status()
        self.assertTrue(status["ready"])
        self.assertIn("(laughs)", status["nonverbal_tags"])
        self.assertEqual(status["worker"]["artifacts"][0]["role"], "model_weights")

    def test_contiguous_frames_and_completion(self) -> None:
        events = self.synthesize(["Hello there (laughs).", "Second part."])
        self.assertEqual(events[0]["kind"], "started")
        self.assertEqual(events[-1]["kind"], "completed")
        offset, sequence, completed = 0, 0, 0
        for event in events[1:-1]:
            if event["kind"] == "audio_frame":
                frame = event["frame"]
                self.assertEqual((frame["sequence"], frame["sample_offset"], frame["chunk_index"]), (sequence, offset, completed))
                self.assertLessEqual(frame["sample_count"], 4_800)
                self.assertEqual(len(base64.b64decode(frame["data_base64"])), frame["sample_count"] * 2)
                sequence += 1
                offset += frame["sample_count"]
            else:
                self.assertEqual((event["kind"], event["chunk_index"], event["final_sample_count"]),
                                 ("chunk_completed", completed, offset))
                completed += 1
        self.assertEqual((completed, events[-1]["final_sample_count"]), (2, offset))

    def test_dead_worker_restarts_for_next_reply(self) -> None:
        os.kill(self.status()["worker_pid"], 9)
        time.sleep(0.5)
        self.assertEqual(self.synthesize(["After a crash."])[-1]["kind"], "completed")


if __name__ == "__main__":
    unittest.main()
