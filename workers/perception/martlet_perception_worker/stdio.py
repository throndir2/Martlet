from __future__ import annotations

import io
import os
from typing import BinaryIO

from .contract import ContractError
from .jsonio import (
    MAX_INPUT_LINE_BYTES,
    JsonContractError,
    canonical_json_bytes,
    parse_canonical_json,
)
from .service import WorkerService


class CanonicalStdio:
    def __init__(self, input_stream: BinaryIO, output_stream: BinaryIO) -> None:
        self.input = input_stream
        self.output: BinaryIO | None = output_stream
        self.output_descriptor: int | None = None
        self.closed = False
        try:
            source_descriptor = output_stream.fileno()
        except (AttributeError, io.UnsupportedOperation):
            source_descriptor = None
        if source_descriptor is not None:
            self.output_descriptor = os.dup(source_descriptor)
            self.output = None
            if source_descriptor == 1:
                null_descriptor = os.open(
                    os.devnull,
                    os.O_WRONLY | getattr(os, "O_BINARY", 0),
                )
                try:
                    os.dup2(null_descriptor, 1)
                    os.dup2(null_descriptor, 2)
                finally:
                    os.close(null_descriptor)
        import threading

        self.output_gate = threading.Lock()

    def emit(self, message: dict[str, object]) -> None:
        with self.output_gate:
            # EOF owns the transport; late cleanup cannot reuse its closed descriptor.
            if self.closed:
                return
            encoded = canonical_json_bytes(message)
            framed = encoded + b"\n"
            if self.output_descriptor is not None:
                view = memoryview(framed)
                while view:
                    written = os.write(self.output_descriptor, view)
                    if written <= 0:
                        raise BrokenPipeError("stdio output made no progress")
                    view = view[written:]
            else:
                if self.output is None:
                    raise BrokenPipeError("stdio output is closed")
                self.output.write(framed)
                self.output.flush()

    def close(self) -> None:
        with self.output_gate:
            if not self.closed:
                self.closed = True
                if self.output_descriptor is not None:
                    os.close(self.output_descriptor)
                    self.output_descriptor = None

    def run(self, service: WorkerService) -> int:
        exit_code = 0
        try:
            service.startup()
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
                                remedy_code="perception.correct-transport",
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
                                remedy_code="perception.correct-transport",
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
                except MemoryError:
                    self.emit(
                        service.protocol_error(
                            ContractError(
                                "resource_exhausted",
                                "The worker exhausted memory while parsing input.",
                                stage="transport",
                                remedy_code="perception.reduce-or-reprovision",
                            )
                        )
                    )
                    continue
                except JsonContractError as error:
                    self.emit(
                        service.protocol_error(
                            ContractError(
                                "invalid_request",
                                "The stdio message is not bounded canonical JSON.",
                                stage="transport",
                                remedy_code="perception.correct-transport",
                            )
                        )
                    )
                    del error
                    continue
                finally:
                    del line
                try:
                    service.handle(message)
                except MemoryError:
                    self.emit(
                        service.protocol_error(
                            ContractError(
                                "resource_exhausted",
                                "The worker exhausted memory while handling input.",
                                stage="transport",
                                remedy_code="perception.reduce-or-reprovision",
                            )
                        )
                    )
                finally:
                    del message
        except (BrokenPipeError, OSError):
            exit_code = 3
        finally:
            try:
                service.close()
            except (BrokenPipeError, OSError):
                exit_code = 3
            finally:
                self.close()
        return exit_code
