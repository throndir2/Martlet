from __future__ import annotations

import threading
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any, Callable, Mapping

from .contract import (
    CONTRACT_ID,
    MAX_CANCEL_SECONDS,
    MAX_OWNED_FRAMES,
    MAX_OWNED_FRAME_BYTES,
    MAX_ROUTE_EPOCH,
    MAX_WARMUP_SECONDS,
    PROTOCOL_VERSION,
    ContractError,
    OfferedFrame,
    PerceptionRequest,
    RequestIds,
    UtcTimestamp,
    format_utc,
    make_observation,
    parse_contract_header,
    parse_utc,
    protocol_error_wire,
    require,
    require_exact_keys,
    require_string,
    response_wire,
    validate_uuid,
)
from .engine import (
    EngineFault,
    EngineInvalidImage,
    EngineModelUnavailable,
    EngineOutOfMemory,
    PerceptionEngine,
    create_engine,
    silence_third_party_output,
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


class _Clock:
    def __init__(self) -> None:
        self.latest = UtcTimestamp.from_datetime(datetime.now(timezone.utc))
        self.monotonic_tick = time.monotonic_ns() // 100
        self.gate = threading.Lock()

    def now(self) -> UtcTimestamp:
        with self.gate:
            current_tick = time.monotonic_ns() // 100
            elapsed = max(0, current_tick - self.monotonic_tick)
            self.monotonic_tick = max(self.monotonic_tick, current_tick)
            self.latest = max(
                UtcTimestamp(self.latest.ticks + elapsed),
                UtcTimestamp.from_datetime(datetime.now(timezone.utc)),
            )
            return self.latest


@dataclass(frozen=True)
class _CancelCommand:
    ids: RequestIds
    action_id: str
    epoch: int

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
                "epoch",
            ),
            "cancel",
        )
        parse_contract_header(message)
        require(message["type"] == "cancel", "invalid_request", "Invalid message type.")
        epoch = message["epoch"]
        require(
            type(epoch) is int and 0 < epoch <= MAX_ROUTE_EPOCH,
            "invalid_request",
            "cancel.epoch must be a positive integer.",
        )
        return cls(
            ids=RequestIds.parse(message["ids"]),
            action_id=validate_uuid(message["action_id"], "action_id") or "",
            epoch=epoch,
        )


class _Job:
    def __init__(
        self,
        request: PerceptionRequest,
        worker: Any,
        emit: Emitter,
        clock: _Clock,
        policy: SideEffectPolicy,
    ) -> None:
        self.request = request
        self.worker = worker
        self.emit = emit
        self.clock = clock
        self.policy = policy
        self.expires_at = min(
            request.deadline_utc,
            request.frame.captured_at_utc + request.maximum_frame_age,
        )
        self.gate = threading.RLock()
        self.engine_cancel = threading.Event()
        self.wake = threading.Event()
        self.terminal = False
        self.thread: threading.Thread | None = None

    def matches_cancel(self, command: _CancelCommand) -> bool:
        return (
            command.ids == self.request.ids
            and command.action_id == self.request.action_id
            and command.epoch == self.request.epoch
        )

    def completed(self, observation: dict[str, Any]) -> None:
        with self.gate, self.policy.gate:
            if self.terminal:
                return
            if self.policy.denied.is_set():
                self.failed(self.policy.error())
                return
            if self.clock.now() >= self.expires_at:
                self._deadline_locked()
                return
            self.terminal = True
            self.wake.set()
            self.emit(
                response_wire(
                    self.request,
                    self.worker,
                    outcome="completed",
                    observation=observation,
                )
            )

    def failed(
        self,
        error: ContractError,
        *,
        compute_may_continue: bool = False,
    ) -> None:
        with self.gate:
            if self.terminal:
                return
            self.terminal = True
            self.wake.set()
            self.emit(
                response_wire(
                    self.request,
                    self.worker,
                    outcome="failed",
                    error=error,
                    compute_cancellation=(
                        self.worker.cancellation if compute_may_continue else None
                    ),
                    worker_may_continue=compute_may_continue,
                )
            )

    def request_cancel(self) -> None:
        with self.gate:
            self.engine_cancel.set()
            if self.terminal:
                return
            self.terminal = True
            self.wake.set()
            self.emit(
                response_wire(
                    self.request,
                    self.worker,
                    outcome="canceled",
                    compute_cancellation=self.worker.cancellation,
                    worker_may_continue=(
                        self.worker.cancellation != "cooperative_compute_cancel"
                    ),
                )
            )

    def deadline(self) -> None:
        with self.gate:
            if not self.terminal:
                self._deadline_locked()

    def is_terminal(self) -> bool:
        with self.gate:
            return self.terminal

    def _deadline_locked(self) -> None:
        self.engine_cancel.set()
        self.terminal = True
        self.wake.set()
        self.emit(
            response_wire(
                self.request,
                self.worker,
                outcome="failed",
                error=ContractError(
                    "deadline_exceeded",
                    "The perception deadline expired; late output was discarded.",
                    stage="inference",
                    remedy_code="perception.retry-with-fresh-frame",
                ),
                compute_cancellation=self.worker.cancellation,
                worker_may_continue=(
                    self.worker.cancellation != "cooperative_compute_cancel"
                ),
            )
        )


class WorkerService:
    def __init__(
        self,
        config: WorkerConfig,
        emit: Emitter,
        *,
        engine: PerceptionEngine | None = None,
        policy: SideEffectPolicy | None = None,
    ) -> None:
        self.config = config
        self.emit = emit
        self.engine = engine or create_engine(config)
        self.policy = policy or SideEffectPolicy()
        self.clock = _Clock()
        self.gate = threading.RLock()
        self.state = "cold"
        self.last_error: dict[str, Any] | None = None
        self.active_job: _Job | None = None
        self.owned_frames: dict[str, OfferedFrame] = {}
        self.warmup_thread: threading.Thread | None = None
        self.warmup_watchdog: threading.Thread | None = None
        self.warmup_done = threading.Event()
        self.warmup_done.set()
        self.warmup_generation = 0
        self.warmup_cancel = threading.Event()
        self.warmup_deadline_utc: UtcTimestamp | None = None
        self.close_event = threading.Event()
        self.closed = False
        self.warmup_in_progress = False
        self.engine_needs_close = True
        self.restart_required = False
        self.frame_wake = threading.Event()
        self.frame_thread: threading.Thread | None = None

    def startup(self) -> None:
        self._emit_state(None)

    def handle(self, message: Mapping[str, Any]) -> None:
        try:
            self._observe_policy()
            message_type = require_string(message.get("type"), "type")
            if message_type == "status":
                self._handle_status(message)
            elif message_type == "warmup":
                self._handle_warmup(message)
            elif message_type == "offer_frame":
                self._handle_offer_frame(message)
            elif message_type == "execute":
                self._handle_execute(message)
            elif message_type == "cancel":
                self._handle_cancel(message)
            else:
                raise ContractError(
                    "invalid_request",
                    "The stdio message type is unsupported.",
                    stage="transport",
                    remedy_code="perception.correct-transport",
                )
        except MemoryError:
            self.emit(
                self.protocol_error(
                    ContractError(
                        "resource_exhausted",
                        "The worker exhausted memory while handling the request.",
                        stage="request_validation",
                        remedy_code="perception.reduce-or-reprovision",
                    )
                )
            )
        except ContractError as error:
            self.emit(self.protocol_error(error))

    def protocol_error(self, error: ContractError) -> dict[str, Any]:
        return {
            "contract_id": CONTRACT_ID,
            "error": protocol_error_wire(error),
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
            self.close_event.set()
            self.frame_wake.set()
            frames = tuple(self.owned_frames.values())
            self.owned_frames.clear()
        self._emit_state(None)
        for frame in frames:
            frame.wipe()
        if job is not None:
            job.request_cancel()
            if job.thread is not None:
                job.thread.join(MAX_CANCEL_SECONDS)
        warmup = self.warmup_thread
        if warmup is not None:
            warmup.join(MAX_CANCEL_SECONDS)
        watchdog = self.warmup_watchdog
        if watchdog is not None:
            watchdog.join(MAX_CANCEL_SECONDS)
        if self.frame_thread is not None:
            self.frame_thread.join(MAX_CANCEL_SECONDS)
        with self.gate:
            self._finish_close_locked()
        self._emit_state(None)

    def _close_engine(self) -> ContractError | None:
        if not self.engine_needs_close:
            return None
        self.engine_needs_close = False
        try:
            with silence_third_party_output():
                self.engine.close()
        except Exception:
            self.restart_required = True
            return ContractError(
                "internal_failure",
                "Engine cleanup failed; restart is required.",
                stage="engine_cleanup",
                remedy_code="perception.restart-worker",
            )
        return None

    def _finish_close_locked(self) -> None:
        if self.closed and self.active_job is None and not self.warmup_in_progress:
            error = self._close_engine()
            if error is not None:
                self.last_error = protocol_error_wire(error)
            self.state = "stopped"

    def _observe_policy(self) -> None:
        with self.gate:
            if self.policy.denied.is_set() and not self.closed:
                self.restart_required = True
                self.warmup_cancel.set()
                self.state = "failed"
                self.last_error = protocol_error_wire(self.policy.error())
                if self.active_job is not None:
                    self.active_job.failed(self.policy.error(), compute_may_continue=True)
                    self.active_job.engine_cancel.set()

    def _handle_status(self, message: Mapping[str, Any]) -> None:
        require_exact_keys(
            message,
            ("contract_id", "protocol_version", "type", "control_id"),
            "status",
        )
        parse_contract_header(message)
        require(message["type"] == "status", "invalid_request", "Invalid message type.")
        control_id = validate_uuid(message["control_id"], "control_id") or ""
        with self.gate:
            self._purge_expired_frames_locked(self.clock.now())
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
        from .contract import WorkerIdentity

        expected = WorkerIdentity.parse(message["expected_worker"])
        verify_expected_identity(expected, self.config.worker)
        deadline = parse_utc(message["deadline_utc"], "deadline_utc")
        remaining = deadline.seconds_from(self.clock.now())
        require(
            0 < remaining <= MAX_WARMUP_SECONDS,
            "deadline_exceeded",
            "The warmup deadline is expired or exceeds five minutes.",
        )
        deadline_monotonic = time.monotonic() + remaining
        with self.gate:
            self.policy.check()
            require(
                not self.restart_required,
                "model_not_ready",
                "Engine cleanup failed; restart is required.",
            )
            if self.closed:
                raise ContractError(
                    "model_not_ready",
                    "The worker is stopping.",
                    stage="warmup",
                    remedy_code="perception.restart-worker",
                )
            if self.active_job is not None or self.state == "busy":
                raise ContractError(
                    "busy",
                    "Inference is active; warmup cannot run concurrently.",
                    stage="warmup",
                    remedy_code="perception.wait-for-readiness",
                    retryable=True,
                )
            if self.state == "ready":
                self._emit_state(control_id)
                return
            if self.warmup_thread is not None and self.warmup_thread.is_alive():
                raise ContractError(
                    "busy",
                    "A worker warmup is already active.",
                    stage="warmup",
                    remedy_code="perception.wait-for-readiness",
                    retryable=True,
                )
            self.warmup_generation += 1
            generation = self.warmup_generation
            self.warmup_cancel = threading.Event()
            self.warmup_deadline_utc = deadline
            self.warmup_done = threading.Event()
            self.last_error = None
            self.state = "verifying_runtime"
            self.warmup_in_progress = True
            self.engine_needs_close = True
            thread = threading.Thread(
                target=self._warmup,
                args=(control_id, deadline_monotonic, generation),
                name="martlet-perception-warmup",
                daemon=True,
            )
            watchdog = threading.Thread(
                target=self._watch_warmup,
                args=(control_id, deadline_monotonic, generation, self.warmup_done),
                name="martlet-perception-warmup-deadline",
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
        ready_candidate = False
        try:
            verify_runtime_metadata(self.config)
            if not self._advance_warmup(
                "verifying_artifacts",
                control_id,
                generation,
            ):
                return
            verified = verify_artifacts(
                self.config,
                deadline_monotonic=deadline_monotonic,
                cancel=self.warmup_cancel,
            )
            observation = verify_runtime_inventory(
                self.config,
                verified.paths["runtime_image"],
                deadline_monotonic=deadline_monotonic,
                cancel=self.warmup_cancel,
            )
            if not self._advance_warmup("loading_model", control_id, generation):
                return
            self.policy.check()
            with silence_third_party_output():
                try:
                    self.engine.load(
                        verified.paths,
                        deadline_monotonic,
                        observation,
                    )
                except ContractError as error:
                    self.policy.check()
                    raise ContractError(
                        error.code,
                        "The exact engine could not complete verified warmup.",
                        stage="warmup",
                        remedy_code="perception.review-worker-runtime",
                    ) from None
            self.policy.check()
            if (
                self.warmup_cancel.is_set()
                or time.monotonic() >= deadline_monotonic
                or not self._warmup_is_current(generation)
            ):
                return
            if not self._advance_warmup("warming", control_id, generation):
                return
            ready_candidate = True
        except ContractError as error:
            self._warmup_failed(control_id, error, generation)
        except MemoryError:
            self._warmup_failed(
                control_id,
                ContractError(
                    "resource_exhausted",
                    "The worker exhausted memory during warmup.",
                    stage="warmup",
                    remedy_code="perception.reduce-or-reprovision",
                ),
                generation,
            )
        except EngineOutOfMemory:
            self._warmup_failed(
                control_id,
                ContractError(
                    "resource_exhausted",
                    "The perception engine exhausted memory during warmup.",
                    stage="warmup",
                    remedy_code="perception.reduce-or-reprovision",
                ),
                generation,
            )
        except EngineModelUnavailable:
            self._warmup_failed(
                control_id,
                ContractError(
                    "model_not_ready",
                    "The exact perception model could not complete warmup.",
                    stage="warmup",
                    remedy_code="perception.review-worker-runtime",
                ),
                generation,
            )
        except EngineFault:
            self._warmup_failed(
                control_id,
                ContractError(
                    "model_not_ready",
                    "The perception engine failed during warmup.",
                    stage="warmup",
                    remedy_code="perception.review-worker-runtime",
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
                    remedy_code="perception.restart-worker",
                ),
                generation,
            )
        finally:
            cleanup_error: ContractError | None = None
            if verified is not None:
                try:
                    verified.cleanup()
                except MemoryError:
                    cleanup_error = ContractError(
                        "resource_exhausted",
                        "Verified artifact cleanup exhausted memory.",
                        stage="warmup_cleanup",
                        remedy_code="perception.restart-worker",
                    )
                except Exception:
                    cleanup_error = ContractError(
                        "internal_failure",
                        "Verified artifact cleanup failed.",
                        stage="warmup_cleanup",
                        remedy_code="perception.restart-worker",
                    )
            if cleanup_error is not None:
                ready_candidate = False
                self._warmup_failed(control_id, cleanup_error, generation)
            publish_ready = False
            with self.gate, self.policy.gate:
                self._observe_policy()
                if (
                    not self.closed
                    and not self.warmup_cancel.is_set()
                    and (
                        time.monotonic() >= deadline_monotonic
                        or self.clock.now() >= self.warmup_deadline_utc
                    )
                ):
                    self._warmup_failed(
                        control_id,
                        ContractError(
                            "deadline_exceeded",
                            "Warmup expired before readiness could be committed.",
                            stage="warmup",
                            remedy_code="perception.retry-warmup",
                        ),
                        generation,
                    )
                if (
                    ready_candidate
                    and not self.closed
                    and generation == self.warmup_generation
                    and not self.warmup_cancel.is_set()
                    and self.active_job is None
                    and time.monotonic() < deadline_monotonic
                    and self.clock.now() < self.warmup_deadline_utc
                ):
                    self.warmup_done.set()
                    self.state = "ready"
                    self.last_error = None
                    publish_ready = True
                else:
                    error = self._close_engine()
                    if error is not None and not self.closed:
                        self._warmup_failed(control_id, error, generation)
                    self.warmup_done.set()
                self.warmup_in_progress = False
                self._finish_close_locked()
            if publish_ready:
                self._emit_state(control_id)
            elif self.closed:
                self._emit_state(None)

    def _watch_warmup(
        self,
        control_id: str,
        deadline_monotonic: float,
        generation: int,
        done: threading.Event,
    ) -> None:
        while True:
            remaining = min(
                deadline_monotonic - time.monotonic(),
                self.warmup_deadline_utc.seconds_from(self.clock.now()),
            )
            if remaining <= 0:
                break
            if done.wait(min(remaining, 0.05)):
                return
        error = ContractError(
            "deadline_exceeded",
            "Worker warmup exceeded its deadline; late readiness is quarantined.",
            stage="warmup",
            remedy_code="perception.retry-warmup",
        )
        with self.gate:
            if self.closed or generation != self.warmup_generation or done.is_set():
                return
            self.warmup_cancel.set()
            self.last_error = protocol_error_wire(error)
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
            self.last_error = protocol_error_wire(error)
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
    ) -> bool:
        with self.gate:
            if (
                self.closed
                or generation != self.warmup_generation
                or self.warmup_cancel.is_set()
                or self.active_job is not None
            ):
                return False
            self.state = state
            if state == "ready":
                self.last_error = None
        self._emit_state(control_id)
        return True

    def _handle_offer_frame(self, message: Mapping[str, Any]) -> None:
        offered = OfferedFrame.parse(message, now=self.clock.now())
        try:
            with self.gate:
                self.policy.check()
                self._purge_expired_frames_locked(self.clock.now())
                if self.closed:
                    raise ContractError(
                        "model_not_ready",
                        "The worker is stopping.",
                        stage="frame_ownership",
                        remedy_code="perception.restart-worker",
                    )
                if self.active_job is not None:
                    raise ContractError(
                        "busy",
                        "A job is active; the worker does not queue frame ownership.",
                        stage="frame_ownership",
                        remedy_code="perception.offer-latest-frame-later",
                        retryable=True,
                    )
                total = sum(item.byte_count for item in self.owned_frames.values())
                require(
                    offered.reference_id not in self.owned_frames
                    and len(self.owned_frames) < MAX_OWNED_FRAMES
                    and total + offered.byte_count <= MAX_OWNED_FRAME_BYTES,
                    "resource_exhausted",
                    "The bounded ephemeral frame store is full.",
                )
                self.owned_frames[offered.reference_id] = offered
                if self.frame_thread is None:
                    self.frame_thread = threading.Thread(
                        target=self._expire_owned_frames,
                        name="martlet-perception-frame-expiry",
                        daemon=True,
                    )
                    self.frame_thread.start()
                self.frame_wake.set()
            self.emit(
                {
                    "byte_count": offered.byte_count,
                    "contract_id": CONTRACT_ID,
                    "control_id": offered.control_id,
                    "expires_at_utc": format_utc(offered.expires_at_utc),
                    "protocol_version": dict(PROTOCOL_VERSION),
                    "reference_id": offered.reference_id,
                    "sha256": offered.sha256,
                    "type": "frame_owned",
                    "worker": self.config.worker.wire(),
                }
            )
        except Exception:
            with self.gate:
                if self.owned_frames.get(offered.reference_id) is offered:
                    self.owned_frames.pop(offered.reference_id)
                offered.wipe()
            raise

    def _handle_execute(self, message: Mapping[str, Any]) -> None:
        request = PerceptionRequest.parse(
            message,
            now=self.clock.now(),
        )
        try:
            verify_expected_identity(
                request.expected_worker,
                self.config.worker,
            )
            require(
                request.task.role == self.config.worker.role,
                "role_mismatch",
                "The request role does not match this exact worker.",
            )
        except ContractError as error:
            self._emit_request_failure(request, error)
            request.wipe()
            return
        with self.gate:
            self._observe_policy()
            self._purge_expired_frames_locked(self.clock.now())
            if self.closed or self.active_job is not None or self.state != "ready" or not self.warmup_done.is_set():
                error = ContractError(
                    "resource_exhausted"
                    if self.state == "busy"
                    else "model_not_ready",
                    "The one-job worker is busy and does not queue."
                    if self.state == "busy"
                    else "The exact model has not completed warmup.",
                    stage="admission",
                    remedy_code="perception.wait-for-readiness",
                    retryable=True,
                )
                self._emit_request_failure(request, error)
                request.wipe()
                return
            if request.frame.content.kind == "ephemeral_gateway_reference":
                reference_id = request.frame.content.reference_id or ""
                offered = self.owned_frames.pop(reference_id, None)
                if (
                    offered is None
                    or offered.expires_at_utc
                    <= self.clock.now()
                    or offered.sha256 != request.frame.content.sha256
                    or offered.byte_count != request.frame.content.byte_count
                    or offered.width != request.frame.content.width
                    or offered.height != request.frame.content.height
                    or offered.expires_at_utc
                    != request.frame.content.reference_expires_at_utc
                ):
                    if offered is not None:
                        offered.wipe()
                    error = ContractError(
                        "invalid_image",
                        "The ephemeral owned frame is absent, expired, or mismatched.",
                        stage="admission",
                        remedy_code="perception.offer-fresh-frame",
                    )
                    self._emit_request_failure(request, error)
                    request.wipe()
                    return
                request.frame.content.attach_owned_data(offered.data)
                offered.data = bytearray()
                self.frame_wake.set()
            if request.frame.content.data is None:
                error = ContractError(
                    "invalid_image",
                    "The selected-window frame has no owned bytes.",
                    stage="admission",
                    remedy_code="perception.offer-fresh-frame",
                )
                self._emit_request_failure(request, error)
                request.wipe()
                return
            job = _Job(request, self.config.worker, self.emit, self.clock, self.policy)
            self.active_job = job
            self.state = "busy"
            self._emit_state(None)
            worker_thread = threading.Thread(
                target=self._run_job,
                args=(job,),
                name="martlet-perception-inference",
                daemon=True,
            )
            watchdog_thread = threading.Thread(
                target=self._watch_job,
                args=(job,),
                name="martlet-perception-deadline",
                daemon=True,
            )
            job.thread = worker_thread
            watchdog_thread.start()
            worker_thread.start()

    def _run_job(self, job: _Job) -> None:
        fatal_error: ContractError | None = None
        try:
            self.policy.check()
            if job.is_terminal():
                return
            if self.clock.now() >= job.expires_at:
                job.deadline()
                return
            data = job.request.frame.content.data
            if data is None:
                raise EngineInvalidImage("frame ownership was lost")
            with silence_third_party_output():
                output = self.engine.infer(
                    job.request,
                    data,
                    job.engine_cancel,
                )
            self.policy.check()
            if job.is_terminal():
                return
            processed_at = self.clock.now()
            observation = make_observation(
                job.request,
                self.config.worker,
                self.config.host_id,
                output,
                processed_at_utc=processed_at,
            )
            job.completed(observation)
        except ContractError as error:
            mapped = (
                self.policy.error()
                if self.policy.denied.is_set()
                else ContractError(
                    "deadline_exceeded",
                    "The perception output expired before publication.",
                    stage="inference",
                    remedy_code="perception.retry-with-fresh-frame",
                )
                if error.code == "deadline_exceeded"
                else ContractError(
                    "internal_failure",
                    "The perception engine returned an unknown or invalid output.",
                    stage="output_validation",
                    remedy_code="perception.repair-adapter",
                )
            )
            job.failed(mapped)
            if mapped.code != "deadline_exceeded":
                fatal_error = mapped
        except EngineInvalidImage:
            job.failed(
                ContractError(
                    "invalid_image",
                    "The engine rejected the bounded PNG after admission.",
                    stage="inference",
                    remedy_code="perception.capture-fresh-frame",
                )
            )
        except EngineModelUnavailable:
            fatal_error = ContractError(
                "model_not_ready",
                "The exact selected model became unavailable.",
                stage="inference",
                remedy_code="perception.review-worker-runtime",
            )
            job.failed(fatal_error)
        except MemoryError:
            fatal_error = ContractError(
                "resource_exhausted",
                "The worker exhausted memory during perception inference.",
                stage="inference",
                remedy_code="perception.reduce-or-reprovision",
            )
            job.failed(fatal_error)
        except EngineOutOfMemory:
            fatal_error = ContractError(
                "resource_exhausted",
                "The perception engine exhausted memory.",
                stage="inference",
                remedy_code="perception.reduce-or-reprovision",
            )
            job.failed(fatal_error)
        except EngineFault:
            fatal_error = ContractError(
                "internal_failure",
                "The perception engine returned an invalid or failed result.",
                stage="inference",
                remedy_code="perception.restart-worker",
            )
            job.failed(fatal_error)
        except Exception:
            fatal_error = ContractError(
                "internal_failure",
                "The worker failed during perception inference.",
                stage="inference",
                remedy_code="perception.restart-worker",
            )
            job.failed(fatal_error)
        finally:
            job.request.wipe()
            with self.gate:
                self._observe_policy()
                if self.policy.denied.is_set():
                    fatal_error = self.policy.error()
                if fatal_error is not None or self.closed:
                    fatal_error = self._close_engine() or fatal_error
                if self.active_job is job:
                    self.active_job = None
                    if not self.closed:
                        if fatal_error is None:
                            self.state = "ready"
                            self.last_error = None
                        else:
                            self.state = "failed"
                            self.last_error = protocol_error_wire(fatal_error)
                self._finish_close_locked()
            self._emit_state(None)

    def _watch_job(self, job: _Job) -> None:
        while not job.wake.is_set():
            remaining = job.expires_at.seconds_from(self.clock.now())
            if remaining <= 0:
                job.deadline()
                return
            job.wake.wait(min(remaining, 0.05))

    def _handle_cancel(self, message: Mapping[str, Any]) -> None:
        command = _CancelCommand.parse(message)
        with self.gate:
            job = self.active_job
        if job is not None and job.matches_cancel(command):
            job.request_cancel()
        self.emit(
            {
                "action_id": command.action_id,
                "compute_cancellation": self.config.worker.cancellation,
                "contract_id": CONTRACT_ID,
                "epoch": command.epoch,
                "ids": command.ids.wire(),
                "local_discard_acknowledged": True,
                "protocol_version": dict(PROTOCOL_VERSION),
                "type": "cancel_response",
                "worker_may_continue": (
                    self.config.worker.cancellation
                    != "cooperative_compute_cancel"
                ),
            }
        )

    def _emit_request_failure(
        self,
        request: PerceptionRequest,
        error: ContractError,
    ) -> None:
        self.emit(
            response_wire(
                request,
                self.config.worker,
                outcome="failed",
                error=error,
            )
        )

    def _expire_owned_frames(self) -> None:
        while not self.close_event.is_set():
            with self.gate:
                self.frame_wake.clear()
                now = self.clock.now()
                self._purge_expired_frames_locked(now)
                remaining = min(
                    (frame.expires_at_utc.seconds_from(now)
                     for frame in self.owned_frames.values()),
                    default=0.05,
                )
            self.frame_wake.wait(min(max(remaining, 0), 0.05))

    def _purge_expired_frames_locked(self, now: datetime | UtcTimestamp) -> None:
        current = UtcTimestamp.from_datetime(now)
        expired = [
            key
            for key, frame in self.owned_frames.items()
            if frame.expires_at_utc <= current
        ]
        for key in expired:
            self.owned_frames.pop(key).wipe()

    def _emit_state(self, control_id: str | None) -> None:
        with self.gate:
            self._observe_policy()
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
                "owned_frame_count": len(self.owned_frames),
                "protocol_version": dict(PROTOCOL_VERSION),
                "ready": self.state == "ready",
                "state": self.state,
                "type": "worker_state",
                "worker": self.config.worker.wire(),
            }
        self.emit(message)
