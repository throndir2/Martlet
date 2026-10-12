"""Pinned source and model files of the Martlet motion host role (HY-Motion 1.0).

The image build fetches the HY-Motion code at a pinned git commit and its Git LFS assets (the normalization statistics and
the wooden body model the pipeline uses to put the feet on the ground) by size and SHA-256. `martlet-motion provision`
downloads the chosen HY-Motion model and its two text encoders into the martlet-motion-models volume and checks every
file's size and SHA-256. Nothing is downloaded at run time.

No SMPL, SMPL-H or MANO body model files from the Max Planck Institute are needed: HY-Motion's pipeline uses its own
wooden mesh from the HY-Motion repository, and the mean hand pose is a list of numbers in its code.
"""

from __future__ import annotations

from typing import NamedTuple

CODE_REPOSITORY = "https://github.com/Tencent-Hunyuan/HY-Motion-1.0.git"
CODE_COMMIT = "4e426f5a1021cbcf7f375458c37b840ee7225229"
CODE_LFS_URL = "https://media.githubusercontent.com/media/Tencent-Hunyuan/HY-Motion-1.0/" + CODE_COMMIT + "/"

MODEL_REPOSITORY = "tencent/HY-Motion-1.0"
MODEL_REVISION = "620dd559f8d964aac2f82f1204fe6a35ad8ad14d"
QWEN_REPOSITORY = "Qwen/Qwen3-8B"
QWEN_REVISION = "b968826d9c46dd6066d109eabc6255188de91218"
CLIP_REPOSITORY = "openai/clip-vit-large-patch14"
CLIP_REVISION = "32bd64288804d66eefd0ccbe215aa642df71cc41"

HY_MOTION_LICENSE = "Tencent-HY-Motion-1.0-Community"
STANDARD = "hy-motion-1.0"
LITE = "hy-motion-1.0-lite"
DEFAULT_MODEL = LITE
FPS = 30
# HY-Motion makes 20 to 360 frames at 30 frames a second (train_frames 360 in its config.yml).
MINIMUM_SECONDS = 1.0
MAXIMUM_SECONDS = 12.0


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

    @property
    def artifact_id(self) -> str:
        return f"{self.repository}/{self.path}"


class CodeAsset(NamedTuple):
    path: str
    size: int
    sha256: str

    @property
    def url(self) -> str:
        return CODE_LFS_URL + self.path


# Git LFS files of the code repository that the pipeline reads (relative to the code root, where HY-Motion reads them).
CODE_ASSETS: tuple[CodeAsset, ...] = (
    CodeAsset("stats/Mean.npy", 1_736, "a4bbe5f68881fe793d509f262ad3dc698a91f79158a9bde9d6f24d46ef9652bd"),
    CodeAsset("stats/Std.npy", 1_736, "6cc86d1ca4b47fbd0e84b677c4397e51b6615abcb34e38fdd9daabbe6a5685cc"),
    CodeAsset("scripts/gradio/static/assets/dump_wooden/v_template.bin", 291_072,
              "b9fbe1f34bfe8a07442d11166e169318022a18da8bc62ce0a9930dfdb3171050"),
    CodeAsset("scripts/gradio/static/assets/dump_wooden/j_template.bin", 624,
              "4f488cc9d0b650a816f5a8eda49eda7bc796f490e9130a6e0dec5be137d7b929"),
    CodeAsset("scripts/gradio/static/assets/dump_wooden/skinWeights.bin", 388_096,
              "343eac2902627ee6b45b547eb7d9f1526562eca7ba178d1dc71f9e466f307a77"),
    CodeAsset("scripts/gradio/static/assets/dump_wooden/skinIndice.bin", 194_048,
              "846794fb90ea01e069435ad242caefb3d5c2f913fef3255c247f48836ddc1bda"),
    CodeAsset("scripts/gradio/static/assets/dump_wooden/kintree.bin", 208,
              "98a20fa3b53193790b63d9ac3a9c917f2f70fbe6e053dca495c75317ff4b756a"),
    CodeAsset("scripts/gradio/static/assets/dump_wooden/faces.bin", 290_376,
              "777b0806d2843c797ed18644eecc11466ff822b33b02d263c22f8ad3730e9bb5"),
)


def _hy(path: str, size: int, sha256: str) -> PinnedFile:
    return PinnedFile(MODEL_REPOSITORY, MODEL_REVISION, path, size, sha256, HY_MOTION_LICENSE, "tencent/" + path)


def _qwen(path: str, size: int, sha256: str) -> PinnedFile:
    return PinnedFile(QWEN_REPOSITORY, QWEN_REVISION, path, size, sha256, "Apache-2.0", "Qwen3-8B/" + path)


def _clip(path: str, size: int, sha256: str) -> PinnedFile:
    # The Hugging Face repository names no license; OpenAI publishes CLIP under the MIT license (github.com/openai/CLIP).
    return PinnedFile(CLIP_REPOSITORY, CLIP_REVISION, path, size, sha256, "MIT", "clip-vit-large-patch14/" + path)


MODEL_FILES: dict[str, tuple[PinnedFile, ...]] = {
    STANDARD: (
        _hy("HY-Motion-1.0/config.yml", 956, "ed90095c0d6a8032520ebdd14a2294adb43a19916dd84d4751e36dcecb5c6427"),
        _hy("HY-Motion-1.0/latest.ckpt", 4_171_703_514, "4fa91c6085f30c49bccda150b28d6c8d0887dc07e6d5d4ec0055482732635f10"),
    ),
    LITE: (
        _hy("HY-Motion-1.0-Lite/config.yml", 955, "f670588ebbde4b05fa7ad77a01b762700d52870cb9fda4e8319c2d58196aee53"),
        _hy("HY-Motion-1.0-Lite/latest.ckpt", 1_843_542_538, "d83f118f8d74db76249db86dcf9982a8229f43ef4e9fa11f683019d6230dd486"),
    ),
}

SHARED_FILES: tuple[PinnedFile, ...] = (
    _hy("LICENSE.txt", 16_087, "03e846e8bcaae503dbb878cb5b125b9810d8cac8bfc38b0275c4886759afd81b"),
    _qwen("LICENSE", 11_343, "832dd9e00a68dd83b3c3fb9f5588dad7dcf337a0db50f7d9483f310cd292e92e"),
    _qwen("config.json", 728, "f7c4eadfbbf522470667b797a3c89be2524832d2d599797248dc304fff447c30"),
    _qwen("generation_config.json", 239, "2325da0f15bb848e018c5ae071b7943332e9f871d6b60e2ed22ca97d4cb993d2"),
    _qwen("merges.txt", 1_671_853, "8831e4f1a044471340f7c0a83d7bd71306a5b867e95fd870f74d0c5308a904d5"),
    _qwen("vocab.json", 2_776_833, "ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910"),
    _qwen("tokenizer.json", 11_422_654, "aeb13307a71acd8fe81861d94ad54ab689df773318809eed3cbe794b4492dae4"),
    _qwen("tokenizer_config.json", 9_732, "d5d09f07b48c3086c508b30d1c9114bd1189145b74e982a265350c923acd8101"),
    _qwen("model.safetensors.index.json", 32_878, "f9fdbcb91c23971c13ec5d5f2573d2349e8f61f2f049371ec699281748fdb1bc"),
    _qwen("model-00001-of-00005.safetensors", 3_996_250_744, "31d6a825ae35f11fb85b195b4c42c146c051e446433125a215336abdf95cbf5f"),
    _qwen("model-00002-of-00005.safetensors", 3_993_160_032, "5991236cea6fe21f3d43cab0f0e84448734fbbe0789816202989f2ddc9d18282"),
    _qwen("model-00003-of-00005.safetensors", 3_959_604_768, "c5185c4794be2d8a9784d5753c9922db38df478ce11f9ed0b415b7304d896836"),
    _qwen("model-00004-of-00005.safetensors", 3_187_841_392, "b5ee7de71fbf17db3d5704e0c8f2bc7d005ca9e1d7ca2aeb19827b0cfcaa917a"),
    _qwen("model-00005-of-00005.safetensors", 1_244_659_840, "20c2d6366ab85c90786ccdd829cd2b9e7d30ef3b2ebbb998280e7e4014b542ff"),
    _clip("config.json", 4_519, "8a09b467700c58138c29d53c605b34ebc69beaadd13274a8a2af8ad2c2f4032a"),
    _clip("merges.txt", 524_619, "9fd691f7c8039210e0fced15865466c65820d09b63988b0174bfe25de299051a"),
    _clip("vocab.json", 961_143, "3f0c4f7d2086b61b38487075278ea9ed04edb53a03cbb045b86c27190fa8fb69"),
    _clip("tokenizer.json", 2_224_003, "a83e0809aa4c3af7208b2df632a7a69668c6d48775b3c3fe4e1b1199d1f8b8f4"),
    _clip("tokenizer_config.json", 905, "deef455e52fa5e8151e339add0582e4235f066009601360999d3a9cda83b1129"),
    _clip("special_tokens_map.json", 389, "f8c0d6c39aee3f8431078ef6646567b0aba7f2246e9c54b8b99d55c22b707cbf"),
    _clip("model.safetensors", 1_710_540_580, "a2bf730a0c7debf160f7a6b50b3aaf3703e7e88ac73de7a314903141db026dcb"),
)

# Where each model's config.yml and latest.ckpt are, under the models volume's ckpts folder.
MODEL_DIRECTORIES = {STANDARD: "tencent/HY-Motion-1.0", LITE: "tencent/HY-Motion-1.0-Lite"}


def models() -> list[str]:
    return list(MODEL_FILES)


def files(model: str) -> tuple[PinnedFile, ...]:
    """Every file `provision` downloads for <model>: the model's config and checkpoint, then the shared text encoders."""
    if model not in MODEL_FILES:
        raise ValueError(f"Unknown motion model '{model}'. Choose one of: {', '.join(MODEL_FILES)}.")
    return MODEL_FILES[model] + SHARED_FILES


def checkpoint(model: str) -> PinnedFile:
    """The model's checkpoint; its revision and SHA-256 identify the model in the gateway route."""
    return next(f for f in files(model) if f.path.endswith("latest.ckpt"))


def total_bytes(model: str) -> int:
    return sum(f.size for f in files(model))
