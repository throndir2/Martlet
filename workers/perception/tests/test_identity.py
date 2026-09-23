from __future__ import annotations

import hashlib
import importlib.metadata
import json
import sys
import tempfile
import time
import types
import unittest
from dataclasses import replace
from pathlib import Path
from threading import Event
from unittest import mock

from martlet_perception_worker.contract import (
    ContractError,
    ResourceRequirements,
)
from martlet_perception_worker.identity import (
    RuntimeObservation,
    WorkerConfig,
    _snapshot_verified_artifact,
    _verify_distribution_tree,
    config_wire,
    verify_artifacts,
    verify_imported_module_origins,
    verify_runtime_metadata,
)
from martlet_perception_worker.jsonio import canonical_json_bytes

from tests.support import FixtureEnvironment


class IdentityTests(unittest.TestCase):
    def test_config_rejects_role_drift_and_model_digest_drift(self) -> None:
        with FixtureEnvironment("ocr") as environment:
            for worker in (
                replace(
                    environment.worker,
                    role="visual_question_answering",
                ),
                replace(
                    environment.worker,
                    model=replace(
                        environment.worker.model,
                        model_sha256="0" * 64,
                    ),
                ),
            ):
                path = environment.root / f"bad-{worker.role}.json"
                path.write_bytes(
                    config_wire(
                        engine="fake",
                        host_id="fixture-host",
                        artifact_root=environment.artifact_root,
                        artifact_files=environment.artifact_files,
                        worker=worker,
                        device=None,
                        resource_budget=ResourceRequirements(4, 2_048),
                    )
                )
                with self.subTest(role=worker.role):
                    with self.assertRaises(ContractError):
                        WorkerConfig.load(path)

    def test_config_rejects_resource_budget_before_warmup(self) -> None:
        with FixtureEnvironment("visual_question_answering") as environment:
            path = environment.root / "small-budget.json"
            path.write_bytes(
                config_wire(
                    engine="fake",
                    host_id="fixture-host",
                    artifact_root=environment.artifact_root,
                    artifact_files=environment.artifact_files,
                    worker=environment.worker,
                    device=None,
                    resource_budget=ResourceRequirements(1, 0),
                )
            )
            with self.assertRaises(ContractError) as raised:
                WorkerConfig.load(path)
            self.assertEqual(raised.exception.code, "resource_exhausted")

    def test_config_forbids_absolute_url_and_traversal_artifact_inputs(self) -> None:
        with FixtureEnvironment("ocr") as environment:
            source = json.loads(environment.config_path.read_text(encoding="utf-8"))
            for value in (
                "../model.bin",
                "/tmp/model.bin",
                "https://models/model.bin",
                "C:\\models\\model.bin",
            ):
                changed = json.loads(json.dumps(source))
                changed["artifact_files"]["model_weights"] = value
                path = environment.root / (
                    hashlib.sha1(value.encode(), usedforsecurity=False).hexdigest()
                    + ".json"
                )
                path.write_bytes(canonical_json_bytes(changed))
                with self.subTest(value=value):
                    with self.assertRaises(ContractError):
                        WorkerConfig.load(path)

    def test_artifact_digest_drift_fails_before_engine_load(self) -> None:
        with FixtureEnvironment("ocr") as environment:
            model = (
                environment.artifact_root
                / environment.artifact_files["model_weights"]
            )
            data = model.read_bytes()
            model.write_bytes(bytes((data[0] ^ 1,)) + data[1:])
            with self.assertRaises(ContractError) as raised:
                verify_artifacts(
                    environment.config,
                    deadline_monotonic=time.monotonic() + 2,
                    cancel=Event(),
                )
            self.assertEqual(raised.exception.code, "identity_mismatch")

    def test_verified_snapshot_resists_source_replacement_after_hash(self) -> None:
        with FixtureEnvironment("ocr") as environment:
            verified = verify_artifacts(
                environment.config,
                deadline_monotonic=time.monotonic() + 2,
                cancel=Event(),
            )
            try:
                source = (
                    environment.artifact_root
                    / environment.artifact_files["model_weights"]
                )
                original = verified.paths["model_weights"].read_bytes()
                source.write_bytes(b"x" * len(original))
                self.assertEqual(
                    verified.paths["model_weights"].read_bytes(),
                    original,
                )
                self.assertNotEqual(source.read_bytes(), original)
            finally:
                verified.cleanup()

    def test_production_artifact_must_be_read_only(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "source.bin"
            destination = root / "snapshot.bin"
            data = b"immutable-model"
            source.write_bytes(data)
            with mock.patch(
                "martlet_perception_worker.identity._is_writable",
                return_value=True,
            ):
                with self.assertRaises(ContractError) as raised:
                    _snapshot_verified_artifact(
                        source,
                        destination,
                        len(data),
                        hashlib.sha256(data).hexdigest(),
                        time.monotonic() + 2,
                        Event(),
                        require_read_only=True,
                    )
            self.assertEqual(raised.exception.code, "identity_mismatch")

    def test_missing_production_dependency_fails_actionably_without_import(self) -> None:
        with FixtureEnvironment("ocr") as environment:
            production = replace(
                environment.config,
                engine="tesserocr_ocr",
                device="cpu",
            )
            missing = importlib.metadata.PackageNotFoundError("Pillow")
            with (
                mock.patch.object(
                    WorkerConfig,
                    "verify_static_identity",
                    autospec=True,
                ),
                mock.patch(
                    "martlet_perception_worker.identity.importlib.metadata.version",
                    side_effect=missing,
                ),
            ):
                with self.assertRaises(ContractError) as raised:
                    verify_runtime_metadata(production)
            self.assertEqual(raised.exception.code, "model_not_ready")
            self.assertIn("Required pinned package", raised.exception.summary)

    def test_runtime_inventory_requires_complete_sorted_tree_and_exact_origin(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "fixture_module"
            root.mkdir()
            init = root / "__init__.py"
            helper = root / "helper.py"
            init.write_bytes(b"VALUE = 1\n")
            helper.write_bytes(b"HELPER = 2\n")
            declared = [
                _file_identity(init, "__init__.py"),
                _file_identity(helper, "helper.py"),
            ]
            spec = types.SimpleNamespace(
                origin=str(init),
                submodule_search_locations=[str(root)],
            )
            with (
                mock.patch(
                    "martlet_perception_worker.identity.importlib.metadata.distribution",
                    return_value=object(),
                ),
                mock.patch(
                    "martlet_perception_worker.identity.importlib.util.find_spec",
                    return_value=spec,
                ),
                mock.patch(
                    "martlet_perception_worker.identity._is_writable",
                    return_value=False,
                ),
            ):
                origin, files = _verify_distribution_tree(
                    "fixture-dist",
                    "fixture_module",
                    declared,
                    time.monotonic() + 2,
                    Event(),
                )
                self.assertEqual(origin, init)
                self.assertEqual(set(files), {init, helper})
                (root / "extra.py").write_bytes(b"EXTRA = 3\n")
                with self.assertRaises(ContractError):
                    _verify_distribution_tree(
                        "fixture-dist",
                        "fixture_module",
                        declared,
                        time.monotonic() + 2,
                        Event(),
                    )
            wrong_spec = types.SimpleNamespace(
                origin=str(helper),
                submodule_search_locations=[str(root)],
            )
            with (
                mock.patch(
                    "martlet_perception_worker.identity.importlib.metadata.distribution",
                    return_value=object(),
                ),
                mock.patch(
                    "martlet_perception_worker.identity.importlib.util.find_spec",
                    return_value=wrong_spec,
                ),
                mock.patch(
                    "martlet_perception_worker.identity._is_writable",
                    return_value=False,
                ),
            ):
                with self.assertRaises(ContractError):
                    _verify_distribution_tree(
                        "fixture-dist",
                        "fixture_module",
                        declared
                        + [_file_identity(root / "extra.py", "extra.py")],
                        time.monotonic() + 2,
                        Event(),
                    )

    def test_import_origin_and_inventory_are_rechecked_after_import(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "verified.py"
            path.write_bytes(b"VALUE = 1\n")
            identity = (len(path.read_bytes()), hashlib.sha256(path.read_bytes()).hexdigest())
            module = types.ModuleType("martlet_test_verified")
            module.__file__ = str(path)
            previous = sys.modules.get(module.__name__)
            sys.modules[module.__name__] = module
            observation = RuntimeObservation(
                module_origins={module.__name__: path},
                verified_files={module.__name__: {path: identity}},
            )
            try:
                with mock.patch(
                    "martlet_perception_worker.identity._is_writable",
                    return_value=False,
                ):
                    verify_imported_module_origins(
                        observation,
                        deadline_monotonic=time.monotonic() + 2,
                        cancel=Event(),
                    )
                    shadow = Path(directory) / "shadow.py"
                    shadow.write_bytes(path.read_bytes())
                    module.__file__ = str(shadow)
                    with self.assertRaises(ContractError):
                        verify_imported_module_origins(
                            observation,
                            deadline_monotonic=time.monotonic() + 2,
                            cancel=Event(),
                        )
            finally:
                if previous is None:
                    sys.modules.pop(module.__name__, None)
                else:
                    sys.modules[module.__name__] = previous


def _file_identity(path: Path, relative: str) -> dict[str, object]:
    data = path.read_bytes()
    return {
        "bytes": len(data),
        "path": relative,
        "sha256": hashlib.sha256(data).hexdigest(),
    }


if __name__ == "__main__":
    unittest.main()
