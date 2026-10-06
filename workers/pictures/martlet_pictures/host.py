"""Martlet pictures host role command.

Usage:
  martlet-pictures provision   download and verify pinned ComfyUI model files
  martlet-pictures serve       run ComfyUI on 127.0.0.1:50086 with only local model files
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import urllib.request
from pathlib import Path
from typing import Any

from . import pins

ROOT = Path(os.environ.get("MARTLET_PICTURES_ROOT", "/opt/martlet-pictures"))
COMFY = ROOT / "ComfyUI"
MODELS = ROOT / "models"
CONFIG = MODELS / "martlet-pictures.json"
OUTPUT = ROOT / "output"
TEMP = ROOT / "temp"
PORT = os.environ.get("MARTLET_PICTURES_PORT", "50086")


def _log(message: str) -> None:
    print(message, flush=True)


def _sha256_file(path: Path) -> tuple[int, str]:
    digest = hashlib.sha256()
    size = 0
    with path.open("rb") as stream:
        while chunk := stream.read(1_048_576):
            size += len(chunk)
            digest.update(chunk)
    return size, digest.hexdigest()


def _download(url: str, target: Path, size: int, sha256: str) -> None:
    if target.is_file():
        if target.stat().st_size == size and _sha256_file(target) == (size, sha256):
            _log(f"{target.name}: already downloaded and verified.")
            return
        target.unlink()
    target.parent.mkdir(parents=True, exist_ok=True)
    partial = target.with_name(target.name + ".partial")
    digest = hashlib.sha256()
    received = 0
    shown = -1
    request = urllib.request.Request(url, headers={"User-Agent": "martlet-pictures-host"})
    with urllib.request.urlopen(request, timeout=60) as response, partial.open("wb") as output:
        while chunk := response.read(1_048_576):
            received += len(chunk)
            if received > size:
                partial.unlink(missing_ok=True)
                raise SystemExit(f"{target.name} is larger than its pinned size; stopped.")
            digest.update(chunk)
            output.write(chunk)
            percent = received * 100 // size
            if size > 50_000_000 and percent != shown and percent % 5 == 0:
                shown = percent
                _log(f"{target.name}: {percent}% of {size // 1_048_576} MiB")
    if received != size or digest.hexdigest() != sha256:
        partial.unlink(missing_ok=True)
        raise SystemExit(f"{target.name} does not match its pinned SHA-256; nothing was installed.")
    partial.replace(target)
    _log(f"{target.name}: verified (SHA-256 {sha256[:12]}...).")


def provision() -> None:
    artifacts: list[dict[str, Any]] = []
    for artifact_id, url, revision, size, sha256, license_id, local in pins.files():
        _log(f"Model file {artifact_id} ({license_id})")
        _download(url, MODELS / local, size, sha256)
        artifacts.append({"artifact_id": artifact_id, "bytes": size, "license_id": license_id, "revision": revision,
                          "sha256": sha256})
    MODELS.mkdir(parents=True, exist_ok=True)
    temporary = CONFIG.with_name(CONFIG.name + ".partial")
    temporary.write_text(json.dumps({
        "engine": "comfyui",
        "model": pins.MODEL_ID,
        "repository": pins.MODEL_REPOSITORY,
        "revision": pins.MODEL_REVISION,
        "sources": {"comfyui": pins.COMFYUI_COMMIT},
        "artifacts": artifacts,
    }, indent=2, sort_keys=True), encoding="utf-8")
    temporary.replace(CONFIG)
    _log("Pictures models are ready.")


def serve() -> None:
    if not CONFIG.is_file():
        _log("Pictures role is not provisioned yet; run martlet-pictures provision.")
    for directory in (MODELS / "diffusion_models", MODELS / "text_encoders", MODELS / "vae", OUTPUT, TEMP):
        directory.mkdir(parents=True, exist_ok=True)
    argv = [
        sys.executable,
        str(COMFY / "main.py"),
        "--listen", "127.0.0.1",
        "--port", PORT,
        "--disable-auto-launch",
        "--lowvram",
        "--output-directory", str(OUTPUT),
        "--temp-directory", str(TEMP),
    ]
    os.execv(sys.executable, argv)


def main() -> None:
    parser = argparse.ArgumentParser(prog="martlet-pictures")
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("provision")
    commands.add_parser("serve")
    args = parser.parse_args()
    if args.command == "provision":
        provision()
    elif args.command == "serve":
        serve()


if __name__ == "__main__":
    main()
