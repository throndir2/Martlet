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
    ResourceRequirements,
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
from .jsonio import MAX_CONFIG_BYTES, JsonContractError, canonical_json_bytes
from .jsonio import parse_canonical_json as parse_json

CONFIG_KIND = "martlet_perception_worker_config"
PINS_KIND = "martlet_perception_runtime_pins"
RUNTIME_INVENTORY_KIND = "martlet_perception_runtime_inventory"
WORKER_BUILD_ID = "martlet-perception-python-worker"
FIXTURE_BUILD_ID = "martlet-perception-deterministic-fixture"
FIXTURE_VERSION = "0.0.0-fixture"
MAX_RUNTIME_INVENTORY_BYTES = 32 * 1_024 * 1_024
MAX_RUNTIME_PACKAGE_FILES = 100_000

ENGINE_ROLES = {
    "tesserocr_ocr": "ocr",
    "transformers_llava_next": "visual_question_answering",
}
ENGINE_ADAPTERS = {
    "tesserocr_ocr": ("tesserocr-ocr", "1.0.0", "tesseract-eng"),
    "transformers_llava_next": (
        "transformers-llava-next",
        "1.0.0",
        "llava-v1.6-mistral-7b",
    ),
}
ENGINE_MODULES = {
    "tesserocr_ocr": {
        "Pillow": "PIL",
        "martlet-perception-worker": "martlet_perception_worker",
        "tesserocr": "tesserocr",
    },
    "transformers_llava_next": {
        "Pillow": "PIL",
        "martlet-perception-worker": "martlet_perception_worker",
        "safetensors": "safetensors",
        "torch": "torch",
        "transformers": "transformers",
    },
}


def _parse_canonical(data: bytes, *, maximum: int, name: str) -> dict[str, Any]:
    try:
        return parse_json(data, maximum=maximum, name=name)
    except JsonContractError as error:
        raise ContractError(
            "invalid_request",
            f"The {name} is not bounded canonical JSON.",
            stage="configuration",
            remedy_code="perception.reprovision-config",
        ) from error


@dataclass(frozen=True)
class RuntimePins:
    python_version: str
    operating_system_id: str
    operating_system_version: str
    engines: dict[str, dict[str, Any]]

    @classmethod
    def load(cls) -> "RuntimePins":
        try:
            data = (
                importlib.resources.files("martlet_perception_worker")
                .joinpath("runtime-pins.v1.json")
                .read_bytes()
            )
        except (FileNotFoundError, OSError, ModuleNotFoundError) as error:
            raise ContractError(
                "model_not_ready",
                "The packaged perception runtime pin lock is missing or unreadable.",
                stage="runtime_verification",
                remedy_code="perception.reinstall-worker",
            ) from error
        data = data.removesuffix(b"\r\n").removesuffix(b"\n")
        source = _parse_canonical(
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
                "operating_system_id",
                "operating_system_version",
                "engines",
                "tooling",
            ),
            "runtime pin lock",
        )
        require(
            require_int(source["format_version"], "format_version") == 1
            and source["kind"] == PINS_KIND,
            "identity_mismatch",
            "The runtime pin lock format is unsupported.",
        )
        tooling = require_object(source["tooling"], "runtime pins tooling")
        require_exact_keys(tooling, ("setuptools",), "runtime pins tooling")
        require_string(tooling["setuptools"], "tooling.setuptools")
        engines = require_object(source["engines"], "runtime pins engines")
        require_exact_keys(engines, ENGINE_ROLES, "runtime pins engines")
        parsed: dict[str, dict[str, Any]] = {}
        for engine, role in ENGINE_ROLES.items():
            item = require_object(engines[engine], f"engines.{engine}")
            require_exact_keys(
                item,
                (
                    "role",
                    "adapter_id",
                    "adapter_version",
                    "model_id",
                    "packages",
                    "native_runtime_version",
                ),
                f"engines.{engine}",
            )
            packages = require_object(item["packages"], f"engines.{engine}.packages")
            require_exact_keys(
                packages,
                ENGINE_MODULES[engine],
                f"engines.{engine}.packages",
            )
            expected_adapter = ENGINE_ADAPTERS[engine]
            require(
                item["role"] == role
                and item["adapter_id"] == expected_adapter[0]
                and item["adapter_version"] == expected_adapter[1]
                and item["model_id"] == expected_adapter[2],
                "identity_mismatch",
                f"The {engine} pin does not match the implemented adapter.",
            )
            parsed[engine] = {
                "role": role,
                "adapter_id": item["adapter_id"],
                "adapter_version": item["adapter_version"],
                "model_id": item["model_id"],
                "native_runtime_version": require_string(
                    item["native_runtime_version"],
                    f"engines.{engine}.native_runtime_version",
                ),
                "packages": {
                    name: require_string(
                        packages[name],
                        f"engines.{engine}.packages.{name}",
                    )
                    for name in sorted(packages)
                },
            }
        return cls(
            python_version=require_string(source["python_version"], "python_version"),
            operating_system_id=require_string(
                source["operating_system_id"],
                "operating_system_id",
            ),
            operating_system_version=require_string(
                source["operating_system_version"],
                "operating_system_version",
            ),
            engines=parsed,
        )


@dataclass
class VerifiedArtifacts:
    paths: dict[str, Path]
    _temporary_directory: tempfile.TemporaryDirectory[str]

    def cleanup(self) -> None:
        self._temporary_directory.cleanup()


@dataclass(frozen=True)
class RuntimeObservation:
    module_origins: dict[str, Path]
    verified_files: dict[str, dict[Path, tuple[int, str]]]


@dataclass(frozen=True)
class WorkerConfig:
    engine: str
    host_id: str
    artifact_root: Path
    artifact_files: dict[str, str]
    worker: WorkerIdentity
    device: str | None
    resource_budget: ResourceRequirements

    @classmethod
    def load(cls, path: Path) -> "WorkerConfig":
        config_path = _validate_config_path(path)
        try:
            require(
                stat.S_ISREG(config_path.stat(follow_symlinks=False).st_mode),
                "invalid_request",
                "The worker configuration must be a regular local file.",
            )
            with config_path.open("rb") as stream:
                data = stream.read(MAX_CONFIG_BYTES + 1)
        except OSError as error:
            raise ContractError(
                "model_not_ready",
                "The worker configuration could not be read.",
                stage="configuration",
                remedy_code="perception.install-worker-config",
            ) from error
        source = _parse_canonical(
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
                "host_id",
                "artifact_root",
                "artifact_files",
                "worker",
                "device",
                "resource_budget",
            ),
            "worker configuration",
        )
        require(
            require_int(source["format_version"], "format_version") == 1
            and source["kind"] == CONFIG_KIND,
            "invalid_request",
            "The worker configuration format is unsupported.",
        )
        engine = require_string(source["engine"], "engine")
        require(
            engine in {"fake", *ENGINE_ROLES},
            "invalid_request",
            "engine must name one implemented adapter.",
        )
        artifact_root_text = require_string(source["artifact_root"], "artifact_root")
        require(
            0 < len(artifact_root_text) <= 1_024,
            "invalid_request",
            "artifact_root exceeds its bound.",
        )
        artifact_root = Path(artifact_root_text)
        require(
            artifact_root.is_absolute()
            and not artifact_root.drive.startswith(("\\\\", "//"))
            and "\x00" not in artifact_root_text,
            "invalid_request",
            "artifact_root must be an absolute local directory.",
        )
        worker = WorkerIdentity.parse(source["worker"])
        expected_roles = (
            ("runtime_image", "model_weights", "model_configuration")
            if worker.role == "ocr"
            else ARTIFACT_ROLES
        )
        require(
            tuple(item.role for item in worker.artifacts) == expected_roles,
            "identity_mismatch",
            "This adapter requires exactly one artifact for each role it implements.",
        )
        files = require_object(source["artifact_files"], "artifact_files")
        require_exact_keys(files, expected_roles, "artifact_files")
        artifact_files = {
            role: _validate_relative_artifact_path(files[role], role)
            for role in expected_roles
        }
        device_source = source["device"]
        if engine == "transformers_llava_next":
            device = require_string(device_source, "device")
            require(
                len(device) <= 7
                and device.startswith("cuda:")
                and device[5:].isdigit()
                and 0 <= int(device[5:]) <= 15,
                "invalid_request",
                "The LLaVA-NeXT adapter requires explicit cuda:0 through cuda:15.",
            )
        elif engine == "tesserocr_ocr":
            require(
                device_source == "cpu",
                "invalid_request",
                "The Tesseract adapter device must be cpu.",
            )
            device = "cpu"
        else:
            require(
                device_source is None,
                "invalid_request",
                "The fake engine does not accept a device.",
            )
            device = None
        config = cls(
            engine=engine,
            host_id=_validate_identifier_local(source["host_id"], "host_id", 64),
            artifact_root=artifact_root,
            artifact_files=artifact_files,
            worker=worker,
            device=device,
            resource_budget=ResourceRequirements.parse(
                source["resource_budget"],
                "resource_budget",
            ),
        )
        if engine != "fake":
            try:
                config_info = config_path.stat(follow_symlinks=False)
            except OSError as error:
                raise ContractError(
                    "model_not_ready",
                    "The production worker configuration could not be rechecked.",
                    stage="configuration",
                    remedy_code="perception.install-worker-config",
                ) from error
            require(
                stat.S_ISREG(config_info.st_mode)
                and not _is_writable(config_path, config_info),
                "identity_mismatch",
                "The production worker configuration must be read-only.",
            )
        config.verify_static_identity()
        return config

    def verify_static_identity(self) -> None:
        worker = self.worker
        runtime = worker.runtime
        expected_build = (
            FIXTURE_BUILD_ID if self.engine == "fake" else WORKER_BUILD_ID
        )
        require(
            runtime.worker_build_id == expected_build
            and runtime.worker_build_revision == compute_worker_build_revision(),
            "identity_mismatch",
            "The configured worker build does not match these worker sources.",
        )
        require(
            runtime.runtime_id == "cpython"
            and runtime.runtime_version == platform.python_version()
            and runtime.runtime_revision == compute_python_runtime_revision(),
            "identity_mismatch",
            "The configured Python runtime identity does not match this interpreter.",
        )
        require(
            worker.resources.cpu_units <= self.resource_budget.cpu_units
            and worker.resources.gpu_memory_mib
            <= self.resource_budget.gpu_memory_mib,
            "resource_exhausted",
            "The selected worker cannot fit the configured resource budget.",
        )
        model_weights = next(
            item for item in worker.artifacts if item.role == "model_weights"
        )
        require(
            worker.model.model_sha256 == model_weights.sha256,
            "identity_mismatch",
            "The model identity digest must match the exact model weights artifact.",
        )
        if self.engine == "fake":
            require(
                worker.evidence == "synthetic_fixture"
                and worker.cancellation == "discard_only"
                and runtime.operating_system_id == "fixture-os"
                and runtime.operating_system_version == FIXTURE_VERSION
                and worker.model.adapter_id
                == f"martlet-fixture-{worker.role.replace('_', '-')}"
                and worker.model.adapter_version == FIXTURE_VERSION,
                "identity_mismatch",
                "The fake worker identity must remain explicit fixture-only evidence.",
            )
            return
        pins = RuntimePins.load()
        pin = pins.engines[self.engine]
        require(
            worker.evidence == "live_worker"
            and worker.cancellation == "discard_only"
            and worker.role == ENGINE_ROLES[self.engine]
            and worker.model.adapter_id == pin["adapter_id"]
            and worker.model.adapter_version == pin["adapter_version"]
            and worker.model.model_id == pin["model_id"]
            and runtime.runtime_version == pins.python_version
            and runtime.operating_system_id == pins.operating_system_id
            and runtime.operating_system_version == pins.operating_system_version,
            "identity_mismatch",
            "The production identity differs from the one implemented pinned adapter.",
        )
        _verify_operating_system(pins)

    def artifact_paths(self) -> dict[str, Path]:
        root = _validate_root(
            self.artifact_root,
            require_read_only=self.engine != "fake",
        )
        result: dict[str, Path] = {}
        for role, relative in self.artifact_files.items():
            candidate = root.joinpath(*PurePosixPath(relative).parts)
            _reject_link_ancestors(candidate, root)
            result[role] = candidate
        return result


def compute_worker_build_revision() -> str:
    package_root = Path(__file__).resolve().parent
    digest = hashlib.sha1(usedforsecurity=False)
    paths = sorted(
        (
            *package_root.glob("*.py"),
            package_root / "runtime-pins.v1.json",
        ),
        key=lambda item: item.name,
    )
    for path in paths:
        relative = path.relative_to(package_root).as_posix().encode("ascii")
        data = path.read_bytes()
        digest.update(len(relative).to_bytes(4, "big"))
        digest.update(relative)
        digest.update(len(data).to_bytes(8, "big"))
        digest.update(data)
    return digest.hexdigest()


def compute_python_runtime_revision() -> str:
    digest = hashlib.sha1(usedforsecurity=False)
    try:
        with open(sys.executable, "rb") as stream:
            while chunk := stream.read(1_048_576):
                digest.update(chunk)
    except OSError as error:
        raise ContractError(
            "model_not_ready",
            "The Python executable could not be identified.",
            stage="runtime_verification",
            remedy_code="perception.rebuild-runtime-image",
        ) from error
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
            remedy_code="perception.review-worker",
        )


def verify_runtime_metadata(config: WorkerConfig) -> None:
    config.verify_static_identity()
    if config.engine == "fake":
        return
    pins = RuntimePins.load()
    for distribution, expected in pins.engines[config.engine]["packages"].items():
        try:
            observed = importlib.metadata.version(distribution)
        except importlib.metadata.PackageNotFoundError as error:
            raise ContractError(
                "model_not_ready",
                f"Required pinned package {distribution}=={expected} is not installed.",
                stage="runtime_verification",
                remedy_code="perception.install-pinned-runtime",
            ) from error
        require(
            observed == expected,
            "identity_mismatch",
            f"Package {distribution} does not match the required direct pin.",
        )


def verify_artifacts(
    config: WorkerConfig,
    *,
    deadline_monotonic: float,
    cancel: Event,
) -> VerifiedArtifacts:
    source_paths = config.artifact_paths()
    by_role = {item.role: item for item in config.worker.artifacts}
    owner = tempfile.TemporaryDirectory(
        prefix="martlet-perception-verified-artifacts-"
    )
    root = Path(owner.name)
    destinations = {
        "runtime_image": root / "runtime" / "runtime-inventory.json",
        "model_weights": (
            root / "ocr" / "tessdata" / "eng.traineddata"
            if config.worker.role == "ocr"
            else root / "vlm" / "model.safetensors"
        ),
        "model_configuration": (
            root / "ocr" / "config.json"
            if config.worker.role == "ocr"
            else root / "vlm" / "config.json"
        ),
        "tokenizer": root / "vlm" / "tokenizer.json",
        "projector": root / "vlm" / "mm_projector.safetensors",
    }
    try:
        for role in config.artifact_files:
            if cancel.is_set() or time.monotonic() >= deadline_monotonic:
                fail(
                    "deadline_exceeded",
                    "Artifact verification exceeded the warmup deadline.",
                    stage="artifact_verification",
                    remedy_code="perception.retry-warmup",
                )
            _snapshot_verified_artifact(
                source_paths[role],
                destinations[role],
                by_role[role].bytes,
                by_role[role].sha256,
                deadline_monotonic,
                cancel,
                require_read_only=config.engine != "fake",
            )
        return VerifiedArtifacts(
            {role: destinations[role] for role in config.artifact_files},
            owner,
        )
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
    *,
    require_read_only: bool,
) -> None:
    try:
        before = source.stat(follow_symlinks=False)
        require(
            stat.S_ISREG(before.st_mode)
            and before.st_size == expected_bytes
            and (not require_read_only or not _is_writable(source, before)),
            "identity_mismatch",
            "A local artifact type, size, or read-only state differs from its identity.",
        )
        descriptor = os.open(
            source,
            os.O_RDONLY
            | getattr(os, "O_BINARY", 0)
            | getattr(os, "O_NOFOLLOW", 0),
        )
        try:
            opened = os.fstat(descriptor)
            require(
                stat.S_ISREG(opened.st_mode)
                and opened.st_size == before.st_size
                and (not before.st_ino or opened.st_ino == before.st_ino)
                and (not before.st_dev or opened.st_dev == before.st_dev),
                "identity_mismatch",
                "A local artifact changed while it was opened.",
            )
            destination.parent.mkdir(parents=True, exist_ok=True)
            output = os.open(
                destination,
                os.O_WRONLY
                | os.O_CREAT
                | os.O_EXCL
                | getattr(os, "O_BINARY", 0),
                0o600,
            )
            digest = hashlib.sha256()
            total = 0
            try:
                while True:
                    if cancel.is_set() or time.monotonic() >= deadline_monotonic:
                        fail(
                            "deadline_exceeded",
                            "Artifact verification exceeded the warmup deadline.",
                            stage="artifact_verification",
                            remedy_code="perception.retry-warmup",
                        )
                    chunk = os.read(descriptor, 1_048_576)
                    if not chunk:
                        break
                    total += len(chunk)
                    require(
                        total <= expected_bytes,
                        "identity_mismatch",
                        "A local artifact grew during verification.",
                    )
                    digest.update(chunk)
                    view = memoryview(chunk)
                    while view:
                        written = os.write(output, view)
                        if written <= 0:
                            raise OSError("artifact copy made no progress")
                        view = view[written:]
                os.fsync(output)
                after = os.fstat(descriptor)
            finally:
                os.close(output)
        finally:
            os.close(descriptor)
    except ContractError:
        raise
    except OSError as error:
        raise ContractError(
            "model_not_ready",
            "A required local artifact could not be read.",
            stage="artifact_verification",
            remedy_code="perception.reprovision-local-artifacts",
        ) from error
    require(
        total == expected_bytes
        and after.st_size == before.st_size
        and after.st_mtime_ns == before.st_mtime_ns
        and digest.hexdigest() == expected_sha256,
        "identity_mismatch",
        "A local artifact digest or stable file identity differs from its pin.",
    )


def verify_runtime_inventory(
    config: WorkerConfig,
    inventory_path: Path,
    *,
    deadline_monotonic: float,
    cancel: Event,
) -> RuntimeObservation | None:
    if config.engine == "fake":
        return None
    pins = RuntimePins.load()
    try:
        with inventory_path.open("rb") as stream:
            data = stream.read(MAX_RUNTIME_INVENTORY_BYTES + 1)
    except OSError as error:
        raise ContractError(
            "model_not_ready",
            "The verified runtime inventory could not be read.",
            stage="runtime_verification",
            remedy_code="perception.reprovision-runtime-inventory",
        ) from error
    source = _parse_canonical(
        data,
        maximum=MAX_RUNTIME_INVENTORY_BYTES,
        name="runtime inventory",
    )
    require_exact_keys(
        source,
        ("format_version", "kind", "engine", "python_executable", "packages"),
        "runtime inventory",
    )
    require(
        require_int(source["format_version"], "format_version") == 1
        and source["kind"] == RUNTIME_INVENTORY_KIND
        and source["engine"] == config.engine,
        "identity_mismatch",
        "The runtime inventory format or engine differs from the selected adapter.",
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
        require_read_only=True,
    )
    expected_modules = ENGINE_MODULES[config.engine]
    package_sources = require_array(source["packages"], "runtime inventory packages")
    require(
        len(package_sources) == len(expected_modules),
        "identity_mismatch",
        "The runtime inventory does not contain every direct import package.",
    )
    observed_distributions: list[str] = []
    module_origins: dict[str, Path] = {}
    verified_files: dict[str, dict[Path, tuple[int, str]]] = {}
    for index, package_source in enumerate(package_sources):
        package = require_object(package_source, f"packages[{index}]")
        require_exact_keys(
            package,
            ("distribution", "module", "version", "files"),
            f"packages[{index}]",
        )
        distribution = require_string(
            package["distribution"],
            f"packages[{index}].distribution",
        )
        observed_distributions.append(distribution)
        require(
            distribution in expected_modules
            and package["module"] == expected_modules[distribution]
            and package["version"]
            == pins.engines[config.engine]["packages"][distribution],
            "identity_mismatch",
            "A runtime package identity differs from the direct pin lock.",
        )
        origin, files = _verify_distribution_tree(
            distribution,
            expected_modules[distribution],
            require_array(package["files"], f"packages[{index}].files"),
            deadline_monotonic,
            cancel,
        )
        module_origins[expected_modules[distribution]] = origin
        verified_files[expected_modules[distribution]] = files
    require(
        observed_distributions == sorted(expected_modules),
        "identity_mismatch",
        "Runtime inventory packages must be unique and sorted.",
    )
    return RuntimeObservation(module_origins, verified_files)


def verify_imported_module_origins(
    observation: RuntimeObservation,
    *,
    deadline_monotonic: float,
    cancel: Event,
) -> None:
    for module_name, origin in observation.module_origins.items():
        module = sys.modules.get(module_name)
        module_file = getattr(module, "__file__", None) if module is not None else None
        require(
            type(module_file) is str and _same_path(Path(module_file), origin),
            "identity_mismatch",
            f"Imported module {module_name} did not originate from its verified tree.",
        )
        allowed = {
            os.path.normcase(os.path.abspath(path)): identity
            for path, identity in observation.verified_files[module_name].items()
        }
        for loaded_name, loaded in tuple(sys.modules.items()):
            if loaded_name != module_name and not loaded_name.startswith(
                module_name + "."
            ):
                continue
            loaded_file = getattr(loaded, "__file__", None)
            if loaded_file is None:
                continue
            require(
                os.path.normcase(os.path.abspath(loaded_file)) in allowed,
                "identity_mismatch",
                f"Imported module {loaded_name} was absent from the verified inventory.",
            )
        for path, (expected_bytes, expected_sha256) in observation.verified_files[
            module_name
        ].items():
            _verify_observed_file(
                path,
                expected_bytes,
                expected_sha256,
                deadline_monotonic,
                cancel,
                require_read_only=True,
            )


def _verify_distribution_tree(
    distribution_name: str,
    module_name: str,
    declared_files_source: list[Any],
    deadline_monotonic: float,
    cancel: Event,
) -> tuple[Path, dict[Path, tuple[int, str]]]:
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
            remedy_code="perception.install-pinned-runtime",
        ) from error
    del distribution
    spec = importlib.util.find_spec(module_name)
    require(
        spec is not None and spec.origin is not None,
        "identity_mismatch",
        f"Import resolution for {module_name} is unavailable.",
    )
    origin = Path(spec.origin)
    _reject_link_ancestors(origin, None)
    if spec.submodule_search_locations:
        locations = tuple(Path(item) for item in spec.submodule_search_locations)
        require(
            len(locations) == 1,
            "identity_mismatch",
            f"Import resolution for {module_name} spans multiple roots.",
        )
        root = locations[0]
        require(
            _same_path(origin, root / "__init__.py"),
            "identity_mismatch",
            f"Import resolution for {module_name} has a noncanonical package origin.",
        )
        actual = _enumerate_package_tree(root, distribution_name)
    else:
        root = origin.parent
        info = origin.stat(follow_symlinks=False)
        require(
            stat.S_ISREG(info.st_mode) and not _is_writable(origin, info),
            "identity_mismatch",
            f"Pinned module {module_name} must be one read-only regular file.",
        )
        actual = {origin.name: origin}
    declared: dict[str, tuple[int, str]] = {}
    declared_order: list[str] = []
    for index, source in enumerate(declared_files_source):
        item = require_object(source, f"{distribution_name}.files[{index}]")
        require_exact_keys(
            item,
            ("path", "bytes", "sha256"),
            f"{distribution_name}.files[{index}]",
        )
        relative = _validate_inventory_relative_path(
            item["path"],
            f"{distribution_name}.files[{index}].path",
        )
        identity = (
            require_int(item["bytes"], f"{distribution_name}.files[{index}].bytes"),
            require_string(
                item["sha256"],
                f"{distribution_name}.files[{index}].sha256",
            ),
        )
        require(
            0 <= identity[0] <= MAX_ARTIFACT_BYTES
            and len(identity[1]) == 64
            and all(character in "0123456789abcdef" for character in identity[1])
            and relative not in declared,
            "identity_mismatch",
            "A runtime package file identity is invalid or duplicated.",
        )
        declared[relative] = identity
        declared_order.append(relative)
    require(
        declared_order == sorted(declared_order)
        and frozenset(actual) == frozenset(declared),
        "identity_mismatch",
        f"Pinned package {distribution_name} files differ from the runtime inventory.",
    )
    verified: dict[Path, tuple[int, str]] = {}
    for relative in sorted(actual):
        expected_bytes, expected_sha256 = declared[relative]
        path = actual[relative]
        _verify_observed_file(
            path,
            expected_bytes,
            expected_sha256,
            deadline_monotonic,
            cancel,
            require_read_only=True,
        )
        verified[path] = (expected_bytes, expected_sha256)
    return origin, verified


def _enumerate_package_tree(root: Path, distribution_name: str) -> dict[str, Path]:
    try:
        root_info = root.stat(follow_symlinks=False)
        require(
            stat.S_ISDIR(root_info.st_mode) and not _is_writable(root, root_info),
            "identity_mismatch",
            f"Pinned package {distribution_name} root must be read-only.",
        )
        actual: dict[str, Path] = {}
        for path in root.rglob("*"):
            require(
                len(actual) < MAX_RUNTIME_PACKAGE_FILES,
                "identity_mismatch",
                "The runtime package tree exceeds its file bound.",
            )
            relative_path = path.relative_to(root)
            if "__pycache__" in relative_path.parts or path.suffix == ".pyc":
                fail(
                    "identity_mismatch",
                    f"Pinned package {distribution_name} contains bytecode caches.",
                    stage="runtime_verification",
                    remedy_code="perception.rebuild-runtime-image",
                )
            if path.is_symlink() or getattr(path, "is_junction", lambda: False)():
                fail(
                    "identity_mismatch",
                    f"Pinned package {distribution_name} contains a link.",
                    stage="runtime_verification",
                    remedy_code="perception.rebuild-runtime-image",
                )
            info = path.stat(follow_symlinks=False)
            if stat.S_ISDIR(info.st_mode):
                require(
                    not _is_writable(path, info),
                    "identity_mismatch",
                    f"Pinned package {distribution_name} has a writable directory.",
                )
                continue
            require(
                stat.S_ISREG(info.st_mode) and not _is_writable(path, info),
                "identity_mismatch",
                f"Pinned package {distribution_name} has a writable or irregular file.",
            )
            actual[relative_path.as_posix()] = path
        return actual
    except ContractError:
        raise
    except OSError as error:
        raise ContractError(
            "model_not_ready",
            f"Pinned package {distribution_name} could not be enumerated.",
            stage="runtime_verification",
            remedy_code="perception.rebuild-runtime-image",
        ) from error


def _verify_observed_file(
    path: Path,
    expected_bytes: int,
    expected_sha256: str,
    deadline_monotonic: float,
    cancel: Event,
    *,
    require_read_only: bool,
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
            stat.S_ISREG(before.st_mode)
            and before.st_size == expected_bytes
            and (not require_read_only or not _is_writable(path, before)),
            "identity_mismatch",
            "An observed runtime file type, size, or read-only state differs.",
        )
        descriptor = os.open(
            path,
            os.O_RDONLY
            | getattr(os, "O_BINARY", 0)
            | getattr(os, "O_NOFOLLOW", 0),
        )
        try:
            opened = os.fstat(descriptor)
            require(
                stat.S_ISREG(opened.st_mode)
                and opened.st_size == before.st_size
                and (not before.st_ino or opened.st_ino == before.st_ino)
                and (not before.st_dev or opened.st_dev == before.st_dev),
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
                        remedy_code="perception.retry-warmup",
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
            remedy_code="perception.rebuild-runtime-image",
        ) from error
    require(
        total == expected_bytes
        and after.st_size == before.st_size
        and after.st_mtime_ns == before.st_mtime_ns
        and digest.hexdigest() == expected_sha256,
        "identity_mismatch",
        "An observed runtime file differs from its inventory.",
    )


def _verify_operating_system(pins: RuntimePins) -> None:
    if os.name != "posix":
        fail(
            "identity_mismatch",
            "Production perception adapters are pinned only for the reviewed Ubuntu image.",
            stage="runtime_verification",
            remedy_code="perception.use-pinned-runtime-image",
        )
    try:
        values: dict[str, str] = {}
        for line in Path("/etc/os-release").read_text(
            encoding="utf-8",
            errors="strict",
        ).splitlines():
            if "=" not in line:
                continue
            key, value = line.split("=", 1)
            values[key] = value.strip().strip('"')
    except OSError as error:
        raise ContractError(
            "model_not_ready",
            "The pinned operating-system identity could not be read.",
            stage="runtime_verification",
            remedy_code="perception.use-pinned-runtime-image",
        ) from error
    require(
        values.get("ID") == pins.operating_system_id
        and values.get("VERSION_ID") == pins.operating_system_version,
        "identity_mismatch",
        "The operating system differs from the direct runtime pin lock.",
    )


def _validate_inventory_relative_path(value: Any, name: str) -> str:
    text = require_string(value, name)
    require(
        0 < len(text) <= 512
        and "\\" not in text
        and ":" not in text
        and all(ord(character) >= 32 and ord(character) != 127 for character in text)
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
        path.is_absolute() and len(str(path)) <= 1_024
        and not path.drive.startswith(("\\\\", "//"))
        and "\x00" not in str(path),
        "invalid_request",
        "The config path must be bounded and absolute.",
    )
    _reject_link_ancestors(path, None)
    return path


def _validate_root(path: Path, *, require_read_only: bool) -> Path:
    _reject_link_ancestors(path, None)
    try:
        info = path.stat(follow_symlinks=False)
    except OSError as error:
        raise ContractError(
            "model_not_ready",
            "The configured artifact root is unavailable.",
            stage="artifact_verification",
            remedy_code="perception.reprovision-local-artifacts",
        ) from error
    require(
        stat.S_ISDIR(info.st_mode)
        and (not require_read_only or not _is_writable(path, info)),
        "model_not_ready",
        "The configured artifact root must be a read-only directory.",
    )
    return path


def _validate_relative_artifact_path(value: Any, role: str) -> str:
    text = require_string(value, f"artifact_files.{role}")
    require(
        0 < len(text) <= 512
        and "\\" not in text
        and ":" not in text
        and all(ord(character) >= 32 and ord(character) != 127 for character in text)
        and not text.startswith("/")
        and not text.endswith("/"),
        "invalid_request",
        f"artifact_files.{role} must be a bounded canonical relative path.",
    )
    parsed = PurePosixPath(text)
    require(
        parsed.as_posix() == text
        and all(
            segment not in {"", ".", ".."}
            and len(segment) <= 128
            and not segment.endswith((" ", "."))
            for segment in parsed.parts
        ),
        "invalid_request",
        f"artifact_files.{role} is not canonical.",
    )
    return text


def _reject_link_ancestors(path: Path, stop: Path | None) -> None:
    current = path
    stop_parent = stop.parent if stop is not None else None
    while True:
        try:
            if current.is_symlink() or getattr(current, "is_junction", lambda: False)():
                fail(
                    "model_not_ready",
                    "Worker configuration and artifact paths may not use links.",
                    stage="artifact_verification",
                    remedy_code="perception.reprovision-local-artifacts",
                )
        except OSError as error:
            raise ContractError(
                "model_not_ready",
                "A configured local path could not be inspected.",
                stage="artifact_verification",
                remedy_code="perception.reprovision-local-artifacts",
            ) from error
        if current.parent == current or current.parent == stop_parent:
            break
        current = current.parent


def _is_writable(path: Path, info: os.stat_result) -> bool:
    if os.name == "nt":
        return bool(info.st_mode & stat.S_IWRITE)
    return os.access(path, os.W_OK)


def _validate_identifier_local(value: Any, name: str, maximum: int) -> str:
    text = require_string(value, name)
    require(
        0 < len(text) <= maximum
        and text[0].isalnum()
        and text[0].isascii()
        and all(
            (character.isalnum() and character.isascii())
            or character in "_.-"
            for character in text
        ),
        "invalid_request",
        f"{name} is not a valid identifier.",
    )
    return text


def config_wire(
    *,
    engine: str,
    host_id: str,
    artifact_root: Path,
    artifact_files: dict[str, str],
    worker: WorkerIdentity,
    device: str | None,
    resource_budget: ResourceRequirements,
) -> bytes:
    return canonical_json_bytes(
        {
            "artifact_files": artifact_files,
            "artifact_root": str(artifact_root),
            "device": device,
            "engine": engine,
            "format_version": 1,
            "host_id": host_id,
            "kind": CONFIG_KIND,
            "resource_budget": resource_budget.wire(),
            "worker": worker.wire(),
        }
    )
