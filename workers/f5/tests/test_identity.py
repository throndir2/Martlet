from __future__ import annotations

import copy
import contextlib
import hashlib
import importlib
import importlib.metadata
import io
import platform
import threading
import time
import types
import unittest
from unittest import mock

from martlet_f5_worker.contract import ContractError, WorkerIdentity
from martlet_f5_worker.engine import DeterministicFakeEngine, F5ProductionEngine
from martlet_f5_worker.identity import (
    RuntimeObservation,
    RuntimePins,
    WORKER_BUILD_ID,
    WorkerConfig,
    compute_worker_build_revision,
    _verify_distribution_tree,
    verify_artifacts,
    verify_expected_identity,
    verify_runtime_metadata,
)
from martlet_f5_worker.service import WorkerService

from support import OutputCollector, WorkerFixture, warmup_message


class IdentityTests(unittest.TestCase):
    def setUp(self) -> None:
        self.fixture = WorkerFixture()

    def tearDown(self) -> None:
        self.fixture.close()

    def test_exact_five_artifacts_are_verified_before_engine_load(self) -> None:
        class RecordingEngine(DeterministicFakeEngine):
            def __init__(self) -> None:
                super().__init__()
                self.load_calls = 0

            def load(
                self,
                artifact_paths,
                deadline_monotonic,
                runtime_observation=None,
            ):
                self.load_calls += 1
                super().load(
                    artifact_paths,
                    deadline_monotonic,
                    runtime_observation,
                )

        engine = RecordingEngine()
        collector = OutputCollector()
        service = WorkerService(self.fixture.config, collector.emit, engine=engine)
        try:
            role = "vocabulary"
            (self.fixture.artifact_root / self.fixture.artifact_files[role]).write_bytes(
                b"changed"
            )
            service.handle(warmup_message(self.fixture.worker))
            failed = collector.wait(
                lambda message: message.get("type") == "worker_state"
                and message.get("state") == "failed"
            )
            self.assertEqual("identity_mismatch", failed["last_error"]["code"])
            self.assertEqual(0, engine.load_calls)
        finally:
            service.close()

    def test_artifact_verification_detects_digest_drift_with_same_size(self) -> None:
        role = "model_weights"
        path = self.fixture.artifact_root / self.fixture.artifact_files[role]
        original = path.read_bytes()
        path.write_bytes(b"x" * len(original))
        with self.assertRaises(ContractError) as caught:
            verify_artifacts(
                self.fixture.config,
                deadline_monotonic=time.monotonic() + 1,
                cancel=threading.Event(),
            )
        self.assertEqual("identity_mismatch", caught.exception.code)

    def test_engine_load_uses_verified_snapshots_not_replaceable_sources(self) -> None:
        role = "model_weights"
        source = self.fixture.artifact_root / self.fixture.artifact_files[role]
        original = source.read_bytes()

        class SnapshotEngine(DeterministicFakeEngine):
            def __init__(self) -> None:
                super().__init__()
                self.loaded_model = b""

            def load(
                inner_self,
                artifact_paths,
                deadline_monotonic,
                runtime_observation=None,
            ):
                source.write_bytes(b"x" * len(original))
                inner_self.loaded_model = artifact_paths["model_weights"].read_bytes()
                super(SnapshotEngine, inner_self).load(
                    artifact_paths,
                    deadline_monotonic,
                    runtime_observation,
                )

        engine = SnapshotEngine()
        collector = OutputCollector()
        service = WorkerService(self.fixture.config, collector.emit, engine=engine)
        try:
            service.handle(warmup_message(self.fixture.worker))
            collector.wait(
                lambda message: message.get("type") == "worker_state"
                and message.get("state") == "ready"
            )
            self.assertEqual(original, engine.loaded_model)
            self.assertNotEqual(source.read_bytes(), engine.loaded_model)
        finally:
            service.close()

    def test_expected_identity_drift_is_rejected(self) -> None:
        wire = copy.deepcopy(self.fixture.worker.wire())
        wire["artifacts"][0]["sha256"] = "f" * 64
        drifted = WorkerIdentity.parse(wire)
        with self.assertRaises(ContractError) as caught:
            verify_expected_identity(drifted, self.fixture.worker)
        self.assertEqual("identity_mismatch", caught.exception.code)

    def test_fake_runtime_verification_imports_no_heavy_packages(self) -> None:
        real_import = importlib.import_module
        imported: list[str] = []

        def recording_import(name: str, package=None):
            imported.append(name)
            return real_import(name, package)

        with mock.patch("importlib.import_module", side_effect=recording_import):
            verify_runtime_metadata(self.fixture.config)
        self.assertFalse(
            any(name == "torch" or name.startswith("f5_tts") or name == "vocos" for name in imported)
        )

    def test_missing_production_dependency_fails_actionably_before_import(self) -> None:
        config = self.production_config()
        real_import = importlib.import_module
        heavy_imports: list[str] = []

        def reject_heavy_import(name: str, package=None):
            if name in {"torch", "torchaudio", "vocos"} or name.startswith("f5_tts"):
                heavy_imports.append(name)
                raise AssertionError("heavy import attempted")
            return real_import(name, package)

        with (
            mock.patch(
                "importlib.metadata.version",
                side_effect=importlib.metadata.PackageNotFoundError("f5-tts"),
            ),
            mock.patch("importlib.import_module", side_effect=reject_heavy_import),
            self.assertRaises(ContractError) as caught,
        ):
            verify_runtime_metadata(config)
        self.assertEqual("model_not_ready", caught.exception.code)
        self.assertIn("f5-tts==1.1.22", caught.exception.summary)
        self.assertEqual([], heavy_imports)

    def test_production_import_and_load_output_is_not_written_to_protocol_stdout(self) -> None:
        config = self.production_config()
        engine = F5ProductionEngine(config)
        pins = RuntimePins.load()

        class DummyModel:
            target_sample_rate = 24_000
            mel_spec_type = "vocos"
            ema_model = object()
            vocoder = object()
            device = "cuda:0"

        modules = {
            "torch": types.SimpleNamespace(
                version=types.SimpleNamespace(cuda=pins.cuda_runtime_version)
            ),
            "torchaudio": types.SimpleNamespace(),
            "f5_tts.api": types.SimpleNamespace(
                F5TTS=lambda **_: (print("private model path"), DummyModel())[1]
            ),
            "f5_tts.infer.utils_infer": types.SimpleNamespace(),
            "vocos": types.SimpleNamespace(),
        }

        real_import = importlib.import_module

        def noisy_import(name: str, package=None):
            if name not in modules:
                return real_import(name, package)
            print(f"private import {name}")
            return modules[name]

        paths = {
            "runtime_image": self.fixture.root / "runtime.json",
            "model_weights": self.fixture.root / "model.safetensors",
            "vocabulary": self.fixture.root / "vocab.txt",
            "vocoder_weights": self.fixture.root / "vocos" / "pytorch_model.bin",
            "vocoder_configuration": self.fixture.root / "vocos" / "config.yaml",
        }
        capture = io.StringIO()
        with (
            contextlib.redirect_stdout(capture),
            mock.patch(
                "martlet_f5_worker.engine.verify_imported_module_origins"
            ),
            mock.patch(
                "martlet_f5_worker.engine.importlib.import_module",
                side_effect=noisy_import,
            ),
        ):
            engine.load(
                paths,
                time.monotonic() + 1,
                RuntimeObservation({}, {}),
            )
        self.assertEqual("", capture.getvalue())
        engine.close()

    def test_shadow_module_is_rejected_before_import_execution(self) -> None:
        site = self.fixture.root / "site"
        module_root = site / "f5_tts"
        module_root.mkdir(parents=True)
        module_file = module_root / "__init__.py"
        module_file.write_text("VALUE = 1\n", encoding="utf-8")
        data = module_file.read_bytes()
        files = [
            {
                "bytes": len(data),
                "path": "__init__.py",
                "sha256": hashlib.sha256(data).hexdigest(),
            }
        ]
        shadow = self.fixture.root / "shadow" / "f5_tts"
        shadow.mkdir(parents=True)
        shadow_file = shadow / "__init__.py"
        shadow_file.write_text("raise RuntimeError('must not execute')\n", encoding="utf-8")

        class Distribution:
            def locate_file(self, name):
                return site / name

        with (
            mock.patch(
                "martlet_f5_worker.identity.importlib.metadata.distribution",
                return_value=Distribution(),
            ),
            mock.patch(
                "martlet_f5_worker.identity.importlib.util.find_spec",
                return_value=types.SimpleNamespace(origin=str(shadow_file)),
            ),
            self.assertRaises(ContractError) as caught,
        ):
            _verify_distribution_tree(
                "f5-tts",
                "f5_tts",
                files,
                time.monotonic() + 1,
                threading.Event(),
            )
        self.assertEqual("identity_mismatch", caught.exception.code)

    def test_warmup_reports_all_readiness_states(self) -> None:
        collector = OutputCollector()
        service = WorkerService(self.fixture.config, collector.emit)
        try:
            service.startup()
            service.handle(warmup_message(self.fixture.worker))
            collector.wait(
                lambda message: message.get("type") == "worker_state"
                and message.get("state") == "ready"
            )
            states = [
                message["state"]
                for message in collector.messages
                if message.get("type") == "worker_state"
            ]
            for expected in (
                "cold",
                "verifying_runtime",
                "verifying_artifacts",
                "loading_model",
                "warming",
                "ready",
            ):
                self.assertIn(expected, states)
            self.assertLess(states.index("verifying_artifacts"), states.index("loading_model"))
        finally:
            service.close()

    def production_config(self) -> WorkerConfig:
        pins = RuntimePins.load()
        wire = copy.deepcopy(self.fixture.worker.wire())
        wire["evidence"] = "live_worker"
        wire["worker_id"] = "martlet-f5-worker"
        wire["runtime"] = {
            "cuda_runtime_version": pins.cuda_runtime_version,
            "f5_package_version": pins.packages["f5-tts"],
            "f5_source_revision": pins.f5_source_revision,
            "python_version": platform.python_version(),
            "torch_version": pins.packages["torch"],
            "torchaudio_version": pins.packages["torchaudio"],
            "worker_build_id": WORKER_BUILD_ID,
            "worker_build_revision": compute_worker_build_revision(),
        }
        return WorkerConfig(
            "f5",
            self.fixture.artifact_root,
            self.fixture.artifact_files,
            WorkerIdentity.parse(wire),
            "cuda:0",
        )
