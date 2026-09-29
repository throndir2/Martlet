from __future__ import annotations

import hashlib
import importlib.metadata
import importlib.resources
import importlib.util
import os
import platform
import stat
import sys
import tempfile
import time
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from threading import Event
from typing import Any

from .contract import (
    ARTIFACT_ROLES,
    MAX_ARTIFACT_BYTES,
    ContractError,
    WorkerIdentity,
    fail,
    identities_match,
    require,
    require_array,
    require_exact_keys,
    require_int,
    require_object,
    require_string,
)
from .jsonio import MAX_CONFIG_BYTES, canonical_json_bytes, parse_canonical_json

CONFIG_KIND = "martlet_f5_worker_config"
PINS_KIND = "martlet_f5_runtime_pins"
WORKER_BUILD_ID = "martlet-f5-worker"
FIXTURE_BUILD_ID = "martlet-f5-deterministic-fixture"
FIXTURE_VERSION = "0.0.0-fixture"
FIXTURE_SOURCE_REVISION = "1111111111111111111111111111111111111111"
MAX_RUNTIME_INVENTORY_BYTES = 32 * 1_024 * 1_024
MAX_RUNTIME_PACKAGE_FILES = 100_000


@dataclass(frozen=True)
class RuntimePins:
    python_version: str
    f5_source_revision: str
    cuda_runtime_version: str
    packages: dict[str, str]

    @classmethod
    def load(cls) -> "RuntimePins":
        try:
            data = (
                importlib.resources.files("martlet_f5_worker")
                .joinpath("runtime-pins.v1.json")
                .read_bytes()
            )
        except (FileNotFoundError, OSError, ModuleNotFoundError) as error:
            raise ContractError(
                "model_not_ready",
                "The packaged runtime pin lock is missing or unreadable.",
                stage="runtime_verification",
                action_id="f5.reinstall-worker",
            ) from error
        if data.endswith(b"\r\n"):
            data = data[:-2]
        elif data.endswith(b"\n"):
            data = data[:-1]
        source = parse_canonical_json(
            data,
            maximum=MAX_CONFIG_BYTES,
            name="runtime pin lock",
        )
        require_exact_keys(
            source,
            (
                "format_version",
                "kind",
                "python_version",
                "f5_source_revision",
                "cuda_runtime_version",
                "packages",
            ),
            "runtime pin lock",
        )
        require(
            require_int(source["format_version"], "runtime pin format_version") == 1
            and source["kind"] == PINS_KIND,
            "invalid_request",
            "The runtime pin lock has an unsupported format.",
        )
        packages = require_object(source["packages"], "runtime pin packages")
        require_exact_keys(
            packages,
            ("f5-tts", "torch", "torchaudio", "vocos"),
            "runtime pin packages",
        )
        return cls(
            python_version=require_string(source["python_version"], "python_version"),
            f5_source_revision=require_string(
                source["f5_source_revision"],
                "f5_source_revision",
            ),
            cuda_runtime_version=require_string(
                source["cuda_runtime_version"],
                "cuda_runtime_version",
            ),
            packages={
                name: require_string(version, f"packages.{name}")
                for name, version in packages.items()
            },
        )


@dataclass
class VerifiedArtifacts:
    paths: dict[str, Path]
    _temporary_directory: tempfile.TemporaryDirectory[str]

    def cleanup(self) -> None:
        self._temporary_directory.cleanup()


@dataclass(frozen=True)
class RuntimeObservation:
    module_roots: dict[str, Path]
    verified_files: dict[str, frozenset[Path]]


@dataclass(frozen=True)
class WorkerConfig:
    engine: str
    artifact_root: Path
    artifact_files: dict[str, str]
    worker: WorkerIdentity
    device: str | None

    @classmethod
    def load(cls, path: Path) -> "WorkerConfig":
        config_path = _validate_config_path(path)
        try:
            with config_path.open("rb") as stream:
                data = stream.read(MAX_CONFIG_BYTES + 1)
        except OSError as error:
            raise ContractError(
                "model_not_ready",
                "The worker configuration could not be read.",
                stage="configuration",
                action_id="f5.install-worker-config",
            ) from error
        source = parse_canonical_json(
            data,
            maximum=MAX_CONFIG_BYTES,
            name="worker configuration",
        )
        require_exact_keys(
            source,
            (
                "format_version",
                "kind",
                "engine",
                "artifact_root",
                "artifact_files",
                "worker",
                "device",
            ),
            "worker configuration",
        )
        require(
            require_int(source["format_version"], "format_version") == 1
            and source["kind"] == CONFIG_KIND,
            "invalid_request",
            "The worker configuration has an unsupported format.",
        )
        engine = require_string(source["engine"], "engine")
        require(engine in {"fake", "f5"}, "invalid_request", "engine must be fake or f5.")
        artifact_root_text = require_string(source["artifact_root"], "artifact_root")
        require(
            0 < len(artifact_root_text) <= 1_024,
            "invalid_request",
            "artifact_root exceeds its bound.",
        )
        artifact_root = Path(artifact_root_text)
        require(
            artifact_root.is_absolute()
            and not artifact_root.drive.startswith("\\\\")
            and not artifact_root_text.startswith(("//", "\\\\")),
            "invalid_request",
            "artifact_root must be an absolute local directory.",
        )
        files = require_object(source["artifact_files"], "artifact_files")
        require_exact_keys(files, ARTIFACT_ROLES, "artifact_files")
        artifact_files = {
            role: _validate_relative_artifact_path(files[role], role)
            for role in ARTIFACT_ROLES
        }
        worker = WorkerIdentity.parse(source["worker"])
        device_source = source["device"]
        if engine == "f5":
            device = require_string(device_source, "device")
            require(
                len(device) <= 7
                and device.startswith("cuda:")
                and device[5:].isdigit()
                and 0 <= int(device[5:]) <= 15,
                "invalid_request",
                "Production F5 requires one explicit cuda:0 through cuda:15 device.",
            )
            require(
                worker.evidence == "live_worker"
                and worker.cancellation == "discard_only",
                "invalid_request",
                "Production F5 identity must be live_worker with discard_only cancellation.",
            )
        else:
            require(
                device_source is None,
                "invalid_request",
                "The fake engine does not accept a device.",
            )
            device = None
            require(
                worker.evidence == "synthetic_fixture"
                and worker.cancellation == "discard_only",
                "invalid_request",
                "Fake identity must be synthetic_fixture with discard_only cancellation.",
            )
        config = cls(engine, artifact_root, artifact_files, worker, device)
        config.verify_static_identity()
        return config

    def verify_static_identity(self) -> None:
        runtime = self.worker.runtime
        expected_build_id = WORKER_BUILD_ID if self.engine == "f5" else FIXTURE_BUILD_ID
        require(
            runtime.worker_build_id == expected_build_id
            and runtime.worker_build_revision == compute_worker_build_revision(),
            "identity_mismatch",
            "The configured worker build identity does not match these worker sources.",
        )
        require(
            runtime.python_version == platform.python_version(),
            "identity_mismatch",
            "The configured Python version does not match this interpreter.",
        )
        if self.engine == "fake":
            require(
                runtime.f5_package_version == FIXTURE_VERSION
                and runtime.f5_source_revision == FIXTURE_SOURCE_REVISION
                and runtime.torch_version == FIXTURE_VERSION
                and runtime.torchaudio_version == FIXTURE_VERSION
                and runtime.cuda_runtime_version == FIXTURE_VERSION,
                "identity_mismatch",
                "The fake worker must use explicit fixture-only runtime identities.",
            )
            return
        pins = RuntimePins.load()
        require(
            runtime.python_version == pins.python_version
            and runtime.f5_package_version == pins.packages["f5-tts"]
            and runtime.f5_source_revision == pins.f5_source_revision
            and runtime.torch_version == pins.packages["torch"]
            and runtime.torchaudio_version == pins.packages["torchaudio"]
            and runtime.cuda_runtime_version == pins.cuda_runtime_version,
            "identity_mismatch",
            "The configured production runtime differs from runtime-pins.v1.json.",
        )

    def artifact_paths(self) -> dict[str, Path]:
        root = _validate_root(self.artifact_root)
        result: dict[str, Path] = {}
        for role in ARTIFACT_ROLES:
            candidate = root.joinpath(*PurePosixPath(self.artifact_files[role]).parts)
            _reject_link_ancestors(candidate, root)
            result[role] = candidate
        return result


def compute_worker_build_revision() -> str:
    package_root = Path(__file__).resolve().parent
    digest = hashlib.sha1(usedforsecurity=False)
    for path in sorted(package_root.glob("*.py"), key=lambda item: item.name):
        relative = path.relative_to(package_root).as_posix().encode("ascii")
        data = path.read_bytes()
        digest.update(len(relative).to_bytes(4, "big"))
        digest.update(relative)
        digest.update(len(data).to_bytes(8, "big"))
        digest.update(data)
    return digest.hexdigest()


def verify_expected_identity(
    expected: WorkerIdentity,
    configured: WorkerIdentity,
) -> None:
    if not identities_match(expected, configured):
        fail(
            "identity_mismatch",
            "The expected worker identity does not match the configured worker.",
            stage="identity",
            action_id="f5.review-worker",
        )


def verify_runtime_metadata(config: WorkerConfig) -> None:
    config.verify_static_identity()
    if config.engine == "fake":
        return
    pins = RuntimePins.load()
    for distribution, expected in pins.packages.items():
        try:
            observed = importlib.metadata.version(distribution)
        except importlib.metadata.PackageNotFoundError as error:
            raise ContractError(
                "model_not_ready",
                f"Required pinned package {distribution}=={expected} is not installed.",
                stage="runtime_verification",
                action_id="f5.install-pinned-runtime",
            ) from error
        if observed != expected:
            fail(
                "identity_mismatch",
                f"Package {distribution} differs from its exact configured pin.",
                stage="runtime_verification",
                action_id="f5.install-pinned-runtime",
            )


def verify_artifacts(
    config: WorkerConfig,
    *,
    deadline_monotonic: float,
    cancel: Event,
) -> VerifiedArtifacts:
    source_paths = config.artifact_paths()
    by_role = {artifact.role: artifact for artifact in config.worker.artifacts}
    if config.engine == "f5":
        require(
            source_paths["vocoder_weights"].name == "pytorch_model.bin"
            and source_paths["vocoder_configuration"].name == "config.yaml"
            and source_paths["vocoder_weights"].parent
            == source_paths["vocoder_configuration"].parent,
            "identity_mismatch",
            "Pinned Vocos weights and config must share one local directory with canonical names.",
        )
        require(
            source_paths["model_weights"].suffix == ".safetensors",
            "identity_mismatch",
            "Pinned F5 model weights must be a safetensors file.",
        )
    owner = tempfile.TemporaryDirectory(prefix="martlet-f5-verified-artifacts-")
    root = Path(owner.name)
    paths = {
        "runtime_image": root / "runtime" / "runtime-inventory.json",
        "model_weights": root / "f5" / "model.safetensors",
        "vocabulary": root / "f5" / "vocab.txt",
        "vocoder_weights": root / "vocos" / "pytorch_model.bin",
        "vocoder_configuration": root / "vocos" / "config.yaml",
    }
    try:
        for role in ARTIFACT_ROLES:
            if cancel.is_set() or time.monotonic() >= deadline_monotonic:
                fail(
                    "deadline_exceeded",
                    "Artifact verification exceeded the warmup deadline.",
                    stage="artifact_verification",
                    action_id="f5.verify-local-artifacts",
                )
            _snapshot_verified_artifact(
                source_paths[role],
                paths[role],
                by_role[role].bytes,
                by_role[role].sha256,
                deadline_monotonic,
                cancel,
            )
        return VerifiedArtifacts(paths, owner)
    except Exception:
        owner.cleanup()
        raise


def _snapshot_verified_artifact(
    source: Path,
    destination: Path,
    expected_bytes: int,
    expected_sha256: str,
    deadline_monotonic: float,
    cancel: Event,
) -> None:
    try:
        before = source.stat(follow_symlinks=False)
        if not stat.S_ISREG(before.st_mode) or before.st_size != expected_bytes:
            fail(
                "identity_mismatch",
                "A local artifact type or byte count differs from its identity.",
                stage="artifact_verification",
                action_id="f5.reprovision-local-artifacts",
            )
        flags = os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0)
        descriptor = os.open(source, flags)
        try:
            destination.parent.mkdir(parents=True, exist_ok=True)
            output_flags = (
                os.O_WRONLY
                | os.O_CREAT
                | os.O_EXCL
                | getattr(os, "O_BINARY", 0)
            )
            output_descriptor = os.open(destination, output_flags, 0o600)
            try:
                opened = os.fstat(descriptor)
                if (
                    not stat.S_ISREG(opened.st_mode)
                    or opened.st_size != before.st_size
                    or (before.st_ino and opened.st_ino != before.st_ino)
                    or (before.st_dev and opened.st_dev != before.st_dev)
                ):
                    fail(
                        "identity_mismatch",
                        "A local artifact changed while it was opened.",
                        stage="artifact_verification",
                        action_id="f5.reprovision-local-artifacts",
                    )
                digest = hashlib.sha256()
                total = 0
                while True:
                    if cancel.is_set() or time.monotonic() >= deadline_monotonic:
                        fail(
                            "deadline_exceeded",
                            "Artifact verification exceeded the warmup deadline.",
                            stage="artifact_verification",
                            action_id="f5.verify-local-artifacts",
                        )
                    chunk = os.read(descriptor, 1_048_576)
                    if not chunk:
                        break
                    total += len(chunk)
                    if total > expected_bytes:
                        fail(
                            "identity_mismatch",
                            "A local artifact grew during verification.",
                            stage="artifact_verification",
                            action_id="f5.reprovision-local-artifacts",
                        )
                    digest.update(chunk)
                    view = memoryview(chunk)
                    while view:
                        written = os.write(output_descriptor, view)
                        if written <= 0:
                            raise OSError("artifact snapshot write made no progress")
                        view = view[written:]
                os.fsync(output_descriptor)
                after = os.fstat(descriptor)
            finally:
                os.close(output_descriptor)
        finally:
            os.close(descriptor)
    except ContractError:
        raise
    except OSError as error:
        raise ContractError(
            "model_not_ready",
            "A required local artifact could not be read.",
            stage="artifact_verification",
            action_id="f5.reprovision-local-artifacts",
        ) from error
    require(
        total == expected_bytes
        and after.st_size == before.st_size
        and after.st_mtime_ns == before.st_mtime_ns
        and digest.hexdigest() == expected_sha256,
        "identity_mismatch",
        "A local artifact digest or stable file identity differs from its pin.",
    )
    destination.chmod(stat.S_IREAD)


def verify_runtime_inventory(
    inventory_path: Path,
    *,
    deadline_monotonic: float,
    cancel: Event,
) -> RuntimeObservation:
    pins = RuntimePins.load()
    try:
        with inventory_path.open("rb") as stream:
            data = stream.read(MAX_RUNTIME_INVENTORY_BYTES + 1)
    except OSError as error:
        raise ContractError(
            "model_not_ready",
            "The verified runtime inventory could not be read.",
            stage="runtime_verification",
            action_id="f5.reprovision-runtime-inventory",
        ) from error
    source = parse_canonical_json(
        data,
        maximum=MAX_RUNTIME_INVENTORY_BYTES,
        name="runtime inventory",
    )
    require_exact_keys(
        source,
        (
            "format_version",
            "kind",
            "f5_source_revision",
            "python_executable",
            "packages",
        ),
        "runtime inventory",
    )
    require(
        require_int(source["format_version"], "runtime inventory format_version") == 1
        and source["kind"] == "martlet_f5_runtime_inventory"
        and source["f5_source_revision"] == pins.f5_source_revision,
        "identity_mismatch",
        "The runtime inventory format or F5 source revision differs from the lock.",
    )
    executable = require_object(
        source["python_executable"],
        "runtime inventory python_executable",
    )
    require_exact_keys(
        executable,
        ("bytes", "sha256"),
        "runtime inventory python_executable",
    )
    _verify_observed_file(
        Path(sys.executable),
        require_int(executable["bytes"], "python_executable.bytes"),
        require_string(executable["sha256"], "python_executable.sha256"),
        deadline_monotonic,
        cancel,
    )
    package_sources = require_array(source["packages"], "runtime inventory packages")
    expected_modules = {
        "f5-tts": "f5_tts",
        "torch": "torch",
        "torchaudio": "torchaudio",
        "vocos": "vocos",
    }
    require(
        len(package_sources) == len(expected_modules),
        "identity_mismatch",
        "The runtime inventory must contain exactly the four direct import packages.",
    )
    module_roots: dict[str, Path] = {}
    verified_files: dict[str, frozenset[Path]] = {}
    observed_distributions: list[str] = []
    for index, package_source in enumerate(package_sources):
        package = require_object(
            package_source,
            f"runtime inventory packages[{index}]",
        )
        require_exact_keys(
            package,
            ("distribution", "module", "version", "files"),
            f"runtime inventory packages[{index}]",
        )
        distribution_name = require_string(
            package["distribution"],
            f"runtime inventory packages[{index}].distribution",
        )
        observed_distributions.append(distribution_name)
        require(
            distribution_name in expected_modules
            and package["module"] == expected_modules[distribution_name]
            and package["version"] == pins.packages[distribution_name],
            "identity_mismatch",
            "A runtime inventory package name, module, or version differs from the lock.",
        )
        observation = _verify_distribution_tree(
            distribution_name,
            expected_modules[distribution_name],
            require_array(
                package["files"],
                f"runtime inventory packages[{index}].files",
            ),
            deadline_monotonic,
            cancel,
        )
        module_roots[expected_modules[distribution_name]] = observation[0]
        verified_files[expected_modules[distribution_name]] = observation[1]
    require(
        observed_distributions == sorted(expected_modules),
        "identity_mismatch",
        "Runtime inventory packages must be unique and sorted.",
    )
    return RuntimeObservation(module_roots, verified_files)


def verify_imported_module_origins(observation: RuntimeObservation) -> None:
    for module_name, root in observation.module_roots.items():
        module = sys.modules.get(module_name)
        module_file = getattr(module, "__file__", None) if module is not None else None
        require(
            type(module_file) is str
            and _same_path(Path(module_file), root / "__init__.py"),
            "identity_mismatch",
            f"Imported module {module_name} did not originate from its verified distribution.",
        )
        allowed = {
            os.path.normcase(os.path.abspath(candidate))
            for candidate in observation.verified_files[module_name]
        }
        for loaded_name, loaded in tuple(sys.modules.items()):
            if loaded_name != module_name and not loaded_name.startswith(module_name + "."):
                continue
            loaded_file = getattr(loaded, "__file__", None)
            if loaded_file is None:
                continue
            # File-less pseudo-modules (torch.ops, torch.classes) carry a bare-name __file__ and no import location;
            # nothing was loaded from disk for them. Every module the import system loaded from a file is checked.
            spec = getattr(loaded, "__spec__", None)
            if not os.path.isabs(loaded_file) and not getattr(spec, "has_location", False):
                continue
            require(
                os.path.normcase(os.path.abspath(loaded_file)) in allowed,
                "identity_mismatch",
                f"Imported module {loaded_name} was not present in the verified inventory.",
            )


def _verify_distribution_tree(
    distribution_name: str,
    module_name: str,
    declared_files_source: list[Any],
    deadline_monotonic: float,
    cancel: Event,
) -> tuple[Path, frozenset[Path]]:
    require(
        0 < len(declared_files_source) <= MAX_RUNTIME_PACKAGE_FILES,
        "identity_mismatch",
        "A runtime package file inventory is empty or over its bound.",
    )
    try:
        distribution = importlib.metadata.distribution(distribution_name)
    except importlib.metadata.PackageNotFoundError as error:
        raise ContractError(
            "model_not_ready",
            f"Required pinned package {distribution_name} is not installed.",
            stage="runtime_verification",
            action_id="f5.install-pinned-runtime",
        ) from error
    module_root = Path(distribution.locate_file(module_name))
    _reject_link_ancestors(module_root, None)
    for ancestor in module_root.parents:
        require(
            not os.access(ancestor, os.W_OK),
            "identity_mismatch",
            "Runtime package ancestors must not permit module-tree replacement.",
        )
    try:
        root_info = module_root.stat(follow_symlinks=False)
    except OSError as error:
        raise ContractError(
            "model_not_ready",
            f"Pinned package {distribution_name} has no regular module directory.",
            stage="runtime_verification",
            action_id="f5.install-pinned-runtime",
        ) from error
    require(
        stat.S_ISDIR(root_info.st_mode) and not os.access(module_root, os.W_OK),
        "identity_mismatch",
        f"Pinned package {distribution_name} module root must be a read-only directory.",
    )
    declared: dict[str, tuple[int, str]] = {}
    declared_order: list[str] = []
    for index, item_source in enumerate(declared_files_source):
        item = require_object(item_source, f"{distribution_name}.files[{index}]")
        require_exact_keys(
            item,
            ("path", "bytes", "sha256"),
            f"{distribution_name}.files[{index}]",
        )
        relative = _validate_inventory_relative_path(
            item["path"],
            f"{distribution_name}.files[{index}].path",
        )
        byte_count = require_int(item["bytes"], f"{distribution_name}.files[{index}].bytes")
        digest = require_string(item["sha256"], f"{distribution_name}.files[{index}].sha256")
        require(
            0 <= byte_count <= MAX_ARTIFACT_BYTES
            and len(digest) == 64
            and all(character in "0123456789abcdef" for character in digest),
            "identity_mismatch",
            "A runtime package file identity is invalid.",
        )
        require(
            relative not in declared,
            "identity_mismatch",
            "Runtime package file paths must be unique.",
        )
        declared[relative] = (byte_count, digest)
        declared_order.append(relative)
    require(
        declared_order == sorted(declared_order),
        "identity_mismatch",
        "Runtime package files must be sorted by canonical path.",
    )
    actual: dict[str, Path] = {}
    try:
        for path in module_root.rglob("*"):
            relative_path = path.relative_to(module_root)
            if "__pycache__" in relative_path.parts or path.suffix == ".pyc":
                fail(
                    "identity_mismatch",
                    f"Pinned package {distribution_name} contains unverified bytecode caches.",
                    stage="runtime_verification",
                    action_id="f5.rebuild-runtime-image",
                )
            if path.is_symlink():
                fail(
                    "identity_mismatch",
                    f"Pinned package {distribution_name} contains a symbolic link.",
                    stage="runtime_verification",
                    action_id="f5.rebuild-runtime-image",
                )
            info = path.stat(follow_symlinks=False)
            if stat.S_ISDIR(info.st_mode):
                require(
                    not os.access(path, os.W_OK),
                    "identity_mismatch",
                    "Runtime package directories must be read-only.",
                )
                continue
            require(
                stat.S_ISREG(info.st_mode) and not os.access(path, os.W_OK),
                "identity_mismatch",
                f"Pinned package {distribution_name} contains a non-regular or writable file.",
            )
            relative = relative_path.as_posix()
            actual[relative] = path
            require(
                len(actual) <= MAX_RUNTIME_PACKAGE_FILES
                and not cancel.is_set()
                and time.monotonic() < deadline_monotonic,
                "identity_mismatch",
                "Runtime enumeration exceeded its file or deadline bound.",
            )
    except OSError as error:
        raise ContractError(
            "model_not_ready",
            f"Pinned package {distribution_name} could not be enumerated.",
            stage="runtime_verification",
            action_id="f5.rebuild-runtime-image",
        ) from error
    require(
        frozenset(actual) == frozenset(declared),
        "identity_mismatch",
        f"Pinned package {distribution_name} files differ from the runtime inventory.",
    )
    for relative in sorted(actual):
        expected_bytes, expected_sha256 = declared[relative]
        _verify_observed_file(
            actual[relative],
            expected_bytes,
            expected_sha256,
            deadline_monotonic,
            cancel,
        )
    spec = importlib.util.find_spec(module_name)
    require(
        spec is not None
        and spec.origin is not None
        and _same_path(Path(spec.origin), module_root / "__init__.py"),
        "identity_mismatch",
        f"Import resolution for {module_name} is shadowed or outside its verified distribution.",
    )
    return module_root, frozenset(actual.values())


def _verify_observed_file(
    path: Path,
    expected_bytes: int,
    expected_sha256: str,
    deadline_monotonic: float,
    cancel: Event,
) -> None:
    require(
        0 <= expected_bytes <= MAX_ARTIFACT_BYTES
        and len(expected_sha256) == 64
        and all(character in "0123456789abcdef" for character in expected_sha256),
        "identity_mismatch",
        "An observed runtime file identity is invalid.",
    )
    try:
        before = path.stat(follow_symlinks=False)
        require(
            stat.S_ISREG(before.st_mode) and before.st_size == expected_bytes,
            "identity_mismatch",
            "An observed runtime file type or size differs from its inventory.",
        )
        descriptor = os.open(
            path,
            os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0),
        )
        try:
            opened = os.fstat(descriptor)
            require(
                stat.S_ISREG(opened.st_mode)
                and (opened.st_dev, opened.st_ino, opened.st_size, opened.st_mtime_ns)
                == (before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns),
                "identity_mismatch",
                "An observed runtime file changed while it was opened.",
            )
            digest = hashlib.sha256()
            total = 0
            while True:
                if cancel.is_set() or time.monotonic() >= deadline_monotonic:
                    fail(
                        "deadline_exceeded",
                        "Runtime inventory verification exceeded the warmup deadline.",
                        stage="runtime_verification",
                        action_id="f5.retry-warmup",
                    )
                chunk = os.read(descriptor, 1_048_576)
                if not chunk:
                    break
                total += len(chunk)
                require(
                    total <= expected_bytes,
                    "identity_mismatch",
                    "An observed runtime file grew during verification.",
                )
                digest.update(chunk)
            after = os.fstat(descriptor)
        finally:
            os.close(descriptor)
    except ContractError:
        raise
    except OSError as error:
        raise ContractError(
            "model_not_ready",
            "An observed runtime file could not be read.",
            stage="runtime_verification",
            action_id="f5.rebuild-runtime-image",
        ) from error
    require(
        total == expected_bytes
        and after.st_size == before.st_size
        and after.st_mtime_ns == before.st_mtime_ns
        and digest.hexdigest() == expected_sha256,
        "identity_mismatch",
        "An observed runtime file differs from its inventory.",
    )


def _validate_inventory_relative_path(value: Any, name: str) -> str:
    text = require_string(value, name)
    require(
        0 < len(text) <= 512
        and "\\" not in text
        and ":" not in text
        and all(ord(character) >= 32 for character in text)
        and not text.startswith("/")
        and not text.endswith("/"),
        "identity_mismatch",
        f"{name} is not a bounded canonical relative path.",
    )
    parsed = PurePosixPath(text)
    require(
        parsed.as_posix() == text
        and all(segment not in {"", ".", ".."} for segment in parsed.parts),
        "identity_mismatch",
        f"{name} is not canonical.",
    )
    return text


def _same_path(left: Path, right: Path) -> bool:
    try:
        return os.path.samefile(left, right)
    except OSError:
        return False


def _validate_config_path(path: Path) -> Path:
    require(
        path.is_absolute()
        and len(str(path)) <= 1_024
        and not path.drive.startswith("\\\\")
        and not str(path).startswith(("//", "\\\\")),
        "invalid_request",
        "The config path must be bounded and absolute.",
    )
    _reject_link_ancestors(path, None)
    return path


def _validate_root(path: Path) -> Path:
    _reject_link_ancestors(path, None)
    try:
        info = path.stat(follow_symlinks=False)
    except OSError as error:
        raise ContractError(
            "model_not_ready",
            "The configured artifact root is unavailable.",
            stage="artifact_verification",
            action_id="f5.reprovision-local-artifacts",
        ) from error
    require(
        stat.S_ISDIR(info.st_mode),
        "model_not_ready",
        "The configured artifact root is not a directory.",
    )
    return path


def _validate_relative_artifact_path(value: Any, role: str) -> str:
    text = require_string(value, f"artifact_files.{role}")
    require(
        0 < len(text) <= 512
        and "\\" not in text
        and ":" not in text
        and all(ord(character) >= 32 for character in text)
        and not text.startswith("/")
        and not text.endswith("/"),
        "invalid_request",
        f"artifact_files.{role} must be a bounded canonical relative path.",
    )
    parsed = PurePosixPath(text)
    require(
        all(
            segment not in {"", ".", ".."}
            and len(segment) <= 128
            and not segment.endswith((" ", "."))
            for segment in parsed.parts
        )
        and parsed.as_posix() == text,
        "invalid_request",
        f"artifact_files.{role} is not canonical.",
    )
    return text


def _reject_link_ancestors(path: Path, stop: Path | None) -> None:
    current = path
    stop_parent = stop.parent if stop is not None else None
    while True:
        try:
            is_junction = getattr(current, "is_junction", lambda: False)
            if current.is_symlink() or is_junction():
                fail(
                    "model_not_ready",
                    "Worker config and artifact paths may not use links or junctions.",
                    stage="artifact_verification",
                    action_id="f5.reprovision-local-artifacts",
                )
        except OSError as error:
            raise ContractError(
                "model_not_ready",
                "A configured local path could not be inspected.",
                stage="artifact_verification",
                action_id="f5.reprovision-local-artifacts",
            ) from error
        if current.parent == current or current.parent == stop_parent:
            break
        current = current.parent


def config_wire(
    *,
    engine: str,
    artifact_root: Path,
    artifact_files: dict[str, str],
    worker: WorkerIdentity,
    device: str | None,
) -> bytes:
    return canonical_json_bytes(
        {
            "artifact_files": artifact_files,
            "artifact_root": str(artifact_root),
            "device": device,
            "engine": engine,
            "format_version": 1,
            "kind": CONFIG_KIND,
            "worker": worker.wire(),
        }
    )
