from __future__ import annotations

import contextlib
import importlib
import io
import math
import os
import sys
import threading
import time
from abc import ABC, abstractmethod
from pathlib import Path
from threading import Event
from typing import Any

from .contract import ContractError, PerceptionRequest, require, require_exact_keys
from .contract import require_int, require_object, require_string
from .identity import RuntimeObservation, RuntimePins, WorkerConfig
from .identity import verify_imported_module_origins
from .jsonio import JsonContractError, parse_canonical_json


class EngineFault(Exception):
    pass


class EngineOutOfMemory(EngineFault):
    pass


class EngineModelUnavailable(EngineFault):
    pass


class EngineInvalidImage(EngineFault):
    pass


class PerceptionEngine(ABC):
    @abstractmethod
    def load(
        self,
        artifact_paths: dict[str, Path],
        deadline_monotonic: float,
        runtime_observation: RuntimeObservation | None = None,
    ) -> None:
        raise NotImplementedError

    @abstractmethod
    def infer(
        self,
        request: PerceptionRequest,
        frame: bytes | bytearray,
        cancel: Event,
    ) -> dict[str, Any]:
        raise NotImplementedError

    @abstractmethod
    def close(self) -> None:
        raise NotImplementedError


class DeterministicFakeEngine(PerceptionEngine):
    """FIXTURE - NOT AI."""

    def __init__(
        self,
        *,
        delay_seconds: float = 0.0,
        warmup_delay_seconds: float = 0.0,
        fault: str | None = None,
        noisy: bool = False,
    ) -> None:
        if not 0 <= delay_seconds <= 2:
            raise ValueError("delay_seconds")
        if not 0 <= warmup_delay_seconds <= 2:
            raise ValueError("warmup_delay_seconds")
        if fault not in {
            None,
            "internal",
            "oom",
            "model",
            "partial",
            "unknown",
        }:
            raise ValueError("fault")
        self.delay_seconds = delay_seconds
        self.warmup_delay_seconds = warmup_delay_seconds
        self.fault = fault
        self.noisy = noisy
        self.loaded = False
        self.closed = False
        self.loaded_artifacts: dict[str, bytes] = {}

    def load(
        self,
        artifact_paths: dict[str, Path],
        deadline_monotonic: float,
        runtime_observation: RuntimeObservation | None = None,
    ) -> None:
        del runtime_observation
        if self.noisy:
            print("fake load noise")
        if self.warmup_delay_seconds:
            time.sleep(self.warmup_delay_seconds)
        if time.monotonic() >= deadline_monotonic:
            raise ContractError(
                "deadline_exceeded",
                "Fake-engine warmup exceeded its deadline.",
                stage="warmup",
                remedy_code="perception.retry-warmup",
            )
        expected = (
            {"runtime_image", "model_weights", "model_configuration"}
            if len(artifact_paths) == 3
            else {
                "runtime_image",
                "model_weights",
                "model_configuration",
                "tokenizer",
                "projector",
            }
        )
        if set(artifact_paths) != expected:
            raise EngineModelUnavailable("missing fake artifacts")
        self.loaded_artifacts = {
            role: path.read_bytes() for role, path in artifact_paths.items()
        }
        self.loaded = True
        self.closed = False

    def infer(
        self,
        request: PerceptionRequest,
        frame: bytes | bytearray,
        cancel: Event,
    ) -> dict[str, Any]:
        if not self.loaded or self.closed:
            raise EngineModelUnavailable("fake engine is not ready")
        if self.noisy:
            print("fake inference noise")
        cancel.wait(self.delay_seconds)
        if self.fault == "oom":
            raise EngineOutOfMemory("configured fake OOM")
        if self.fault == "model":
            raise EngineModelUnavailable("configured fake model fault")
        if self.fault == "internal":
            raise EngineFault("configured fake fault")
        if self.fault == "partial":
            return {
                "role": request.task.role,
                "ocr" if request.task.role == "ocr" else "vlm": {},
            }
        if self.fault == "unknown":
            return {"role": "unknown", "output": "unknown"}
        digest = __import__("hashlib").sha256(frame).hexdigest()[:12]
        if request.task.role == "ocr":
            return {
                "ocr": {
                    "detected_language": "fixture",
                    "regions": [
                        {
                            "bounds": {
                                "bottom": 10_000,
                                "left": 0,
                                "right": 10_000,
                                "top": 0,
                            },
                            "confidence": {"kind": "unavailable"},
                            "index": 0,
                            "text": f"FIXTURE - NOT AI [{digest}]",
                        }
                    ],
                },
                "role": "ocr",
            }
        return {
            "role": "visual_question_answering",
            "vlm": {
                "answer": f"FIXTURE - NOT AI [{digest}]",
                "confidence": {"kind": "unavailable"},
                "uncertainty": "Synthetic fixture output; no image understanding occurred.",
            },
        }

    def close(self) -> None:
        self.loaded = False
        self.closed = True
        self.loaded_artifacts.clear()


class TesserocrOcrEngine(PerceptionEngine):
    def __init__(self, config: WorkerConfig) -> None:
        if config.engine != "tesserocr_ocr" or config.device != "cpu":
            raise ValueError("TesserocrOcrEngine requires the exact OCR config")
        self.config = config
        self._api: Any = None
        self._image_module: Any = None
        self._tesserocr: Any = None

    def load(
        self,
        artifact_paths: dict[str, Path],
        deadline_monotonic: float,
        runtime_observation: RuntimeObservation | None = None,
    ) -> None:
        if runtime_observation is None:
            raise EngineModelUnavailable("verified runtime inventory is required")
        _require_before_deadline(deadline_monotonic, "before OCR imports")
        _force_offline()
        model_config = _load_model_config(
            artifact_paths["model_configuration"],
            kind="martlet_tesserocr_model_config",
            required=("language", "oem", "psm", "tesseract_version"),
        )
        require(
            model_config["language"] == "eng"
            and require_int(model_config["oem"], "model config oem") == 1
            and require_int(model_config["psm"], "model config psm") == 6,
            "identity_mismatch",
            "The OCR configuration is not the implemented English LSTM adapter.",
        )
        pins = RuntimePins.load()
        expected_native = pins.engines["tesserocr_ocr"]["native_runtime_version"]
        require(
            model_config["tesseract_version"] == expected_native,
            "identity_mismatch",
            "The OCR model configuration names another Tesseract runtime.",
        )
        require(
            "tesserocr" not in sys.modules,
            "identity_mismatch",
            "tesserocr was imported before the verified tessdata path was sealed.",
        )
        os.environ["TESSDATA_PREFIX"] = str(
            artifact_paths["model_weights"].parent
        )
        try:
            image_module = importlib.import_module("PIL.Image")
            tesserocr = importlib.import_module("tesserocr")
        except ImportError as error:
            raise EngineModelUnavailable(
                "A pinned Pillow or tesserocr module could not be imported."
            ) from error
        verify_imported_module_origins(
            runtime_observation,
            deadline_monotonic=deadline_monotonic,
            cancel=Event(),
        )
        try:
            observed_native = str(tesserocr.tesseract_version()).splitlines()[0]
        except Exception as error:
            raise EngineModelUnavailable(
                "The pinned Tesseract native runtime could not report its identity."
            ) from error
        require(
            observed_native in {expected_native, f"tesseract {expected_native}"},
            "identity_mismatch",
            "The loaded Tesseract native runtime differs from the direct pin.",
        )
        _require_before_deadline(deadline_monotonic, "before OCR model load")
        try:
            api = tesserocr.PyTessBaseAPI(
                path=str(artifact_paths["model_weights"].parent),
                lang="eng",
                oem=1,
                psm=6,
            )
        except Exception as error:
            _raise_load_error(error, "The exact local Tesseract model could not load.")
        self._api = api
        self._image_module = image_module
        self._tesserocr = tesserocr

    def infer(
        self,
        request: PerceptionRequest,
        frame: bytes | bytearray,
        cancel: Event,
    ) -> dict[str, Any]:
        if self._api is None or self._image_module is None or self._tesserocr is None:
            raise EngineModelUnavailable("Tesseract is not loaded")
        if cancel.is_set():
            return {"ocr": {"regions": []}, "role": "ocr"}
        source_image: Any = None
        rgb_image: Any = None
        image_stream = io.BytesIO(frame)
        try:
            source_image = self._image_module.open(image_stream)
            source_image.load()
            if (
                source_image.format != "PNG"
                or source_image.mode not in {"RGB", "RGBA"}
            ):
                raise EngineInvalidImage("decoded image format changed")
            rgb_image = source_image.convert("RGB")
            self._api.SetImage(rgb_image)
            self._api.Recognize()
            iterator = self._api.GetIterator()
            level = self._tesserocr.RIL.TEXTLINE
            regions: list[dict[str, Any]] = []
            if iterator is not None:
                for candidate_index in range(129):
                    if cancel.is_set():
                        break
                    if candidate_index == 128:
                        raise EngineFault("The OCR output exceeds its region bound.")
                    text = iterator.GetUTF8Text(level)
                    bounds = iterator.BoundingBox(level)
                    confidence = iterator.Confidence(level)
                    if text is not None and bounds is not None and text.strip():
                        if not math.isfinite(float(confidence)) or not 0 <= float(confidence) <= 100:
                            raise EngineFault("The OCR confidence is outside its bound.")
                        regions.append(
                            {
                                "bounds": _normalize_box(
                                    bounds,
                                    request.frame.content.width,
                                    request.frame.content.height,
                                ),
                                "confidence": {
                                    "kind": "uncalibrated",
                                    "value": float(confidence) / 100.0,
                                },
                                "index": len(regions),
                                "text": text.strip(),
                            }
                        )
                    if not iterator.Next(level):
                        break
            return {
                "ocr": {
                    "detected_language": "eng",
                    "regions": regions,
                },
                "role": "ocr",
            }
        except EngineInvalidImage:
            raise
        except Exception as error:
            _raise_inference_error(error, "Tesseract OCR failed.")
        finally:
            cleanup_failed = False
            try:
                self._api.Clear()
            except Exception:
                cleanup_failed = True
            _close_image_stream(image_stream)
            seen_images: set[int] = set()
            for image in (rgb_image, source_image):
                if image is not None and id(image) not in seen_images:
                    seen_images.add(id(image))
                    try:
                        image.close()
                    except Exception:
                        cleanup_failed = True
            if cleanup_failed:
                api = self._api
                self._api = None
                try:
                    api.End()
                except Exception:
                    pass
                raise EngineModelUnavailable(
                    "Tesseract image cleanup failed; the engine was quarantined."
                )

    def close(self) -> None:
        api = self._api
        self._api = None
        self._image_module = None
        self._tesserocr = None
        if api is not None:
            try:
                api.End()
            except Exception as error:
                raise EngineModelUnavailable("Tesseract cleanup failed.") from error


class TransformersLlavaNextEngine(PerceptionEngine):
    def __init__(self, config: WorkerConfig) -> None:
        if config.engine != "transformers_llava_next" or config.device is None:
            raise ValueError("TransformersLlavaNextEngine requires the exact VLM config")
        self.config = config
        self._torch: Any = None
        self._model: Any = None
        self._processor: Any = None
        self._image_module: Any = None
        self._prompt_template = ""
        self._max_new_tokens = 0

    def load(
        self,
        artifact_paths: dict[str, Path],
        deadline_monotonic: float,
        runtime_observation: RuntimeObservation | None = None,
    ) -> None:
        if runtime_observation is None:
            raise EngineModelUnavailable("verified runtime inventory is required")
        _require_before_deadline(deadline_monotonic, "before VLM imports")
        _force_offline()
        try:
            torch = importlib.import_module("torch")
            transformers = importlib.import_module("transformers")
            safetensors_torch = importlib.import_module("safetensors.torch")
            image_module = importlib.import_module("PIL.Image")
        except ImportError as error:
            raise EngineModelUnavailable(
                "A pinned Torch, Transformers, safetensors, or Pillow module "
                "could not be imported."
            ) from error
        verify_imported_module_origins(
            runtime_observation,
            deadline_monotonic=deadline_monotonic,
            cancel=Event(),
        )
        pins = RuntimePins.load()
        expected_cuda = pins.engines["transformers_llava_next"][
            "native_runtime_version"
        ]
        require(
            getattr(getattr(torch, "version", None), "cuda", None)
            == expected_cuda,
            "identity_mismatch",
            "The loaded Torch CUDA runtime differs from the direct pin.",
        )
        try:
            device_index = int((self.config.device or "").split(":", 1)[1])
            device_ready = bool(torch.cuda.is_available()) and (
                torch.cuda.device_count() > device_index
            )
        except (AttributeError, IndexError, ValueError):
            device_ready = False
        if not device_ready:
            raise EngineModelUnavailable(
                "The explicitly selected CUDA device is unavailable."
            )
        model_config = _load_model_config(
            artifact_paths["model_configuration"],
            kind="martlet_llava_next_model_config",
            required=(
                "architecture",
                "model_config",
                "image_processor",
                "prompt_template",
                "max_new_tokens",
            ),
        )
        require(
            model_config["architecture"] == "LlavaNextForConditionalGeneration"
            and model_config["prompt_template"] == "[INST] <image>\n{question} [/INST]"
            and require_int(model_config["max_new_tokens"], "max_new_tokens") == 128,
            "identity_mismatch",
            "The VLM configuration is not the implemented LLaVA-NeXT adapter.",
        )
        hf_config = require_object(model_config["model_config"], "model_config")
        text_config = require_object(hf_config.get("text_config"), "text_config")
        vision_config = require_object(hf_config.get("vision_config"), "vision_config")
        require(
            hf_config.get("model_type") == "llava_next"
            and hf_config.get("architectures")
            == ["LlavaNextForConditionalGeneration"]
            and text_config.get("model_type") == "mistral"
            and vision_config.get("model_type") == "clip_vision_model",
            "identity_mismatch",
            "Only the exact LLaVA-NeXT Mistral/CLIP architecture is supported.",
        )
        _require_before_deadline(deadline_monotonic, "before VLM model load")
        try:
            config = transformers.LlavaNextConfig.from_dict(hf_config)
            model = transformers.LlavaNextForConditionalGeneration(config)
            model_state = safetensors_torch.load_file(
                str(artifact_paths["model_weights"]),
                device="cpu",
            )
            incompatible = model.load_state_dict(model_state, strict=False)
            missing = tuple(incompatible.missing_keys)
            unexpected = tuple(incompatible.unexpected_keys)
            require(
                missing
                and all(
                    key.startswith("multi_modal_projector.")
                    for key in missing
                )
                and not unexpected,
                "identity_mismatch",
                "The VLM weights do not exactly match the implemented architecture.",
            )
            projector_state = safetensors_torch.load_file(
                str(artifact_paths["projector"]),
                device="cpu",
            )
            normalized_projector = {
                key.removeprefix("multi_modal_projector."): value
                for key, value in projector_state.items()
            }
            projector_result = model.multi_modal_projector.load_state_dict(
                normalized_projector,
                strict=True,
            )
            require(
                not projector_result.missing_keys
                and not projector_result.unexpected_keys,
                "identity_mismatch",
                "The VLM projector does not exactly match the model.",
            )
            tokenizer = transformers.PreTrainedTokenizerFast(
                tokenizer_file=str(artifact_paths["tokenizer"])
            )
            image_processor = transformers.LlavaNextImageProcessor(
                **require_object(
                    model_config["image_processor"],
                    "image_processor",
                )
            )
            processor = transformers.LlavaNextProcessor(
                image_processor=image_processor,
                tokenizer=tokenizer,
                patch_size=config.vision_config.patch_size,
                vision_feature_select_strategy=(
                    config.vision_feature_select_strategy
                ),
            )
            image_token = tokenizer.convert_tokens_to_ids("<image>")
            require(
                type(image_token) is int
                and image_token >= 0
                and image_token != tokenizer.unk_token_id
                and image_token == config.image_token_index,
                "identity_mismatch",
                "The exact tokenizer does not define the LLaVA image token.",
            )
            model.to(self.config.device)
            model.eval()
        except ContractError:
            raise
        except Exception as error:
            _raise_load_error(error, "The exact local LLaVA-NeXT model could not load.")
        self._torch = torch
        self._model = model
        self._processor = processor
        self._image_module = image_module
        self._prompt_template = model_config["prompt_template"]
        self._max_new_tokens = model_config["max_new_tokens"]

    def infer(
        self,
        request: PerceptionRequest,
        frame: bytes | bytearray,
        cancel: Event,
    ) -> dict[str, Any]:
        if (
            self._torch is None
            or self._model is None
            or self._processor is None
            or self._image_module is None
        ):
            raise EngineModelUnavailable("LLaVA-NeXT is not loaded")
        if cancel.is_set():
            return {
                "role": "visual_question_answering",
                "vlm": {
                    "answer": "Canceled before inference.",
                    "confidence": {"kind": "unavailable"},
                    "uncertainty": "Output was discarded.",
                },
            }
        source_image: Any = None
        rgb_image: Any = None
        image_stream = io.BytesIO(frame)
        try:
            source_image = self._image_module.open(image_stream)
            source_image.load()
            if (
                source_image.format != "PNG"
                or source_image.mode not in {"RGB", "RGBA"}
            ):
                raise EngineInvalidImage("decoded image format changed")
            rgb_image = source_image.convert("RGB")
            prompt = self._prompt_template.format(question=request.task.question)
            inputs = self._processor(
                images=rgb_image,
                text=prompt,
                return_tensors="pt",
            )
            prompt_tokens = int(inputs["input_ids"].shape[-1])
            moved = {
                key: value.to(self.config.device)
                if hasattr(value, "to")
                else value
                for key, value in inputs.items()
            }
            with self._torch.inference_mode():
                generated = self._model.generate(
                    **moved,
                    do_sample=False,
                    max_new_tokens=self._max_new_tokens,
                    use_cache=True,
                )
            answer = self._processor.decode(
                generated[0][prompt_tokens:],
                skip_special_tokens=True,
            ).strip()
            if not answer:
                raise EngineFault("The VLM emitted an empty answer.")
            return {
                "role": "visual_question_answering",
                "vlm": {
                    "answer": answer,
                    "confidence": {"kind": "unavailable"},
                    "uncertainty": (
                        "Uncalibrated local model output; verify it against the "
                        "selected window."
                    ),
                },
            }
        except EngineInvalidImage:
            raise
        except Exception as error:
            _raise_inference_error(error, "LLaVA-NeXT inference failed.")
        finally:
            cleanup_failed = False
            seen_images: set[int] = set()
            for image in (rgb_image, source_image):
                if image is not None and id(image) not in seen_images:
                    seen_images.add(id(image))
                    try:
                        image.close()
                    except Exception:
                        cleanup_failed = True
            _close_image_stream(image_stream)
            if cleanup_failed:
                raise EngineModelUnavailable("VLM image cleanup failed.")

    def close(self) -> None:
        model = self._model
        torch = self._torch
        self._model = None
        self._processor = None
        self._image_module = None
        self._torch = None
        if model is not None:
            del model
        if torch is not None:
            try:
                torch.cuda.empty_cache()
            except Exception as error:
                raise EngineModelUnavailable("VLM runtime cleanup failed.") from error


def create_engine(config: WorkerConfig) -> PerceptionEngine:
    if config.engine == "fake":
        return DeterministicFakeEngine()
    if config.engine == "tesserocr_ocr":
        return TesserocrOcrEngine(config)
    if config.engine == "transformers_llava_next":
        return TransformersLlavaNextEngine(config)
    raise ValueError("unsupported engine")


def _close_image_stream(stream: io.BytesIO) -> None:
    if not stream.closed:
        with stream.getbuffer() as buffer:
            buffer[:] = b"\x00" * len(buffer)
        stream.close()


def silence_third_party_output() -> contextlib.AbstractContextManager[None]:
    sink = _DiscardTextOutput()
    return _RedirectThirdPartyOutput(sink)


class _DiscardTextOutput(io.TextIOBase):
    @property
    def encoding(self) -> str:
        return "utf-8"

    def writable(self) -> bool:
        return True

    def write(self, value: str) -> int:
        return len(value)

    def flush(self) -> None:
        return None


_FD_REDIRECT_GATE = threading.RLock()


class _RedirectThirdPartyOutput(contextlib.AbstractContextManager[None]):
    def __init__(self, sink: _DiscardTextOutput) -> None:
        self.stdout = contextlib.redirect_stdout(sink)
        self.stderr = contextlib.redirect_stderr(sink)
        self.saved_descriptors: list[tuple[int, int]] = []

    def __enter__(self) -> None:
        _FD_REDIRECT_GATE.acquire()
        try:
            for stream in (sys.stdout, sys.stderr):
                try:
                    stream.flush()
                except Exception:
                    pass
            self.stdout.__enter__()
            self.stderr.__enter__()
            null_descriptor = os.open(
                os.devnull,
                os.O_WRONLY | getattr(os, "O_BINARY", 0),
            )
            try:
                for target in (1, 2):
                    saved = os.dup(target)
                    self.saved_descriptors.append((target, saved))
                    os.dup2(null_descriptor, target)
            finally:
                os.close(null_descriptor)
            return None
        except Exception:
            self._restore_descriptors()
            self.stderr.__exit__(*sys.exc_info())
            self.stdout.__exit__(*sys.exc_info())
            _FD_REDIRECT_GATE.release()
            raise

    def __exit__(self, exception_type, exception, traceback) -> bool:
        try:
            self._restore_descriptors()
            self.stderr.__exit__(exception_type, exception, traceback)
            return self.stdout.__exit__(exception_type, exception, traceback)
        finally:
            _FD_REDIRECT_GATE.release()

    def _restore_descriptors(self) -> None:
        while self.saved_descriptors:
            target, saved = self.saved_descriptors.pop()
            try:
                os.dup2(saved, target)
            finally:
                os.close(saved)


def _load_model_config(
    path: Path,
    *,
    kind: str,
    required: tuple[str, ...],
) -> dict[str, Any]:
    try:
        with path.open("rb") as stream:
            data = stream.read(1_048_577)
        source = parse_canonical_json(
            data,
            maximum=1_048_576,
            name="model configuration",
        )
    except (OSError, JsonContractError) as error:
        raise EngineModelUnavailable(
            "The exact model configuration is missing or not canonical."
        ) from error
    require_exact_keys(
        source,
        ("format_version", "kind", *required),
        "model configuration",
    )
    require(
        require_int(source["format_version"], "format_version") == 1
        and source["kind"] == kind,
        "identity_mismatch",
        "The model configuration kind is not supported by this adapter.",
    )
    return source


def _force_offline() -> None:
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    os.environ["HF_DATASETS_OFFLINE"] = "1"
    os.environ["TOKENIZERS_PARALLELISM"] = "false"


def _require_before_deadline(deadline_monotonic: float, stage: str) -> None:
    if time.monotonic() >= deadline_monotonic:
        raise ContractError(
            "deadline_exceeded",
            f"Warmup exceeded its deadline {stage}.",
            stage="warmup",
            remedy_code="perception.retry-warmup",
        )


def _normalize_box(
    bounds: tuple[int, int, int, int],
    width: int,
    height: int,
) -> dict[str, int]:
    left, top, right, bottom = bounds
    left_value = max(0, min(9_999, left * 10_000 // width))
    top_value = max(0, min(9_999, top * 10_000 // height))
    right_value = max(left_value + 1, min(10_000, math.ceil(right * 10_000 / width)))
    bottom_value = max(
        top_value + 1,
        min(10_000, math.ceil(bottom * 10_000 / height)),
    )
    return {
        "bottom": bottom_value,
        "left": left_value,
        "right": right_value,
        "top": top_value,
    }


def _is_oom(error: Exception) -> bool:
    return isinstance(error, MemoryError) or error.__class__.__name__ in {
        "OutOfMemoryError",
        "CUDAOutOfMemoryError",
    }


def _raise_load_error(error: Exception, summary: str) -> None:
    if _is_oom(error):
        raise EngineOutOfMemory(summary) from error
    raise EngineModelUnavailable(summary) from error


def _raise_inference_error(error: Exception, summary: str) -> None:
    if _is_oom(error):
        raise EngineOutOfMemory(summary) from error
    raise EngineFault(summary) from error
