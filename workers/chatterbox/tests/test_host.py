import base64
import contextlib
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


def _have(*modules):
    try:
        for module in modules:
            __import__(module)
        return True
    except ImportError:
        return False


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

    def run_job(self, generate, chunks=None):
        host = self.host
        engine = host.EngineHost()
        engine.model, engine.engine_kind, engine.state = object(), "chatterbox", "busy"
        engine.identity = host._identity("chatterbox")
        request = host._parse_request(request_body(chunks=chunks or [{"index": 0, "chunk_id": "a", "text": "hello"}]))
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
        self.assertNotIn("restarting", error["summary"])
        self.assertEqual(engine.state, "failed")
        self.assertIsNone(engine.model)
        self.assertIn("ValueError: bad tensor shape", engine.error)
        self.assertFalse(engine.restarting)

    def test_a_broken_graphics_context_restarts_the_service(self):
        host = self.host
        restarts = []
        saved = host._cuda_broken, host._restart_process, host.RESTART_DELAY_SECONDS
        host._cuda_broken = lambda device: "AcceleratorError: CUDA error: an illegal memory access was encountered"
        host._restart_process = lambda: restarts.append(time.monotonic())
        host.RESTART_DELAY_SECONDS = 0.01
        try:
            def generate(model, text, path):
                raise RuntimeError("CUDA error: an illegal memory access was encountered")
            engine, events = self.run_job(generate)
            deadline = time.monotonic() + 5
            while not restarts and time.monotonic() < deadline:
                time.sleep(0.01)
        finally:
            host._cuda_broken, host._restart_process, host.RESTART_DELAY_SECONDS = saved
        # The reply still says why it failed, then the service exits so Docker starts it with a fresh CUDA context; meanwhile it
        # loads nothing (a reload in the broken context would fail every reply) and refuses replies with why.
        self.assertEqual(events[-1]["kind"], "failed")
        self.assertIn("the voice service is restarting", events[-1]["error"]["summary"])
        self.assertEqual(len(restarts), 1)
        self.assertTrue(engine.restarting)
        self.assertEqual(engine.state, "failed")
        self.assertIn("CUDA context broke", engine.error)
        engine.start_loading()
        self.assertFalse(engine.loading)

    def test_quotation_marks_are_not_spoken(self):
        host = self.host
        self.assertEqual(host._speakable("\"Hello,\" she said.  \u201cIt's fine!\u201d"), "Hello, she said. It's fine!")
        spoken = []
        def generate(model, text, path):
            spoken.append(text)
            return b"\x01\x00" * 480
        _, events = self.run_job(generate, chunks=[{"index": 0, "chunk_id": "a", "text": "\u201c \u201d"},
                                                   {"index": 1, "chunk_id": "b", "text": "He said \"hi\"."}])
        # A piece of nothing but quotation marks completes empty instead of the library's "You need to add some text" line.
        self.assertEqual(spoken, ["He said hi."])
        self.assertEqual([e["chunk_index"] for e in events if e["kind"] == "chunk_completed"], [0, 1])
        self.assertEqual(events[-1]["kind"], "completed")

    def test_a_sentence_starting_with_whispering_is_whispered(self):
        host = self.host
        whispering = []
        spoken = []

        @contextlib.contextmanager
        def whisper(model):
            whispering.append(model)
            try:
                yield
            finally:
                whispering.pop()

        def generate(model, text, path):
            spoken.append((text, bool(whispering)))
            return b"\x01\x00" * 480

        saved = host._whispering
        host._whispering = whisper
        try:
            engine, events = self.run_job(generate, chunks=[
                {"index": 0, "chunk_id": "a", "text": "Okay. [whispering] It's a secret. Got it?"},
                {"index": 1, "chunk_id": "b", "text": "[whispering] Ha [laugh] keep it down."}])
        finally:
            host._whispering = saved
        # Only the sentence the tag starts is whispered, in order, into the same piece; Turbo still reads the tag.
        self.assertEqual(spoken, [("Okay.", False), ("[whispering] It's a secret.", True), ("Got it?", False),
                                  ("[whispering] Ha [laugh] keep it down.", True)])
        frames = [e["frame"] for e in events if e["kind"] == "audio_frame"]
        self.assertEqual([f["chunk_index"] for f in frames], [0, 0, 0, 1])
        self.assertEqual([f["sample_offset"] for f in frames], [0, 480, 960, 1440])
        self.assertEqual([e["chunk_index"] for e in events if e["kind"] == "chunk_completed"], [0, 1])
        self.assertEqual(events[-1]["kind"], "completed")
        self.assertEqual(engine.status()["whisper"], {"level_db": host.WHISPER_DB, "parts": 2})


class WhisperTests(unittest.TestCase):
    """[whispering]: which words are whispered, and the whisper itself (FIXTURE - NOT AI: synthetic signals)."""

    @classmethod
    def setUpClass(cls):
        sys.path.insert(0, str(ROOT))
        import martlet_chatterbox_host as host
        cls.host = host

    def test_from_the_tag_to_the_end_of_its_sentence(self):
        parts = self.host._whisper_parts
        self.assertEqual(parts("Hello there."), [("Hello there.", False)])
        self.assertEqual(parts("[whispering] Keep it a secret."), [("[whispering] Keep it a secret.", True)])
        self.assertEqual(parts("Okay. [whispering] It's a secret! Got it?"),
                         [("Okay.", False), ("[whispering] It's a secret!", True), ("Got it?", False)])
        # Whispered sentences in a row stay one part; the tag is matched whatever its case.
        self.assertEqual(parts("[whispering] One. [Whispering] Two. Three."),
                         [("[whispering] One. [Whispering] Two.", True), ("Three.", False)])
        self.assertEqual(parts("Shh [whispering] quiet now"), [("Shh", False), ("[whispering] quiet now", True)])
        self.assertEqual(parts("I said, [whispering] \u201cdon't.\u201d Fine."),
                         [("I said,", False), ("[whispering] \u201cdon't.\u201d", True), ("Fine.", False)])
        # A tag with nothing after it in its sentence whispers nothing; a sound after it is whispered.
        self.assertEqual(parts("Hello [whispering]"), [("Hello [whispering]", False)])
        self.assertEqual(parts("Hi. [whispering]. Bye."), [("Hi. [whispering]. Bye.", False)])
        self.assertEqual(parts("[whispering] [laugh]. Hi."), [("[whispering] [laugh].", True), ("Hi.", False)])

    def _voice(self, seconds=1.0, rate=24_000):
        import numpy as np

        # FIXTURE - NOT a voice: a 150 Hz buzz with falling harmonics, its loudness rising and falling like syllables.
        t = np.arange(int(rate * seconds)) / rate
        buzz = sum(np.sin(2 * np.pi * 150 * k * t) / k for k in range(1, 30))
        return (0.1 * buzz * (0.6 + 0.4 * np.sin(2 * np.pi * 4 * t))).astype(np.float32)

    @staticmethod
    def _periodicity(samples, lag=160):
        import numpy as np

        a, b = samples[:-lag], samples[lag:]
        return float(np.dot(a, b) / np.sqrt(np.dot(a, a) * np.dot(b, b)))

    @unittest.skipUnless(_have("numpy", "scipy"), "needs NumPy and SciPy")
    def test_a_voice_becomes_breath_at_a_lower_level(self):
        import numpy as np

        voice = self._voice()
        whisper = self.host.whispered(voice)
        self.assertEqual(whisper.shape, voice.shape)
        self.assertEqual(whisper.dtype, np.float32)
        # The buzz repeats every 160 samples (150 Hz); the whisper doesn't.
        self.assertGreater(self._periodicity(voice), 0.9)
        self.assertLess(abs(self._periodicity(whisper)), 0.3)
        level = 20 * np.log10(np.sqrt(np.mean(whisper ** 2)) / np.sqrt(np.mean(voice ** 2)))
        self.assertLess(level, -3)
        self.assertGreater(level, -20)

    @unittest.skipUnless(_have("numpy", "scipy"), "needs NumPy and SciPy")
    def test_streamed_audio_is_whispered_the_same_whatever_the_chunks(self):
        import numpy as np

        voice = np.concatenate([np.zeros(2_000, dtype=np.float32), self._voice(0.8)])
        whole = self.host.whispered(voice)
        whisperer = self.host.Whisperer()
        out, start = [], 0
        for size in (7, 1_000, 299, 4_800, 3, 12_000):
            out.append(whisperer.feed(voice[start:start + size]))
            start += size
        out.append(whisperer.feed(voice[start:]))
        out.append(whisperer.finish())
        streamed = np.concatenate(out)
        self.assertEqual(streamed.shape, voice.shape)
        np.testing.assert_allclose(streamed, whole, atol=1e-6)
        # Silence stays silent, and nothing is held back beyond the last 40 ms until the end.
        self.assertEqual(float(np.max(np.abs(whole[:1_400]))), 0.0)
        self.assertGreaterEqual(sum(part.size for part in out[:-1]), voice.size - 960)

    @unittest.skipUnless(_have("numpy", "scipy"), "needs NumPy and SciPy")
    def test_generate_whispers_before_the_watermark(self):
        import numpy as np

        marked = []

        class Watermarker:
            def apply_watermark(self, wav, sample_rate):
                marked.append((np.asarray(wav).copy(), sample_rate))
                return wav

        class Model:
            sr = 24_000
            watermarker = Watermarker()

        model, voice = Model(), self._voice(0.5)
        with self.host._whispering(model):
            model.watermarker.apply_watermark(voice, sample_rate=24_000)
        model.watermarker.apply_watermark(voice, sample_rate=24_000)
        (whisper, rate), (plain, _) = marked
        # Inside, the watermark goes on the whisper (so it survives); afterwards the watermarker is its own again.
        self.assertEqual(rate, 24_000)
        self.assertEqual(whisper.shape, voice.shape)
        self.assertLess(abs(self._periodicity(whisper)), 0.3)
        np.testing.assert_array_equal(plain, voice)
        self.assertNotIn("apply_watermark", vars(model.watermarker))


class IdleCheckTests(unittest.TestCase):
    """The idle check that keeps the model in graphics memory while nobody speaks, in process with a stand-in for the model
    (FIXTURE - NOT AI)."""

    @classmethod
    def setUpClass(cls):
        sys.path.insert(0, str(ROOT))
        import martlet_chatterbox_host as host
        cls.host = host

    def engine(self, idle_pass):
        class Fast:
            decoder_steps, on_cpu, streams = 2, False, True

            def idle_pass(self, cancelled):
                idle_pass(cancelled)

        engine = self.host.EngineHost()
        engine.model, engine.fast, engine.engine_kind, engine.state = object(), Fast(), "chatterbox", "ready"
        engine.last_activity = time.monotonic() - self.host.IDLE_CHECK_SECONDS - 1
        return engine

    def test_runs_only_after_the_model_was_idle(self):
        calls = []
        engine = self.engine(lambda cancelled: calls.append(1))
        self.assertTrue(engine.idle_check())
        self.assertFalse(engine.idle_check())  # it just ran: not idle long enough again
        self.assertEqual(len(calls), 1)
        status = engine.status()["idle_check"]
        self.assertEqual(status["checks"], 1)
        self.assertIsNotNone(status["last_ms"])
        engine.last_activity -= self.host.IDLE_CHECK_SECONDS + 1
        engine.active = object()  # a reply is being spoken
        self.assertFalse(engine.idle_check())
        self.assertEqual(len(calls), 1)

    def test_a_reply_stops_the_idle_check_at_once(self):
        running = threading.Event()
        def idle_pass(cancelled):
            running.set()
            deadline = time.monotonic() + 10
            while not cancelled() and time.monotonic() < deadline:
                time.sleep(0.005)
        engine = self.engine(idle_pass)
        thread = threading.Thread(target=engine.idle_check)
        thread.start()
        self.assertTrue(running.wait(5))
        started = time.monotonic()
        engine.wait_admissible(5)
        waited = time.monotonic() - started
        thread.join(5)
        self.assertLess(waited, 1.0)
        self.assertIsNone(engine.idle)
        self.assertEqual(engine.status()["idle_check"]["checks"], 0)  # a stopped check isn't a measurement

    def test_a_broken_graphics_context_found_while_idle_restarts_the_service(self):
        host = self.host
        restarts = []
        saved = host._cuda_broken, host._restart_process, host.RESTART_DELAY_SECONDS
        host._cuda_broken = lambda device: "AcceleratorError: CUDA error: unspecified launch failure"
        host._restart_process = lambda: restarts.append(1)
        host.RESTART_DELAY_SECONDS = 0.01
        try:
            def idle_pass(cancelled):
                raise RuntimeError("CUDA error: unspecified launch failure")
            engine = self.engine(idle_pass)
            self.assertTrue(engine.idle_check())
            deadline = time.monotonic() + 5
            while not restarts and time.monotonic() < deadline:
                time.sleep(0.01)
        finally:
            host._cuda_broken, host._restart_process, host.RESTART_DELAY_SECONDS = saved
        self.assertEqual(restarts, [1])
        self.assertTrue(engine.restarting)
        self.assertIsNotNone(engine.model)


class FastTurboTests(unittest.TestCase):
    """The streaming decoder's cancellation and warm-up, in process with stand-ins for T3 and the watermarker (FIXTURE - NOT
    AI). Needs PyTorch and transformers (the service's image has them); skipped elsewhere."""

    @classmethod
    def setUpClass(cls):
        sys.path.insert(0, str(ROOT))
        import martlet_chatterbox_host as host
        cls.host = host

    @unittest.skipUnless(_have("torch", "transformers"), "needs PyTorch and transformers")
    def test_a_stopped_reply_stops_decoding_at_the_next_token(self):
        import torch

        vocabulary, width = 64, 8
        replays = []
        stop_after = 20

        class Graph:
            def replay(self):
                replays.append(1)

        class Hp:
            start_speech_token, stop_speech_token = 0, vocabulary - 1

        class T3:
            hp = Hp()
            speech_emb = torch.nn.Embedding(vocabulary, width)

            def prepare_input_embeds(self, t3_cond, text_tokens, speech_tokens, cfg_weight):
                return torch.zeros(1, 4, width), None

        logits = torch.zeros(1, 1, vocabulary)
        logits[..., vocabulary - 1] = -float("inf")  # never the stop token: only cancellation ends it
        graph = object.__new__(self.host._T3Graph)
        graph.torch, graph.t3, graph.cuda_graph, graph.out = torch, T3(), Graph(), logits
        graph.x, graph.position = torch.zeros(1, 1, width), torch.zeros(1, dtype=torch.long)
        graph._prefill = lambda embeds: logits
        chunks = list(graph.chunks(None, torch.zeros(1, 3, dtype=torch.long), cancelled=lambda: len(replays) >= stop_after))
        # The first chunk (12 tokens plus the decoder's 3 of lookahead) left before the reply was stopped; nothing after it,
        # and no token was decoded once it was stopped, though its chunk had 25 more to go.
        self.assertEqual([last for _, last in chunks], [False])
        self.assertEqual(chunks[0][0].shape[1], 15)
        self.assertEqual(len(replays), stop_after)

    @unittest.skipUnless(_have("numpy"), "needs NumPy")
    def test_warming_up_runs_the_watermarker_once(self):
        calls = []

        class Watermarker:
            def apply_watermark(self, wav, sample_rate):
                calls.append((wav.dtype.name, wav.shape, sample_rate))
                return wav

        class Model:
            sr = 24_000
            watermarker = Watermarker()

        fast = object.__new__(self.host.FastTurbo)
        fast.model = Model()
        fast._warm_watermarker()
        self.assertEqual(calls, [("float32", (24_000,), 24_000)])

    def test_closing_lets_go_of_the_model(self):
        class T3:
            def inference_turbo(self):
                return "library"

        class S3Gen:
            def inference(self):
                return "library"

        class Model:
            t3, s3gen = T3(), S3Gen()

        fast = object.__new__(self.host.FastTurbo)
        fast.model, fast.conditionals, fast.graph, fast.warm_conds = Model(), {"voice": object()}, object(), object()
        fast.model.t3.inference_turbo = fast._inference_turbo  # what __init__ installs: t3 -> fast -> model -> t3
        fast.model.s3gen.inference = lambda: fast  # and on the CPU: s3gen -> fast -> model -> s3gen
        fast.close()
        self.assertNotIn("inference_turbo", vars(fast.model.t3))
        self.assertEqual(fast.model.t3.inference_turbo(), "library")
        self.assertNotIn("inference", vars(fast.model.s3gen))
        self.assertEqual(fast.model.s3gen.inference(), "library")
        self.assertIsNone(fast.graph)
        self.assertEqual(fast.conditionals, {})

    def _graph(self, logits_for):
        """A _T3Graph over stand-ins: replay() makes the logits logits_for(step) (FIXTURE - NOT AI)."""
        import torch

        vocabulary, width = 64, 8
        graph = object.__new__(self.host._T3Graph)
        replays = []

        class Graph:
            def replay(self):
                replays.append(1)
                graph.out = logits_for(len(replays))

        class Hp:
            start_speech_token, stop_speech_token = 0, vocabulary - 1

        class T3:
            hp = Hp()
            speech_emb = torch.nn.Embedding(vocabulary, width)

            def prepare_input_embeds(self, t3_cond, text_tokens, speech_tokens, cfg_weight):
                return torch.zeros(1, 4, width), None

        graph.torch, graph.t3, graph.cuda_graph, graph.capped = torch, T3(), Graph(), False
        graph.x, graph.position = torch.zeros(1, 1, width), torch.zeros(1, dtype=torch.long)
        graph._prefill = lambda embeds: logits_for(0)
        return graph, replays, vocabulary

    @staticmethod
    def _only(token, vocabulary):
        import torch

        logits = torch.full((1, 1, vocabulary), -float("inf"))
        logits[..., token] = 0.0
        return logits

    @unittest.skipUnless(_have("torch", "transformers"), "needs PyTorch and transformers")
    def test_the_stop_token_ends_a_piece_though_the_gpu_is_asked_only_every_few_tokens(self):
        import torch

        vocabulary = 64
        graph, replays, _ = self._graph(lambda step: self._only(vocabulary - 1 if step == 30 else 5, vocabulary))
        chunks = list(graph.chunks(None, torch.zeros(1, 3, dtype=torch.long)))
        tokens, last = chunks[-1]
        # The first token and the 29 drawn before the stop token at the 30th step; nothing after it, though a few more were drawn
        # before the GPU was asked.
        self.assertTrue(last)
        self.assertEqual(tokens.shape[1], 30)
        self.assertNotIn(vocabulary - 1, tokens[0].tolist())
        self.assertLess(len(replays), 30 + self.host.CHECK_EVERY)
        self.assertFalse(graph.capped)
        self.assertEqual([chunk[0].shape[1] for chunk in chunks[:-1]], [15])

    @unittest.skipUnless(_have("torch", "transformers"), "needs PyTorch and transformers")
    def test_a_piece_that_never_stops_ends_at_its_budget(self):
        import torch

        vocabulary = 64
        graph, replays, _ = self._graph(lambda step: self._only(5, vocabulary))
        text = torch.zeros(1, 3, dtype=torch.long)
        text[0, 0] = self.host.TAG_TOKEN_FIRST  # one tag and two words
        budget = graph.budget(text)
        self.assertEqual(budget, self.host.SPEECH_TOKENS_BASE + 2 * self.host.SPEECH_TOKENS_PER_TEXT_TOKEN +
                         self.host.SPEECH_TOKENS_PER_TAG)
        tokens, last = list(graph.chunks(None, text))[-1]
        self.assertTrue(last)
        self.assertTrue(graph.capped)
        self.assertEqual(tokens.shape[1], budget + 1)
        self.assertEqual(len(replays), budget)

    @unittest.skipUnless(_have("torch", "transformers"), "needs PyTorch and transformers")
    def test_a_step_without_any_possible_token_ends_the_piece(self):
        import torch

        vocabulary = 64
        nothing = torch.full((1, 1, vocabulary), -float("inf"))
        graph, _, _ = self._graph(lambda step: nothing if step == 8 else self._only(5, vocabulary))
        tokens, last = list(graph.chunks(None, torch.zeros(1, 3, dtype=torch.long)))[-1]
        # The library stops when every logit is -inf; here that step draws the stop token without asking the GPU.
        self.assertTrue(last)
        self.assertEqual(tokens.shape[1], 8)
        self.assertFalse(graph.capped)

    @unittest.skipUnless(_have("torch"), "needs PyTorch")
    def test_without_a_graph_the_library_decodes_within_the_budget(self):
        import torch

        calls = []

        class T3:
            def inference_turbo(self, t3_cond, text_tokens, **kwargs):
                calls.append(kwargs["max_gen_len"])
                return "tokens"

        class S3Gen:
            def inference(self, **kwargs):
                return kwargs["n_cfm_timesteps"]

        class Model:
            t3, s3gen, device = T3(), S3Gen(), "cpu"

        fast = self.host.FastTurbo(Model(), graph=True, name="Chatterbox Nano")
        # On the CPU there is no CUDA graph, but decoding still goes through the service, so a piece that never stops ends.
        self.assertIsNone(fast.graph)
        self.assertEqual(fast.model.t3.inference_turbo(None, torch.zeros(1, 4, dtype=torch.long)), "tokens")
        self.assertEqual(calls, [self.host.SPEECH_TOKENS_BASE + 4 * self.host.SPEECH_TOKENS_PER_TEXT_TOKEN])
        fast.close()
        self.assertNotIn("inference_turbo", vars(fast.model.t3))

    def test_on_the_cpu_a_whole_piece_takes_one_decoder_step(self):
        class T3:
            def inference_turbo(self, *args, **kwargs):
                return "tokens"

        class S3Gen:
            def inference(self, speech_tokens=None, ref_dict=None, n_cfm_timesteps=None):
                return n_cfm_timesteps

        def fast_on(device):
            class Model:
                t3, s3gen = T3(), S3Gen()

            model = Model()
            model.device = device
            return self.host.FastTurbo(model, graph=False, name="Chatterbox Nano")

        cpu = fast_on("cpu")
        self.assertEqual(self.host.CPU_DECODER_STEPS, 1)
        self.assertEqual(cpu.decoder_steps, 1)
        # The library's generate() asks for 2; the CPU decodes with 1.
        self.assertEqual(cpu.model.s3gen.inference(speech_tokens="speech", ref_dict={}, n_cfm_timesteps=2), 1)
        cpu.close()
        self.assertEqual(cpu.model.s3gen.inference(n_cfm_timesteps=2), 2)
        gpu = fast_on("cuda:0")
        self.assertEqual(gpu.decoder_steps, 2)
        self.assertNotIn("inference", vars(gpu.model.s3gen))
        self.assertEqual(gpu.model.s3gen.inference(n_cfm_timesteps=2), 2)

    def test_the_cpu_streams_unless_turned_off(self):
        from unittest import mock

        class T3:
            def inference_turbo(self, *args, **kwargs):
                return "tokens"

        class S3Gen:
            def inference(self, **kwargs):
                return None

        def fast_on(device):
            class Model:
                t3, s3gen = T3(), S3Gen()

            model = Model()
            model.device = device
            return self.host.FastTurbo(model, graph=False, name="Chatterbox Nano")

        cpu = fast_on("cpu")
        self.assertTrue(cpu.streams)
        with mock.patch.object(self.host, "CPU_FIRST_TOKENS", 0):
            self.assertFalse(cpu.streams)
        with mock.patch.dict(os.environ, {"MARTLET_CHATTERBOX_STREAM": "0"}):
            self.assertFalse(cpu.streams)
        # On a GPU only the CUDA graph streams; without it each piece is spoken whole, as before.
        self.assertFalse(fast_on("cuda:0").streams)

    @unittest.skipUnless(_have("torch", "transformers", "chatterbox"), "needs PyTorch, transformers and chatterbox-tts")
    def test_the_cpu_draws_the_speech_tokens_the_library_draws(self):
        import types

        import torch
        from chatterbox.models.t3.t3 import T3
        from transformers import GPT2Config, GPT2Model

        vocabulary, width = 64, 16

        class Hp:
            start_speech_token, stop_speech_token = 0, vocabulary - 1

        class StandIn(torch.nn.Module):
            """A tiny T3 (FIXTURE - NOT AI): random GPT-2 layers, speech embedding and head."""

            def __init__(self):
                super().__init__()
                self.hp = Hp()
                self.tfmr = GPT2Model(GPT2Config(n_layer=2, n_head=2, n_embd=width, n_positions=1024, vocab_size=8))
                self.speech_emb = torch.nn.Embedding(vocabulary, width)
                self.speech_head = torch.nn.Linear(width, vocabulary)

            def prepare_input_embeds(self, t3_cond, text_tokens, speech_tokens, cfg_weight):
                text = torch.linspace(-1, 1, text_tokens.shape[1] * width).reshape(1, text_tokens.shape[1], width)
                return torch.cat([text, self.speech_emb(speech_tokens)], dim=1), None

        torch.manual_seed(0)
        t3 = StandIn().eval()
        fast = object.__new__(self.host.FastTurbo)
        fast.model = types.SimpleNamespace(t3=t3)
        text = torch.zeros(1, 4, dtype=torch.long)
        budget = self.host._speech_budget(text)
        due_at = {58, 90}
        for never_stops in (False, True):
            with torch.no_grad():
                t3.speech_head.bias[vocabulary - 1] = -1e9 if never_stops else 4.0
            for seed in (1, 2, 3):
                torch.manual_seed(seed)
                library = T3.inference_turbo(t3, None, text, max_gen_len=budget)
                torch.manual_seed(seed)
                chunks = list(fast._eager_chunks(None, text, None, lambda count: count in due_at))
                tokens, last = chunks[-1]
                self.assertTrue(last)
                # The same tokens, drawn in the same order, as the library's own decoding.
                self.assertTrue(torch.equal(tokens, library), f"seed {seed}")
                self.assertEqual(fast.eager_capped, never_stops)
                if never_stops:
                    self.assertEqual(tokens.shape[1], budget + 1)
                else:
                    self.assertLess(tokens.shape[1], budget + 1)
                # Each early chunk is the start of the piece, at the counts it was asked for.
                self.assertEqual([early.shape[1] for early, _ in chunks[:-1]], sorted(c for c in due_at if c < tokens.shape[1]))
                for early, done in chunks[:-1]:
                    self.assertFalse(done)
                    self.assertTrue(torch.equal(early, tokens[:, :early.shape[1]]))


class PlaybackTests(unittest.TestCase):
    """When the CPU decodes the next chunk of a streamed reply (FIXTURE - NOT AI: a stand-in clock and CPU speed)."""

    @classmethod
    def setUpClass(cls):
        sys.path.insert(0, str(ROOT))
        import martlet_chatterbox_host as host
        cls.host = host

    @unittest.skipUnless(_have("torch"), "needs PyTorch")
    def test_two_decodings_meet_smoothly(self):
        import torch

        old, new = torch.zeros(1, 80, 8), torch.ones(1, 80, 8)
        joined = self.host._join(old, new)
        # From the last decoding's frames to this one's, one step at a time across the overlap.
        self.assertEqual(joined.shape, (1, 80, 8))
        self.assertTrue(torch.allclose(joined[0, 0], torch.linspace(0.0, 1.0, 8)))
        self.assertTrue(torch.equal(joined[:, :, 0], old[:, :, 0]))
        self.assertTrue(torch.equal(joined[:, :, -1], new[:, :, -1]))
        # Frames that don't line up are left as they were.
        self.assertIs(self.host._join(old, torch.ones(1, 80, 5)), old)

    def playback(self, now, token=0.018, scale=1.0):
        return self.host._Playback(first_tokens=55, clock=lambda: now[0], speed=self.host._CpuSpeed(token, scale))

    def test_a_reply_starts_after_its_first_tokens_then_decodes_only_before_the_audio_runs_out(self):
        now = [10.0]
        playback = self.playback(now)
        playback.begin_piece(expected=140)
        self.assertEqual(playback.first, 55)
        # Nothing plays yet: the first chunk waits for 55 tokens and the decoder's 3 of lookahead.
        self.assertFalse(playback.due(57))
        self.assertTrue(playback.due(58))
        # Decoded in 0.83 s (as the shape expects), it sent 2.2 s of audio, which plays until 13.03.
        now[0] = 10.83
        playback.decoded_chunk(58, 2.2, 0.83)
        self.assertAlmostEqual(playback.ends, 13.03)
        self.assertAlmostEqual(playback.speed.scale, 1.0, places=3)
        # Fewer than 25 new tokens are never worth a decoding. 110 tokens would take 0.36 + 0.0028 * 110 + 0.0053 * 52 =
        # 0.94 s; with 0.2 s to spare, they are due once 1.14 s of audio is left.
        now[0] = 11.5
        self.assertFalse(playback.due(82))
        now[0] = 11.85
        self.assertFalse(playback.due(110))
        now[0] = 11.9
        self.assertTrue(playback.due(110))
        now[0] = 12.85
        playback.decoded_chunk(110, 2.08, 0.95)
        self.assertAlmostEqual(playback.ends, 15.11)
        # T3 drew 28 tokens in 0.4 s since the last decoding: 14.3 ms a token, averaged with what was known.
        self.assertAlmostEqual(playback.speed.token, (0.018 + 0.4 / 28) / 2)
        # The audio ran out (the listener hears a pause): the next chunk still needs as many new tokens as pay for their own
        # drawing and decoding (0.67 s fixed / (40 - 16.1 - 8.1) ms a token = 43), and plays from when it arrives.
        now[0] = 15.5
        self.assertFalse(playback.due(152))
        self.assertTrue(playback.due(153))
        playback.decoded_chunk(153, 2.08, 1.0)
        self.assertAlmostEqual(playback.ends, 17.58)

    def test_a_cpu_too_slow_to_keep_up_sends_one_early_chunk_then_the_rest_whole(self):
        now = [10.0]
        playback = self.playback(now, token=0.035)
        playback.begin_piece(expected=140)
        # T3 at 35 ms a token and 8.1 ms to decode each: no later chunk pays for itself, so the first chunk waits until the
        # rest of the expected piece can follow in one decoding while it plays.
        first = playback.first
        self.assertGreater(first, 55)
        self.assertLess(first, 140)
        self.assertFalse(playback.due(first + 2))
        self.assertTrue(playback.due(first + 3))
        playback.decoded_chunk(first + 3, first * 0.04, 1.2)
        # The rest, however long it turns out, is decoded whole, once.
        for count in range(first + 4, 400):
            now[0] += 0.035
            self.assertFalse(playback.due(count))

    def test_a_later_piece_streams_only_when_its_audio_is_needed(self):
        now = [10.0]
        playback = self.playback(now)
        playback.begin_piece(expected=40)
        playback.decoded_chunk(40, 1.6, 0.6)  # a short first piece, sent whole: it plays until 11.6
        playback.begin_piece(expected=60)
        # While it plays, the next piece has no early chunk: only one that its audio needs (its decoding, about 0.53 s, and
        # 0.2 s before the audio runs out).
        now[0] = 10.8
        self.assertFalse(playback.due(25))
        now[0] = 10.9
        self.assertTrue(playback.due(25))
        # Once everything has played, a new piece starts like a reply.
        playback.begin_piece(expected=140)
        now[0] = 20.0
        self.assertFalse(playback.due(57))
        self.assertTrue(playback.due(58))

    def test_a_long_piece_or_a_slow_cpu_waits_for_a_bigger_first_chunk(self):
        now = [10.0]
        playback = self.playback(now)
        # A 5.6 s sentence keeps up after 55 tokens; a 14 s one needs a bigger first chunk, so that the later chunks, each as
        # late as playback allows, don't fall behind before its end.
        playback.begin_piece(expected=140)
        self.assertEqual(playback.first, 55)
        playback.begin_piece(expected=350)
        self.assertGreater(playback.first, 55)
        self.assertLess(playback.first, 100)
        # A short piece is spoken whole.
        playback.begin_piece(expected=30)
        self.assertFalse(playback.due(40))
        # T3 at 30 ms a token instead of 18: the same sentence waits longer.
        slow = self.playback(now, token=0.03)
        slow.begin_piece(expected=140)
        self.assertGreater(slow.first, 55)

    def test_the_holdback_never_makes_a_piece_start_later(self):
        now = [10.0]
        self.assertEqual(self.host.CPU_HOLD_TOKENS, 8)
        self.assertEqual(self.host._Playback().most_hold, 8)
        for token in (0.018, 0.025):
            holds = {}
            for expected in range(30, 501):
                speed = self.host._CpuSpeed(token, 1.0)
                held = self.host._Playback(first_tokens=55, clock=lambda: now[0], speed=speed)
                lookahead_only = self.host._Playback(first_tokens=55, clock=lambda: now[0], speed=speed, hold=3)
                held.begin_piece(expected)
                lookahead_only.begin_piece(expected)
                # The first chunk comes exactly where keeping back only the decoder's lookahead puts it.
                self.assertEqual(held.first, lookahead_only.first, (token, expected))
                self.assertEqual(lookahead_only.hold, 3)
                self.assertTrue(3 <= held.hold <= 8)
                holds[expected] = held.hold
            if token == 0.018:
                # On a quiet i7-13700K a sentence up to 5.7 s keeps back all 8; a longer piece as many as its timing allows.
                self.assertEqual({holds[e] for e in range(30, 143)}, {8})
                self.assertLess(min(holds.values()), 8)


class CpuTests(unittest.TestCase):
    """How many threads Chatterbox uses on the CPU and the performance cores it is pinned to, from a stand-in for Linux's
    /sys/devices (FIXTURE - NOT a real CPU)."""

    @classmethod
    def setUpClass(cls):
        sys.path.insert(0, str(ROOT))
        import martlet_chatterbox_host as host
        cls.host = host

    def sysfs(self, root, performance, siblings):
        """performance: what /sys/devices/cpu_core/cpus says (None: not a hybrid CPU); siblings: each CPU's
        thread_siblings_list."""
        devices = Path(root)
        if performance is not None:
            (devices / "cpu_core").mkdir(parents=True)
            (devices / "cpu_core" / "cpus").write_text(performance + "\n", encoding="ascii")
        for cpu, listed in siblings.items():
            topology = devices / "system" / "cpu" / f"cpu{cpu}" / "topology"
            topology.mkdir(parents=True)
            (topology / "thread_siblings_list").write_text(listed + "\n", encoding="ascii")
        return devices

    def test_cpu_lists_read_like_linux_writes_them(self):
        self.assertEqual(self.host._cpu_list("0-3,8,10-11\n"), [0, 1, 2, 3, 8, 10, 11])
        self.assertEqual(self.host._cpu_list(""), [])

    def test_a_hybrid_cpu_names_one_cpu_on_each_performance_core(self):
        # An i7-13700K: 8 performance cores with two CPUs each (0-15, siblings 0-1, 2-3...) and 8 efficiency cores (16-23).
        siblings = {cpu: f"{cpu - cpu % 2}-{cpu - cpu % 2 + 1}" for cpu in range(16)} | {cpu: str(cpu) for cpu in range(16, 24)}
        with tempfile.TemporaryDirectory(prefix="chatterbox-sysfs-", dir=os.getcwd()) as root:
            self.assertEqual(self.host._performance_cores(self.sysfs(root, "0-15", siblings)), [0, 2, 4, 6, 8, 10, 12, 14])
        # Linux numbers some CPUs' second threads after all the first ones (siblings 0,8).
        with tempfile.TemporaryDirectory(prefix="chatterbox-sysfs-", dir=os.getcwd()) as root:
            spread = {cpu: f"{cpu % 4},{cpu % 4 + 4}" for cpu in range(8)}
            self.assertEqual(self.host._performance_cores(self.sysfs(root, "0-7", spread)), [0, 1, 2, 3])
        with tempfile.TemporaryDirectory(prefix="chatterbox-sysfs-", dir=os.getcwd()) as root:
            self.assertEqual(self.host._performance_cores(self.sysfs(root, None, {0: "0-1", 1: "0-1"})), [])

    def test_the_plan_uses_eight_threads_at_most_and_only_performance_cores(self):
        plan = self.host._cpu_plan
        performance = [0, 2, 4, 6, 8, 10, 12, 14]
        # The i7-13700K: PyTorch would use its 16 physical cores; Chatterbox uses 8, one on each performance core.
        self.assertEqual(plan("", 16, performance, None), (8, performance))
        # Fewer performance cores than 8 (an i5-12600K's 6): one thread on each.
        self.assertEqual(plan("", 10, [0, 2, 4, 6, 8, 10], None), (6, [0, 2, 4, 6, 8, 10]))
        # Not a hybrid CPU (or Linux doesn't say): 8 threads or the physical cores, whichever is fewer, not pinned.
        self.assertEqual(plan("", 16, [], None), (8, []))
        self.assertEqual(plan("", 4, [], None), (4, []))
        # Only the CPUs this container may use.
        self.assertEqual(plan("", 16, performance, {0, 2, 4, 6, 16, 17}), (4, [0, 2, 4, 6]))
        # MARTLET_CHATTERBOX_CPU_THREADS: pinned while it fits on the performance cores, otherwise on any core.
        self.assertEqual(plan("4", 16, performance, None), (4, [0, 2, 4, 6]))
        self.assertEqual(plan("12", 16, performance, None), (12, []))
        self.assertEqual(plan("12", 16, [], None), (12, []))
        for bad in ("0", "-2", "many"):
            with self.assertRaises(ValueError):
                plan(bad, 16, performance, None)


class ModelTests(unittest.TestCase):
    """Turbo, Nano and the original model: what each role provisions and reports, and the original model's style
    (FIXTURE - NOT AI: no model is loaded)."""

    @classmethod
    def setUpClass(cls):
        sys.path.insert(0, str(ROOT))
        import martlet_chatterbox_host as host
        cls.host = host

    def provision(self, model):
        with tempfile.TemporaryDirectory(prefix="chatterbox-model-", dir=os.getcwd()) as root:
            env = os.environ.copy()
            env.update(MARTLET_CHATTERBOX_ROOT=root, CHATTERBOX_MODEL=model, MARTLET_CHATTERBOX_FIXTURE_REAL_IDENTITY="1")
            done = subprocess.run([sys.executable, str(HOST), "provision", "--fixture"], env=env, cwd=ROOT.parents[1],
                                  stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            config = json.loads((Path(root) / "models" / "worker-config.json").read_text(encoding="utf-8")) if done.returncode == 0 else None
            return done, config

    def test_each_model_reports_its_own_pinned_weights(self):
        pinned = self.host.PINNED_MODELS
        self.assertEqual(sorted(pinned), ["chatterbox-nano", "chatterbox-original", "chatterbox-turbo"])
        for key, model in pinned.items():
            weights = next(a for a in self.host._identity("chatterbox", pinned=model)["artifacts"] if a["role"] == "model_weights")
            # The relay accepts a worker only when this names the route's model, revision and SHA-256.
            self.assertEqual(weights["artifact_id"], key)
            self.assertEqual(weights["revision"], model.revision)
            self.assertEqual(len(weights["sha256"]), 64)
        nano, turbo = pinned["chatterbox-nano"].files, pinned["chatterbox-turbo"].files
        # Nano's decoder, voice encoder and tokenizer are Turbo's files; only T3 differs.
        self.assertEqual({r: f[:3] for r, f in nano.items() if r != "model_weights"}, {r: f[:3] for r, f in turbo.items() if r != "model_weights"})
        self.assertEqual(nano["model_weights"][0], "t3_nano_v1.safetensors")
        self.assertEqual(pinned["chatterbox-original"].files["model_weights"][0], "t3_cfg.safetensors")
        self.assertEqual(pinned["chatterbox-original"].family, "original")
        self.assertEqual(pinned["chatterbox-original"].base_url,
                         "https://huggingface.co/ResembleAI/chatterbox/resolve/5bb1f6ee58e50c3b8d408bc82a6d3740c2db6e18/")

    def test_provisioning_writes_the_chosen_model_and_refuses_others(self):
        for key in ("chatterbox-nano", "chatterbox-original"):
            done, config = self.provision(key)
            self.assertEqual(done.returncode, 0, done.stderr)
            self.assertEqual(config["model"], key)
            self.assertEqual(set(config["files"]), set(self.host.PINNED_MODELS[key].files))
        done, _ = self.provision("chatterbox-multilingual")
        self.assertNotEqual(done.returncode, 0)
        self.assertIn("not a model this service installs", done.stderr)

    @unittest.skipUnless(_have("torch"), "needs PyTorch")
    def test_auto_uses_the_gpu_only_when_there_is_one(self):
        import torch

        self.assertEqual(self.host._device("auto"), "cuda:0" if torch.cuda.is_available() else "cpu")
        self.assertEqual(self.host._device("cuda:1"), "cuda:1")

    def test_each_sentence_gets_its_style_and_the_original_model_never_reads_the_tags(self):
        parts = self.host._original_parts
        self.assertEqual(parts("Okay. [expressive] That's amazing! Calm again."),
                         [("Okay.", False, False), ("That's amazing!", True, False), ("Calm again.", False, False)])
        self.assertEqual(parts("[Whispering] It's a secret. [expressive] [whispering] Wow, really?"),
                         [("It's a secret.", False, True), ("Wow, really?", True, True)])
        # Neighbouring sentences in one style stay one part; a tag with nothing after it styles nothing.
        self.assertEqual(parts("[expressive] Yes! [expressive] Yes! [expressive]"), [("Yes! Yes!", True, False)])
        self.assertEqual(parts("Well, [expressive] look at that. Hm."), [("Well,", False, False), ("look at that.", True, False),
                                                                           ("Hm.", False, False)])

    def test_style_defaults_to_resembles_tips_and_must_stay_in_range(self):
        host = self.host
        self.assertEqual(host._parse_request(request_body()).style, host.VoiceStyle((0.5, 0.5), (0.7, 0.3)))
        body = request_body()
        body["style"] = {"general": {"exaggeration": 0.4, "cfg_weight": 0.6}, "expressive": {"exaggeration": 1, "cfg_weight": 0}}
        self.assertEqual(host._parse_request(body).style, host.VoiceStyle((0.4, 0.6), (1.0, 0.0)))
        for bad in ({"general": {"exaggeration": 0.1, "cfg_weight": 0.5}, "expressive": {"exaggeration": 0.7, "cfg_weight": 0.3}},
                    {"general": {"exaggeration": 0.5, "cfg_weight": 1.5}, "expressive": {"exaggeration": 0.7, "cfg_weight": 0.3}},
                    {"general": {"exaggeration": 0.5, "cfg_weight": 0.5}},
                    {"general": {"exaggeration": "0.5", "cfg_weight": 0.5}, "expressive": {"exaggeration": 0.7, "cfg_weight": 0.3}}):
            body["style"] = bad
            with self.assertRaises(host.ContractError):
                host._parse_request(body)

    def test_the_original_model_speaks_each_part_in_its_style(self):
        host = self.host
        spoken = []

        class Original:
            device, capped = "cpu", False

            def use_reference(self, audio):
                return False

            def speak(self, text, exaggeration, cfg_weight, whisper=False):
                spoken.append((text, exaggeration, cfg_weight, whisper))
                return b"\x01\x00" * 480

        engine = host.EngineHost()
        engine.model, engine.original, engine.engine_kind, engine.state = object(), Original(), "chatterbox", "busy"
        engine.identity = host._identity("chatterbox", pinned=host.PINNED_MODELS["chatterbox-original"])
        body = request_body(chunks=[{"index": 0, "chunk_id": "a", "text": "Hi. [expressive] We won!"},
                                    {"index": 1, "chunk_id": "b", "text": "[whispering] \"Don't tell.\""}])
        body["style"] = {"general": {"exaggeration": 0.45, "cfg_weight": 0.5}, "expressive": {"exaggeration": 0.9, "cfg_weight": 0.25}}
        job = host.Job(host._parse_request(body), engine.identity)
        engine.active = job
        engine.run(job)
        events = []
        while (event := job.queue.get(timeout=1)) is not None:
            events.append(event)
        self.assertEqual(spoken, [("Hi.", 0.45, 0.5, False), ("We won!", 0.9, 0.25, False), ("Don't tell.", 0.45, 0.5, True)])
        self.assertEqual([f["frame"]["chunk_index"] for f in events if f["kind"] == "audio_frame"], [0, 0, 1])
        self.assertEqual(events[-1]["kind"], "completed")
        self.assertEqual(engine.state, "ready")
        self.assertEqual((engine.expressive_parts, engine.whispered_parts), (1, 1))
        self.assertEqual(engine.last_style, host.VoiceStyle((0.45, 0.5), (0.9, 0.25)))


if __name__ == "__main__":
    unittest.main()
