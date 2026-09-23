from __future__ import annotations

import copy
import io
import json
import os
import subprocess
import sys
import threading
import time
import types
import unittest
from dataclasses import replace
from datetime import datetime, timedelta, timezone
from unittest import mock

from martlet_perception_worker import contract
from martlet_perception_worker.contract import (
    ContractError, PerceptionRequest, UtcTimestamp, format_utc, inspect_png,
    validate_engine_output, validate_text,
)
from martlet_perception_worker.engine import (
    DeterministicFakeEngine, EngineModelUnavailable, TesserocrOcrEngine,
    TransformersLlavaNextEngine,
)
from martlet_perception_worker.identity import WorkerConfig
from martlet_perception_worker.jsonio import (
    JsonContractError, canonical_json_bytes, parse_canonical_json,
)
from martlet_perception_worker.policy import SideEffectPolicy
from martlet_perception_worker.service import WorkerService, _Clock
from martlet_perception_worker.stdio import CanonicalStdio
from tests.support import (
    FixtureEnvironment, RecordingEmitter, cancel_wire, offer_wire, png,
    reference_from_offer, request_wire, status_wire, warm_service, warmup_wire,
)
from tests.test_production_adapters import _FakeImage, _FakeTesseractApi


class RegressionTests(unittest.TestCase):
    def setUp(self):
        self.environment = FixtureEnvironment()
        self.services = []

    def tearDown(self):
        for service in self.services:
            service.close()
        self.environment.close()

    def service(self, engine=None, policy=None):
        emitter = RecordingEmitter()
        service = WorkerService(
            self.environment.config, emitter, engine=engine, policy=policy,
        )
        self.services.append(service)
        return service, emitter

    def ready(self, service, emitter):
        warm_service(service, emitter, self.environment)
        service.warmup_thread.join(2)

    def test_deep_json_surrogates_and_nonfinite_numbers_fail_redacted_and_recover(self):
        for data in (
            b'{"x":"\\ud800"}', b'{"x":1e999}', b'{"x":"\xff"}',
            b'{"x":' + b"[" * 2000 + b"0" + b"]" * 2000 + b"}",
        ):
            with self.subTest(data=data[:20]), self.assertRaises(JsonContractError):
                parse_canonical_json(data, maximum=10000, name="input")
            output = io.BytesIO()
            transport = CanonicalStdio(
                io.BytesIO(data + b"\n" + canonical_json_bytes(status_wire()) + b"\n"),
                output,
            )
            service = WorkerService(self.environment.config, transport.emit)
            self.assertEqual(0, transport.run(service))
            messages = [json.loads(line) for line in output.getvalue().splitlines()]
            self.assertEqual(1, sum(m["type"] == "protocol_error" for m in messages))
            self.assertTrue(any(m.get("control_id") for m in messages))

    def test_detected_language_counts_in_exact_output_budget(self):
        output = {"role": "ocr", "ocr": {"regions": [], "detected_language": "eng"}}
        validate_engine_output(output, role="ocr", maximum_output_bytes=3)
        with self.assertRaises(ContractError):
            validate_engine_output(output, role="ocr", maximum_output_bytes=2)

    def test_h03b_mapper_rejects_wrong_owner_and_contradictory_terminal_fields(self):
        request = PerceptionRequest.parse(request_wire(self.environment), now=datetime.now(timezone.utc))
        output = DeterministicFakeEngine()
        output.loaded = True
        observation = contract.make_observation(
            request, self.environment.worker, "fixture-host",
            output.infer(request, request.frame.content.data, threading.Event()),
            processed_at_utc=datetime.now(timezone.utc),
        )
        response = contract.response_wire(
            request, self.environment.worker, outcome="completed", observation=observation,
        )
        mutations = (
            lambda m: m.update(action_id="00000000-0000-0000-0000-000000000001"),
            lambda m: m.update(epoch=2),
            lambda m: m.update(worker_may_continue=True),
            lambda m: m["observation"]["provenance"].update(frame_sha256="f" * 64),
            lambda m: m["observation"]["provenance"].update(capture_permission_revision="f" * 64),
            lambda m: m.update(outcome="unknown"),
        )
        try:
            for mutate in mutations:
                changed = copy.deepcopy(response)
                mutate(changed)
                with self.assertRaises(ContractError):
                    contract.h03b_route_events(
                        request, changed, trace_id="00000000-0000-0000-0000-000000000002",
                    )
        finally:
            request.wipe()

    def test_duplicate_offer_wipes_new_buffer_without_destroying_original(self):
        service, emitter = self.service()
        offer = offer_wire()
        service.handle(offer)
        original = service.owned_frames[offer["reference_id"]]
        duplicate = contract.OfferedFrame.parse(offer, now=datetime.now(timezone.utc))
        with mock.patch.object(contract.OfferedFrame, "parse", return_value=duplicate):
            service.handle(offer)
        self.assertIs(original, service.owned_frames[offer["reference_id"]])
        self.assertTrue(any(original.data))
        self.assertFalse(any(duplicate.data))
        self.assertEqual("resource_exhausted", emitter.all("protocol_error")[-1]["error"]["code"])

    def test_transport_drops_parsed_input_before_waiting_for_another_line(self):
        test = self

        class Input:
            count = 0

            def readline(self, maximum):
                self.count += 1
                if self.count == 1:
                    return canonical_json_bytes(offer_wire()) + b"\n"
                locals_ = sys._getframe(1).f_locals
                test.assertNotIn("message", locals_)
                test.assertNotIn("line", locals_)
                return b""

        transport = CanonicalStdio(Input(), io.BytesIO())
        service = WorkerService(self.environment.config, transport.emit)
        self.assertEqual(0, transport.run(service))

    def test_c1_controls_match_managed_text_rejection(self):
        for character in ("\x7f", "\x80", "\x85", "\x9f"):
            with self.subTest(character=repr(character)), self.assertRaises(ContractError):
                validate_text("a" + character, "text", 8, 16)

    def test_exact_inner_epoch_boundary_and_cancel_overflow(self):
        for epoch, valid in (
            (0, False), (2_147_483_646, True), (2_147_483_647, False),
            (2**63 - 1, False), (2**63, False),
        ):
            request = request_wire(self.environment)
            request["epoch"] = request["frame"]["capture_epoch"] = epoch
            with self.subTest(epoch=epoch):
                if valid:
                    parsed = PerceptionRequest.parse(request, now=datetime.now(timezone.utc))
                    self.assertEqual(epoch, parsed.epoch)
                    parsed.wipe()
                else:
                    with self.assertRaises(ContractError):
                        PerceptionRequest.parse(request, now=datetime.now(timezone.utc))
                service, emitter = self.service()
                service.handle(cancel_wire(request))
                self.assertEqual(valid, bool(emitter.all("cancel_response")))

    def test_clock_never_rewinds_after_elapsed_time_or_forward_jump(self):
        base = datetime(2030, 1, 1, tzinfo=timezone.utc)
        with mock.patch("martlet_perception_worker.service.datetime") as utc, \
                mock.patch("martlet_perception_worker.service.time.monotonic_ns") as monotonic:
            utc.now.return_value = base
            monotonic.return_value = 10_000_000_000
            clock = _Clock()
            utc.now.return_value = base - timedelta(days=1)
            monotonic.return_value = 11_000_000_000
            self.assertEqual(UtcTimestamp.from_datetime(base + timedelta(seconds=1)), clock.now())
            utc.now.return_value = base + timedelta(seconds=9)
            self.assertEqual(UtcTimestamp.from_datetime(base + timedelta(seconds=9)), clock.now())
            utc.now.return_value = base
            monotonic.return_value = 12_000_000_000
            self.assertEqual(UtcTimestamp.from_datetime(base + timedelta(seconds=10)), clock.now())

    def test_lifetime_admitted_after_forward_jump_keeps_ticking_after_rollback(self):
        base = datetime(2030, 1, 1, tzinfo=timezone.utc)
        with mock.patch("martlet_perception_worker.service.datetime") as utc, \
                mock.patch("martlet_perception_worker.service.time.monotonic_ns") as monotonic:
            utc.now.return_value = base
            monotonic.return_value = 0
            clock = _Clock()
            utc.now.return_value = base + timedelta(seconds=100)
            monotonic.return_value = 1_000_000_000
            deadline = clock.now() + timedelta(seconds=1)
            utc.now.return_value = base
            monotonic.return_value = 2_000_000_000 - 100
            self.assertEqual(deadline.ticks - 1, clock.now().ticks)
            monotonic.return_value = 2_000_000_000
            self.assertEqual(deadline, clock.now())
            monotonic.return_value = 3_000_000_000
            self.assertGreater(clock.now(), deadline)

    def test_expiry_exact_seventh_tick_without_watchdog_scheduling(self):
        service, emitter = self.service()
        offer = offer_wire()
        expiry = contract.parse_utc(offer["expires_at_utc"], "expiry")
        with mock.patch.object(service, "_expire_owned_frames"):
            service.handle(offer)
        data = service.owned_frames[offer["reference_id"]].data
        with mock.patch.object(service.clock, "now", return_value=UtcTimestamp(expiry.ticks - 1)):
            service.handle(status_wire())
            self.assertTrue(service.owned_frames)
        with mock.patch.object(service.clock, "now", return_value=expiry):
            service.handle(status_wire())
        self.assertFalse(service.owned_frames)
        self.assertFalse(any(data))

    def test_consumed_and_expired_offers_share_one_bounded_watchdog(self):
        service, emitter = self.service()
        self.ready(service, emitter)
        expiry_thread = None
        for index in range(12):
            offer = offer_wire(reference_id=f"frame-{index}")
            service.handle(offer)
            if expiry_thread is None:
                expiry_thread = service.frame_thread
            self.assertIs(service.frame_thread, expiry_thread)
            request = request_wire(self.environment, reference=reference_from_offer(offer))
            service.handle(request)
            job = service.active_job
            if job is not None:
                job.thread.join(2)
            response = emitter.wait_for(lambda m: m.get("action_id") == request["action_id"])
            self.assertEqual("completed", response["outcome"])
        service.close()
        self.assertFalse(expiry_thread.is_alive())

    def test_expired_admitted_job_cannot_enter_engine_even_without_watchdog(self):
        engine = DeterministicFakeEngine()
        service, emitter = self.service(engine)
        self.ready(service, emitter)
        original_run = service._run_job

        def expire_then_run(job):
            with mock.patch.object(service.clock, "now", return_value=job.expires_at):
                original_run(job)

        with mock.patch.object(service, "_run_job", side_effect=expire_then_run), \
                mock.patch.object(service, "_watch_job"), \
                mock.patch.object(engine, "infer") as infer:
            service.handle(request_wire(self.environment))
            response = emitter.wait_for(lambda m: m.get("type") == "response")
            infer.assert_not_called()
        self.assertEqual("deadline_exceeded", response["error"]["code"])

    def test_blocked_close_retains_frame_and_engine_until_compute_returns(self):
        engine = BlockingEngine()
        service, emitter = self.service(engine)
        self.ready(service, emitter)
        service.handle(request_wire(self.environment))
        self.assertTrue(engine.entered.wait(2))
        job = service.active_job
        try:
            with mock.patch("martlet_perception_worker.service.MAX_CANCEL_SECONDS", 0.01):
                service.close()
            self.assertEqual("stopping", service.state)
            self.assertFalse(engine.closed)
            self.assertTrue(any(engine.frame))
            self.assertIs(job, service.active_job)
        finally:
            engine.release.set()
            job.thread.join(2)
        self.assertFalse(job.thread.is_alive())
        self.assertTrue(engine.closed)
        self.assertEqual("stopped", service.state)
        self.assertFalse(any(engine.frame))
        self.assertEqual(["canceled"], [m["outcome"] for m in emitter.all("response")])

    def test_cancel_retains_busy_and_suppresses_late_output(self):
        engine = BlockingEngine()
        service, emitter = self.service(engine)
        self.ready(service, emitter)
        first = request_wire(self.environment)
        service.handle(first)
        self.assertTrue(engine.entered.wait(2))
        job = service.active_job
        try:
            service.handle(cancel_wire(first))
            second = request_wire(self.environment)
            service.handle(second)
            rejected = emitter.wait_for(lambda m: m.get("action_id") == second["action_id"])
            self.assertEqual("resource_exhausted", rejected["error"]["code"])
            self.assertEqual("busy", service.state)
        finally:
            engine.release.set()
            job.thread.join(2)
        self.assertEqual(1, len([m for m in emitter.all("response")
                                 if m["action_id"] == first["action_id"]]))

    def test_warmup_commit_enforces_deadline_without_watchdog(self):
        class LateLoad(DeterministicFakeEngine):
            def load(self, paths, deadline, observation=None):
                time.sleep(max(0, deadline - time.monotonic()) + 0.02)
                self.loaded = True

        engine = LateLoad()
        service, emitter = self.service(engine)
        with mock.patch.object(service, "_watch_warmup"):
            service.handle(warmup_wire(self.environment, deadline_seconds=0.08))
            service.warmup_thread.join(2)
        self.assertEqual("failed", service.state)
        self.assertEqual("deadline_exceeded", service.last_error["code"])
        self.assertTrue(engine.closed)
        self.assertFalse(any(m.get("state") == "ready" for m in emitter.messages))

    def test_partially_loaded_engine_is_closed_on_load_failure(self):
        class PartialLoad(DeterministicFakeEngine):
            def load(self, paths, deadline, observation=None):
                self.loaded = True
                raise RuntimeError("private load diagnostic")

        engine = PartialLoad()
        service, emitter = self.service(engine)
        service.handle(warmup_wire(self.environment))
        service.warmup_thread.join(2)
        self.assertTrue(engine.closed)
        self.assertEqual("failed", service.state)
        self.assertNotIn("private", json.dumps(emitter.messages))

    def test_blocked_warmup_retains_snapshots_until_return(self):
        class BlockingLoad(DeterministicFakeEngine):
            def __init__(self):
                super().__init__()
                self.entered, self.release = threading.Event(), threading.Event()

            def load(self, paths, deadline, observation=None):
                self.paths = paths
                self.entered.set()
                self.release.wait(5)
                self.loaded = True

        engine = BlockingLoad()
        service, emitter = self.service(engine)
        service.handle(warmup_wire(self.environment))
        self.assertTrue(engine.entered.wait(2))
        try:
            with mock.patch("martlet_perception_worker.service.MAX_CANCEL_SECONDS", 0.01):
                service.close()
            self.assertEqual("stopping", service.state)
            self.assertFalse(engine.closed)
            self.assertTrue(all(path.exists() for path in engine.paths.values()))
        finally:
            engine.release.set()
            service.warmup_thread.join(2)
        self.assertEqual("stopped", service.state)
        self.assertTrue(engine.closed)
        self.assertFalse(any(path.exists() for path in engine.paths.values()))

    def test_swallowed_warmup_side_effect_latches_failure(self):
        policy = SideEffectPolicy()

        class DeniedLoad(DeterministicFakeEngine):
            def load(self, paths, deadline, observation=None):
                try:
                    policy.audit("socket.connect", ())
                except ContractError:
                    pass
                self.loaded = True

        engine = DeniedLoad()
        service, emitter = self.service(engine, policy)
        service.handle(warmup_wire(self.environment))
        service.warmup_thread.join(2)
        self.assertEqual("failed", service.state)
        self.assertEqual("execution_policy", service.last_error["stage"])
        self.assertTrue(engine.closed)
        service.handle(warmup_wire(self.environment))
        self.assertFalse(any(m.get("state") == "ready" for m in emitter.messages))

    def test_swallowed_inference_side_effect_cannot_publish_success(self):
        policy = SideEffectPolicy()

        class DeniedInference(DeterministicFakeEngine):
            def infer(self, request, frame, cancel):
                try:
                    policy.audit("subprocess.Popen", ())
                except ContractError:
                    pass
                return super().infer(request, frame, cancel)

        service, emitter = self.service(DeniedInference(), policy)
        self.ready(service, emitter)
        service.handle(request_wire(self.environment))
        response = emitter.wait_for(lambda m: m.get("type") == "response")
        self.assertEqual("failed", response["outcome"])
        emitter.wait_for(lambda m: m.get("state") == "failed")
        self.assertEqual("execution_policy", service.last_error["stage"])

    def test_background_denial_during_observation_cannot_commit_success(self):
        policy = SideEffectPolicy()
        service, emitter = self.service(policy=policy)
        self.ready(service, emitter)
        original = contract.make_observation

        def during_observation(*args, **kwargs):
            def denied():
                try:
                    policy.audit("socket.connect", ())
                except ContractError:
                    pass
            thread = threading.Thread(target=denied)
            thread.start()
            thread.join(2)
            self.assertFalse(thread.is_alive())
            return original(*args, **kwargs)

        with mock.patch(
            "martlet_perception_worker.service.make_observation",
            side_effect=during_observation,
        ):
            service.handle(request_wire(self.environment))
            response = emitter.wait_for(lambda m: m.get("type") == "response")
            emitter.wait_for(lambda m: m.get("state") == "failed")
        self.assertEqual("failed", response["outcome"])
        self.assertEqual("model_not_ready", response["error"]["code"])
        self.assertEqual(1, len(emitter.all("response")))

    def test_background_denial_invalidates_ready_but_not_shutdown(self):
        policy = SideEffectPolicy()
        service, emitter = self.service(policy=policy)
        self.ready(service, emitter)
        with self.assertRaises(ContractError):
            policy.audit("os.system", ())
        service.handle(status_wire())
        self.assertEqual("failed", service.state)
        service.close()
        service.handle(status_wire())
        self.assertEqual("stopped", service.state)

    def test_cleanup_failure_is_redacted_and_requires_restart(self):
        class BadClose(DeterministicFakeEngine):
            def close(self):
                raise RuntimeError("private cleanup diagnostic")

        service, emitter = self.service(BadClose(fault="model"))
        self.ready(service, emitter)
        service.handle(request_wire(self.environment))
        emitter.wait_for(lambda m: m.get("state") == "failed")
        self.assertTrue(service.restart_required)
        service.handle(warmup_wire(self.environment))
        self.assertTrue(emitter.all("protocol_error"))
        self.assertNotIn("private", json.dumps(emitter.messages))

    def test_png_validation_wipes_owned_compressed_and_row_buffers(self):
        for image, valid in ((png(), True), (png(filter_type=9), False)):
            buffers = []

            def tracked(*args):
                buffer = bytearray(*args)
                buffers.append(buffer)
                return buffer

            with mock.patch.object(contract, "bytearray", side_effect=tracked, create=True):
                if valid:
                    self.assertEqual((2, 2), inspect_png(image))
                else:
                    with self.assertRaises(ContractError):
                        inspect_png(image)
            self.assertTrue(buffers)
            self.assertTrue(all(not any(buffer) for buffer in buffers))

    def test_artifact_paths_reject_drive_stream_and_control_characters(self):
        source = json.loads(self.environment.config_path.read_bytes())
        for path in ("C:/escape", "model:stream", "bad\x00name", "bad\nname"):
            source["artifact_files"]["model_weights"] = path
            self.environment.config_path.write_bytes(canonical_json_bytes(source))
            with self.subTest(path=path), self.assertRaises(ContractError):
                WorkerConfig.load(self.environment.config_path)

    def test_transport_closes_duplicate_descriptor_and_discards_late_state(self):
        path = self.environment.root / "output.ndjson"
        with path.open("wb") as output:
            transport = CanonicalStdio(io.BytesIO(), output)
            descriptor = transport.output_descriptor
            service = WorkerService(self.environment.config, transport.emit)
            self.assertEqual(0, transport.run(service))
            with self.assertRaises(OSError):
                os.fstat(descriptor)
            transport.emit({"late": True})
        self.assertNotIn(b"late", path.read_bytes())

    def test_real_audit_guard_denies_before_socket_or_child_creation(self):
        code = """
import sys, socket, subprocess
from martlet_perception_worker.policy import SideEffectPolicy
from martlet_perception_worker.contract import ContractError
policy = SideEffectPolicy()
sys.addaudithook(policy.audit)
for call in (lambda: socket.socket(), lambda: subprocess.Popen([sys.executable, '-c', 'pass'])):
    try:
        call()
    except ContractError:
        pass
    else:
        raise AssertionError('side effect allowed')
assert policy.denied.is_set()
"""
        result = subprocess.run(
            [sys.executable, "-c", code], capture_output=True, timeout=5, check=False,
        )
        self.assertEqual(0, result.returncode, result.stderr.decode())
        self.assertEqual(b"", result.stdout)

    def test_native_background_output_is_sealed_after_engine_context_returns(self):
        code = """
import os, sys
from martlet_perception_worker.service import WorkerService
from martlet_perception_worker.__main__ import main
original = WorkerService.startup
def noisy(self):
    os.write(1, b'private native stdout')
    os.write(2, b'private native stderr')
    original(self)
WorkerService.startup = noisy
raise SystemExit(main(['--config', sys.argv[1]]))
"""
        result = subprocess.run(
            [sys.executable, "-c", code, str(self.environment.config_path)],
            input=b"", capture_output=True, timeout=5, check=False,
        )
        self.assertEqual(0, result.returncode, result.stderr.decode())
        self.assertEqual(b"", result.stderr)
        self.assertNotIn(b"private", result.stdout)
        self.assertEqual(["cold", "stopping", "stopped"],
                         [json.loads(line)["state"] for line in result.stdout.splitlines()])

    def test_ocr_overflow_is_not_silently_truncated(self):
        engine = TesserocrOcrEngine(replace(self.environment.config, engine="tesserocr_ocr", device="cpu"))
        source = _FakeImage("RGB")
        iterator = types.SimpleNamespace(
            GetUTF8Text=lambda _: "fixture", BoundingBox=lambda _: (0, 0, 2, 2),
            Confidence=lambda _: 50, Next=lambda _: True,
        )
        api = _FakeTesseractApi()
        api.GetIterator = lambda: iterator
        streams = []

        def opened(stream):
            streams.append(stream)
            return source

        engine._api = api
        engine._image_module = types.SimpleNamespace(open=opened)
        engine._tesserocr = types.SimpleNamespace(RIL=types.SimpleNamespace(TEXTLINE=1))
        request = PerceptionRequest.parse(request_wire(self.environment), now=datetime.now(timezone.utc))
        try:
            from martlet_perception_worker.engine import EngineFault
            with self.assertRaises(EngineFault):
                engine.infer(request, request.frame.content.data, threading.Event())
            self.assertTrue(source.closed)
            self.assertTrue(streams[0].closed)
            self.assertEqual(1, api.cleared)
        finally:
            request.wipe()
            engine.close()

    def test_vlm_image_cleanup_failure_cannot_return_success(self):
        engine = TransformersLlavaNextEngine(replace(
            self.environment.config, engine="transformers_llava_next", device="cuda:0",
        ))
        image = _FakeImage("RGB")
        image.close = mock.Mock(side_effect=RuntimeError("private image cleanup"))
        engine._image_module = types.SimpleNamespace(open=lambda _: image)
        engine._model = object()
        engine._processor = mock.Mock(side_effect=RuntimeError("fixture stops before tensors"))
        engine._torch = object()
        request = PerceptionRequest.parse(request_wire(self.environment), now=datetime.now(timezone.utc))
        try:
            with self.assertRaises(EngineModelUnavailable):
                engine.infer(request, request.frame.content.data, threading.Event())
        finally:
            request.wipe()


class BlockingEngine(DeterministicFakeEngine):
    def __init__(self):
        super().__init__()
        self.entered, self.release = threading.Event(), threading.Event()
        self.frame = None

    def infer(self, request, frame, cancel):
        self.frame = frame
        self.entered.set()
        self.release.wait(5)
        return super().infer(request, frame, cancel)
