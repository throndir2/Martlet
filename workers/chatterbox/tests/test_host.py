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


def _have(*modules):
    try:
        for module in modules:
            __import__(module)
        return True
    except ImportError:
        return False


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

        class Model:
            t3 = T3()

        fast = object.__new__(self.host.FastTurbo)
        fast.model, fast.conditionals, fast.graph, fast.warm_conds = Model(), {"voice": object()}, object(), object()
        fast.model.t3.inference_turbo = fast._inference_turbo  # what __init__ installs: t3 -> fast -> model -> t3
        fast.close()
        self.assertNotIn("inference_turbo", vars(fast.model.t3))
        self.assertEqual(fast.model.t3.inference_turbo(), "library")
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


if __name__ == "__main__":
    unittest.main()
