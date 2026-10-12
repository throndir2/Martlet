"""Runs HY-Motion 1.0 and turns its output into a VRMA clip.

HY-Motion's pipeline (`hymotion.pipeline.motion_diffusion.MotionFlowMatching.generate`) returns, for each seed, `rot6d`
(F, 22, 6): the pelvis and the 21 body joints as 6D rotations, and `transl` (F, 3): the SMPL translation, already moved so the
lowest vertex of the clip is at y = 0. Its frame is the SMPL frame (Y up, the body faces +Z, left at +X, T-pose rest), which is
the converter's frame, so no axis change is needed. HY-Motion's own `construct_smpl_data_dict` turns `rot6d` into axis-angle
and adds the mean hand pose (52 joints); `martlet_motion.vrma.smplh_to_vrma` then writes the clip. HY-Motion's FBX export
(Autodesk FBX SDK) is never used.

HY-Motion reads its normalization statistics (`stats/`), its wooden body model (`scripts/gradio/static/assets/dump_wooden/`)
and the text encoders (`ckpts/Qwen3-8B`, `ckpts/clip-vit-large-patch14`) relative to the working directory, so the runtime
runs in the code folder, whose `ckpts` links to the models volume.
"""

from __future__ import annotations

import gc
import os
import sys
import time
from pathlib import Path
from typing import Any, Callable

import numpy as np

from . import pins


class MotionError(Exception):
    """A failure with a stable code (motion.busy, motion.unavailable, motion.out_of_memory, request.invalid, motion.failed)
    and the HTTP status the service answers with."""

    def __init__(self, code: str, summary: str, status: int = 500) -> None:
        super().__init__(summary)
        self.code = code
        self.summary = summary
        self.status = status


def to_smplh(output: dict[str, Any], construct: Callable[..., dict[str, Any]] | None = None) -> tuple[np.ndarray, np.ndarray]:
    """The converter's input from HY-Motion's dict output (its first seed): `poses` (F, 52, 3) axis-angle in radians in
    SMPL-H joint order and `trans` (F, 3), HY-Motion's `transl` unchanged. <construct> is HY-Motion's
    `construct_smpl_data_dict` (given in tests)."""
    if construct is None:
        from hymotion.pipeline.body_model import construct_smpl_data_dict as construct
    rot6d = output["rot6d"][0]
    transl = output["transl"][0]
    data = construct(rot6d, transl)
    poses = np.asarray(data["poses"], dtype=np.float64)
    trans = np.asarray(data["trans"], dtype=np.float64)
    if poses.ndim != 2 or poses.shape[1] != 156 or trans.shape != (poses.shape[0], 3):
        raise MotionError("motion.failed", f"HY-Motion returned an unexpected shape: poses {poses.shape}, trans {trans.shape}.")
    return poses.reshape(-1, 52, 3), trans


def to_vrma(output: dict[str, Any], construct: Callable[..., dict[str, Any]] | None = None) -> tuple[bytes, int]:
    """The VRMA clip of HY-Motion's output and its frame count."""
    from .vrma import smplh_to_vrma

    poses, trans = to_smplh(output, construct)
    try:
        return smplh_to_vrma(poses, trans, pins.FPS), int(poses.shape[0])
    except ValueError as error:
        raise MotionError("motion.failed", f"The motion could not be written as VRMA: {error}") from error


def _out_of_memory(error: BaseException) -> bool:
    return type(error).__name__ == "OutOfMemoryError" or "out of memory" in str(error).lower()


class Runtime:
    """One HY-Motion model, loaded on first use and unloaded by `free` (or after idle minutes, by the service)."""

    def __init__(self, model: str, code: Path, ckpts: Path, device: str = "cuda") -> None:
        if model not in pins.MODEL_FILES:
            raise MotionError("motion.unavailable", f"Unknown motion model '{model}'.", 503)
        self.model = model
        self.code = code
        self.ckpts = ckpts
        self.device = device
        self.pipeline: Any = None
        self.load_milliseconds = 0

    @property
    def loaded(self) -> bool:
        return self.pipeline is not None

    def missing(self) -> list[str]:
        """The pinned files not in the models volume at their pinned size (fast: sizes only; provision checks SHA-256)."""
        absent = []
        for pinned in pins.files(self.model):
            path = self.ckpts / pinned.local
            if not path.is_file() or path.stat().st_size != pinned.size:
                absent.append(pinned.local)
        for asset in pins.CODE_ASSETS:
            path = self.code / asset.path
            if not path.is_file() or path.stat().st_size != asset.size:
                absent.append(asset.path)
        return absent

    def load(self, weights: bool = True, config_dir: Path | None = None) -> None:
        if self.pipeline is not None:
            return
        if weights and (absent := self.missing()):
            raise MotionError("motion.unavailable",
                              f"The motion role is not provisioned ({len(absent)} files missing); run martlet-motion provision.", 503)
        started = time.monotonic()
        os.chdir(self.code)
        if str(self.code) not in sys.path:
            sys.path.insert(0, str(self.code))
        import torch
        import yaml
        from hymotion.utils.loaders import load_object

        if self.device.startswith("cuda") and not torch.cuda.is_available():
            raise MotionError("motion.unavailable", "No NVIDIA GPU is visible to the motion service.", 503)
        model_dir = config_dir or self.ckpts / pins.MODEL_DIRECTORIES[self.model]
        with (model_dir / "config.yml").open(encoding="utf-8") as stream:
            config = yaml.safe_load(stream)
        pipeline = load_object(config["train_pipeline"], config["train_pipeline_args"],
                               network_module=config["network_module"], network_module_args=config["network_module_args"])
        # Without weights (selftest): HY-Motion's random initial weights and no text encoder.
        pipeline.load_in_demo(str(model_dir / "latest.ckpt"), build_text_encoder=weights, allow_empty_ckpt=not weights)
        pipeline.to(torch.device(self.device))
        pipeline.eval()
        self.pipeline = pipeline
        self.load_milliseconds = int((time.monotonic() - started) * 1000)

    def generate(self, prompt: str, seconds: float, seed: int, steps: int | None = None) -> dict[str, Any]:
        """HY-Motion's dict output for one seed. <steps> (selftest only: random weights, no text encoder, loaded before)
        replaces the 50 flow steps."""
        import torch

        if steps is None:
            self.load()
        pipeline = self.pipeline
        try:
            with torch.no_grad():
                if steps is None:
                    return pipeline.generate(prompt, [seed], seconds)
                pipeline.validation_steps = steps
                device = next(pipeline.parameters()).device
                hidden = {
                    "text_vec_raw": pipeline.null_vtxt_feat.expand(1, -1, -1),
                    "text_ctxt_raw": pipeline.null_ctxt_input.expand(1, -1, -1),
                    "text_ctxt_raw_length": torch.tensor([1], device=device),
                }
                return pipeline.generate(prompt, [seed], seconds, cfg_scale=1.0, hidden_state_dict=hidden)
        except MotionError:
            raise
        except Exception as error:
            if _out_of_memory(error):
                self.free()
                raise MotionError("motion.out_of_memory",
                                  "The graphics card ran out of memory. Free it (other roles, other programs) and try again, "
                                  "or use a shorter motion.", 507) from error
            raise MotionError("motion.failed", f"HY-Motion failed: {type(error).__name__}: {error}"[:400]) from error

    def free(self) -> bool:
        """Unloads the model and returns the graphics card's memory; true when it was loaded."""
        if self.pipeline is None:
            return False
        self.pipeline = None
        gc.collect()
        try:
            import torch

            if torch.cuda.is_available():
                torch.cuda.empty_cache()
        except ImportError:
            pass
        return True

    def gpu(self) -> dict[str, Any] | None:
        """The graphics card's name and memory while the model is loaded (CUDA is only started by loading)."""
        if self.pipeline is None or not self.device.startswith("cuda"):
            return None
        import torch

        free, total = torch.cuda.mem_get_info()
        return {"name": torch.cuda.get_device_name(0), "total_mib": total // 1_048_576, "free_mib": free // 1_048_576}
