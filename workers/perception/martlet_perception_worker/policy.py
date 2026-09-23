from __future__ import annotations

import threading

from .contract import ContractError


class SideEffectPolicy:
    """Process audit guard, not a sandbox for untrusted native code."""

    def __init__(self) -> None:
        self.denied = threading.Event()
        self.gate = threading.RLock()

    def audit(self, event: str, args: tuple) -> None:
        if (
            event.startswith(("socket.", "subprocess.", "os.spawn"))
            or event in {"os.system", "os.exec", "os.posix_spawn", "os.fork", "os.forkpty"}
        ):
            with self.gate:
                self.denied.set()
                self.check()

    def check(self) -> None:
        with self.gate:
            if self.denied.is_set():
                raise self.error()

    @staticmethod
    def error() -> ContractError:
        return ContractError(
            "model_not_ready",
            "A prohibited network or child-process operation was denied; restart required.",
            stage="execution_policy",
            remedy_code="perception.review-worker-runtime",
        )
