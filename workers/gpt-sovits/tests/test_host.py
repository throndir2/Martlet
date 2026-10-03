"""End-to-end checks of the GPT-SoVITS host front and worker with the FIXTURE - NOT AI engine (no model, no GPU).

Run from workers/gpt-sovits:  python -m unittest discover -s tests
"""

from __future__ import annotations

import base64
import hashlib
import json
import os
import socket
import struct
import subprocess
import sys
import tempfile
import time
import unittest
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parents[1]


def wav(seconds: float, rate: int = 24_000) -> bytes:
    samples = int(seconds * rate)
    data = b"".join(struct.pack("<h", 1000 if i % 50 < 25 else -1000) for i in range(samples))
    return b"RIFF" + struct.pack("<I", 36 + len(data)) + b"WAVEfmt " + struct.pack("<IHHIIHH", 16, 1, 1, rate, rate * 2, 2, 16) + \
        b"data" + struct.pack("<I", len(data)) + data


def body(audio: bytes, text: str = "Hello there. How are you today?", transcript: str = "A reference line.") -> dict:
    transcript_revision = hashlib.sha256(transcript.encode()).hexdigest()
    audio_sha = hashlib.sha256(audio).hexdigest()
    return {
        "ids": {"session_id": str(uuid.uuid4()), "turn_id": str(uuid.uuid4()), "request_id": str(uuid.uuid4()),
                "parent_request_id": None},
        "deadline_utc": (datetime.now(timezone.utc) + timedelta(seconds=60)).strftime("%Y-%m-%dT%H:%M:%S.%fZ"),
        "reference": {"preset_id": str(uuid.uuid4()), "audio_sha256": audio_sha, "transcript": transcript,
                      "transcript_revision": transcript_revision,
                      "reference_revision": hashlib.sha256(bytes.fromhex(audio_sha) + bytes.fromhex(transcript_revision)).hexdigest(),
                      "language": "en", "audio_base64": base64.b64encode(audio).decode()},
        "chunks": [{"index": 0, "chunk_id": "segment-0", "text": text}],
    }


class HostTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.root = tempfile.mkdtemp(prefix="martlet-gpt-sovits-")
        (Path(cls.root) / "GPT-SoVITS" / "GPT_SoVITS" / "pretrained_models").mkdir(parents=True)
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", 0))
            cls.port = probe.getsockname()[1]
        cls.env = {**os.environ, "PYTHONPATH": str(HERE), "MARTLET_GPT_SOVITS_ROOT": cls.root,
                   "MARTLET_GPT_SOVITS_PORT": str(cls.port),
                   "MARTLET_GPT_SOVITS_REFERENCES": str(Path(cls.root) / "references")}
        subprocess.run([sys.executable, "-m", "martlet_gpt_sovits.host", "provision", "--fixture"], env=cls.env, check=True,
                       capture_output=True)
        cls.server = subprocess.Popen([sys.executable, "-m", "martlet_gpt_sovits.host", "serve"], env=cls.env,
                                      stdout=subprocess.DEVNULL)
        for _ in range(100):
            try:
                cls.get("/status")
                break
            except OSError:
                time.sleep(0.1)

    @classmethod
    def tearDownClass(cls) -> None:
        cls.server.kill()
        cls.server.wait()

    @classmethod
    def get(cls, path: str) -> dict:
        with urllib.request.urlopen(f"http://127.0.0.1:{cls.port}{path}", timeout=10) as response:
            return json.loads(response.read())

    def post(self, path: str, value: dict) -> tuple[int, list[dict]]:
        request = urllib.request.Request(f"http://127.0.0.1:{self.port}{path}", data=json.dumps(value).encode(),
                                         headers={"Content-Type": "application/json"}, method="POST")
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                return response.status, [json.loads(line) for line in response.read().splitlines() if line.strip()]
        except urllib.error.HTTPError as error:
            return error.code, [json.loads(error.read() or b"{}")]

    def test_streams_contiguous_24khz_frames(self) -> None:
        status, _ = self.post("/warmup", {})
        self.assertEqual(200, status)
        self.assertEqual("synthetic_fixture", self.get("/status")["worker"]["evidence"])
        status, events = self.post("/synthesize", body(wav(5)))
        self.assertEqual(200, status)
        kinds = [event["kind"] for event in events]
        self.assertEqual("started", kinds[0])
        self.assertEqual(["chunk_completed", "completed"], kinds[-2:])
        offset = 0
        for sequence, event in enumerate(events):
            self.assertEqual(sequence, event["sequence"])
            if event["kind"] == "audio_frame":
                frame = event["frame"]
                self.assertEqual(offset, frame["sample_offset"])
                self.assertLessEqual(frame["sample_count"], 4800)
                self.assertEqual(24_000, frame["format"]["sample_rate"])
                offset += frame["sample_count"]
        self.assertEqual(offset, events[-1]["final_sample_count"])
        model = next(a for a in events[0]["worker"]["artifacts"] if a["role"] == "model_weights")
        self.assertEqual("gpt-sovits-v2pro", model["artifact_id"])

    def test_rejects_references_outside_three_to_ten_seconds(self) -> None:
        self.assertEqual(400, self.post("/synthesize", body(wav(10.6)))[0])
        self.assertEqual(400, self.post("/synthesize", body(wav(2.5)))[0])

    def test_restarts_a_dead_worker_for_the_next_reply(self) -> None:
        self.post("/warmup", {})
        subprocess.run([sys.executable, "-c", "import os,signal,sys; os.kill(int(sys.argv[1]), signal.SIGTERM)",
                        str(self.worker_pid())], check=False)
        time.sleep(0.5)
        status, events = self.post("/synthesize", body(wav(4)))
        self.assertEqual(200, status)
        self.assertEqual("completed", events[-1]["kind"])

    def worker_pid(self) -> int:
        # The worker is the server's only child process.
        output = subprocess.run(["powershell", "-NoProfile", "-Command",
                                 f"(Get-CimInstance Win32_Process -Filter 'ParentProcessId={self.server.pid}').ProcessId"]
                                if os.name == "nt" else ["pgrep", "-P", str(self.server.pid)],
                                capture_output=True, text=True, check=True).stdout.split()
        return int(output[0])


if __name__ == "__main__":
    unittest.main()
