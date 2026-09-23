from __future__ import annotations

from typing import BinaryIO

from .contract import ContractError
from .jsonio import MAX_INPUT_LINE_BYTES, canonical_json_bytes, parse_canonical_json
from .service import WorkerService


class CanonicalStdio:
    def __init__(self, input_stream: BinaryIO, output_stream: BinaryIO) -> None:
        self.input = input_stream
        self.output = output_stream
        import threading

        self.output_gate = threading.Lock()

    def emit(self, message: dict[str, object]) -> None:
        encoded = canonical_json_bytes(message)
        with self.output_gate:
            self.output.write(encoded + b"\n")
            self.output.flush()

    def run(self, service: WorkerService) -> int:
        service.startup()
        exit_code = 0
        try:
            while True:
                line = self.input.readline(MAX_INPUT_LINE_BYTES + 1)
                if line == b"":
                    break
                if len(line) > MAX_INPUT_LINE_BYTES:
                    self.emit(
                        service.protocol_error(
                            ContractError(
                                "invalid_request",
                                "The stdio message exceeds its byte bound.",
                                stage="transport",
                                action_id="f5.correct-transport",
                            )
                        )
                    )
                    exit_code = 2
                    break
                if not line.endswith(b"\n"):
                    self.emit(
                        service.protocol_error(
                            ContractError(
                                "invalid_request",
                                "The final stdio message was truncated.",
                                stage="transport",
                                action_id="f5.correct-transport",
                            )
                        )
                    )
                    exit_code = 2
                    break
                try:
                    message = parse_canonical_json(
                        line[:-1],
                        maximum=MAX_INPUT_LINE_BYTES,
                        name="stdio message",
                    )
                except ContractError as error:
                    self.emit(service.protocol_error(error))
                    continue
                service.handle(message)
        finally:
            service.close()
        return exit_code
