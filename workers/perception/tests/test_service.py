from __future__ import annotations

import sys
import threading
import time
import unittest
from unittest import mock

from martlet_perception_worker.engine import DeterministicFakeEngine
from martlet_perception_worker import service as service_module
from martlet_perception_worker.service import WorkerService

from tests.support import (
    FixtureEnvironment,
    RecordingEmitter,
    cancel_wire,
    offer_wire,
    reference_from_offer,
    request_wire,
    warm_service,
    warmup_wire,
)


class WorkerServiceTests(unittest.TestCase):
    def test_one_warmup_state_machine_reaches_ready(self) -> None:
        with FixtureEnvironment("ocr") as environment:
            emitter = RecordingEmitter()
            service = WorkerService(environment.config, emitter)
            try:
                service.startup()
                warm_service(service, emitter, environment)
                states = [
                    item["state"]
                    for item in emitter.all("worker_state")
                    if item["state"]
                    in {
                        "verifying_runtime",
                        "verifying_artifacts",
                        "loading_model",
                        "warming",
                        "ready",
                    }
                ]
                self.assertEqual(
                    states,
                    [
                        "verifying_runtime",
                        "verifying_artifacts",
                        "loading_model",
                        "warming",
                        "ready",
                    ],
                )
                service.handle(warmup_wire(environment))
                self.assertEqual(service.state, "ready")
            finally:
                service.close()

    def test_ocr_and_vlm_complete_with_provenance_and_uncertainty(self) -> None:
        for role in ("ocr", "visual_question_answering"):
            with self.subTest(role=role), FixtureEnvironment(role) as environment:
                emitter = RecordingEmitter()
                service = WorkerService(environment.config, emitter)
                try:
                    service.startup()
                    warm_service(service, emitter, environment)
                    request = request_wire(environment)
                    service.handle(request)
                    response = emitter.wait_for(
                        lambda item: item.get("type") == "response"
                        and item.get("action_id") == request["action_id"]
                    )
                    self.assertEqual(response["outcome"], "completed")
                    observation = response["observation"]
                    self.assertEqual(observation["role"], role)
                    self.assertEqual(
                        observation["provenance"]["destination_id"],
                        "fixture-destination",
                    )
                    self.assertEqual(
                        observation["provenance"]["host_id"],
                        "fixture-host",
                    )
                    if role == "ocr":
                        self.assertEqual(
                            observation["ocr"]["regions"][0]["confidence"],
                            {"kind": "unavailable"},
                        )
                    else:
                        self.assertIn("uncertainty", observation["vlm"])
                        self.assertEqual(
                            observation["vlm"]["confidence"],
                            {"kind": "unavailable"},
                        )
                finally:
                    service.close()

    def test_one_job_admission_rejects_second_request_without_queue(self) -> None:
        with FixtureEnvironment("ocr") as environment:
            emitter = RecordingEmitter()
            engine = DeterministicFakeEngine(delay_seconds=0.25)
            service = WorkerService(environment.config, emitter, engine=engine)
            try:
                service.startup()
                warm_service(service, emitter, environment)
                first = request_wire(environment)
                second = request_wire(environment)
                service.handle(first)
                _wait_until(lambda: service.state == "busy")
                service.handle(second)
                refused = emitter.wait_for(
                    lambda item: item.get("type") == "response"
                    and item.get("action_id") == second["action_id"]
                )
                self.assertEqual(refused["outcome"], "failed")
                self.assertEqual(
                    refused["error"]["code"],
                    "resource_exhausted",
                )
                completed = emitter.wait_for(
                    lambda item: item.get("type") == "response"
                    and item.get("action_id") == first["action_id"]
                )
                self.assertEqual(completed["outcome"], "completed")
            finally:
                service.close()

    def test_warmup_and_job_races_fail_busy_or_not_ready(self) -> None:
        with FixtureEnvironment("ocr") as environment:
            emitter = RecordingEmitter()
            engine = DeterministicFakeEngine(
                warmup_delay_seconds=0.2,
                delay_seconds=0.2,
            )
            service = WorkerService(environment.config, emitter, engine=engine)
            try:
                service.startup()
                service.handle(warmup_wire(environment))
                request_before_ready = request_wire(environment)
                service.handle(request_before_ready)
                response = emitter.wait_for(
                    lambda item: item.get("type") == "response"
                    and item.get("action_id") == request_before_ready["action_id"]
                )
                self.assertEqual(response["error"]["code"], "model_not_ready")
                service.handle(warmup_wire(environment))
                busy = emitter.wait_for(
                    lambda item: item.get("type") == "protocol_error"
                    and item["error"]["code"] == "busy"
                )
                self.assertTrue(busy["error"]["retryable"])
                emitter.wait_for(
                    lambda item: item.get("type") == "worker_state"
                    and item.get("state") == "ready"
                )
                active = request_wire(environment)
                service.handle(active)
                _wait_until(lambda: service.state == "busy")
                previous_busy_count = len(
                    [
                        item
                        for item in emitter.all("protocol_error")
                        if item["error"]["code"] == "busy"
                    ]
                )
                service.handle(warmup_wire(environment))
                _wait_until(
                    lambda: len(
                        [
                            item
                            for item in emitter.all("protocol_error")
                            if item["error"]["code"] == "busy"
                        ]
                    )
                    > previous_busy_count
                )
            finally:
                service.close()

    def test_cancel_is_idempotent_discard_only_and_late_output_is_suppressed(
        self,
    ) -> None:
        with FixtureEnvironment("ocr") as environment:
            emitter = RecordingEmitter()
            service = WorkerService(
                environment.config,
                emitter,
                engine=DeterministicFakeEngine(delay_seconds=0.3),
            )
            try:
                service.startup()
                warm_service(service, emitter, environment)
                request = request_wire(environment)
                service.handle(request)
                _wait_until(lambda: service.state == "busy")
                cancel = cancel_wire(request)
                service.handle(cancel)
                service.handle(cancel)
                response = emitter.wait_for(
                    lambda item: item.get("type") == "response"
                    and item.get("action_id") == request["action_id"]
                )
                self.assertEqual(response["outcome"], "canceled")
                self.assertEqual(response["compute_cancellation"], "discard_only")
                self.assertTrue(response["worker_may_continue"])
                self.assertEqual(len(emitter.all("cancel_response")), 2)
                _wait_until(lambda: service.active_job is None)
                responses = [
                    item
                    for item in emitter.all("response")
                    if item["action_id"] == request["action_id"]
                ]
                self.assertEqual(len(responses), 1)
            finally:
                service.close()

    def test_deadline_discards_late_compute_and_never_publishes_observation(
        self,
    ) -> None:
        with FixtureEnvironment("ocr") as environment:
            emitter = RecordingEmitter()
            service = WorkerService(
                environment.config,
                emitter,
                engine=DeterministicFakeEngine(delay_seconds=0.3),
            )
            try:
                service.startup()
                warm_service(service, emitter, environment)
                request = request_wire(environment, deadline_seconds=0.08)
                service.handle(request)
                response = emitter.wait_for(
                    lambda item: item.get("type") == "response"
                    and item.get("action_id") == request["action_id"]
                )
                self.assertEqual(response["outcome"], "failed")
                self.assertEqual(
                    response["error"]["code"],
                    "deadline_exceeded",
                )
                self.assertIsNone(response["observation"])
                self.assertTrue(response["worker_may_continue"])
                _wait_until(lambda: service.active_job is None)
                self.assertEqual(
                    len(
                        [
                            item
                            for item in emitter.all("response")
                            if item["action_id"] == request["action_id"]
                        ]
                    ),
                    1,
                )
            finally:
                service.close()

    def test_partial_and_unknown_outputs_fail_without_synthetic_success(self) -> None:
        for fault in ("partial", "unknown"):
            with self.subTest(fault=fault), FixtureEnvironment("ocr") as environment:
                emitter = RecordingEmitter()
                service = WorkerService(
                    environment.config,
                    emitter,
                    engine=DeterministicFakeEngine(fault=fault),
                )
                try:
                    service.startup()
                    warm_service(service, emitter, environment)
                    request = request_wire(environment)
                    service.handle(request)
                    response = emitter.wait_for(
                        lambda item: item.get("type") == "response"
                    )
                    self.assertEqual(response["outcome"], "failed")
                    self.assertEqual(
                        response["error"]["code"],
                        "internal_failure",
                    )
                    self.assertIsNone(response["observation"])
                finally:
                    service.close()

    def test_oom_and_model_faults_map_truthfully(self) -> None:
        expectations = {
            "oom": "resource_exhausted",
            "model": "model_not_ready",
            "internal": "internal_failure",
        }
        for fault, expected in expectations.items():
            with self.subTest(fault=fault), FixtureEnvironment("ocr") as environment:
                emitter = RecordingEmitter()
                service = WorkerService(
                    environment.config,
                    emitter,
                    engine=DeterministicFakeEngine(fault=fault),
                )
                try:
                    service.startup()
                    warm_service(service, emitter, environment)
                    service.handle(request_wire(environment))
                    response = emitter.wait_for(
                        lambda item: item.get("type") == "response"
                    )
                    self.assertEqual(response["error"]["code"], expected)
                finally:
                    service.close()

    def test_ephemeral_owned_frame_is_one_use_and_cleaned(self) -> None:
        with FixtureEnvironment("ocr") as environment:
            emitter = RecordingEmitter()
            engine = CapturingFakeEngine()
            service = WorkerService(environment.config, emitter, engine=engine)
            try:
                service.startup()
                warm_service(service, emitter, environment)
                offer = offer_wire()
                service.handle(offer)
                emitter.wait_for(
                    lambda item: item.get("type") == "frame_owned"
                )
                request = request_wire(
                    environment,
                    reference=reference_from_offer(offer),
                )
                service.handle(request)
                response = emitter.wait_for(
                    lambda item: item.get("type") == "response"
                    and item.get("action_id") == request["action_id"]
                )
                self.assertEqual(response["outcome"], "completed")
                _wait_until(lambda: service.active_job is None)
                self.assertEqual(service.owned_frames, {})
                self.assertTrue(engine.frames)
                self.assertTrue(all(value == 0 for value in engine.frames[0]))
                replay = request_wire(
                    environment,
                    reference=reference_from_offer(offer),
                )
                service.handle(replay)
                replay_response = emitter.wait_for(
                    lambda item: item.get("type") == "response"
                    and item.get("action_id") == replay["action_id"]
                )
                self.assertEqual(
                    replay_response["error"]["code"],
                    "invalid_image",
                )
            finally:
                service.close()

    def test_host_loss_close_cancels_active_job_and_cleans_frame(self) -> None:
        with FixtureEnvironment("ocr") as environment:
            emitter = RecordingEmitter()
            engine = CapturingFakeEngine(delay_seconds=0.4)
            service = WorkerService(environment.config, emitter, engine=engine)
            service.startup()
            warm_service(service, emitter, environment)
            request = request_wire(environment)
            service.handle(request)
            _wait_until(lambda: service.state == "busy")
            service.close()
            response = emitter.wait_for(
                lambda item: item.get("type") == "response"
                and item.get("action_id") == request["action_id"]
            )
            self.assertEqual(response["outcome"], "canceled")
            self.assertEqual(service.state, "stopped")
            _wait_until(lambda: bool(engine.frames))
            _wait_until(lambda: all(value == 0 for value in engine.frames[0]))

    def test_blocking_warmup_deadline_quarantines_late_ready(self) -> None:
        with FixtureEnvironment("ocr") as environment:
            emitter = RecordingEmitter()
            service = WorkerService(
                environment.config,
                emitter,
                engine=DeterministicFakeEngine(warmup_delay_seconds=0.2),
            )
            try:
                service.startup()
                service.handle(warmup_wire(environment, deadline_seconds=0.05))
                failed = emitter.wait_for(
                    lambda item: item.get("type") == "worker_state"
                    and item.get("state") == "failed"
                )
                self.assertEqual(
                    failed["last_error"]["code"],
                    "deadline_exceeded",
                )
                time.sleep(0.25)
                self.assertEqual(service.state, "failed")
                self.assertFalse(
                    any(
                        item.get("state") == "ready"
                        for item in emitter.all("worker_state")
                    )
                )
            finally:
                service.close()

    def test_ready_is_not_published_until_verified_snapshots_are_cleaned(
        self,
    ) -> None:
        with FixtureEnvironment("ocr") as environment:
            emitter = RecordingEmitter()
            service = WorkerService(environment.config, emitter)
            cleanup_started = threading.Event()
            release_cleanup = threading.Event()
            real_verify = service_module.verify_artifacts

            def blocking_verify(*args, **kwargs):  # type: ignore[no-untyped-def]
                verified = real_verify(*args, **kwargs)
                real_cleanup = verified.cleanup

                def cleanup() -> None:
                    cleanup_started.set()
                    release_cleanup.wait(2)
                    real_cleanup()

                verified.cleanup = cleanup
                return verified

            try:
                service.startup()
                with mock.patch(
                    "martlet_perception_worker.service.verify_artifacts",
                    side_effect=blocking_verify,
                ):
                    service.handle(warmup_wire(environment))
                    self.assertTrue(cleanup_started.wait(2))
                    self.assertFalse(service.warmup_done.is_set())
                    self.assertNotEqual(service.state, "ready")
                    request = request_wire(environment)
                    service.handle(request)
                    refused = emitter.wait_for(
                        lambda item: item.get("type") == "response"
                        and item.get("action_id") == request["action_id"]
                    )
                    self.assertEqual(
                        refused["error"]["code"],
                        "model_not_ready",
                    )
                    release_cleanup.set()
                    emitter.wait_for(
                        lambda item: item.get("type") == "worker_state"
                        and item.get("state") == "ready"
                    )
                    self.assertTrue(service.warmup_done.is_set())
            finally:
                release_cleanup.set()
                service.close()

    def test_owned_frame_expires_and_is_wiped_without_another_command(self) -> None:
        with FixtureEnvironment("ocr") as environment:
            emitter = RecordingEmitter()
            service = WorkerService(environment.config, emitter)
            try:
                service.startup()
                offer = offer_wire(expires_seconds=0.06)
                service.handle(offer)
                emitter.wait_for(lambda item: item.get("type") == "frame_owned")
                retained = service.owned_frames[offer["reference_id"]].data
                _wait_until(
                    lambda: offer["reference_id"] not in service.owned_frames,
                    timeout=2,
                )
                self.assertTrue(all(value == 0 for value in retained))
            finally:
                service.close()

    def test_engine_health_failure_requires_another_warmup(self) -> None:
        with FixtureEnvironment("ocr") as environment:
            emitter = RecordingEmitter()
            service = WorkerService(
                environment.config,
                emitter,
                engine=DeterministicFakeEngine(fault="model"),
            )
            try:
                service.startup()
                warm_service(service, emitter, environment)
                failed_request = request_wire(environment)
                service.handle(failed_request)
                failed = emitter.wait_for(
                    lambda item: item.get("type") == "response"
                    and item.get("action_id") == failed_request["action_id"]
                )
                self.assertEqual(failed["error"]["code"], "model_not_ready")
                _wait_until(lambda: service.active_job is None)
                self.assertEqual(service.state, "failed")
                next_request = request_wire(environment)
                service.handle(next_request)
                refused = emitter.wait_for(
                    lambda item: item.get("type") == "response"
                    and item.get("action_id") == next_request["action_id"]
                )
                self.assertEqual(refused["error"]["code"], "model_not_ready")
            finally:
                service.close()

    def test_raw_memory_error_maps_to_resource_exhausted_and_quarantine(
        self,
    ) -> None:
        with FixtureEnvironment("ocr") as environment:
            emitter = RecordingEmitter()
            service = WorkerService(
                environment.config,
                emitter,
                engine=RawMemoryErrorEngine(),
            )
            try:
                service.startup()
                warm_service(service, emitter, environment)
                request = request_wire(environment)
                service.handle(request)
                response = emitter.wait_for(
                    lambda item: item.get("type") == "response"
                    and item.get("action_id") == request["action_id"]
                )
                self.assertEqual(
                    response["error"]["code"],
                    "resource_exhausted",
                )
                _wait_until(lambda: service.active_job is None)
                self.assertEqual(service.state, "failed")
            finally:
                service.close()

    def test_request_validation_memory_error_is_actionable(self) -> None:
        with FixtureEnvironment("ocr") as environment:
            emitter = RecordingEmitter()
            service = WorkerService(environment.config, emitter)
            try:
                service.startup()
                with mock.patch(
                    "martlet_perception_worker.service.PerceptionRequest.parse",
                    side_effect=MemoryError,
                ):
                    service.handle({"type": "execute"})
                error = emitter.wait_for(
                    lambda item: item.get("type") == "protocol_error"
                )
                self.assertEqual(
                    error["error"]["code"],
                    "resource_exhausted",
                )
            finally:
                service.close()

    def test_warmup_memory_error_maps_to_resource_exhausted(self) -> None:
        with FixtureEnvironment("ocr") as environment:
            emitter = RecordingEmitter()
            service = WorkerService(
                environment.config,
                emitter,
                engine=WarmupMemoryErrorEngine(),
            )
            try:
                service.startup()
                service.handle(warmup_wire(environment))
                failed = emitter.wait_for(
                    lambda item: item.get("type") == "worker_state"
                    and item.get("state") == "failed"
                )
                self.assertEqual(
                    failed["last_error"]["code"],
                    "resource_exhausted",
                )
            finally:
                service.close()

    def test_fake_never_imports_heavy_runtime_modules(self) -> None:
        names = ("torch", "transformers", "tesserocr", "PIL")
        before = {name: name in sys.modules for name in names}
        with FixtureEnvironment("visual_question_answering") as environment:
            emitter = RecordingEmitter()
            service = WorkerService(environment.config, emitter)
            try:
                service.startup()
                warm_service(service, emitter, environment)
                service.handle(request_wire(environment))
                emitter.wait_for(lambda item: item.get("type") == "response")
            finally:
                service.close()
        self.assertEqual(
            {name: name in sys.modules for name in names},
            before,
        )


class CapturingFakeEngine(DeterministicFakeEngine):
    def __init__(self, **kwargs: object) -> None:
        super().__init__(**kwargs)
        self.frames: list[bytearray] = []

    def infer(self, request, frame, cancel):  # type: ignore[no-untyped-def]
        if isinstance(frame, bytearray):
            self.frames.append(frame)
        return super().infer(request, frame, cancel)


class RawMemoryErrorEngine(DeterministicFakeEngine):
    def infer(self, request, frame, cancel):  # type: ignore[no-untyped-def]
        raise MemoryError


class WarmupMemoryErrorEngine(DeterministicFakeEngine):
    def load(self, artifact_paths, deadline_monotonic, runtime_observation=None):  # type: ignore[no-untyped-def]
        raise MemoryError


def _wait_until(predicate, timeout: float = 3.0) -> None:  # type: ignore[no-untyped-def]
    deadline = time.monotonic() + timeout
    while not predicate():
        if time.monotonic() >= deadline:
            raise AssertionError("condition was not reached")
        time.sleep(0.005)


if __name__ == "__main__":
    unittest.main()
