from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

from .contract import CONTRACT_ID, PROTOCOL_VERSION, ContractError, error_wire
from .engine import DeterministicFakeEngine, F5ProductionEngine
from .identity import WorkerConfig, compute_worker_build_revision
from .jsonio import canonical_json_bytes
from .policy import SideEffectPolicy
from .service import WorkerService
from .stdio import CanonicalStdio


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="martlet-f5-worker",
        description="Canonical stdio martlet.f5.worker v1 process.",
    )
    parser.add_argument("--config", type=Path)
    parser.add_argument("--print-build-revision", action="store_true")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    if args.print_build_revision:
        if args.config is not None:
            _parser().error("--config cannot be combined with --print-build-revision")
        print(compute_worker_build_revision())
        return 0
    if args.config is None:
        _parser().error("--config is required")
    # Keep protocol output separate from Python and native dependency diagnostics.
    sys.stdout.flush()
    sys.stderr.flush()
    protocol_output = os.fdopen(os.dup(sys.stdout.fileno()), "wb")
    with open(os.devnull, "wb") as sink:
        os.dup2(sink.fileno(), sys.stdout.fileno())
        os.dup2(sink.fileno(), sys.stderr.fileno())
    try:
        return _run(args.config, protocol_output)
    finally:
        protocol_output.close()


def _run(config_path: Path, protocol_output) -> int:
    policy = SideEffectPolicy()
    sys.addaudithook(policy.audit)
    transport = CanonicalStdio(sys.stdin.buffer, protocol_output)
    try:
        config = WorkerConfig.load(config_path)
        engine = (
            DeterministicFakeEngine()
            if config.engine == "fake"
            else F5ProductionEngine(config)
        )
        service = WorkerService(config, transport.emit, engine=engine, policy=policy)
    except ContractError as error:
        protocol_output.write(
            canonical_json_bytes(
                {
                    "contract_id": CONTRACT_ID,
                    "error": error_wire(error),
                    "protocol_version": dict(PROTOCOL_VERSION),
                    "type": "protocol_error",
                }
            )
            + b"\n"
        )
        protocol_output.flush()
        return 2
    try:
        return transport.run(service)
    finally:
        service.wait_closed()


if __name__ == "__main__":
    raise SystemExit(main())
