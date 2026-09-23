from __future__ import annotations

import io
import json
import os
import queue
import subprocess
import sys
import threading
import time
import unittest
from pathlib import Path

from martlet_perception_worker.engine import DeterministicFakeEngine
from martlet_perception_worker.identity import compute_worker_build_revision
from martlet_perception_worker.jsonio import (
    JsonContractError,
    canonical_json_bytes,
    parse_canonical_json,
)
from martlet_perception_worker.service import WorkerService
from martlet_perception_worker.stdio import CanonicalStdio

from tests.support import (
    FixtureEnvironment,
    canonical_line,
    request_wire,
    status_wire,
    warmup_wire,
)


class EntrypointTests(unittest.TestCase):
    def test_clean_eof_stops_worker_and_every_stdout_line_is_canonical(self) -> None:
        with FixtureEnvironment("ocr") as environment:
            input_stream = io.BytesIO(canonical_line(status_wire()))
            output_stream = io.BytesIO()
            transport = CanonicalStdio(input_stream, output_stream)
            service = WorkerService(environment.config, transport.emit)
            exit_code = transport.run(service)
            self.assertEqual(exit_code, 0)
            messages = _parse_output(output_stream.getvalue())
            self.assertEqual(messages[0]["state"], "cold")
            self.assertEqual(messages[-2]["state"], "stopping")
            self.assertEqual(messages[-1]["state"], "stopped")
            self.assertTrue(any(item.get("control_id") for item in messages))

    def test_truncated_eof_and_noncanonical_crlf_fail_closed(self) -> None:
        with FixtureEnvironment("ocr") as environment:
            truncated = canonical_json_bytes(status_wire())
            output = io.BytesIO()
            exit_code = CanonicalStdio(io.BytesIO(truncated), output).run(
                WorkerService(environment.config, lambda _: None)
            )
            self.assertEqual(exit_code, 2)
            # The first service above intentionally used a separate emitter.
            output = io.BytesIO()
            transport = CanonicalStdio(
                io.BytesIO(canonical_json_bytes(status_wire()) + b"\r\n"),
                output,
            )
            service = WorkerService(environment.config, transport.emit)
            self.assertEqual(transport.run(service), 0)
            messages = _parse_output(output.getvalue())
            error = next(item for item in messages if item["type"] == "protocol_error")
            self.assertEqual(error["error"]["code"], "invalid_request")

    def test_truncated_eof_emits_transport_error(self) -> None:
        with FixtureEnvironment("ocr") as environment:
            output = io.BytesIO()
            transport = CanonicalStdio(
                io.BytesIO(canonical_json_bytes(status_wire())),
                output,
            )
            service = WorkerService(environment.config, transport.emit)
            self.assertEqual(transport.run(service), 2)
            messages = _parse_output(output.getvalue())
            error = next(item for item in messages if item["type"] == "protocol_error")
            self.assertIn("truncated", error["error"]["summary"])

    def test_engine_stdout_and_stderr_cannot_contaminate_ndjson(self) -> None:
        with FixtureEnvironment("ocr") as environment:
            output = io.BytesIO()
            transport = CanonicalStdio(io.BytesIO(), output)
            service = WorkerService(
                environment.config,
                transport.emit,
                engine=DeterministicFakeEngine(noisy=True),
            )
            service.startup()
            service.handle(warmup_wire(environment))
            _wait_until(lambda: service.state == "ready")
            request = request_wire(environment)
            service.handle(request)
            _wait_until(
                lambda: any(
                    item.get("type") == "response"
                    for item in _parse_output(output.getvalue())
                )
            )
            service.close()
            raw = output.getvalue()
            self.assertNotIn(b"noise", raw)
            _parse_output(raw)

    def test_native_file_descriptor_output_is_suppressed_without_hiding_protocol(
        self,
    ) -> None:
        repository = Path(__file__).resolve().parents[3]
        script = (
            "import os,sys\n"
            "from martlet_perception_worker.engine import "
            "silence_third_party_output\n"
            "from martlet_perception_worker.stdio import CanonicalStdio\n"
            "transport=CanonicalStdio(sys.stdin.buffer,sys.stdout.buffer)\n"
            "with silence_third_party_output():\n"
            " os.write(1,b'native-stdout-noise\\n')\n"
            " os.write(2,b'native-stderr-noise\\n')\n"
            " print('python-noise')\n"
            " transport.emit({'sequence':1,'type':'fixture'})\n"
            "transport.emit({'sequence':2,'type':'fixture'})\n"
        )
        result = subprocess.run(
            [sys.executable, "-c", script],
            cwd=repository,
            env=_subprocess_environment(repository),
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
            timeout=10,
        )
        self.assertEqual(result.returncode, 0, result.stderr.decode())
        self.assertEqual(result.stderr, b"")
        self.assertEqual(
            _parse_output(result.stdout),
            [
                {"sequence": 1, "type": "fixture"},
                {"sequence": 2, "type": "fixture"},
            ],
        )

    def test_print_build_revision_is_exact_and_stable(self) -> None:
        repository = Path(__file__).resolve().parents[3]
        environment = _subprocess_environment(repository)
        result = subprocess.run(
            [
                sys.executable,
                "-m",
                "martlet_perception_worker",
                "--print-build-revision",
            ],
            cwd=repository,
            env=environment,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
            timeout=10,
        )
        self.assertEqual(result.returncode, 0, result.stderr.decode())
        self.assertEqual(
            result.stdout.decode("ascii").strip(),
            compute_worker_build_revision(),
        )
        self.assertEqual(result.stderr, b"")

    def test_real_fake_worker_subprocess_warmup_execute_and_eof(self) -> None:
        repository = Path(__file__).resolve().parents[3]
        with FixtureEnvironment("visual_question_answering") as fixture:
            process = subprocess.Popen(
                [
                    sys.executable,
                    "-m",
                    "martlet_perception_worker",
                    "--config",
                    str(fixture.config_path),
                ],
                cwd=repository,
                env=_subprocess_environment(repository),
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
            )
            self.assertIsNotNone(process.stdin)
            self.assertIsNotNone(process.stdout)
            reader = _ProcessReader(process.stdout)
            try:
                cold = reader.wait_for(
                    lambda item: item.get("type") == "worker_state"
                    and item.get("state") == "cold"
                )
                self.assertFalse(cold["ready"])
                process.stdin.write(canonical_line(warmup_wire(fixture)))
                process.stdin.flush()
                reader.wait_for(
                    lambda item: item.get("type") == "worker_state"
                    and item.get("state") == "ready"
                )
                request = request_wire(fixture)
                process.stdin.write(canonical_line(request))
                process.stdin.flush()
                response = reader.wait_for(
                    lambda item: item.get("type") == "response"
                    and item.get("action_id") == request["action_id"]
                )
                self.assertEqual(response["outcome"], "completed")
                self.assertIn("uncertainty", response["observation"]["vlm"])
                process.stdin.close()
                self.assertEqual(process.wait(timeout=10), 0)
                reader.join()
                self.assertEqual(process.stderr.read(), b"")
                self.assertTrue(
                    any(
                        item.get("state") == "stopped"
                        for item in reader.messages
                    )
                )
            finally:
                if process.stdin is not None and not process.stdin.closed:
                    process.stdin.close()
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=5)
                if process.stdout is not None:
                    process.stdout.close()
                if process.stderr is not None:
                    process.stderr.close()


class _ProcessReader:
    def __init__(self, stream: io.BufferedReader) -> None:
        self.stream = stream
        self.messages: list[dict[str, object]] = []
        self.errors: list[BaseException] = []
        self.events: queue.Queue[dict[str, object] | None] = queue.Queue()
        self.thread = threading.Thread(target=self._run, daemon=True)
        self.thread.start()

    def _run(self) -> None:
        try:
            while line := self.stream.readline():
                message = parse_canonical_json(
                    line.removesuffix(b"\n"),
                    maximum=6 * 1_024 * 1_024,
                    name="subprocess output",
                )
                self.messages.append(message)
                self.events.put(message)
        except BaseException as error:
            self.errors.append(error)
        finally:
            self.events.put(None)

    def wait_for(self, predicate, timeout: float = 5.0):  # type: ignore[no-untyped-def]
        deadline = time.monotonic() + timeout
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise AssertionError(
                    f"subprocess message not observed: {self.messages!r}"
                )
            item = self.events.get(timeout=remaining)
            if item is None:
                if self.errors:
                    raise self.errors[0]
                raise AssertionError(
                    f"subprocess ended before message: {self.messages!r}"
                )
            if predicate(item):
                return item

    def join(self) -> None:
        self.thread.join(timeout=5)
        if self.errors:
            raise self.errors[0]


def _subprocess_environment(repository: Path) -> dict[str, str]:
    environment = os.environ.copy()
    environment["PYTHONDONTWRITEBYTECODE"] = "1"
    environment["PYTHONPATH"] = str(repository / "workers" / "perception")
    environment["HF_HUB_OFFLINE"] = "1"
    environment["TRANSFORMERS_OFFLINE"] = "1"
    environment["HF_DATASETS_OFFLINE"] = "1"
    return environment


def _parse_output(raw: bytes) -> list[dict[str, object]]:
    messages: list[dict[str, object]] = []
    for line in raw.splitlines():
        if not line:
            continue
        messages.append(
            parse_canonical_json(
                line,
                maximum=6 * 1_024 * 1_024,
                name="worker output",
            )
        )
    return messages


def _wait_until(predicate, timeout: float = 3.0) -> None:  # type: ignore[no-untyped-def]
    deadline = time.monotonic() + timeout
    while not predicate():
        if time.monotonic() >= deadline:
            raise AssertionError("condition was not reached")
        time.sleep(0.005)


if __name__ == "__main__":
    unittest.main()
