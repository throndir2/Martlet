from __future__ import annotations

import os
import sys
from pathlib import Path

os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"
os.environ["HF_DATASETS_OFFLINE"] = "1"
os.environ["TOKENIZERS_PARALLELISM"] = "false"
sys.dont_write_bytecode = True

from .contract import CONTRACT_ID, PROTOCOL_VERSION, ContractError
from .contract import protocol_error_wire
from .identity import WorkerConfig, compute_worker_build_revision
from .jsonio import canonical_json_bytes
from .policy import SideEffectPolicy
from .service import WorkerService
from .stdio import CanonicalStdio


def _configuration_error(error: ContractError) -> dict[str, object]:
    return {
        "contract_id": CONTRACT_ID,
        "error": protocol_error_wire(error),
        "protocol_version": dict(PROTOCOL_VERSION),
        "type": "protocol_error",
    }


def main(argv: list[str] | None = None) -> int:
    arguments = list(sys.argv[1:] if argv is None else argv)
    if arguments == ["--print-build-revision"]:
        sys.stdout.write(compute_worker_build_revision() + "\n")
        sys.stdout.flush()
        return 0
    if len(arguments) != 2 or arguments[0] != "--config":
        error = ContractError(
            "invalid_request",
            "Use exactly --config ABSOLUTE_PATH or --print-build-revision.",
            stage="configuration",
            remedy_code="perception.correct-command",
        )
        sys.stdout.buffer.write(canonical_json_bytes(_configuration_error(error)) + b"\n")
        sys.stdout.buffer.flush()
        return 2
    try:
        config = WorkerConfig.load(Path(arguments[1]))
    except MemoryError:
        error = ContractError(
            "resource_exhausted",
            "The worker exhausted memory while loading configuration.",
            stage="configuration",
            remedy_code="perception.reduce-or-reprovision",
        )
        sys.stdout.buffer.write(canonical_json_bytes(_configuration_error(error)) + b"\n")
        sys.stdout.buffer.flush()
        return 2
    except ContractError as error:
        sys.stdout.buffer.write(canonical_json_bytes(_configuration_error(error)) + b"\n")
        sys.stdout.buffer.flush()
        return 2
    transport = CanonicalStdio(sys.stdin.buffer, sys.stdout.buffer)
    policy = SideEffectPolicy()
    sys.addaudithook(policy.audit)
    service = WorkerService(config, transport.emit, policy=policy)
    try:
        return transport.run(service)
    except BrokenPipeError:
        service.close()
        return 3


if __name__ == "__main__":
    raise SystemExit(main())
