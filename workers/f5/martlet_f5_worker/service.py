from __future__ import annotations

import threading
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any, Callable, Mapping

from .contract import (
    CONTRACT_ID,
    MAX_CANCEL_SECONDS,
    MAX_EVENTS,
    MAX_FRAME_BYTES,
    MAX_SAMPLES,
    MAX_WARMUP_SECONDS,
    PROTOCOL_VERSION,
    ContractError,
    RequestIds,
    SynthesisRequest,
    WorkerIdentity,
    error_wire,
    fail,
    format_utc,
    make_event,
    make_frame,
    parse_contract_header,
    parse_utc,
    require,
    require_array,
    require_exact_keys,
    require_object,
    require_string,
    validate_identifier,
    validate_sha256,
    validate_uuid,
)
from .engine import (
    DeterministicFakeEngine,
    EngineFault,
    EngineOutOfMemory,
    F5ProductionEngine,
    SynthesisEngine,
)
from .identity import (
    WorkerConfig,
    verify_artifacts,
    verify_expected_identity,
    verify_runtime_inventory,
    verify_runtime_metadata,
)
from .policy import SideEffectPolicy

Emitter = Callable[[dict[str, Any]], None]


@dataclass(frozen=True)
class _CancelCommand:
    ids: RequestIds
    action_id: str
    destination_id: str
    reference_revision: str

    @classmethod
    def parse(cls, message: Mapping[str, Any]) -> "_CancelCommand":
        require_exact_keys(
            message,
            (
                "contract_id",
                "protocol_version",
                "type",
                "ids",
                "action_id",
                "destination_id",
                "reference_revision",
            ),
            "cancel",
        )
        parse_contract_header(message)
        require(message["type"] == "cancel", "invalid_request", "Invalid message type.")
        return cls(
            ids=RequestIds.parse(message["ids"]),
            action_id=validate_uuid(message["action_id"], "action_id") or "",
            destination_id=validate_identifier(
                message["destination_id"],
                "destination_id",
                128,
            ),
            reference_revision=validate_sha256(
                message["reference_revision"],
                "reference_revision",
            ),
        )


class _Job:
    def __init__(
        self,
        request: SynthesisRequest,
        worker: WorkerIdentity,
        emit: Emitter,
    ) -> None:
        self.request = request
        self.worker = worker
        self.emit = emit
        self.gate = threading.Lock()
        self.wake = threading.Event()
        self.engine_cancel = threading.Event()
        self.cancel_requested = False
        self.terminal = False
        self.event_sequence = 0
        self.frame_sequence = 0
        self.sample_offset = 0
        self.event_count = 0
        self.deadline_monotonic = time.monotonic() + max(
            0.0, (request.deadline_utc - datetime.now(timezone.utc)).total_seconds()
        )
        self.thread: threading.Thread | None = None

    def matches_cancel(self, command: _CancelCommand) -> bool:
        request = self.request
        return (
            command.ids == request.ids
            and command.action_id == request.action_id
            and command.destination_id == request.destination_id
            and command.reference_revision == request.reference.reference_revision
        )

    def started(self) -> None:
        with self.gate:
            self._emit_locked(
                make_event(
                    kind="started",
                    sequence=self.event_sequence,
                    ids=self.request.ids,
                    worker=self.worker,
                    reference_revision=self.request.reference.reference_revision,
                )
            )
            self.event_sequence += 1

    def request_cancel(self) -> None:
        with self.gate:
            self.cancel_requested = True
            self.engine_cancel.set()
            self.wake.set()

    def emit_pcm(self, chunk_index: int, pcm: bytes) -> bool:
        with self.gate:
            if self.terminal or self.cancel_requested:
                return False
            if self._deadline_expired():
                self.engine_cancel.set()
                self._fail_locked(
                    ContractError(
                        "deadline_exceeded",
                        "The synthesis deadline expired; late output was discarded.",
                        stage="synthesis",
                        action_id="f5.retry-request",
                    )
                )
                return False
            if (
                len(pcm) == 0
                or len(pcm) % 2 != 0
                or len(pcm) > MAX_FRAME_BYTES
                or self.sample_offset + len(pcm) // 2 > MAX_SAMPLES
            ):
                self.engine_cancel.set()
                self._fail_locked(
                    ContractError(
                        "internal_failure",
                        "The engine emitted invalid or over-limit PCM.",
                        stage="pcm_conversion",
                        action_id="f5.restart-worker",
                    )
                )
                return False
            frame = make_frame(
                sequence=self.frame_sequence,
                chunk_index=chunk_index,
                sample_offset=self.sample_offset,
                pcm=pcm,
            )
            self._emit_locked(
                make_event(
                    kind="audio_frame",
                    sequence=self.event_sequence,
                    ids=self.request.ids,
                    worker=self.worker,
                    reference_revision=self.request.reference.reference_revision,
                    frame=frame,
                )
            )
            self.frame_sequence += 1
            self.event_sequence += 1
            self.sample_offset += len(pcm) // 2
            return True

    def chunk_completed(self, chunk_index: int) -> bool:
        with self.gate:
            if self.terminal or self.cancel_requested:
                return False
            if self._deadline_expired():
                self.engine_cancel.set()
                self._fail_locked(self._deadline_error())
                return False
            self._emit_locked(
                make_event(
                    kind="chunk_completed",
                    sequence=self.event_sequence,
                    ids=self.request.ids,
                    worker=self.worker,
                    reference_revision=self.request.reference.reference_revision,
                    chunk_index=chunk_index,
                    final_sample_count=self.sample_offset,
                )
            )
            self.event_sequence += 1
            return True

    def completed(self) -> None:
        with self.gate:
            if self.terminal:
                return
            if self.cancel_requested:
                self._canceled_locked()
                return
            if self._deadline_expired():
                self._fail_locked(
                    ContractError(
                        "deadline_exceeded",
                        "The synthesis deadline expired; late output was discarded.",
                        stage="synthesis",
                        action_id="f5.retry-request",
                    )
                )
                return
            self._emit_locked(
                make_event(
                    kind="completed",
                    sequence=self.event_sequence,
                    ids=self.request.ids,
                    worker=self.worker,
                    reference_revision=self.request.reference.reference_revision,
                    final_sample_count=self.sample_offset,
                )
            )
            self.event_sequence += 1
            self.terminal = True
            self.wake.set()

    def canceled(self) -> None:
        with self.gate:
            if not self.terminal and self.cancel_requested:
                self._canceled_locked()

    def expired(self) -> None:
        with self.gate:
            if not self.terminal:
                self.engine_cancel.set()
                self._fail_locked(self._deadline_error())

    def failed(self, error: ContractError) -> None:
        with self.gate:
            if not self.terminal:
                self._fail_locked(error)

    def is_terminal(self) -> bool:
        with self.gate:
            return self.terminal

    def _deadline_expired(self) -> bool:
        return (
            time.monotonic() >= self.deadline_monotonic
            or datetime.now(timezone.utc) >= self.request.deadline_utc
        )

    @staticmethod
    def _deadline_error() -> ContractError:
        return ContractError(
            "deadline_exceeded",
            "The synthesis deadline expired; late output was discarded.",
            stage="synthesis",
            action_id="f5.retry-request",
        )

    def _canceled_locked(self) -> None:
        self._emit_locked(
            make_event(
                kind="canceled",
                sequence=self.event_sequence,
                ids=self.request.ids,
                worker=self.worker,
                reference_revision=self.request.reference.reference_revision,
                final_sample_count=self.sample_offset,
                cancellation=self.worker.cancellation,
            )
        )
        self.event_sequence += 1
        self.terminal = True
        self.wake.set()

    def _fail_locked(self, error: ContractError) -> None:
        self._emit_locked(
            make_event(
                kind="failed",
                sequence=self.event_sequence,
                ids=self.request.ids,
                worker=self.worker,
                reference_revision=self.request.reference.reference_revision,
                error=error_wire(error),
            )
        )
        self.event_sequence += 1
        self.terminal = True
        self.wake.set()

    def _emit_locked(self, message: dict[str, Any]) -> None:
        terminal = message["kind"] in {"completed", "canceled", "failed"}
        if self.event_count >= MAX_EVENTS - (0 if terminal else 1):
            self.engine_cancel.set()
            raise EngineFault("event limit exceeded")
        self.emit(message)
        self.event_count += 1


class WorkerService:
    def __init__(
        self,
        config: WorkerConfig,
        emit: Emitter,
        *,
        engine: SynthesisEngine | None = None,
        policy: SideEffectPolicy | None = None,
    ) -> None:
        self.config = config
        self.emit = emit
        self.policy = policy or SideEffectPolicy()
        self.engine = engine or (
            DeterministicFakeEngine()
            if config.engine == "fake"
            else F5ProductionEngine(config)
        )
        self.gate = threading.RLock()
        self.state = "cold"
        self.last_error: dict[str, Any] | None = None
        self.active_job: _Job | None = None
        self.job_threads: set[threading.Thread] = set()
        self.warmup_thread: threading.Thread | None = None
        self.warmup_watchdog: threading.Thread | None = None
        self.warmup_done = threading.Event()
        self.warmup_done.set()
        self.warmup_generation = 0
        self.warmup_cancel = threading.Event()
        self.closed = False
        self.engine_closed = False

    def startup(self) -> None:
        self._emit_state(None)

    def handle(self, message: Mapping[str, Any]) -> None:
        try:
            message_type = require_string(message.get("type"), "type")
            if message_type == "status":
                self._handle_status(message)
            elif message_type == "warmup":
                self._handle_warmup(message)
            elif message_type == "synthesize":
                self._handle_synthesize(message)
            elif message_type == "cancel":
                self._handle_cancel(message)
            elif message_type == "invalidate_reference_cache":
                self._handle_invalidate(message)
            else:
                fail("invalid_request", "The stdio message type is unsupported.")
        except ContractError as error:
            self.emit(self.protocol_error(error))

    def protocol_error(self, error: ContractError) -> dict[str, Any]:
        return {
            "contract_id": CONTRACT_ID,
            "error": error_wire(error),
            "protocol_version": dict(PROTOCOL_VERSION),
            "type": "protocol_error",
        }

    def close(self) -> None:
        with self.gate:
            if self.closed:
                return
            self.closed = True
            self.state = "stopping"
            job = self.active_job
            self.warmup_cancel.set()
        self._emit_state(None)
        if job is not None:
            job.request_cancel()
            job.canceled()
            if job.thread is not None:
                job.thread.join(MAX_CANCEL_SECONDS)
        warmup = self.warmup_thread
        if warmup is not None:
            warmup.join(MAX_CANCEL_SECONDS)
        warmup_watchdog = self.warmup_watchdog
        if warmup_watchdog is not None:
            warmup_watchdog.join(MAX_CANCEL_SECONDS)
        self._finish_shutdown()

    def _finish_shutdown(self) -> None:
        with self.gate:
            if (
                not self.closed
                or self.active_job is not None
                or not self.warmup_done.is_set()
                or self.engine_closed
            ):
                return
            self.engine.close()
            self.engine_closed = True
            self.state = "stopped"
            self._emit_state(None)

    def wait_closed(self) -> None:
        with self.gate:
            threads = (self.warmup_thread, self.warmup_watchdog, *self.job_threads)
        for thread in threads:
            if thread is not None:
                thread.join()
        self._finish_shutdown()

    def _handle_status(self, message: Mapping[str, Any]) -> None:
        require_exact_keys(
            message,
            ("contract_id", "protocol_version", "type", "control_id"),
            "status",
        )
        parse_contract_header(message)
        require(message["type"] == "status", "invalid_request", "Invalid message type.")
        control_id = validate_uuid(message["control_id"], "control_id") or ""
        self._emit_state(control_id)

    def _handle_warmup(self, message: Mapping[str, Any]) -> None:
        require_exact_keys(
            message,
            (
                "contract_id",
                "protocol_version",
                "type",
                "control_id",
                "expected_worker",
                "deadline_utc",
            ),
            "warmup",
        )
        parse_contract_header(message)
        require(message["type"] == "warmup", "invalid_request", "Invalid message type.")
        control_id = validate_uuid(message["control_id"], "control_id") or ""
        expected = WorkerIdentity.parse(message["expected_worker"])
        verify_expected_identity(expected, self.config.worker)
        deadline = parse_utc(message["deadline_utc"], "deadline_utc")
        remaining = (deadline - datetime.now(timezone.utc)).total_seconds()
        require(
            0 < remaining <= MAX_WARMUP_SECONDS,
            "deadline_exceeded",
            "The warmup deadline is expired or exceeds five minutes.",
        )
        deadline_monotonic = time.monotonic() + remaining
        with self.gate:
            self.policy.check()
            if self.closed:
                fail("model_not_ready", "The worker is stopping.")
            if self.active_job is not None or self.state == "busy":
                fail(
                    "busy",
                    "Synthesis is active; warmup cannot run concurrently.",
                    stage="warmup",
                    action_id="f5.wait-for-readiness",
                    retryable=True,
                )
            if self.state == "ready":
                self._emit_state(control_id)
                return
            if self.warmup_thread is not None and self.warmup_thread.is_alive():
                fail(
                    "busy",
                    "A worker warmup is already active.",
                    stage="warmup",
                    action_id="f5.wait-for-readiness",
                    retryable=True,
                )
            self.warmup_generation += 1
            generation = self.warmup_generation
            self.warmup_cancel = threading.Event()
            self.warmup_done = threading.Event()
            self.last_error = None
            self.state = "verifying_runtime"
            thread = threading.Thread(
                target=self._warmup,
                args=(control_id, deadline_monotonic, generation),
                name="martlet-f5-warmup",
                daemon=False,
            )
            watchdog = threading.Thread(
                target=self._watch_warmup,
                args=(control_id, deadline_monotonic, generation, self.warmup_done),
                name="martlet-f5-warmup-deadline",
                daemon=True,
            )
            self.warmup_thread = thread
            self.warmup_watchdog = watchdog
            self._emit_state(control_id)
            watchdog.start()
            thread.start()

    def _warmup(
        self,
        control_id: str,
        deadline_monotonic: float,
        generation: int,
    ) -> None:
        verified = None
        ready = False
        try:
            self.policy.check()
            verify_runtime_metadata(self.config)
            if not self._advance_warmup(
                "verifying_artifacts",
                control_id,
                generation,
                deadline_monotonic,
            ):
                return
            verified = verify_artifacts(
                self.config,
                deadline_monotonic=deadline_monotonic,
                cancel=self.warmup_cancel,
            )
            runtime_observation = (
                verify_runtime_inventory(
                    verified.paths["runtime_image"],
                    deadline_monotonic=deadline_monotonic,
                    cancel=self.warmup_cancel,
                )
                if self.config.engine == "f5"
                else None
            )
            if not self._advance_warmup("loading_model", control_id, generation, deadline_monotonic):
                return
            if not self._advance_warmup("warming", control_id, generation, deadline_monotonic):
                return
            self.engine.load(
                verified.paths,
                deadline_monotonic,
                runtime_observation,
            )
            verified.cleanup()
            verified = None
            ready = self._advance_warmup("ready", control_id, generation, deadline_monotonic)
        except ContractError as error:
            self._warmup_failed(control_id, error, generation)
        except EngineOutOfMemory:
            self._warmup_failed(
                control_id,
                ContractError(
                    "gpu_out_of_memory",
                    "The F5 engine exhausted GPU memory during warmup.",
                    stage="warmup",
                    action_id="f5.reduce-or-reprovision",
                ),
                generation,
            )
        except EngineFault:
            self._warmup_failed(
                control_id,
                ContractError(
                    "model_not_ready",
                    "The F5 engine could not complete warmup.",
                    stage="warmup",
                    action_id="f5.review-worker-runtime",
                ),
                generation,
            )
        except Exception:
            self._warmup_failed(
                control_id,
                ContractError(
                    "internal_failure",
                    "The worker failed during warmup.",
                    stage="warmup",
                    action_id="f5.restart-worker",
                ),
                generation,
            )
        finally:
            if not ready and not self.closed:
                self.engine.close()
            if verified is not None:
                verified.cleanup()
            self.warmup_done.set()
            self._finish_shutdown()

    def _watch_warmup(
        self,
        control_id: str,
        deadline_monotonic: float,
        generation: int,
        done: threading.Event,
    ) -> None:
        while True:
            remaining = max(0.0, deadline_monotonic - time.monotonic())
            if done.wait(min(remaining, 0.05)) or self.warmup_cancel.is_set():
                return
            if remaining <= 0:
                break
        error = ContractError(
            "deadline_exceeded",
            "Worker warmup exceeded its deadline; late readiness is quarantined.",
            stage="warmup",
            action_id="f5.retry-warmup",
        )
        with self.gate:
            if (
                self.closed
                or generation != self.warmup_generation
                or done.is_set()
            ):
                return
            self.warmup_cancel.set()
            self.last_error = error_wire(error)
            self.state = "failed"
        self._emit_state(control_id)

    def _warmup_failed(
        self,
        control_id: str,
        error: ContractError,
        generation: int,
    ) -> None:
        with self.gate:
            if self.closed or generation != self.warmup_generation:
                return
            if self.state == "failed" and self.warmup_cancel.is_set():
                return
            self.warmup_cancel.set()
            self.last_error = error_wire(error)
            self.state = "failed"
        self._emit_state(control_id)

    def _warmup_is_current(self, generation: int) -> bool:
        with self.gate:
            return (
                not self.closed
                and generation == self.warmup_generation
                and not self.warmup_cancel.is_set()
                and self.active_job is None
            )

    def _advance_warmup(
        self,
        state: str,
        control_id: str,
        generation: int,
        deadline_monotonic: float,
    ) -> bool:
        with self.gate:
            self.policy.check()
            if (
                self.closed
                or generation != self.warmup_generation
                or self.warmup_cancel.is_set()
                or self.active_job is not None
            ):
                return False
            if time.monotonic() >= deadline_monotonic:
                self._warmup_failed(
                    control_id,
                    ContractError(
                        "deadline_exceeded",
                        "Worker warmup exceeded its original deadline.",
                        stage="warmup",
                        action_id="f5.retry-warmup",
                    ),
                    generation,
                )
                return False
            self.state = state
            if state == "ready":
                self.last_error = None
                self.warmup_done.set()
            self._emit_state(control_id)
        return True

    def _handle_synthesize(self, message: Mapping[str, Any]) -> None:
        request = SynthesisRequest.parse(message, now=datetime.now(timezone.utc))
        try:
            if not self.config.worker or not request.expected_worker:
                fail("identity_mismatch", "A worker identity is required.")
            verify_expected_identity(request.expected_worker, self.config.worker)
        except ContractError as error:
            self._emit_request_failure(request, error)
            request.wipe()
            return
        with self.gate:
            if self.closed or self.state != "ready" or self.policy.denied.is_set():
                error = ContractError(
                    "busy" if self.state == "busy" else "model_not_ready",
                    "The worker is busy."
                    if self.state == "busy"
                    else "The worker has not completed warmup.",
                    stage="admission",
                    action_id="f5.wait-for-readiness",
                    retryable=True,
                )
                self._emit_request_failure(request, error)
                request.wipe()
                return
            job = _Job(request, self.config.worker, self.emit)
            self.active_job = job
            self.state = "busy"
            self._emit_state(None)
            job.started()
            worker_thread = threading.Thread(
                target=self._run_job,
                args=(job,),
                name="martlet-f5-synthesis",
                daemon=False,
            )
            watchdog_thread = threading.Thread(
                target=self._watch_job,
                args=(job,),
                name="martlet-f5-deadline",
                daemon=True,
            )
            job.thread = worker_thread
            self.job_threads = {thread for thread in self.job_threads if thread.is_alive()}
            self.job_threads.add(worker_thread)
            watchdog_thread.start()
            worker_thread.start()

    def _run_job(self, job: _Job) -> None:
        try:
            for chunk in job.request.chunks:
                self.policy.check()
                if job.is_terminal() or job.engine_cancel.is_set():
                    return
                if job._deadline_expired():
                    job.failed(job._deadline_error())
                    return
                emitted = False
                for fragment in self.engine.synthesize(
                    job.request.reference,
                    chunk.text,
                    job.engine_cancel,
                ):
                    self.policy.check()
                    if type(fragment) is not bytes:
                        raise EngineFault("engine fragment type")
                    for offset in range(0, len(fragment), MAX_FRAME_BYTES):
                        frame = fragment[offset : offset + MAX_FRAME_BYTES]
                        if not job.emit_pcm(chunk.index, frame):
                            return
                        emitted = True
                self.policy.check()
                if job.engine_cancel.is_set():
                    job.canceled()
                    return
                if not emitted:
                    raise EngineFault("engine emitted no samples")
                if not job.chunk_completed(chunk.index):
                    return
            job.completed()
        except ContractError as error:
            job.failed(error)
        except EngineOutOfMemory:
            job.failed(
                ContractError(
                    "gpu_out_of_memory",
                    "The F5 engine exhausted GPU memory; partial audio is incomplete.",
                    stage="synthesis",
                    action_id="f5.reduce-or-reprovision",
                )
            )
        except EngineFault:
            job.failed(
                ContractError(
                    "internal_failure",
                    "The F5 engine failed; partial audio is incomplete.",
                    stage="synthesis",
                    action_id="f5.restart-worker",
                )
            )
        except Exception:
            job.failed(
                ContractError(
                    "internal_failure",
                    "The worker failed during synthesis; partial audio is incomplete.",
                    stage="synthesis",
                    action_id="f5.restart-worker",
                )
            )
        finally:
            job.request.wipe()
            with self.gate:
                if self.active_job is job:
                    self.active_job = None
                    if not self.closed:
                        self.state = "failed" if self.policy.denied.is_set() else "ready"
                        if self.policy.denied.is_set():
                            self.last_error = error_wire(self.policy.error())
            self._emit_state(None)
            self._finish_shutdown()

    def _watch_job(self, job: _Job) -> None:
        remaining = max(0.0, job.deadline_monotonic - time.monotonic())
        signaled = job.wake.wait(remaining)
        if job.is_terminal():
            return
        if signaled and job.cancel_requested:
            job.canceled()
        elif not signaled:
            job.expired()

    def _handle_cancel(self, message: Mapping[str, Any]) -> None:
        command = _CancelCommand.parse(message)
        with self.gate:
            job = self.active_job
        if job is not None and job.matches_cancel(command):
            job.request_cancel()
        self.emit(
            {
                "compute_cancellation": self.config.worker.cancellation,
                "contract_id": CONTRACT_ID,
                "ids": command.ids.wire(),
                "local_discard_acknowledged": True,
                "protocol_version": dict(PROTOCOL_VERSION),
                "type": "cancel_response",
                "worker_may_continue": (
                    self.config.worker.cancellation != "cooperative_compute_cancel"
                ),
            }
        )

    def _handle_invalidate(self, message: Mapping[str, Any]) -> None:
        require_exact_keys(
            message,
            (
                "contract_id",
                "protocol_version",
                "type",
                "request_id",
                "destination_id",
                "expected_worker",
                "reference_revisions",
            ),
            "invalidate_reference_cache",
        )
        parse_contract_header(message)
        require(
            message["type"] == "invalidate_reference_cache",
            "invalid_request",
            "Invalid message type.",
        )
        request_id = validate_uuid(message["request_id"], "request_id") or ""
        validate_identifier(message["destination_id"], "destination_id", 128)
        expected = WorkerIdentity.parse(message["expected_worker"])
        verify_expected_identity(expected, self.config.worker)
        revisions_source = require_array(
            message["reference_revisions"],
            "reference_revisions",
        )
        require(
            0 < len(revisions_source) <= 2,
            "invalid_request",
            "One or two reference revisions are required.",
        )
        revisions = sorted(
            validate_sha256(value, f"reference_revisions[{index}]")
            for index, value in enumerate(revisions_source)
        )
        require(
            len(set(revisions)) == len(revisions),
            "invalid_request",
            "Reference revisions must be unique.",
        )
        self.emit(
            {
                "contract_id": CONTRACT_ID,
                "invalidated_reference_revisions": revisions,
                "protocol_version": dict(PROTOCOL_VERSION),
                "request_id": request_id,
                "type": "invalidate_reference_cache_response",
                "worker": self.config.worker.wire(),
            }
        )

    def _emit_request_failure(
        self,
        request: SynthesisRequest,
        error: ContractError,
    ) -> None:
        self.emit(
            make_event(
                kind="started",
                sequence=0,
                ids=request.ids,
                worker=self.config.worker,
                reference_revision=request.reference.reference_revision,
            )
        )
        self.emit(
            make_event(
                kind="failed",
                sequence=1,
                ids=request.ids,
                worker=self.config.worker,
                reference_revision=request.reference.reference_revision,
                error=error_wire(error),
            )
        )

    def _set_state(self, state: str, control_id: str | None) -> None:
        with self.gate:
            if self.closed and state not in {"stopping", "stopped"}:
                return
            self.state = state
        self._emit_state(control_id)

    def _emit_state(self, control_id: str | None) -> None:
        with self.gate:
            if self.policy.denied.is_set() and self.state not in {"stopping", "stopped"}:
                self.state = "failed"
                self.last_error = error_wire(self.policy.error())
            active_request_id = (
                self.active_job.request.ids.request_id
                if self.active_job is not None
                else None
            )
            message = {
                "active_request_id": active_request_id,
                "contract_id": CONTRACT_ID,
                "control_id": control_id,
                "last_error": self.last_error,
                "observed_at_utc": format_utc(datetime.now(timezone.utc)),
                "protocol_version": dict(PROTOCOL_VERSION),
                "ready": self.state == "ready",
                "state": self.state,
                "type": "worker_state",
                "worker": self.config.worker.wire(),
            }
        self.emit(message)
