from __future__ import annotations

import copy
import threading
import time
import unittest

from martlet_f5_worker.engine import DeterministicFakeEngine, SynthesisEngine
from martlet_f5_worker.service import WorkerService

from support import (
    OutputCollector,
    WorkerFixture,
    cancel_message,
    synthesize_message,
    warmup_message,
)


class ServiceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.fixture = WorkerFixture()
        self.services: list[WorkerService] = []

    def tearDown(self) -> None:
        for service in self.services:
            service.close()
        self.fixture.close()

    def service(
        self,
        engine: SynthesisEngine | None = None,
    ) -> tuple[WorkerService, OutputCollector]:
        collector = OutputCollector()
        service = WorkerService(self.fixture.config, collector.emit, engine=engine)
        self.services.append(service)
        service.handle(warmup_message(self.fixture.worker))
        collector.wait(
            lambda message: message.get("type") == "worker_state"
            and message.get("state") == "ready"
        )
        return service, collector

    def terminal(self, collector: OutputCollector) -> dict:
        return collector.wait(
            lambda message: message.get("type") == "event"
            and message.get("kind") in {"completed", "canceled", "failed"}
        )

    def test_fake_end_to_end_streams_contiguous_partial_frames(self) -> None:
        service, collector = self.service(
            DeterministicFakeEngine(sample_count=997, fragment_samples=173)
        )
        message = synthesize_message(self.fixture.worker)
        service.handle(message)
        terminal = self.terminal(collector)
        self.assertEqual("completed", terminal["kind"])
        self.assertEqual(997, terminal["final_sample_count"])
        events = [
            value
            for value in collector.messages
            if value.get("type") == "event"
            and value["ids"]["request_id"] == message["ids"]["request_id"]
        ]
        self.assertEqual(list(range(len(events))), [event["sequence"] for event in events])
        frames = [event["frame"] for event in events if event["kind"] == "audio_frame"]
        self.assertGreater(len(frames), 1)
        self.assertLess(frames[-1]["sample_count"], frames[0]["sample_count"])
        offset = 0
        for sequence, frame in enumerate(frames):
            self.assertEqual(sequence, frame["sequence"])
            self.assertEqual(offset, frame["sample_offset"])
            self.assertLessEqual(frame["sample_count"], 4_800)
            self.assertEqual(24_000, frame["format"]["sample_rate"])
            self.assertEqual(1, frame["format"]["channels"])
            self.assertEqual(16, frame["format"]["bits_per_sample"])
            offset += frame["sample_count"]
        self.assertEqual(997, offset)
        chunk_done = next(event for event in events if event["kind"] == "chunk_completed")
        self.assertEqual(997, chunk_done["final_sample_count"])

    def test_cancel_is_idempotent_and_discards_late_output(self) -> None:
        service, collector = self.service(
            DeterministicFakeEngine(
                sample_count=2_000,
                fragment_samples=100,
                delay_seconds=0.03,
            )
        )
        message = synthesize_message(self.fixture.worker)
        service.handle(message)
        collector.wait(
            lambda value: value.get("type") == "event"
            and value.get("kind") == "audio_frame"
        )
        cancel = cancel_message(message)
        service.handle(cancel)
        service.handle(cancel)
        terminal = self.terminal(collector)
        self.assertEqual("canceled", terminal["kind"])
        self.assertEqual("discard_only", terminal["cancellation"])
        responses = [
            value for value in collector.messages if value.get("type") == "cancel_response"
        ]
        self.assertEqual(2, len(responses))
        self.assertTrue(all(value["worker_may_continue"] for value in responses))
        terminal_index = collector.messages.index(terminal)
        time.sleep(0.1)
        self.assertFalse(
            any(
                value.get("type") == "event"
                and value.get("kind") == "audio_frame"
                for value in collector.messages[terminal_index + 1 :]
            )
        )

    def test_deadline_emits_failure_and_discards_late_output(self) -> None:
        service, collector = self.service(
            DeterministicFakeEngine(
                sample_count=500,
                fragment_samples=500,
                delay_seconds=0.25,
            )
        )
        message = synthesize_message(self.fixture.worker, deadline_seconds=0.08)
        service.handle(message)
        terminal = self.terminal(collector)
        self.assertEqual("failed", terminal["kind"])
        self.assertEqual("deadline_exceeded", terminal["error"]["code"])
        terminal_index = collector.messages.index(terminal)
        time.sleep(0.3)
        self.assertFalse(
            any(
                value.get("type") == "event"
                and value.get("kind") == "audio_frame"
                for value in collector.messages[terminal_index + 1 :]
            )
        )

    def test_engine_fault_and_oom_have_distinct_mapping(self) -> None:
        for fault, expected in (
            ("internal", "internal_failure"),
            ("oom", "gpu_out_of_memory"),
        ):
            with self.subTest(fault=fault):
                service, collector = self.service(
                    DeterministicFakeEngine(fault=fault)
                )
                message = synthesize_message(self.fixture.worker)
                service.handle(message)
                terminal = collector.wait(
                    lambda value: value.get("type") == "event"
                    and value.get("ids", {}).get("request_id")
                    == message["ids"]["request_id"]
                    and value.get("kind") == "failed"
                )
                self.assertEqual(expected, terminal["error"]["code"])

    def test_second_job_is_rejected_busy_without_queueing(self) -> None:
        service, collector = self.service(
            DeterministicFakeEngine(
                sample_count=1_000,
                fragment_samples=100,
                delay_seconds=0.03,
            )
        )
        first = synthesize_message(self.fixture.worker)
        second = synthesize_message(self.fixture.worker)
        service.handle(first)
        collector.wait(
            lambda value: value.get("type") == "event"
            and value.get("kind") == "started"
        )
        service.handle(second)
        rejected = collector.wait(
            lambda value: value.get("type") == "event"
            and value.get("ids", {}).get("request_id") == second["ids"]["request_id"]
            and value.get("kind") == "failed"
        )
        self.assertEqual("busy", rejected["error"]["code"])
        second_events = [
            value
            for value in collector.messages
            if value.get("type") == "event"
            and value.get("ids", {}).get("request_id") == second["ids"]["request_id"]
        ]
        self.assertEqual(["started", "failed"], [value["kind"] for value in second_events])
        self.assertEqual([0, 1], [value["sequence"] for value in second_events])
        service.handle(cancel_message(first))

    def test_warmup_is_rejected_while_synthesis_is_active(self) -> None:
        service, collector = self.service(
            DeterministicFakeEngine(
                sample_count=1_000,
                fragment_samples=100,
                delay_seconds=0.03,
            )
        )
        first = synthesize_message(self.fixture.worker)
        service.handle(first)
        collector.wait(
            lambda value: value.get("type") == "event"
            and value.get("ids", {}).get("request_id") == first["ids"]["request_id"]
            and value.get("kind") == "started"
        )
        service.handle(warmup_message(self.fixture.worker))
        error = collector.wait(
            lambda value: value.get("type") == "protocol_error"
            and value.get("error", {}).get("stage") == "warmup"
        )
        self.assertEqual("busy", error["error"]["code"])
        self.assertEqual("busy", service.state)
        self.assertEqual(first["ids"]["request_id"], service.active_job.request.ids.request_id)
        service.handle(cancel_message(first))

    def test_blocking_warmup_fails_at_deadline_and_cannot_publish_late_ready(self) -> None:
        class BlockingLoadEngine(DeterministicFakeEngine):
            def __init__(self) -> None:
                super().__init__()
                self.entered = threading.Event()
                self.release = threading.Event()

            def load(
                inner_self,
                artifact_paths,
                deadline_monotonic,
                runtime_observation=None,
            ):
                inner_self.entered.set()
                inner_self.release.wait(1)
                super(BlockingLoadEngine, inner_self).load(
                    artifact_paths,
                    deadline_monotonic + 1,
                    runtime_observation,
                )

        engine = BlockingLoadEngine()
        collector = OutputCollector()
        service = WorkerService(self.fixture.config, collector.emit, engine=engine)
        self.services.append(service)
        started = time.monotonic()
        service.handle(warmup_message(self.fixture.worker, seconds=0.08))
        self.assertTrue(engine.entered.wait(1))
        failed = collector.wait(
            lambda value: value.get("type") == "worker_state"
            and value.get("state") == "failed",
            timeout=0.5,
        )
        self.assertLess(time.monotonic() - started, 0.4)
        self.assertEqual("deadline_exceeded", failed["last_error"]["code"])
        engine.release.set()
        assert service.warmup_thread is not None
        service.warmup_thread.join(1)
        self.assertEqual("failed", service.state)

    def test_not_ready_failure_starts_then_fails_for_managed_stream_validator(self) -> None:
        collector = OutputCollector()
        service = WorkerService(self.fixture.config, collector.emit)
        self.services.append(service)
        message = synthesize_message(self.fixture.worker)
        service.handle(message)
        events = [
            value
            for value in collector.messages
            if value.get("type") == "event"
            and value.get("ids", {}).get("request_id") == message["ids"]["request_id"]
        ]
        self.assertEqual(["started", "failed"], [value["kind"] for value in events])
        self.assertEqual([0, 1], [value["sequence"] for value in events])
        self.assertEqual("model_not_ready", events[-1]["error"]["code"])

    def test_reference_buffers_are_wiped_after_completion(self) -> None:
        from martlet_f5_worker.contract import SynthesisRequest
        from datetime import datetime, timezone

        captured = SynthesisRequest.parse(
            synthesize_message(self.fixture.worker),
            now=datetime.now(timezone.utc),
        )

        class CapturingService(WorkerService):
            def _handle_synthesize(inner_self, message):
                original = SynthesisRequest.parse

                def return_captured(*args, **kwargs):
                    return captured

                with __import__("unittest").mock.patch(
                    "martlet_f5_worker.service.SynthesisRequest.parse",
                    side_effect=return_captured,
                ):
                    super(CapturingService, inner_self)._handle_synthesize(message)

        collector = OutputCollector()
        service = CapturingService(self.fixture.config, collector.emit)
        self.services.append(service)
        service.handle(warmup_message(self.fixture.worker))
        collector.wait(lambda value: value.get("state") == "ready")
        service.handle(synthesize_message(self.fixture.worker))
        self.terminal(collector)
        collector.wait(
            lambda value: value.get("type") == "worker_state"
            and value.get("state") == "ready"
        )
        self.assertEqual("", captured.reference.transcript)
        self.assertTrue(all(value == 0 for value in captured.reference.audio))

    def test_invalid_engine_fragment_maps_to_internal_failure(self) -> None:
        class OddEngine(DeterministicFakeEngine):
            def synthesize(self, reference, text, cancel):
                yield b"\x00"

        service, collector = self.service(OddEngine())
        service.handle(synthesize_message(self.fixture.worker))
        terminal = self.terminal(collector)
        self.assertEqual("internal_failure", terminal["error"]["code"])

    def test_cache_invalidation_echoes_exact_sorted_revisions(self) -> None:
        service, collector = self.service()
        revisions = ["b" * 64, "a" * 64]
        request_id = str(__import__("uuid").uuid4())
        service.handle(
            {
                "contract_id": "martlet.f5.worker",
                "destination_id": "fixture-local-gateway",
                "expected_worker": self.fixture.worker.wire(),
                "protocol_version": {"major": 1, "minor": 0},
                "reference_revisions": revisions,
                "request_id": request_id,
                "type": "invalidate_reference_cache",
            }
        )
        response = collector.wait(
            lambda value: value.get("type")
            == "invalidate_reference_cache_response"
        )
        self.assertEqual(sorted(revisions), response["invalidated_reference_revisions"])
        self.assertEqual(request_id, response["request_id"])
