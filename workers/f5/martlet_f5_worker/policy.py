from __future__ import annotations

import threading

from .contract import ContractError


class SideEffectPolicy:
    """Process audit guard, not a sandbox for untrusted native code."""

    def __init__(self) -> None:
        self.denied = threading.Event()

    def audit(self, event: str, args: tuple) -> None:
        if (
            event.startswith("socket.")
            or event.startswith("subprocess.")
            or event.startswith("os.spawn")
            or event in {"os.system", "os.exec", "os.posix_spawn", "os.fork", "os.forkpty"}
        ):
            self.denied.set()
            self.check()

    def check(self) -> None:
        if self.denied.is_set():
            raise self.error()

    @staticmethod
    def error() -> ContractError:
        return ContractError(
            "model_not_ready",
            "A prohibited network or child-process operation was denied; restart required.",
            stage="execution_policy",
            action_id="f5.review-worker-runtime",
        )
