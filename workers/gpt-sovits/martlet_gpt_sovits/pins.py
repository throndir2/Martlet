"""The one GPT-SoVITS release Martlet runs: source, weights pair and the version-specific auxiliary models, all pinned.

Release 20250606v2pro (RVC-Boss/GPT-SoVITS, MIT) with the v2Pro pair from lj1995/GPT-SoVITS (MIT model card):
GPT (text-to-semantic) s1v3.ckpt and SoVITS v2Pro s2Gv2Pro.pth, plus what v2Pro loads with them: the ERes2NetV2
speaker-verification model, chinese-hubert-base and chinese-roberta-wwm-ext-large. The fastText language-ID model is
what GPT-SoVITS's language splitter loads for mixed Japanese/English text. Every file is checked by size and SHA-256
before the worker loads anything; nothing else is ever downloaded or loaded.
"""

from __future__ import annotations

from dataclasses import dataclass

SOURCE_REPOSITORY = "RVC-Boss/GPT-SoVITS"
SOURCE_TAG = "20250606v2pro"
SOURCE_COMMIT = "d7c2210da8c013e81a94bfc7b811a477c99fd506"
SOURCE_LICENSE = "MIT"
WEIGHTS_REPOSITORY = "lj1995/GPT-SoVITS"
WEIGHTS_REVISION = "336b2ec4e8d4ac74740798dd40af44e74659ecaf"
MODEL_ID = "gpt-sovits-v2pro"
MODEL_VERSION = "v2Pro"
# v2Pro's SoVITS decoder writes 32 kHz; Martlet's speech contract is 24 kHz.
MODEL_SAMPLE_RATE = 32_000
OUTPUT_SAMPLE_RATE = 24_000
MIN_REFERENCE_MILLISECONDS = 3_000
MAX_REFERENCE_MILLISECONDS = 10_000
# Reference recording languages this image provisions text front ends for (English: NLTK data; Japanese: OpenJTalk).
REFERENCE_LANGUAGES = ("en", "ja")


@dataclass(frozen=True)
class Artifact:
    role: str
    artifact_id: str
    path: str  # relative to GPT_SoVITS/pretrained_models (the models volume)
    bytes: int
    sha256: str
    license_id: str
    url: str | None = None

    @property
    def source_url(self) -> str:
        return self.url or f"https://huggingface.co/{WEIGHTS_REPOSITORY}/resolve/{WEIGHTS_REVISION}/{self.path}"

    @property
    def revision(self) -> str:
        return WEIGHTS_REVISION if self.url is None else self.sha256[:40]

    def wire(self) -> dict[str, object]:
        return {
            "artifact_id": self.artifact_id,
            "bytes": self.bytes,
            "license_id": self.license_id,
            "revision": self.revision,
            "role": self.role,
            "sha256": self.sha256,
        }


# "model_weights" is the SoVITS half (the identity the gateway route advertises); "gpt_weights" is the GPT half.
ARTIFACTS: tuple[Artifact, ...] = (
    Artifact("model_weights", MODEL_ID, "v2Pro/s2Gv2Pro.pth", 162_303_657,
             "0f8ead815234365edf045c6d86370ed6e4f440e8195be77ff0ea72684ad406a5", "MIT"),
    Artifact("gpt_weights", "gpt-sovits-s1v3", "s1v3.ckpt", 155_284_856,
             "87133414860ea14ff6620c483a3db5ed07b44be42e2c3fcdad65523a729a745a", "MIT"),
    Artifact("speaker_verification", "eres2netv2w24s4ep4", "sv/pretrained_eres2netv2w24s4ep4.ckpt", 107_528_697,
             "4f5a0bf73c61eb41b174e1bb54e7ee3c83233892be8e0af1f187024e8e581a35", "Apache-2.0"),
    Artifact("hubert_weights", "chinese-hubert-base", "chinese-hubert-base/pytorch_model.bin", 188_811_417,
             "24164f129c66499d1346e2aa55f183250c223161ec2770c0da3d3b08cf432d3c", "MIT"),
    Artifact("hubert_config", "chinese-hubert-base-config", "chinese-hubert-base/config.json", 1_449,
             "c3e5060a1277e0f078cc6be9da4528a605dba6ece93018981fe2c820e5c7b103", "MIT"),
    Artifact("hubert_preprocessor", "chinese-hubert-base-preprocessor", "chinese-hubert-base/preprocessor_config.json", 212,
             "dcd684124d06722947939d41ea6ae58dbf10968c60a11a29f23ddc602c64a29b", "MIT"),
    Artifact("bert_weights", "chinese-roberta-wwm-ext-large", "chinese-roberta-wwm-ext-large/pytorch_model.bin", 651_225_145,
             "e53a693acc59ace251d143d068096ae0d7b79e4b1b503fa84c9dcf576448c1d8", "Apache-2.0"),
    Artifact("bert_config", "chinese-roberta-wwm-ext-large-config", "chinese-roberta-wwm-ext-large/config.json", 963,
             "3d57de2fd7e80d0e5c8ff194f0bbb6baa10df7e43fc262a0cc71298a78b0a3e5", "Apache-2.0"),
    Artifact("bert_tokenizer", "chinese-roberta-wwm-ext-large-tokenizer", "chinese-roberta-wwm-ext-large/tokenizer.json",
             268_962, "173796956820ea27bd14f76bf28162607ff4254807e2948253eb5b46f5bb643b", "Apache-2.0"),
    Artifact("language_id", "fasttext-lid-176", "fast_langdetect/lid.176.bin", 131_266_198,
             "7e69ec5451bc261cc7844e49e4792a85d7f09c06789ec800fc4a44aec362764e", "CC-BY-SA-3.0",
             "https://dl.fbaipublicfiles.com/fasttext/supervised-models/lid.176.bin"),
)


def artifact(role: str) -> Artifact:
    return next(item for item in ARTIFACTS if item.role == role)
