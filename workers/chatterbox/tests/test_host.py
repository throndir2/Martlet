import base64
import hashlib
import http.client
import json
import math
import os
import socket
import struct
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import uuid
import wave
from datetime import datetime, timedelta, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
HOST = ROOT / "martlet_chatterbox_host.py"


def free_port():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


class _Buffer:
    def __init__(self, backing):
        self.backing = backing
    def write(self, data):
        self.backing.extend(data)
    def flush(self):
        pass


def wav_bytes(seconds):
    rate = 24000
    data = bytearray()
    for i in range(int(rate * seconds)):
        data += struct.pack("<h", int(math.sin(2 * math.pi * 220 * i / rate) * 6000))
    out = bytearray()
    with wave.open(_Buffer(out), "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(rate)
        wav.writeframes(bytes(data))
    return bytes(out)


def request_body(seconds=6, chunks=None, request_id=None, deadline_seconds=20):
    audio = wav_bytes(seconds)
    transcript = "reference transcript"
    transcript_revision = hashlib.sha256(transcript.encode()).hexdigest()
    audio_sha = hashlib.sha256(audio).hexdigest()
    return {
        "ids": {"session_id": str(uuid.uuid4()), "turn_id": str(uuid.uuid4()), "request_id": request_id or str(uuid.uuid4()), "parent_request_id": None},
        "deadline_utc": (datetime.now(timezone.utc) + timedelta(seconds=deadline_seconds)).isoformat(timespec="microseconds").replace("+00:00", "Z"),
        "reference": {
            "preset_id": str(uuid.uuid4()),
            "reference_revision": hashlib.sha256(bytes.fromhex(audio_sha) + bytes.fromhex(transcript_revision)).hexdigest(),
            "audio_sha256": audio_sha,
            "transcript": transcript,
            "transcript_revision": transcript_revision,
            "audio_base64": base64.b64encode(audio).decode("ascii"),
        },
        "chunks": chunks or [{"index": 0, "chunk_id": "a", "text": "hello [laugh]"}, {"index": 1, "chunk_id": "b", "text": "goodbye [sigh]"}],
    }


def http_json(port, method, path, body=None, timeout=10):
    conn = http.client.HTTPConnection("127.0.0.1", port, timeout=timeout)
    try:
        payload = None if body is None else json.dumps(body).encode()
        headers = {} if body is None else {"Content-Type": "application/json"}
        conn.request(method, path, body=payload, headers=headers)
        response = conn.getresponse()
        raw = response.read()
        return response.status, json.loads(raw or b"{}"), response.getheader("Content-Type")
    finally:
        conn.close()


def synthesize(port, body, timeout=30):
    conn = http.client.HTTPConnection("127.0.0.1", port, timeout=timeout)
    conn.request("POST", "/synthesize", body=json.dumps(body).encode(), headers={"Content-Type": "application/json"})
    response = conn.getresponse()
    raw = response.read()
    content_type = response.getheader("Content-Type")
    conn.close()
    if response.status == 200:
        return response.status, [json.loads(line) for line in raw.splitlines() if line], content_type
    return response.status, json.loads(raw or b"{}"), content_type


class ChatterboxHostTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory(prefix="chatterbox-host-", dir=os.getcwd())
        cls.port = free_port()
        cls.env = os.environ.copy()
        cls.env["MARTLET_CHATTERBOX_ROOT"] = cls.tmp.name
        cls.env["MARTLET_CHATTERBOX_PORT"] = str(cls.port)
        subprocess.run([sys.executable, str(HOST), "provision", "--fixture"], env=cls.env, cwd=ROOT.parents[1], check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        cls.proc = subprocess.Popen([sys.executable, str(HOST), "serve"], env=cls.env, cwd=ROOT.parents[1], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        deadline = time.time() + 10
        while time.time() < deadline:
            try:
                status, body, _ = http_json(cls.port, "GET", "/status", timeout=1)
                if status == 200 and body.get("ready"):
                    return
            except OSError:
                pass
            time.sleep(0.1)
        raise RuntimeError("service did not become ready")

    @classmethod
    def tearDownClass(cls):
        cls.proc.terminate()
        try:
            cls.proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            cls.proc.kill()
        cls.tmp.cleanup()

    def test_status(self):
        status, body, _ = http_json(self.port, "GET", "/status")
        self.assertEqual(status, 200)
        self.assertTrue(body["ready"])
        self.assertEqual(body["worker"]["evidence"], "synthetic_fixture")

    def test_synthesize_event_shape(self):
        status, events, content_type = synthesize(self.port, request_body())
        self.assertEqual(status, 200)
        self.assertEqual(content_type, "application/x-ndjson")
        kinds = [event["kind"] for event in events]
        self.assertEqual(kinds[0], "started")
        self.assertEqual(kinds[-1], "completed")
        self.assertEqual([event["chunk_index"] for event in events if event["kind"] == "chunk_completed"], [0, 1])
        offset = 0
        frame_sequence = 0
        for event in events:
            self.assertEqual(event["type"], "event")
            self.assertEqual(event["contract_id"], "martlet.f5.worker")
            if event["kind"] == "audio_frame":
                frame = event["frame"]
                self.assertLessEqual(frame["sample_count"], 4800)
                self.assertEqual(frame["sample_offset"], offset)
                self.assertEqual(frame["sequence"], frame_sequence)
                self.assertEqual(len(base64.b64decode(frame["data_base64"])), frame["sample_count"] * 2)
                offset += frame["sample_count"]
                frame_sequence += 1
        self.assertEqual(events[-1]["final_sample_count"], offset)

    def test_reference_too_short_fails_in_stream(self):
        status, events, _ = synthesize(self.port, request_body(seconds=3))
        self.assertEqual(status, 200)
        self.assertEqual(events[-1]["kind"], "failed")
        self.assertEqual(events[-1]["error"]["code"], "reference_too_short")

    def test_busy_wait_returns_busy_or_succeeds_after_wait(self):
        first = request_body(chunks=[{"index": 0, "chunk_id": "slow", "text": "x" * 1200}], deadline_seconds=20)
        result = {}
        def run_first():
            result["first"] = synthesize(self.port, first, timeout=30)
        thread = threading.Thread(target=run_first)
        thread.start()
        time.sleep(0.1)
        second = request_body(chunks=[{"index": 0, "chunk_id": "second", "text": "second"}], deadline_seconds=2)
        start = time.time()
        status, body, _ = synthesize(self.port, second, timeout=10)
        elapsed = time.time() - start
        self.assertGreater(elapsed, 0.2)
        if status == 503:
            self.assertEqual(body["error"], "worker.busy")
        else:
            self.assertEqual(status, 200)
            self.assertEqual(body[-1]["kind"], "completed")
        thread.join(timeout=10)
        self.assertFalse(thread.is_alive())


class EngineFailureTests(unittest.TestCase):
    """The live engine's failure handling, in process with a stand-in for Chatterbox's generate (FIXTURE - NOT AI)."""

    @classmethod
    def setUpClass(cls):
        sys.path.insert(0, str(ROOT))
        import martlet_chatterbox_host as host
        cls.host = host

    def run_job(self, generate):
        host = self.host
        engine = host.EngineHost()
        engine.model, engine.engine_kind, engine.state = object(), "chatterbox", "busy"
        engine.identity = host._identity("chatterbox")
        request = host._parse_request(request_body(chunks=[{"index": 0, "chunk_id": "a", "text": "hello"}]))
        job = host.Job(request, engine.identity)
        engine.active = job
        original = host._real_generate_pcm
        host._real_generate_pcm = generate
        try:
            with tempfile.TemporaryDirectory() as root:
                host.ROOT = Path(root)
                engine.run(job)
        finally:
            host._real_generate_pcm = original
        events = []
        while (event := job.queue.get(timeout=1)) is not None:
            events.append(event)
        return engine, events

    def test_out_of_memory_frees_memory_and_tries_once_more(self):
        calls = []
        def generate(model, text, path):
            calls.append(text)
            if len(calls) == 1:
                raise RuntimeError("CUDA out of memory. Tried to allocate 20.00 MiB")
            return b"\x01\x00" * 480
        engine, events = self.run_job(generate)
        self.assertEqual(len(calls), 2)
        self.assertEqual(events[-1]["kind"], "completed")
        self.assertEqual(engine.state, "ready")
        self.assertIsNotNone(engine.model)

    def test_out_of_memory_twice_fails_the_reply_but_keeps_the_model(self):
        def generate(model, text, path):
            raise RuntimeError("CUDA out of memory. Tried to allocate 20.00 MiB")
        engine, events = self.run_job(generate)
        error = events[-1]["error"]
        self.assertEqual(events[-1]["kind"], "failed")
        self.assertEqual(error["code"], "gpu_out_of_memory")
        self.assertIn("RuntimeError: CUDA out of memory", error["summary"])
        self.assertEqual(engine.state, "ready")
        self.assertIsNotNone(engine.model)

    def test_other_engine_failures_say_what_failed_and_reload(self):
        def generate(model, text, path):
            raise ValueError("bad tensor shape\nsecond line is not shown")
        engine, events = self.run_job(generate)
        error = events[-1]["error"]
        self.assertEqual(error["code"], "internal_failure")
        self.assertIn("ValueError: bad tensor shape", error["summary"])
        self.assertNotIn("second line", error["summary"])
        self.assertEqual(engine.state, "failed")
        self.assertIsNone(engine.model)
        self.assertIn("ValueError: bad tensor shape", engine.error)


if __name__ == "__main__":
    unittest.main()
