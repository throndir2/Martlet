"""The code that Martlet's worker host services share, so that a fix reaches every engine at once.

The xtts, dia, gpt-sovits, f5, chatterbox, singing, parakeet and voice-recognition host services import it:

* ``sha256_file``, ``download`` and ``fetch_pinned``: downloads that install a file only when it has its pinned size and
  SHA-256 (a partial download is never kept);
* ``JsonHandler``: the loopback HTTP handler base (JSON answers, no request content in logs, one ``Content-Length`` check:
  411 when it is missing, 400 when it is not a whole number, 413 when the body is too large);
* ``ModelProcess``: the owner of one model process that speaks newline-delimited JSON on stdin and stdout.

Each role image copies this file beside its host module (workers/<engine>/host/Dockerfile, built with the context
``workers``). In a checkout, a host finds it here in workers/common. It uses the standard library only (Python 3.11+).
"""

from __future__ import annotations

import hashlib
import http.server
import json
import queue
import subprocess
import sys
import threading
import time
import urllib.request
from pathlib import Path
from typing import Any, Callable

MAX_BODY_BYTES = 6 * 1024 * 1024
CHUNK_BYTES = 1 << 20
# Files larger than this show their download progress.
PROGRESS_BYTES = 10_000_000


def log(text: str) -> None:
    print(text, flush=True)


# ------------------------------------------------------------------ pinned downloads


def sha256_file(path: Path) -> tuple[int, str]:
    """The file's size in bytes and its SHA-256 (lowercase hex)."""
    digest = hashlib.sha256()
    size = 0
    with Path(path).open("rb") as stream:
        while chunk := stream.read(CHUNK_BYTES):
            size += len(chunk)
            digest.update(chunk)
    return size, digest.hexdigest()


def is_pinned(path: Path, size: int, sha256: str) -> bool:
    """The file is there with exactly its pinned size and SHA-256."""
    path = Path(path)
    try:
        return path.is_file() and path.stat().st_size == size and sha256_file(path) == (size, sha256)
    except OSError:
        return False


class DownloadError(ValueError):
    """A download that is not its pinned file (larger, shorter or another SHA-256). Nothing was installed."""


def download(url: str, target: Path, size: int, sha256: str, *, agent: str, label: str | None = None,
             opener: Callable[..., Any] = urllib.request.urlopen, timeout: float = 60,
             progress: Callable[[int, int], None] | None = None) -> None:
    """Downloads ``url`` to ``target`` through ``<target>.partial``. It stops as soon as more than ``size`` bytes arrive and
    installs the file only with exactly ``size`` bytes and ``sha256``; otherwise it raises DownloadError. The partial file
    is always removed. ``opener`` has urllib.request.urlopen's signature (the tests give a fake one)."""
    target = Path(target)
    label = label or target.name
    target.parent.mkdir(parents=True, exist_ok=True)
    partial = target.with_name(target.name + ".partial")
    partial.unlink(missing_ok=True)
    digest = hashlib.sha256()
    received = 0
    try:
        request = urllib.request.Request(url, headers={"User-Agent": agent})
        with opener(request, timeout=timeout) as response, partial.open("wb") as output:
            while chunk := response.read(CHUNK_BYTES):
                received += len(chunk)
                if received > size:
                    raise DownloadError(f"{label} is larger than its pinned {size} bytes")
                digest.update(chunk)
                output.write(chunk)
                if progress is not None:
                    progress(received, size)
        if received != size:
            raise DownloadError(f"{label} has {received} bytes, not its pinned {size}")
        if digest.hexdigest() != sha256:
            raise DownloadError(f"{label} does not match its pinned SHA-256")
        partial.replace(target)
    finally:
        partial.unlink(missing_ok=True)


class _Progress:
    """Download progress on the console: one line that changes (``inline``) or a line every 5% (``lines``, for logs)."""

    def __init__(self, name: str, lines: bool) -> None:
        self.name = name
        self.lines = lines
        self.shown = -1

    def __call__(self, received: int, size: int) -> None:
        percent = received * 100 // size
        if percent == self.shown or (self.lines and percent % 5):
            return
        self.shown = percent
        if self.lines:
            log(f"{self.name}: {percent}% of {size // 1_048_576} MiB")
        else:
            sys.stdout.write(f"\r{self.name}: {percent}% of {size // 1_048_576} MiB")
            sys.stdout.flush()

    def end(self) -> None:
        if not self.lines and self.shown >= 0:
            sys.stdout.write("\n")
            sys.stdout.flush()


def fetch_pinned(url: str, target: Path, size: int, sha256: str, *, agent: str,
                 opener: Callable[..., Any] = urllib.request.urlopen, progress: str = "inline") -> None:
    """A provisioning step's download: keeps ``target`` when it already is the pinned file, otherwise downloads it (see
    ``download``). It ends the command (SystemExit) when the download is not the pinned file. ``progress`` is ``inline``
    or ``lines``."""
    target = Path(target)
    if target.is_file():
        if is_pinned(target, size, sha256):
            log(f"{target.name}: already downloaded and verified.")
            return
        target.unlink()
    shown = _Progress(target.name, lines=progress == "lines") if size > PROGRESS_BYTES else None
    try:
        download(url, target, size, sha256, agent=agent, opener=opener, progress=shown)
    except DownloadError as error:
        raise SystemExit(f"{error}; nothing was installed.") from None
    finally:
        if shown is not None:
            shown.end()
    log(f"{target.name}: verified (SHA-256 {sha256[:12]}...).")


# ------------------------------------------------------------------ the loopback HTTP handler


class JsonHandler(http.server.BaseHTTPRequestHandler):
    """The base of a host service's loopback HTTP handler: JSON answers, no request content in logs and one check of
    the request's Content-Length. A host whose errors have another shape overrides ``refusal``."""

    server_version = "martlet-worker-host"
    sys_version = ""
    max_body_bytes = MAX_BODY_BYTES

    def log_message(self, format: str, *args: Any) -> None:  # noqa: A002 - no request content in logs
        return

    def encode(self, value: Any) -> bytes:
        return json.dumps(value, separators=(",", ":")).encode("utf-8")

    def _json(self, status: int, value: Any) -> None:
        body = self.encode(value)
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def refusal(self, code: str, summary: str) -> dict[str, Any]:
        """The body of the answer to a refused request (``code`` is request.invalid, request.too_large, ...)."""
        return {"error": code}

    def refuse(self, status: int, code: str, summary: str) -> None:
        """Answers a refused request. The connection then closes, because the request's body may not have been read."""
        self.close_connection = True
        self._json(status, self.refusal(code, summary))

    def content_length(self, limit: int | None = None, too_large: int = 413) -> int | None:
        """The request's Content-Length (0 to ``limit``, by default max_body_bytes). Otherwise it answers 411 (missing),
        400 (not a whole number) or ``too_large`` (more than ``limit``) and returns None."""
        limit = self.max_body_bytes if limit is None else limit
        value = self.headers.get("Content-Length")
        if value is None or not value.strip():
            self.refuse(411, "request.length_required", "Send Content-Length.")
            return None
        value = value.strip()
        if not value.isascii() or not value.isdigit():
            self.refuse(400, "request.invalid", "Content-Length is not a whole number.")
            return None
        length = int(value)
        if length > limit:
            self.refuse(too_large, "request.too_large", f"Send at most {limit} bytes.")
            return None
        return length

    def _body(self) -> dict[str, Any] | None:
        """The request's JSON object, or None after answering why it is refused."""
        length = self.content_length()
        if length is None:
            return None
        try:
            value = json.loads(self.rfile.read(length)) if length else None
        except ValueError:
            value = None
        if not isinstance(value, dict):
            self.refuse(400, "request.invalid", "The request is not a JSON object.")
            return None
        return value


# ------------------------------------------------------------------ the model process


class ModelProcess:
    """Owns one model process that reads and writes newline-delimited JSON on stdin and stdout. One reply runs at a time:
    ``pending`` maps the request in flight to the queue that gets its events (None when the process stops). The host's
    subclass starts the process (``start``) and can read more message types (``on_message``)."""

    name = "model"
    # The states in which wait_settled stops waiting, and wait_admissible stops waiting without a ready worker.
    settled_states = frozenset({"ready", "failed", "stopped", "not_provisioned"})
    unavailable_states = frozenset({"not_provisioned", "failed", "stopped"})
    stopped_error = "The worker process stopped; it restarts with the next reply."
    send_errors: tuple[type[BaseException], ...] = ()

    def __init__(self, state: str) -> None:
        self.lock = threading.Lock()
        self.write_lock = threading.Lock()
        self.changed = threading.Condition(self.lock)
        self.process: subprocess.Popen[bytes] | None = None
        self.identity: dict[str, Any] | None = None
        self.state = state
        self.error: str | None = None
        self.pending: dict[str, queue.Queue[dict[str, Any] | None]] = {}

    def status(self) -> dict[str, Any]:
        with self.lock:
            return {"error": self.error, "ready": self.state == "ready", "state": self.state, "worker": self.identity}

    def running(self) -> bool:
        return self.process is not None and self.process.poll() is None

    def encode(self, message: dict[str, Any]) -> bytes:
        return json.dumps(message, separators=(",", ":")).encode("utf-8")

    def send(self, message: dict[str, Any]) -> bool:
        process = self.process
        if process is None or process.stdin is None:
            return False
        try:
            with self.write_lock:
                process.stdin.write(self.encode(message) + b"\n")
                process.stdin.flush()
            return True
        except (BrokenPipeError, OSError, *self.send_errors):
            return False

    def settled(self) -> bool:
        return self.state in self.settled_states

    def wait_settled(self, seconds: float) -> dict[str, Any]:
        """Waits (bounded) until the worker is ready or has stopped loading, and returns its status."""
        deadline = time.monotonic() + seconds
        with self.lock:
            while not self.settled():
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                self.changed.wait(remaining)
        return self.status()

    def wait_admissible(self, seconds: float) -> None:
        """Waits (bounded) until the worker is ready with no reply in flight, or is in a state that cannot become ready."""
        deadline = time.monotonic() + seconds
        with self.lock:
            while (self.state != "ready" or self.pending) and self.state not in self.unavailable_states:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                self.changed.wait(remaining)

    def read_in_background(self, process: subprocess.Popen[bytes]) -> None:
        threading.Thread(target=self._read, args=(process,), name=f"{self.name}-worker-reader", daemon=True).start()

    def _read(self, process: subprocess.Popen[bytes]) -> None:
        assert process.stdout is not None
        for line in process.stdout:
            try:
                message = json.loads(line)
            except ValueError:
                continue
            if isinstance(message, dict):
                self.on_message(process, message)
        with self.lock:
            if self.process is process:
                self.state = "failed"
                self.error = self.error or self.stopped_error
                self.changed.notify_all()
            waiting = list(self.pending.values())
        for target in waiting:
            target.put(None)

    def on_message(self, process: subprocess.Popen[bytes], message: dict[str, Any]) -> None:
        kind = message.get("type")
        if kind == "worker_state":
            with self.lock:
                if self.process is process:
                    self.on_worker_state(message)
                    self.changed.notify_all()
        elif kind == "event":
            self.deliver(message)

    def on_worker_state(self, message: dict[str, Any]) -> None:
        """A ``worker_state`` message of the current process (called with the lock held)."""
        self.state = "ready" if message.get("ready") else str(message.get("state"))
        self.error = (message.get("error") or {}).get("summary")

    def deliver(self, message: dict[str, Any]) -> None:
        """Gives an ``event`` message to the reply that waits for its request."""
        with self.lock:
            target = self.pending.get(str((message.get("ids") or {}).get("request_id")))
        if target is not None:
            target.put(message)
