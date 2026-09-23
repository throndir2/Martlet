from __future__ import annotations

import importlib
import contextlib
import os
import sys
import time
import types
import unittest
from dataclasses import replace
from datetime import datetime, timezone
from pathlib import Path
from threading import Event
from unittest import mock

from martlet_perception_worker.contract import ContractError, PerceptionRequest
from martlet_perception_worker.engine import (
    EngineModelUnavailable,
    TesserocrOcrEngine,
    TransformersLlavaNextEngine,
)
from martlet_perception_worker.identity import RuntimeObservation
from martlet_perception_worker.jsonio import canonical_json_bytes

from tests.support import FixtureEnvironment, request_wire


class ProductionAdapterBoundaryTests(unittest.TestCase):
    def test_vlm_controlled_load_and_inference_binds_image_token_and_cleans_images(self):
        with FixtureEnvironment("visual_question_answering") as environment:
            config = replace(environment.config, engine="transformers_llava_next", device="cuda:0")
            path = environment.root / "vlm-config.json"
            path.write_bytes(canonical_json_bytes({
                "format_version": 1,
                "kind": "martlet_llava_next_model_config",
                "architecture": "LlavaNextForConditionalGeneration",
                "prompt_template": "[INST] <image>\n{question} [/INST]",
                "max_new_tokens": 128,
                "image_processor": {},
                "model_config": {
                    "model_type": "llava_next",
                    "architectures": ["LlavaNextForConditionalGeneration"],
                    "text_config": {"model_type": "mistral"},
                    "vision_config": {"model_type": "clip_vision_model"},
                },
            }))
            paths = {role: environment.artifact_root / relative
                     for role, relative in environment.artifact_files.items()}
            paths["model_configuration"] = path
            model = mock.Mock()
            model.load_state_dict.return_value = types.SimpleNamespace(
                missing_keys=["multi_modal_projector.weight"], unexpected_keys=[],
            )
            model.multi_modal_projector.load_state_dict.return_value = types.SimpleNamespace(
                missing_keys=[], unexpected_keys=[],
            )
            model.generate.return_value = [[0, 1, 2, 99]]
            processor = mock.Mock(return_value={"input_ids": types.SimpleNamespace(shape=[1, 3])})
            processor.decode.return_value = "Fixture answer - NOT AI"
            tokenizer = types.SimpleNamespace(
                convert_tokens_to_ids=lambda _: 32000, unk_token_id=0,
            )
            hf_config = types.SimpleNamespace(
                vision_config=types.SimpleNamespace(patch_size=14),
                vision_feature_select_strategy="default", image_token_index=32000,
            )
            torch = types.SimpleNamespace(
                version=types.SimpleNamespace(cuda="12.4"),
                cuda=types.SimpleNamespace(
                    is_available=lambda: True, device_count=lambda: 1,
                    empty_cache=mock.Mock(),
                ),
                inference_mode=contextlib.nullcontext,
            )
            transformers = types.SimpleNamespace(
                LlavaNextConfig=types.SimpleNamespace(from_dict=lambda _: hf_config),
                LlavaNextForConditionalGeneration=lambda _: model,
                PreTrainedTokenizerFast=lambda **_: tokenizer,
                LlavaNextImageProcessor=lambda **_: object(),
                LlavaNextProcessor=lambda **_: processor,
            )
            source, converted = _FakeImage("RGBA"), _FakeImage("RGB")
            source.converted = converted
            streams = []

            def opened(stream):
                streams.append(stream)
                return source

            modules = {
                "torch": torch, "transformers": transformers,
                "safetensors.torch": types.SimpleNamespace(load_file=lambda *args, **kwargs: {}),
                "PIL.Image": types.SimpleNamespace(open=opened),
            }
            real_import = importlib.import_module

            def imported(name):
                return modules[name] if name in modules else real_import(name)

            with mock.patch(
                "martlet_perception_worker.engine.verify_imported_module_origins",
            ), mock.patch(
                "martlet_perception_worker.engine.importlib.import_module", side_effect=imported,
            ):
                engine = TransformersLlavaNextEngine(config)
                engine.load(paths, time.monotonic() + 2, RuntimeObservation({}, {}))
                request = PerceptionRequest.parse(request_wire(environment), now=datetime.now(timezone.utc))
                try:
                    result = engine.infer(request, request.frame.content.data, Event())
                    self.assertEqual("Fixture answer - NOT AI", result["vlm"]["answer"])
                    self.assertEqual({"kind": "unavailable"}, result["vlm"]["confidence"])
                    processor.decode.assert_called_once_with([99], skip_special_tokens=True)
                    self.assertEqual(128, model.generate.call_args.kwargs["max_new_tokens"])
                    self.assertFalse(model.generate.call_args.kwargs["do_sample"])
                    self.assertTrue(source.closed and converted.closed and streams[0].closed)
                finally:
                    request.wipe()
                    engine.close()
                model.to.reset_mock()
                hf_config.image_token_index = 32001
                with self.assertRaises(ContractError):
                    TransformersLlavaNextEngine(config).load(
                        paths, time.monotonic() + 2, RuntimeObservation({}, {}),
                    )
                model.to.assert_not_called()

    def test_ocr_seals_verified_tessdata_before_tesserocr_import(self) -> None:
        with FixtureEnvironment("ocr") as environment:
            config = replace(
                environment.config,
                engine="tesserocr_ocr",
                device="cpu",
            )
            engine = TesserocrOcrEngine(config)
            model = environment.root / "verified" / "eng.traineddata"
            model.parent.mkdir()
            model.write_bytes(b"verified-model")
            model_config = environment.root / "ocr-config.json"
            model_config.write_bytes(
                canonical_json_bytes(
                    {
                        "format_version": 1,
                        "kind": "martlet_tesserocr_model_config",
                        "language": "eng",
                        "oem": 1,
                        "psm": 6,
                        "tesseract_version": "5.5.0",
                    }
                )
            )
            api = _FakeTesseractApi()
            tesserocr = types.SimpleNamespace(
                PyTessBaseAPI=lambda **_: api,
                tesseract_version=lambda: "5.5.0",
            )
            image_module = types.SimpleNamespace()
            real_import = importlib.import_module

            def imported(name: str):
                if name == "PIL.Image":
                    return image_module
                if name == "tesserocr":
                    self.assertEqual(
                        os.environ.get("TESSDATA_PREFIX"),
                        str(model.parent),
                    )
                    return tesserocr
                return real_import(name)

            previous = sys.modules.pop("tesserocr", None)
            try:
                with (
                    mock.patch(
                        "martlet_perception_worker.engine.verify_imported_module_origins"
                    ),
                    mock.patch(
                        "martlet_perception_worker.engine.importlib.import_module",
                        side_effect=imported,
                    ),
                ):
                    engine.load(
                        {
                            "model_configuration": model_config,
                            "model_weights": model,
                        },
                        time.monotonic() + 2,
                        RuntimeObservation({}, {}),
                    )
                self.assertIs(engine._api, api)
                engine.close()
                self.assertTrue(api.ended)
            finally:
                if previous is not None:
                    sys.modules["tesserocr"] = previous
                else:
                    sys.modules.pop("tesserocr", None)

    def test_ocr_clears_native_image_and_closes_pillow_objects(self) -> None:
        with FixtureEnvironment("ocr") as environment:
            config = replace(
                environment.config,
                engine="tesserocr_ocr",
                device="cpu",
            )
            engine = TesserocrOcrEngine(config)
            source = _FakeImage("RGBA")
            converted = _FakeImage("RGB")
            source.converted = converted
            image_module = types.SimpleNamespace(open=lambda _: source)
            api = _FakeTesseractApi()
            engine._api = api
            engine._image_module = image_module
            engine._tesserocr = types.SimpleNamespace(
                RIL=types.SimpleNamespace(TEXTLINE=1)
            )
            request = PerceptionRequest.parse(
                request_wire(environment),
                now=datetime.now(timezone.utc),
            )
            try:
                output = engine.infer(
                    request,
                    request.frame.content.data or b"",
                    Event(),
                )
                self.assertEqual(output["ocr"]["regions"], [])
                self.assertEqual(api.cleared, 1)
                self.assertTrue(source.closed)
                self.assertTrue(converted.closed)
            finally:
                request.wipe()

    def test_ocr_cleanup_failure_quarantines_engine(self) -> None:
        with FixtureEnvironment("ocr") as environment:
            config = replace(
                environment.config,
                engine="tesserocr_ocr",
                device="cpu",
            )
            engine = TesserocrOcrEngine(config)
            source = _FakeImage("RGB")
            source.converted = _FakeImage("RGB")
            api = _FakeTesseractApi(clear_fails=True)
            engine._api = api
            engine._image_module = types.SimpleNamespace(open=lambda _: source)
            engine._tesserocr = types.SimpleNamespace(
                RIL=types.SimpleNamespace(TEXTLINE=1)
            )
            request = PerceptionRequest.parse(
                request_wire(environment),
                now=datetime.now(timezone.utc),
            )
            try:
                with self.assertRaises(EngineModelUnavailable):
                    engine.infer(
                        request,
                        request.frame.content.data or b"",
                        Event(),
                    )
                self.assertIsNone(engine._api)
                self.assertTrue(api.ended)
            finally:
                request.wipe()

    def test_vlm_validates_cuda_runtime_and_device_before_model_allocation(
        self,
    ) -> None:
        with FixtureEnvironment("visual_question_answering") as environment:
            config = replace(
                environment.config,
                engine="transformers_llava_next",
                device="cuda:0",
            )
            engine = TransformersLlavaNextEngine(config)
            wrong_torch = types.SimpleNamespace(
                version=types.SimpleNamespace(cuda="0.0"),
                cuda=types.SimpleNamespace(
                    is_available=lambda: True,
                    device_count=lambda: 1,
                ),
            )
            real_import = importlib.import_module

            def imported(name: str):
                if name == "torch":
                    return wrong_torch
                if name in ("transformers", "safetensors.torch", "PIL.Image"):
                    return types.SimpleNamespace()
                return real_import(name)

            with (
                mock.patch(
                    "martlet_perception_worker.engine.verify_imported_module_origins"
                ),
                mock.patch(
                    "martlet_perception_worker.engine.importlib.import_module",
                    side_effect=imported,
                ),
            ):
                with self.assertRaises(ContractError) as raised:
                    engine.load(
                        {},
                        time.monotonic() + 2,
                        RuntimeObservation({}, {}),
                    )
            self.assertEqual(raised.exception.code, "identity_mismatch")
            self.assertIn("CUDA runtime", raised.exception.summary)


class _FakeTesseractApi:
    def __init__(self, *, clear_fails: bool = False) -> None:
        self.clear_fails = clear_fails
        self.cleared = 0
        self.ended = False

    def SetImage(self, _: object) -> None:
        return None

    def Recognize(self) -> None:
        return None

    def GetIterator(self):
        return None

    def Clear(self) -> None:
        self.cleared += 1
        if self.clear_fails:
            raise RuntimeError("fixture cleanup failure")

    def End(self) -> None:
        self.ended = True


class _FakeImage:
    def __init__(self, mode: str) -> None:
        self.format = "PNG"
        self.mode = mode
        self.converted: "_FakeImage | None" = None
        self.closed = False

    def load(self) -> None:
        return None

    def convert(self, _: str) -> "_FakeImage":
        return self.converted or self

    def close(self) -> None:
        self.closed = True


if __name__ == "__main__":
    unittest.main()
