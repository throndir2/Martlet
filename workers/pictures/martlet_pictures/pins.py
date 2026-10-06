"""Pinned source and model files of the Martlet pictures host role.

ComfyUI is fetched by the image build at a pinned git commit. Model files are downloaded by
`martlet-pictures provision` into the martlet-pictures-models volume and verified by size and SHA-256.
"""

from __future__ import annotations

from typing import NamedTuple

COMFYUI_REPOSITORY = "https://github.com/comfyanonymous/ComfyUI.git"
COMFYUI_TAG = "v0.39.0"
COMFYUI_COMMIT = "b0b743566f65daafc423b4fea8a2fbda94b3384a"

MODEL_ID = "z-image-turbo"
MODEL_REPOSITORY = "Comfy-Org/z_image_turbo"
MODEL_REVISION = "6fc90a3b1b653e935a0d175e260736de25b84df5"


class PinnedFile(NamedTuple):
    repository: str
    revision: str
    path: str
    size: int
    sha256: str
    license_id: str
    local: str

    @property
    def url(self) -> str:
        return f"https://huggingface.co/{self.repository}/resolve/{self.revision}/{self.path}"


PINNED_FILES: tuple[PinnedFile, ...] = (
    PinnedFile(MODEL_REPOSITORY, MODEL_REVISION, "split_files/diffusion_models/z_image_turbo_bf16.safetensors",
               12_309_866_400, "2407613050b809ffdff18a4ac99af83ea6b95443ecebdf80e064a79c825574a6",
               "Apache-2.0", "diffusion_models/z_image_turbo_bf16.safetensors"),
    PinnedFile(MODEL_REPOSITORY, MODEL_REVISION, "split_files/text_encoders/qwen_3_4b.safetensors",
               8_044_982_048, "6c671498573ac2f7a5501502ccce8d2b08ea6ca2f661c458e708f36b36edfc5a",
               "Apache-2.0", "text_encoders/qwen_3_4b.safetensors"),
    PinnedFile(MODEL_REPOSITORY, MODEL_REVISION, "split_files/vae/ae.safetensors",
               335_304_388, "afc8e28272cd15db3919bacdb6918ce9c1ed22e96cb12c4d5ed0fba823529e38",
               "Apache-2.0", "vae/ae.safetensors"),
)


def files() -> list[tuple[str, str, str, int, str, str, str]]:
    """(artifact ID, URL, revision, bytes, SHA-256, license, local path) for every provisioned model file."""
    return [(f"{p.repository}/{p.path}", p.url, p.revision, p.size, p.sha256, p.license_id, p.local)
            for p in PINNED_FILES]
