from __future__ import annotations

import io
import json
import os
import subprocess
import sys
import threading
import time
import unittest
from dataclasses import replace
from pathlib import Path
from unittest import mock

from martlet_f5_worker.contract import ContractError, MAX_EVENTS
from martlet_f5_worker.engine import DeterministicFakeEngine, F5ProductionEngine
from martlet_f5_worker.identity import WorkerConfig
from martlet_f5_worker.jsonio import MAX_INPUT_LINE_BYTES, parse_canonical_json
from martlet_f5_worker.policy import SideEffectPolicy
from martlet_f5_worker.service import WorkerService
from martlet_f5_worker.stdio import CanonicalStdio

from support import (
    OutputCollector, WorkerFixture, cancel_message, synthesize_message, warmup_message,
)


class RegressionTests(unittest.TestCase):
    def setUp(self):
        self.fixture = WorkerFixture()
        self.services = []

    def tearDown(self):
        for service in self.services:
            service.close()
        self.fixture.close()

    def service(self, engine=None, policy=None):
        collector = OutputCollector()
        service = WorkerService(
            self.fixture.config, collector.emit, engine=engine, policy=policy,
        )
        self.services.append(service)
        return service, collector

    def ready(self, service, collector):
        service.handle(warmup_message(self.fixture.worker))
        collector.wait(lambda value: value.get("state") == "ready")
        service.warmup_thread.join(2)

    def test_hostile_json_is_bounded_content_free_and_recoverable(self):
        for data in (
            b'{"x":"\\ud800"}', b'{"x":1e999}', b'{"x":"\xff"}',
            b'{"x":' + b"[" * 2000 + b"0" + b"]" * 2000 + b"}",
        ):
            with self.subTest(data=data[:30]), self.assertRaises(ContractError) as caught:
                parse_canonical_json(data, maximum=MAX_INPUT_LINE_BYTES, name="input")
            self.assertEqual("invalid_request", caught.exception.code)
            self.assertNotIn("ud800", caught.exception.summary)
        output = io.BytesIO()
        transport = CanonicalStdio(io.BytesIO(b'{"x":"\\ud800"}\n'), output)
        service = WorkerService(self.fixture.config, transport.emit)
        self.assertEqual(0, transport.run(service))
        messages = [json.loads(line) for line in output.getvalue().splitlines()]
        self.assertEqual(1, sum(m["type"] == "protocol_error" for m in messages))

    def test_stdio_oversize_and_crlf_never_reach_engine(self):
        for data, exit_code in (
            (b"x" * (MAX_INPUT_LINE_BYTES + 1) + b"\n", 2),
            (b'{"type":"status"}\r\n', 0),
        ):
            output = io.BytesIO()
            transport = CanonicalStdio(io.BytesIO(data), output)
            service = WorkerService(self.fixture.config, transport.emit)
            self.assertEqual(exit_code, transport.run(service))
            messages = [json.loads(line) for line in output.getvalue().splitlines()]
            self.assertTrue(any(m["type"] == "protocol_error" for m in messages))
            self.assertFalse(any(m.get("state") == "ready" for m in messages))

    def test_event_bound_reserves_one_terminal_failure(self):
        class TinyFrames(DeterministicFakeEngine):
            def synthesize(self, reference, text, cancel):
                for _ in range(MAX_EVENTS + 1):
                    yield b"\x01\x00"

        service, collector = self.service(TinyFrames())
        self.ready(service, collector)
        service.handle(synthesize_message(self.fixture.worker, deadline_seconds=10))
        collector.wait(lambda m: m.get("kind") == "failed", timeout=10)
        events = [m for m in collector.messages if m["type"] == "event"]
        self.assertEqual(MAX_EVENTS, len(events))
        self.assertEqual("failed", events[-1]["kind"])
        self.assertEqual(list(range(MAX_EVENTS)), [m["sequence"] for m in events])

    def test_expired_admitted_job_never_enters_engine(self):
        class NeverCalled(DeterministicFakeEngine):
            def synthesize(self, reference, text, cancel):
                raise AssertionError("expired job invoked engine")

        service, collector = self.service(NeverCalled())
        self.ready(service, collector)
        with mock.patch("martlet_f5_worker.service._Job._deadline_expired", return_value=True):
            service.handle(synthesize_message(self.fixture.worker))
            terminal = collector.wait(lambda m: m.get("kind") == "failed")
        self.assertEqual("deadline_exceeded", terminal["error"]["code"])

    def test_warmup_ready_commit_checks_original_deadline_without_watchdog(self):
        class LateLoad(DeterministicFakeEngine):
            def load(self, paths, deadline, observation=None):
                time.sleep(max(0, deadline - time.monotonic()) + 0.03)
                self.loaded = True

        engine = LateLoad()
        service, collector = self.service(engine)
        with mock.patch.object(service, "_watch_warmup"):
            service.handle(warmup_message(self.fixture.worker, seconds=0.08))
            service.warmup_thread.join(2)
        self.assertEqual("failed", service.state)
        self.assertEqual("deadline_exceeded", service.last_error["code"])
        self.assertTrue(engine.closed)
        self.assertFalse(any(m.get("state") == "ready" for m in collector.messages))

    def test_close_retains_blocked_job_reference_and_engine_until_return(self):
        class Blocked(DeterministicFakeEngine):
            def __init__(self):
                super().__init__()
                self.entered = threading.Event()
                self.release = threading.Event()
                self.reference = None

            def synthesize(self, reference, text, cancel):
                self.reference = reference
                self.entered.set()
                self.release.wait(5)
                yield b"\x01\x00"

        engine = Blocked()
        service, collector = self.service(engine)
        self.ready(service, collector)
        service.handle(synthesize_message(self.fixture.worker))
        self.assertTrue(engine.entered.wait(2))
        thread = service.active_job.thread
        try:
            with mock.patch("martlet_f5_worker.service.MAX_CANCEL_SECONDS", 0.01):
                service.close()
            self.assertEqual("stopping", service.state)
            self.assertFalse(engine.closed)
            self.assertTrue(any(engine.reference.audio))
            self.assertNotEqual("", engine.reference.transcript)
        finally:
            engine.release.set()
            thread.join(2)
        self.assertTrue(engine.closed)
        self.assertEqual("stopped", service.state)
        self.assertFalse(any(engine.reference.audio))
        events = [m for m in collector.messages if m["type"] == "event"]
        self.assertEqual(["started", "canceled"], [m["kind"] for m in events])

    def test_cancel_blocked_job_is_busy_until_compute_returns(self):
        class Blocked(DeterministicFakeEngine):
            def __init__(self):
                super().__init__()
                self.entered = threading.Event()
                self.release = threading.Event()

            def synthesize(self, reference, text, cancel):
                self.entered.set()
                self.release.wait(5)
                yield b"\x01\x00"

        engine = Blocked()
        service, collector = self.service(engine)
        self.ready(service, collector)
        first = synthesize_message(self.fixture.worker)
        service.handle(first)
        self.assertTrue(engine.entered.wait(2))
        thread = service.active_job.thread
        try:
            service.handle(cancel_message(first))
            collector.wait(lambda m: m.get("kind") == "canceled")
            second = synthesize_message(self.fixture.worker)
            service.handle(second)
            rejected = collector.wait(
                lambda m: m.get("kind") == "failed"
                and m["ids"]["request_id"] == second["ids"]["request_id"]
            )
            self.assertEqual("busy", rejected["error"]["code"])
            self.assertEqual("busy", service.state)
        finally:
            engine.release.set()
            thread.join(2)
        first_events = [m for m in collector.messages if m.get("type") == "event"
                        and m["ids"]["request_id"] == first["ids"]["request_id"]]
        self.assertEqual(["started", "canceled"], [m["kind"] for m in first_events])

    def test_swallowed_denied_side_effect_cannot_publish_ready(self):
        policy = SideEffectPolicy()

        class DeniedLoad(DeterministicFakeEngine):
            def load(self, paths, deadline, observation=None):
                try:
                    policy.audit("socket.connect", ())
                except ContractError:
                    pass
                self.loaded = True

        engine = DeniedLoad()
        service, collector = self.service(engine, policy)
        service.handle(warmup_message(self.fixture.worker))
        service.warmup_thread.join(2)
        self.assertEqual("failed", service.state)
        self.assertEqual("execution_policy", service.last_error["stage"])
        self.assertTrue(engine.closed)
        service.handle(warmup_message(self.fixture.worker))
        self.assertFalse(any(m.get("state") == "ready" for m in collector.messages))

    def test_swallowed_denial_during_synthesis_latches_failed(self):
        policy = SideEffectPolicy()

        class DeniedSynthesis(DeterministicFakeEngine):
            def synthesize(self, reference, text, cancel):
                try:
                    policy.audit("subprocess.Popen", ())
                except ContractError:
                    pass
                yield b"\x01\x00"

        service, collector = self.service(DeniedSynthesis(), policy)
        self.ready(service, collector)
        service.handle(synthesize_message(self.fixture.worker))
        collector.wait(lambda m: m.get("state") == "failed")
        self.assertEqual("execution_policy", service.last_error["stage"])
        self.assertFalse(any(m.get("kind") in {"audio_frame", "completed"}
                             for m in collector.messages))

    def test_config_rejects_remote_and_drive_qualified_artifact_paths(self):
        source = json.loads(self.fixture.config_path.read_bytes())
        from martlet_f5_worker.jsonio import canonical_json_bytes

        for path in ("C:/escape", "vocab.txt:stream", "//remote/share", "bad\x00name"):
            source["artifact_files"]["vocabulary"] = path
            self.fixture.config_path.write_bytes(canonical_json_bytes(source))
            with self.subTest(path=path), self.assertRaises(ContractError):
                WorkerConfig.load(self.fixture.config_path)

    def test_real_audit_guard_blocks_socket_and_child_before_side_effect(self):
        code = """
import sys, socket, subprocess
from martlet_f5_worker.policy import SideEffectPolicy
from martlet_f5_worker.contract import ContractError
p = SideEffectPolicy()
sys.addaudithook(p.audit)
for call in (lambda: socket.socket(), lambda: subprocess.Popen([sys.executable, '-c', 'pass'])):
    try:
        call()
    except ContractError:
        pass
    else:
        raise AssertionError('side effect allowed')
assert p.denied.is_set()
"""
        result = subprocess.run(
            [sys.executable, "-c", code], capture_output=True, timeout=5,
            env=os.environ.copy(), check=False,
        )
        self.assertEqual(0, result.returncode, result.stderr.decode())
        self.assertEqual(b"", result.stdout)

    def test_native_diagnostics_cannot_corrupt_stdio_or_leak_to_stderr(self):
        code = """
import os, sys
from martlet_f5_worker.service import WorkerService
from martlet_f5_worker.__main__ import main
original = WorkerService.startup
def noisy(self):
    os.write(1, b'private native stdout')
    os.write(2, b'private native stderr')
    original(self)
WorkerService.startup = noisy
raise SystemExit(main(['--config', sys.argv[1]]))
"""
        result = subprocess.run(
            [sys.executable, "-c", code, str(self.fixture.config_path)],
            input=b"", capture_output=True, timeout=5, check=False,
        )
        self.assertEqual(0, result.returncode)
        self.assertEqual(b"", result.stderr)
        self.assertNotIn(b"private", result.stdout)
        messages = [json.loads(line) for line in result.stdout.splitlines()]
        self.assertEqual(["cold", "stopping", "stopped"], [m["state"] for m in messages])

    def test_production_synthesis_retains_temp_reference_until_compute_returns(self):
        import types

        config = replace(self.fixture.config, engine="f5", device="cuda:0")
        engine = F5ProductionEngine(config)
        entered, release = threading.Event(), threading.Event()
        captured = []

        def load_reference(path):
            captured.append(Path(path))
            self.assertTrue(Path(path).is_file())
            return object(), 24_000

        def infer(*args, **kwargs):
            entered.set()
            release.wait(5)
            yield [0.25] * 20, 24_000

        engine._model = types.SimpleNamespace(
            ema_model=object(), vocoder=object(), mel_spec_type="vocos", device="cuda:0",
        )
        engine._torchaudio = types.SimpleNamespace(load=load_reference)
        engine._utils = types.SimpleNamespace(infer_batch_process=infer)
        service, collector = self.service(engine)
        # Only the production synthesis boundary runs; imports/models are controlled doubles.
        service.state = "ready"
        message = synthesize_message(self.fixture.worker)
        service.handle(message)
        self.assertTrue(entered.wait(2))
        job = service.active_job
        try:
            service.handle(cancel_message(message))
            collector.wait(lambda m: m.get("kind") == "canceled")
            self.assertTrue(captured[0].exists())
            self.assertTrue(any(job.request.reference.audio))
            self.assertIsNotNone(engine._model)
        finally:
            release.set()
            job.thread.join(2)
        self.assertFalse(captured[0].exists())
        self.assertFalse(any(job.request.reference.audio))
        self.assertFalse(any(m.get("kind") == "audio_frame" for m in collector.messages))

    def test_warmup_close_retains_verified_artifacts_until_load_returns(self):
        class BlockingLoad(DeterministicFakeEngine):
            def __init__(self):
                super().__init__()
                self.entered, self.release = threading.Event(), threading.Event()
                self.paths = None

            def load(self, paths, deadline, observation=None):
                self.paths = paths
                self.entered.set()
                self.release.wait(5)
                self.loaded = True

        engine = BlockingLoad()
        service, collector = self.service(engine)
        service.handle(warmup_message(self.fixture.worker))
        self.assertTrue(engine.entered.wait(2))
        try:
            with mock.patch("martlet_f5_worker.service.MAX_CANCEL_SECONDS", 0.01):
                service.close()
            self.assertEqual("stopping", service.state)
            self.assertFalse(engine.closed)
            self.assertTrue(all(p.exists() for p in engine.paths.values()))
        finally:
            engine.release.set()
            service.warmup_thread.join(2)
        self.assertTrue(engine.closed)
        self.assertFalse(any(p.exists() for p in engine.paths.values()))
        self.assertEqual("stopped", service.state)
        self.assertFalse(any(m.get("state") == "ready" for m in collector.messages))

    def test_pcm_total_bound_fails_without_exceeding_sample_limit(self):
        from martlet_f5_worker.contract import MAX_SAMPLES

        class ExcessAudio(DeterministicFakeEngine):
            def synthesize(self, reference, text, cancel):
                for _ in range(MAX_SAMPLES // 4800 + 1):
                    yield b"\x01\x00" * 4800

        service, collector = self.service(ExcessAudio())
        self.ready(service, collector)
        service.handle(synthesize_message(self.fixture.worker, deadline_seconds=10))
        collector.wait(lambda m: m.get("kind") == "failed", timeout=10)
        frames = [m["frame"] for m in collector.messages if m.get("kind") == "audio_frame"]
        self.assertEqual(MAX_SAMPLES, sum(f["sample_count"] for f in frames))
        self.assertTrue(all(f["sample_count"] <= 4800 for f in frames))
        self.assertFalse(any(m.get("kind") == "completed" for m in collector.messages))

    def test_deadline_signal_race_cannot_be_reported_as_explicit_cancellation(self):
        from martlet_f5_worker.service import _Job

        class DeadlineReturn(DeterministicFakeEngine):
            def synthesize(self, reference, text, cancel):
                cancel.wait(3)
                return
                yield

        entered, release = threading.Event(), threading.Event()
        original = _Job._fail_locked

        def pause_timeout(job, error):
            if error.code == "deadline_exceeded":
                entered.set()
                release.wait(3)
            return original(job, error)

        service, collector = self.service(DeadlineReturn())
        self.ready(service, collector)
        with mock.patch.object(_Job, "_fail_locked", pause_timeout):
            service.handle(synthesize_message(self.fixture.worker, deadline_seconds=0.08))
            try:
                self.assertTrue(entered.wait(2))
                self.assertFalse(any(m.get("kind") == "canceled" for m in collector.messages))
            finally:
                release.set()
            terminal = collector.wait(lambda m: m.get("kind") == "failed")
        self.assertEqual("deadline_exceeded", terminal["error"]["code"])
        self.assertFalse(any(m.get("kind") == "canceled" for m in collector.messages))

    def test_background_denial_invalidates_ready_status_without_rewriting_shutdown(self):
        policy = SideEffectPolicy()
        service, collector = self.service(policy=policy)
        self.ready(service, collector)

        def denied_background_operation():
            try:
                policy.audit("socket.connect", ())
            except ContractError:
                pass

        thread = threading.Thread(target=denied_background_operation)
        thread.start()
        thread.join(2)
        service._emit_state(None)
        self.assertEqual("failed", collector.messages[-1]["state"])
        self.assertFalse(collector.messages[-1]["ready"])
        self.assertEqual("execution_policy", collector.messages[-1]["last_error"]["stage"])
        service.close()
        self.assertEqual("stopped", collector.messages[-1]["state"])

    def test_shutdown_joins_prior_job_still_publishing_after_readiness(self):
        service, collector = self.service()
        self.ready(service, collector)
        publishing, release, closed = threading.Event(), threading.Event(), threading.Event()
        original = service._emit_state
        first_thread = []

        def pause_first_ready(control_id):
            current = threading.current_thread()
            if current.name == "martlet-f5-synthesis" and not first_thread:
                first_thread.append(current)
                publishing.set()
                release.wait(5)
            original(control_id)

        with mock.patch.object(service, "_emit_state", side_effect=pause_first_ready):
            service.handle(synthesize_message(self.fixture.worker))
            self.assertTrue(publishing.wait(2))
            second = synthesize_message(self.fixture.worker)
            service.handle(second)
            collector.wait(lambda m: m.get("kind") == "completed"
                           and m["ids"]["request_id"] == second["ids"]["request_id"])
            service.close()

            def wait_shutdown():
                service.wait_closed()
                closed.set()

            waiter = threading.Thread(target=wait_shutdown)
            waiter.start()
            try:
                self.assertFalse(closed.wait(0.05))
            finally:
                release.set()
                waiter.join(2)
            self.assertTrue(closed.is_set())
            self.assertFalse(first_thread[0].is_alive())
