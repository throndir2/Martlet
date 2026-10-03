"""Plumbing check of the XTTS host service with the FIXTURE - NOT AI tone engine: no model, no GPU, no network.

Run: python -m unittest discover -s workers/xtts/tests
"""

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

HOST = Path(__file__).resolve().parents[1] / "host" / "martlet_xtts_host.py"


def reference_wav() -> bytes:
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(24_000)
        output.writeframes(b"\x01\x00" * 24_000)
    return buffer.getvalue()


def request(text_chunks: list[str]) -> dict:
    audio = reference_wav()
    return {
        "ids": {"session_id": str(uuid.uuid4()), "turn_id": str(uuid.uuid4()), "request_id": str(uuid.uuid4()), "parent_request_id": None},
        "deadline_utc": (datetime.now(timezone.utc) + timedelta(seconds=30)).isoformat().replace("+00:00", "Z"),
        "reference": {
            "preset_id": str(uuid.uuid4()),
            "reference_revision": "sha256:" + "a" * 64,
            "audio_sha256": hashlib.sha256(audio).hexdigest(),
            "transcript": "unused by XTTS",
            "transcript_revision": "sha256:" + "b" * 64,
            "audio_base64": base64.b64encode(audio).decode("ascii"),
        },
        "chunks": [{"index": i, "chunk_id": f"c{i}", "text": text} for i, text in enumerate(text_chunks)],
    }


def post(port: int, path: str, body: dict, timeout: float = 60) -> list[dict]:
    data = json.dumps(body).encode()
    call = urllib.request.Request(f"http://127.0.0.1:{port}{path}", data=data, method="POST",
                                  headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(call, timeout=timeout) as response:
        return [json.loads(line) for line in response.read().splitlines() if line]


class FixtureServiceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.root = tempfile.mkdtemp()
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", 0))
            cls.port = probe.getsockname()[1]
        env = {**os.environ, "MARTLET_XTTS_ROOT": cls.root, "MARTLET_XTTS_PORT": str(cls.port)}
        subprocess.run([sys.executable, str(HOST), "provision", "--fixture"], env=env, check=True, capture_output=True)
        cls.service = subprocess.Popen([sys.executable, str(HOST), "serve"], env=env, stdout=subprocess.DEVNULL)
        for _ in range(100):
            try:
                urllib.request.urlopen(f"http://127.0.0.1:{cls.port}/status", timeout=1).close()
                break
            except OSError:
                time.sleep(0.1)
        subprocess.run([sys.executable, str(HOST), "warm"], env=env, check=True, capture_output=True)

    @classmethod
    def tearDownClass(cls) -> None:
        cls.service.kill()
        cls.service.wait()

    def test_streams_contiguous_bounded_frames(self) -> None:
        events = post(self.port, "/synthesize", request(["Hello there.", "A second chunk of text."]))
        kinds = [event["kind"] for event in events]
        self.assertEqual(kinds[0], "started")
        self.assertEqual(kinds[-1], "completed")
        offset = 0
        frames = [event for event in events if event["kind"] == "audio_frame"]
        for number, event in enumerate(frames):
            frame = event["frame"]
            self.assertEqual(frame["sequence"], number)
            self.assertEqual(frame["sample_offset"], offset)
            self.assertLessEqual(frame["sample_count"], 4_800)
            self.assertEqual(len(base64.b64decode(frame["data_base64"])), frame["sample_count"] * 2)
            offset += frame["sample_count"]
        completions = [event for event in events if event["kind"] == "chunk_completed"]
        self.assertEqual([event["chunk_index"] for event in completions], [0, 1])
        self.assertEqual(events[-1]["final_sample_count"], offset)
        self.assertEqual([event["sequence"] for event in events], list(range(len(events))))
        status = json.loads(urllib.request.urlopen(f"http://127.0.0.1:{self.port}/status").read())
        self.assertEqual(status["last_reply"]["outcome"], "completed")

    def test_restarts_a_dead_worker_for_the_next_reply(self) -> None:
        status = json.loads(urllib.request.urlopen(f"http://127.0.0.1:{self.port}/status").read())
        self.assertTrue(status["ready"])
        # Kill the model process the service owns; the next reply must restart it rather than fail.
        if sys.platform == "win32":
            subprocess.run(["powershell", "-NoProfile", "-Command",
                            f"Get-CimInstance Win32_Process -Filter 'ParentProcessId={self.service.pid}' | "
                            "ForEach-Object { Stop-Process -Id $_.ProcessId -Force }"], check=False)
        else:
            subprocess.run(["pkill", "-P", str(self.service.pid)], check=False)
        time.sleep(0.5)
        events = post(self.port, "/synthesize", request(["After a restart."]))
        self.assertEqual(events[-1]["kind"], "completed")

    def test_rejects_a_reference_that_does_not_match_its_digest(self) -> None:
        body = request(["x"])
        body["reference"]["audio_sha256"] = "0" * 64
        with self.assertRaises(urllib.error.HTTPError) as error:
            post(self.port, "/synthesize", body)
        self.assertEqual(error.exception.code, 400)


if __name__ == "__main__":
    unittest.main()
