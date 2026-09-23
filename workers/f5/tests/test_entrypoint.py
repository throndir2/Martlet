from __future__ import annotations

import json
import os
import subprocess
import sys
import unittest
from pathlib import Path

from support import (
    WorkerFixture,
    canonical_line,
    synthesize_message,
    warmup_message,
)


class EntrypointTests(unittest.TestCase):
    def test_fake_worker_subprocess_end_to_end(self) -> None:
        fixture = WorkerFixture()
        process = None
        try:
            worker_root = Path(__file__).resolve().parents[1]
            environment = os.environ.copy()
            environment["PYTHONPATH"] = str(worker_root)
            process = subprocess.Popen(
                [
                    sys.executable,
                    "-m",
                    "martlet_f5_worker",
                    "--config",
                    str(fixture.config_path),
                ],
                cwd=worker_root,
                env=environment,
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
            )
            assert process.stdin is not None
            assert process.stdout is not None
            startup = json.loads(process.stdout.readline())
            self.assertEqual("worker_state", startup["type"])
            self.assertEqual("cold", startup["state"])

            process.stdin.write(canonical_line(warmup_message(fixture.worker)))
            process.stdin.flush()
            states: list[str] = []
            while "ready" not in states:
                line = process.stdout.readline()
                self.assertNotEqual(b"", line)
                message = json.loads(line)
                if message["type"] == "worker_state":
                    states.append(message["state"])
            self.assertIn("verifying_artifacts", states)
            self.assertIn("warming", states)

            synthesis = synthesize_message(fixture.worker)
            process.stdin.write(canonical_line(synthesis))
            process.stdin.flush()
            events: list[dict] = []
            while not any(event["kind"] == "completed" for event in events):
                line = process.stdout.readline()
                self.assertNotEqual(b"", line)
                message = json.loads(line)
                if (
                    message["type"] == "event"
                    and message["ids"]["request_id"]
                    == synthesis["ids"]["request_id"]
                ):
                    events.append(message)
            process.stdin.close()
            self.assertEqual("started", events[0]["kind"])
            self.assertEqual("completed", events[-1]["kind"])
            self.assertTrue(any(event["kind"] == "audio_frame" for event in events))
            self.assertEqual(
                list(range(len(events))),
                [event["sequence"] for event in events],
            )
            process.stdin = None
            remaining_stdout, stderr = process.communicate(timeout=5)
            self.assertEqual(0, process.returncode)
            shutdown = [
                json.loads(line)
                for line in remaining_stdout.splitlines()
                if line
            ]
            self.assertIn(
                "stopped",
                [
                    message["state"]
                    for message in shutdown
                    if message.get("type") == "worker_state"
                ],
            )
            self.assertEqual(b"", stderr)
        finally:
            if process is not None and process.poll() is None:
                process.kill()
                process.wait(timeout=2)
            fixture.close()
