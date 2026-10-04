"""Martlet singing worker process: one song at a time, JSON lines on stdin/stdout.

Started by the host service (martlet_singing.host) as ``python -m martlet_singing.worker --config <worker-config.json>``.
Nothing is downloaded: every model loads from the provisioned paths (HF_HUB_OFFLINE is set by the image). The process
exits when the host releases it after an idle period, which frees all of its memory; the next song starts a new one.

stdin:  {"type": "run", "job": {...}} | {"type": "cancel", "job_id"}
stdout: {"type": "state", "state"} | {"type": "progress", "job_id", "stage", "fraction"}
        | {"type": "completed", "job_id", "meta"} | {"type": "failed", "job_id", "error": {"code", "summary"}}
        | {"type": "canceled", "job_id"}
"""

from __future__ import annotations

import argparse
import json
import os
import queue
import sys
import threading
import traceback
from pathlib import Path
from typing import Any, BinaryIO

from martlet_singing import audio, engines


class Worker:
    def __init__(self, config: dict[str, Any], output: BinaryIO) -> None:
        self.config = config
        if config["engine"] == "fixture":
            self.engine: Any = engines.FixtureEngine()
        else:
            self.engine = engines.SongEngine(Path(config["models"]), config["device"], list(config.get("voice_matches", ["soulx"])))
        self.output = output
        self.lock = threading.Lock()
        self.canceled: set[str] = set()
        self.inbox: queue.Queue[dict[str, Any] | None] = queue.Queue()

    def emit(self, message: dict[str, Any]) -> None:
        with self.lock:
            self.output.write(json.dumps(message, separators=(",", ":"), sort_keys=True).encode("utf-8") + b"\n")
            self.output.flush()

    def read(self, stream: BinaryIO) -> None:
        for line in _lines(stream):
            try:
                message = json.loads(line)
            except ValueError:
                continue
            if not isinstance(message, dict):
                continue
            if message.get("type") == "cancel":
                with self.lock:
                    self.canceled.add(str(message.get("job_id")))
            else:
                self.inbox.put(message)
        self.inbox.put(None)

    def run(self) -> None:
        self.emit({"type": "state", "state": "ready"})
        while (message := self.inbox.get()) is not None:
            if message.get("type") == "run":
                self.song(message["job"])

    def song(self, job: dict[str, Any]) -> None:
        job_id = str(job["job_id"])
        directory = Path(job["directory"])

        def report(stage: str, fraction: float) -> None:
            self.emit({"type": "progress", "job_id": job_id, "stage": stage, "fraction": round(fraction, 3)})

        def canceled() -> bool:
            with self.lock:
                return job_id in self.canceled

        try:
            job["reference_audio"] = (directory / "reference.wav").read_bytes()
            meta = self.engine.run(job, directory, report, canceled)
            self.emit({"type": "completed", "job_id": job_id, "meta": meta})
        except engines.JobCanceled:
            self.emit({"type": "canceled", "job_id": job_id})
        except engines.SongError as error:
            self.emit({"type": "failed", "job_id": job_id, "error": {"code": error.code, "summary": error.summary}})
        except audio.AudioError:
            self.emit({"type": "failed", "job_id": job_id,
                       "error": {"code": "voice.missing", "summary": "The voice's recording cannot be used for singing."}})
        except Exception as error:  # noqa: BLE001 - one failed song must not stop the worker
            traceback.print_exc(file=sys.stderr)
            summary = f"{type(error).__name__}: {error}"
            if "out of memory" in summary.lower():
                summary = "The graphics card ran out of memory: " + summary
            self.emit({"type": "failed", "job_id": job_id, "error": {"code": "song.failed", "summary": summary[:300]}})
        finally:
            with self.lock:
                self.canceled.discard(job_id)
            job.pop("reference_audio", None)


def _lines(stream: BinaryIO):
    """The lines arriving on stdin. On Windows a thread blocked reading an anonymous pipe stalls every DLL that starts its own
    C runtime in another thread (numpy, torch...), so there the pipe is polled instead of read while empty."""
    if os.name != "nt":
        yield from stream
        return
    import ctypes
    import msvcrt
    import time
    from ctypes import wintypes

    descriptor = stream.fileno()
    handle = msvcrt.get_osfhandle(descriptor)
    available = wintypes.DWORD()
    pending = b""
    while True:
        if not ctypes.windll.kernel32.PeekNamedPipe(wintypes.HANDLE(handle), None, 0, None, ctypes.byref(available), None):
            break
        if available.value == 0:
            time.sleep(0.05)
            continue
        chunk = os.read(descriptor, available.value)
        if not chunk:
            break
        pending += chunk
        while b"\n" in pending:
            line, pending = pending.split(b"\n", 1)
            yield line + b"\n"
    if pending:
        yield pending


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="martlet-singing-worker")
    parser.add_argument("--config", type=Path, required=True)
    args = parser.parse_args(argv)
    # Only protocol lines go to the host: anything the libraries print goes to stderr.
    protocol = os.fdopen(os.dup(sys.stdout.fileno()), "wb")
    os.dup2(sys.stderr.fileno(), sys.stdout.fileno())
    worker = Worker(json.loads(args.config.read_bytes()), protocol)
    threading.Thread(target=worker.read, args=(sys.stdin.buffer,), name="singing-worker-stdin", daemon=True).start()
    worker.run()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
