"""Pinned sources and model files of the Martlet singing host role. Nothing is downloaded at run time.

Sources are fetched by the image build at these git commits (the commit ID is the content's identity). Model files are
downloaded by `martlet-singing provision` into the martlet-singing-models volume and verified by size and SHA-256.
"""

from __future__ import annotations

from typing import NamedTuple

# ace-step/ACE-Step-1.5 (MIT): the music model's code, with nano-vllm (MIT) under acestep/third_parts.
ACE_STEP_REPOSITORY = "https://github.com/ace-step/ACE-Step-1.5.git"
ACE_STEP_COMMIT = "ca1e85fe9430179831e6bc6be790c332190a3866"
# Soul-AILab/SoulX-Singer (Apache-2.0): SoulX-Singer-SVC and the RMVPE pitch extractor it uses.
SOULX_REPOSITORY = "https://github.com/Soul-AILab/SoulX-Singer.git"
SOULX_COMMIT = "81aeb3ae772c70093c3de74dc23c92d983801ae4"
# open-mmlab/Amphion (code MIT): VevoSing (Vevo1.5), used only when the owner sets VevoSing up.
AMPHION_REPOSITORY = "https://github.com/open-mmlab/Amphion.git"
AMPHION_COMMIT = "26f6883110181f1dbfe95c70a7c7dbaf4de5f42a"

ACE_LM = "acestep-5Hz-lm-0.6B"
ACE_TURBO = "acestep-v15-turbo"
ACE_SFT = "acestep-v15-sft"
GENERATOR_IDS = {"fast": "ace-step-v15-turbo", "high_quality": "ace-step-v15-sft"}
SEPARATOR_ID = "demucs-htdemucs-ft-vocals"
CONVERTER_IDS = {"soulx": "soulx-singer-svc", "vevosing": "vevo1.5-fm-singnet7k"}


class PinnedFile(NamedTuple):
    group: str  # "core" (always) or "vevosing" (only when the owner sets VevoSing up)
    repository: str
    revision: str
    path: str
    size: int
    sha256: str
    license_id: str
    local: str  # path under the models directory

    @property
    def url(self) -> str:
        return f"https://huggingface.co/{self.repository}/resolve/{self.revision}/{self.path}"


PINNED_FILES: tuple[PinnedFile, ...] = (
    PinnedFile("core", "ACE-Step/Ace-Step1.5", "19671f406d603126926c1b7e2adc169acbcade22", "acestep-v15-turbo/config.json",
               1_968, "74745ff704ea49164c3d2d1c99fc0670f3fc635a869f0aec2d1311e6a52d400a", "MIT", "ace-step/acestep-v15-turbo/config.json"),
    PinnedFile("core", "ACE-Step/Ace-Step1.5", "19671f406d603126926c1b7e2adc169acbcade22", "acestep-v15-turbo/model.safetensors",
               4_787_825_604, "3f6e0797fad420a39bd33979eb6e840e30989e34a3794e843d23b60ec6e422d7", "MIT", "ace-step/acestep-v15-turbo/model.safetensors"),
    PinnedFile("core", "ACE-Step/Ace-Step1.5", "19671f406d603126926c1b7e2adc169acbcade22", "acestep-v15-turbo/silence_latent.pt",
               3_841_215, "a778e9dd942f5e8b2c09c55370782d318834432b03dabbcdf70e6ed49ad6358b", "MIT", "ace-step/acestep-v15-turbo/silence_latent.pt"),
    PinnedFile("core", "ACE-Step/Ace-Step1.5", "19671f406d603126926c1b7e2adc169acbcade22", "acestep-v15-turbo/configuration_acestep_v15.py",
               13_130, "b89870c5c7a7ce060eb0bcdbb5ffc86b0b1a324ca325a26be552ea1b42496dc5", "MIT", "ace-step/acestep-v15-turbo/configuration_acestep_v15.py"),
    PinnedFile("core", "ACE-Step/Ace-Step1.5", "19671f406d603126926c1b7e2adc169acbcade22", "acestep-v15-turbo/modeling_acestep_v15_turbo.py",
               96_036, "c1ab0dd547124fee7ada449b2b86eae8201dc7d15889932643bbb67e3c982444", "MIT", "ace-step/acestep-v15-turbo/modeling_acestep_v15_turbo.py"),
    PinnedFile("core", "ACE-Step/Ace-Step1.5", "19671f406d603126926c1b7e2adc169acbcade22", "vae/config.json",
               425, "14e019904df567f26df750317a70e2bd08f9f8f3c40ff4a24c97d1cd3f20ccd2", "MIT", "ace-step/vae/config.json"),
    PinnedFile("core", "ACE-Step/Ace-Step1.5", "19671f406d603126926c1b7e2adc169acbcade22", "vae/diffusion_pytorch_model.safetensors",
               337_431_388, "da17edb604c40deaf09e9b24974e590d1ca83a374070e5d0884cfa4bed9a99b0", "MIT", "ace-step/vae/diffusion_pytorch_model.safetensors"),
    PinnedFile("core", "ACE-Step/Ace-Step1.5", "19671f406d603126926c1b7e2adc169acbcade22", "Qwen3-Embedding-0.6B/added_tokens.json",
               707, "c0284b582e14987fbd3d5a2cb2bd139084371ed9acbae488829a1c900833c680", "MIT", "ace-step/Qwen3-Embedding-0.6B/added_tokens.json"),
    PinnedFile("core", "ACE-Step/Ace-Step1.5", "19671f406d603126926c1b7e2adc169acbcade22", "Qwen3-Embedding-0.6B/chat_template.jinja",
               4_116, "87a2728cb8dc9fe424d624542f6060ec05a1d285ebbec578bb078900e33396b5", "MIT", "ace-step/Qwen3-Embedding-0.6B/chat_template.jinja"),
    PinnedFile("core", "ACE-Step/Ace-Step1.5", "19671f406d603126926c1b7e2adc169acbcade22", "Qwen3-Embedding-0.6B/config.json",
               1_359, "bb23c1607cfe059a58d8f0196cf1cebb52082b1056b8e358a579da80a5759420", "MIT", "ace-step/Qwen3-Embedding-0.6B/config.json"),
    PinnedFile("core", "ACE-Step/Ace-Step1.5", "19671f406d603126926c1b7e2adc169acbcade22", "Qwen3-Embedding-0.6B/merges.txt",
               1_671_853, "8831e4f1a044471340f7c0a83d7bd71306a5b867e95fd870f74d0c5308a904d5", "MIT", "ace-step/Qwen3-Embedding-0.6B/merges.txt"),
    PinnedFile("core", "ACE-Step/Ace-Step1.5", "19671f406d603126926c1b7e2adc169acbcade22", "Qwen3-Embedding-0.6B/model.safetensors",
               1_191_586_416, "0437e45c94563b09e13cb7a64478fc406947a93cb34a7e05870fc8dcd48e23fd", "MIT", "ace-step/Qwen3-Embedding-0.6B/model.safetensors"),
    PinnedFile("core", "ACE-Step/Ace-Step1.5", "19671f406d603126926c1b7e2adc169acbcade22", "Qwen3-Embedding-0.6B/special_tokens_map.json",
               613, "76862e765266b85aa9459767e33cbaf13970f327a0e88d1c65846c2ddd3a1ecd", "MIT", "ace-step/Qwen3-Embedding-0.6B/special_tokens_map.json"),
    PinnedFile("core", "ACE-Step/Ace-Step1.5", "19671f406d603126926c1b7e2adc169acbcade22", "Qwen3-Embedding-0.6B/tokenizer.json",
               11_423_705, "def76fb086971c7867b829c23a26261e38d9d74e02139253b38aeb9df8b4b50a", "MIT", "ace-step/Qwen3-Embedding-0.6B/tokenizer.json"),
    PinnedFile("core", "ACE-Step/Ace-Step1.5", "19671f406d603126926c1b7e2adc169acbcade22", "Qwen3-Embedding-0.6B/tokenizer_config.json",
               5_404, "443bfa629eb16387a12edbf92a76f6a6f10b2af3b53d87ba1550adfcf45f7fa0", "MIT", "ace-step/Qwen3-Embedding-0.6B/tokenizer_config.json"),
    PinnedFile("core", "ACE-Step/Ace-Step1.5", "19671f406d603126926c1b7e2adc169acbcade22", "Qwen3-Embedding-0.6B/vocab.json",
               2_776_833, "ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910", "MIT", "ace-step/Qwen3-Embedding-0.6B/vocab.json"),
    PinnedFile("core", "ACE-Step/acestep-5Hz-lm-0.6B", "148d8ea0225bdab342ee1ae3a354275ccd60ca80", "added_tokens.json",
               2_217_787, "db08b66a515fb5d6acca0b3492d25bb44e0deda6241fc1113ac0679d40558c48", "MIT", "ace-step/acestep-5Hz-lm-0.6B/added_tokens.json"),
    PinnedFile("core", "ACE-Step/acestep-5Hz-lm-0.6B", "148d8ea0225bdab342ee1ae3a354275ccd60ca80", "chat_template.jinja",
               4_168, "a55ee1b1660128b7098723e0abcd92caa0788061051c62d51cbe87d9cf1974d8", "MIT", "ace-step/acestep-5Hz-lm-0.6B/chat_template.jinja"),
    PinnedFile("core", "ACE-Step/acestep-5Hz-lm-0.6B", "148d8ea0225bdab342ee1ae3a354275ccd60ca80", "config.json",
               1_386, "873ca63808b74e1e008ab950114765553b898b489b9e077e80db83348a118384", "MIT", "ace-step/acestep-5Hz-lm-0.6B/config.json"),
    PinnedFile("core", "ACE-Step/acestep-5Hz-lm-0.6B", "148d8ea0225bdab342ee1ae3a354275ccd60ca80", "merges.txt",
               1_671_853, "8831e4f1a044471340f7c0a83d7bd71306a5b867e95fd870f74d0c5308a904d5", "MIT", "ace-step/acestep-5Hz-lm-0.6B/merges.txt"),
    PinnedFile("core", "ACE-Step/acestep-5Hz-lm-0.6B", "148d8ea0225bdab342ee1ae3a354275ccd60ca80", "model.safetensors",
               1_325_804_024, "5d92a60806e2e88c04de58ddc6dde93f2bc8f1336162b3ad5853886c9bcc6b82", "MIT", "ace-step/acestep-5Hz-lm-0.6B/model.safetensors"),
    PinnedFile("core", "ACE-Step/acestep-5Hz-lm-0.6B", "148d8ea0225bdab342ee1ae3a354275ccd60ca80", "special_tokens_map.json",
               1_824_199, "76e233bfc357b0b03d7b6e6ba8e799244f358552575a7cdadb78d3d19106b298", "MIT", "ace-step/acestep-5Hz-lm-0.6B/special_tokens_map.json"),
    PinnedFile("core", "ACE-Step/acestep-5Hz-lm-0.6B", "148d8ea0225bdab342ee1ae3a354275ccd60ca80", "tokenizer.json",
               24_321_939, "35af56c3f5cb3ea2cc578aa28a8937770981d504f183ac5c8c38baf4bbd4af4d", "MIT", "ace-step/acestep-5Hz-lm-0.6B/tokenizer.json"),
    PinnedFile("core", "ACE-Step/acestep-5Hz-lm-0.6B", "148d8ea0225bdab342ee1ae3a354275ccd60ca80", "tokenizer_config.json",
               14_072_925, "6cd70cdd89425971794f5235562edcc608b0629a6c4686ae51a8b8c8b8ba5e95", "MIT", "ace-step/acestep-5Hz-lm-0.6B/tokenizer_config.json"),
    PinnedFile("core", "ACE-Step/acestep-5Hz-lm-0.6B", "148d8ea0225bdab342ee1ae3a354275ccd60ca80", "vocab.json",
               2_776_833, "ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910", "MIT", "ace-step/acestep-5Hz-lm-0.6B/vocab.json"),
    PinnedFile("core", "ACE-Step/acestep-v15-sft", "c410d249e71ea9385a7b586865e65b1473e1098d", "config.json",
               1_940, "9bb4f832f2e5e6c8bf7bddab3c6a6da1b13c01d072efbf0ed3c830536c473359", "MIT", "ace-step/acestep-v15-sft/config.json"),
    PinnedFile("core", "ACE-Step/acestep-v15-sft", "c410d249e71ea9385a7b586865e65b1473e1098d", "model.safetensors",
               4_787_825_604, "d4dd3a93870f06720027965b90771f529ab02094b3d29e2518f1d5e097e1af7e", "MIT", "ace-step/acestep-v15-sft/model.safetensors"),
    PinnedFile("core", "ACE-Step/acestep-v15-sft", "c410d249e71ea9385a7b586865e65b1473e1098d", "silence_latent.pt",
               3_841_215, "a778e9dd942f5e8b2c09c55370782d318834432b03dabbcdf70e6ed49ad6358b", "MIT", "ace-step/acestep-v15-sft/silence_latent.pt"),
    PinnedFile("core", "ACE-Step/acestep-v15-sft", "c410d249e71ea9385a7b586865e65b1473e1098d", "configuration_acestep_v15.py",
               13_130, "b89870c5c7a7ce060eb0bcdbb5ffc86b0b1a324ca325a26be552ea1b42496dc5", "MIT", "ace-step/acestep-v15-sft/configuration_acestep_v15.py"),
    PinnedFile("core", "ACE-Step/acestep-v15-sft", "c410d249e71ea9385a7b586865e65b1473e1098d", "modeling_acestep_v15_base.py",
               95_910, "5e3b475d46965dcd5b0d037e53a9af359305a7dbd78e2ee08b39e94c4b02ca48", "MIT", "ace-step/acestep-v15-sft/modeling_acestep_v15_base.py"),
    PinnedFile("core", "ACE-Step/acestep-v15-sft", "c410d249e71ea9385a7b586865e65b1473e1098d", "apg_guidance.py",
               7_956, "0c0ce9755952c99307ecad2fac67863d43258e34541cd37ce2f2221f59e76b15", "MIT", "ace-step/acestep-v15-sft/apg_guidance.py"),
    PinnedFile("core", "Soul-AILab/SoulX-Singer", "40493ad90286056c7a9095035164434a79daa8c9", "model-svc.pt",
               2_793_965_154, "38e72d9e15be6eef3cc937daaf81421a7b5b546816786530bb9aa346cee5d43c", "Apache-2.0", "soulx/model-svc.pt"),
    PinnedFile("core", "Soul-AILab/SoulX-Singer-Preprocess", "83dc50289d22a81b1e9998f5b9e111aef7c1fdcd", "rmvpe/rmvpe.pt",
               181_184_272, "6d62215f4306e3ca278246188607209f09af3dc77ed4232efdd069798c4ec193", "Apache-2.0", "soulx/rmvpe/rmvpe.pt"),
    PinnedFile("core", "openai/whisper-base", "e37978b90ca9030d5170a5c07aadb050351a65bb", "config.json",
               1_983, "a153c53883a6799b6f056b4a8d1a515c9926d03994682ba88a7616618d7da0c1", "Apache-2.0", "huggingface/hub/models--openai--whisper-base/snapshots/e37978b90ca9030d5170a5c07aadb050351a65bb/config.json"),
    PinnedFile("core", "openai/whisper-base", "e37978b90ca9030d5170a5c07aadb050351a65bb", "preprocessor_config.json",
               184_990, "9b5cd03a36fbb8a627c64d98a5b5b126ead95a77720723944487311f0110b666", "Apache-2.0", "huggingface/hub/models--openai--whisper-base/snapshots/e37978b90ca9030d5170a5c07aadb050351a65bb/preprocessor_config.json"),
    PinnedFile("core", "openai/whisper-base", "e37978b90ca9030d5170a5c07aadb050351a65bb", "model.safetensors",
               290_403_936, "07cadb9f25677c8d50df603e66a98fbd842cce45047139baeb16e6219a1e807b", "Apache-2.0", "huggingface/hub/models--openai--whisper-base/snapshots/e37978b90ca9030d5170a5c07aadb050351a65bb/model.safetensors"),
    PinnedFile("vevosing", "amphion/Vevo1.5", "f4053ca0d8badb57dffdce44eac97c08b7f6f922", "tokenizer/contentstyle_fvq16384_12.5hz/model.safetensors",
               244_781_696, "bebdcea39e2d0134dbcce193aed0e7b0d393a866346317cd196389e40e9151b0", "CC-BY-NC-ND-4.0", "vevo/tokenizer/contentstyle_fvq16384_12.5hz/model.safetensors"),
    PinnedFile("vevosing", "amphion/Vevo1.5", "f4053ca0d8badb57dffdce44eac97c08b7f6f922", "acoustic_modeling/fm_emilia101k_singnet7k/model.safetensors",
               1_417_921_192, "9d6888b7cb8ecd09cd9700f289964340dc0d8da7621c74261e4941d4075257ee", "CC-BY-NC-ND-4.0", "vevo/acoustic_modeling/fm_emilia101k_singnet7k/model.safetensors"),
    PinnedFile("vevosing", "amphion/Vevo1.5", "f4053ca0d8badb57dffdce44eac97c08b7f6f922", "acoustic_modeling/Vocoder/model.safetensors",
               1_020_206_416, "5b5d1a46b19351c9a71bd8a5a59dd16be0be2ddefe70d3a0b4915d9a425e56d3", "CC-BY-NC-ND-4.0", "vevo/acoustic_modeling/Vocoder/model.safetensors"),
    PinnedFile("vevosing", "amphion/Vevo1.5", "f4053ca0d8badb57dffdce44eac97c08b7f6f922", "acoustic_modeling/Vocoder/model_1.safetensors",
               69_768_280, "850799d78699134b969056183fc9d490c51f8d8154d0ed00fae3e738b6b30af6", "CC-BY-NC-ND-4.0", "vevo/acoustic_modeling/Vocoder/model_1.safetensors"),
    PinnedFile("vevosing", "amphion/Vevo1.5", "f4053ca0d8badb57dffdce44eac97c08b7f6f922", "acoustic_modeling/Vocoder/model_2.safetensors",
               180_693_296, "56130fd13d5fbe828e56d61edb0049d35700db0472a866b8167d1d217d2687f8", "CC-BY-NC-ND-4.0", "vevo/acoustic_modeling/Vocoder/model_2.safetensors"),
)

# Demucs (facebookresearch/demucs, MIT): the vocals specialist of htdemucs_ft (its file name ends with its SHA-256 prefix).
DEMUCS_FILE = ("https://dl.fbaipublicfiles.com/demucs/hybrid_transformer/04573f0d-f3cf25b2.th", 84_141_271,
               "f3cf25b222c4eed7cd49dd8b2c9597d50c18bd154090f7b919cfa5f93cf22c49", "MIT", "demucs/04573f0d-f3cf25b2.th")
# OpenAI Whisper medium (MIT), the content encoder VevoSing loads with whisper.load_model("medium").
WHISPER_MEDIUM_FILE = ("https://openaipublic.azureedge.net/main/whisper/models/"
                       "345ae4da62f9b3d59415adc60127b97c714f32e89e936602e85993674d08dcb1/medium.pt", 1_528_008_539,
                       "345ae4da62f9b3d59415adc60127b97c714f32e89e936602e85993674d08dcb1", "MIT", "whisper/medium.pt")


def files(groups: set[str]) -> list[tuple[str, str, str, int, str, str, str]]:
    """(artifact ID, URL, revision, bytes, SHA-256, license, local path) of every file the groups need."""
    selected = [(f"{p.repository}/{p.path}", p.url, p.revision, p.size, p.sha256, p.license_id, p.local)
                for p in PINNED_FILES if p.group in groups]
    selected.append(("demucs/" + DEMUCS_FILE[4].rsplit("/", 1)[1], DEMUCS_FILE[0], "4.0.1", DEMUCS_FILE[1], DEMUCS_FILE[2],
                     DEMUCS_FILE[3], DEMUCS_FILE[4]))
    if "vevosing" in groups:
        selected.append(("openai/whisper-medium.pt", WHISPER_MEDIUM_FILE[0], "20231117", WHISPER_MEDIUM_FILE[1],
                         WHISPER_MEDIUM_FILE[2], WHISPER_MEDIUM_FILE[3], WHISPER_MEDIUM_FILE[4]))
    return selected
