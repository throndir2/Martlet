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
# The models the owner may add, each fetched by the image build only when chosen (its SINGING_<NAME> choice is yes).
# HeartMuLa/heartlib (Apache-2.0): HeartMuLa's song model and HeartCodec.
HEARTLIB_REPOSITORY = "https://github.com/HeartMuLa/heartlib.git"
HEARTLIB_COMMIT = "5858ca8f1ffe58d62be7c007ea55f1033c5ca456"
# ASLP-lab/DiffRhythm (Apache-2.0): DiffRhythm 1.2's diffusion transformer and its lyrics front end.
DIFFRHYTHM_REPOSITORY = "https://github.com/ASLP-lab/DiffRhythm.git"
DIFFRHYTHM_COMMIT = "28ad63c0f096fe2ee258bcabbcf081d5d9366afd"
# Plachtaa/seed-vc (GPL-3.0, archived): Seed-VC's singing voice conversion.
SEEDVC_REPOSITORY = "https://github.com/Plachtaa/seed-vc.git"
SEEDVC_COMMIT = "51383efd921027683c89e5348211d93ff12ac2a8"
# m-a-p/YuE2-3B's own inference package (its wheel in the model repository, installed by the image build by SHA-256).
YUE2_WHEEL = ("https://huggingface.co/m-a-p/YuE2-3B/resolve/8f24312f187763b9854adeec87e437e2b03bbff1/"
              "yue2_infer-0.1.5-py3-none-any.whl", "8801e2c0d969db02df78d2994150b4ccd86077d87c24fdb8509b1f6f31462641")

ACE_LM = "acestep-5Hz-lm-0.6B"
ACE_TURBO = "acestep-v15-turbo"
ACE_SFT = "acestep-v15-sft"
# ACE-Step 1.5 XL SFT (a 4B music transformer, 50 steps), set up only when the owner chooses SINGING_MUSIC_MODELS standard-xl.
ACE_XL_SFT = "acestep-v15-xl-sft"
GENERATOR_IDS = {"fast": "ace-step-v15-turbo", "high_quality": "ace-step-v15-sft"}
XL_GENERATOR_ID = "ace-step-v15-xl-sft"
# The music models a song or music job can ask for ("model"); the first is the default.
MUSIC_MODELS = ("ace-step-v15", "ace-step-v15-xl", "heartmula", "diffrhythm", "yue")
# The music models that run in their own process (martlet_singing.extras), with the generator ID a result reports.
EXTRA_MUSIC = {"heartmula": "heartmula-oss-3b", "diffrhythm": "diffrhythm-1.2-full", "yue": "yue2-3b"}
SEPARATOR_ID = "demucs-htdemucs-ft-vocals"
CONVERTER_IDS = {"soulx": "soulx-singer-svc", "vevosing": "vevo1.5-fm-singnet7k", "vevo2": "vevo2-fm-singnet7k-repa",
                 "seedvc": "seed-vc-svc-44k-v2"}
# Each model's name in messages.
NAMES = {"ace-step-v15": "ACE-Step 1.5", "ace-step-v15-xl": "ACE-Step 1.5 XL", "heartmula": "HeartMuLa",
         "diffrhythm": "DiffRhythm", "yue": "YuE", "soulx": "SoulX-Singer", "vevosing": "VevoSing", "vevo2": "Vevo2",
         "seedvc": "Seed-VC"}
# The voice matches that run in martlet_singing.extras rather than martlet_singing.match.
EXTRA_MATCHES = ("vevo2", "seedvc")
# Each model the owner may add: the role choice that sets it up (yes or no). VevoSing and ACE-Step 1.5 XL keep their older
# choices (SINGING_VOICE_MATCHES, SINGING_MUSIC_MODELS).
EXTRA_CHOICES = {"heartmula": "SINGING_HEARTMULA", "diffrhythm": "SINGING_DIFFRHYTHM", "yue": "SINGING_YUE",
                 "vevo2": "SINGING_VEVO2", "seedvc": "SINGING_SEEDVC"}


class PinnedFile(NamedTuple):
    group: str  # "core" (always), "vevosing", "xl", or an added model's name (EXTRA_CHOICES): only when the owner adds it
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
    PinnedFile("xl", "ACE-Step/acestep-v15-xl-sft", "d06de46b4622f781cf07f4a013a67d591ca52819", "apg_guidance.py",
               8_340, "89ecdc4686e174225d6e6d304939d75892297dc3c19fbb11e04345dc3d4685db", "MIT", "ace-step/acestep-v15-xl-sft/apg_guidance.py"),
    PinnedFile("xl", "ACE-Step/acestep-v15-xl-sft", "d06de46b4622f781cf07f4a013a67d591ca52819", "config.json",
               2_525, "174cf8c4afc2c41212546b4dca9afb11e7958f8b1e1a770d7cd009a82d72a6f1", "MIT", "ace-step/acestep-v15-xl-sft/config.json"),
    PinnedFile("xl", "ACE-Step/acestep-v15-xl-sft", "d06de46b4622f781cf07f4a013a67d591ca52819", "configuration_acestep_v15.py",
               13_225, "0d66f38eba6a3d1665c9449a1a52724d68354477bc88dcf02389640e3602a0c7", "MIT", "ace-step/acestep-v15-xl-sft/configuration_acestep_v15.py"),
    PinnedFile("xl", "ACE-Step/acestep-v15-xl-sft", "d06de46b4622f781cf07f4a013a67d591ca52819", "model-00001-of-00004.safetensors",
               4_986_971_456, "1d304b8a34859cd92c349c71ae6c27d50564844c7f7611d2d32913f67508e5a4", "MIT", "ace-step/acestep-v15-xl-sft/model-00001-of-00004.safetensors"),
    PinnedFile("xl", "ACE-Step/acestep-v15-xl-sft", "d06de46b4622f781cf07f4a013a67d591ca52819", "model-00002-of-00004.safetensors",
               4_986_942_776, "94e33648f514fdd00f473f2d2680af959204b58b15f7ab37246ac16d066aac87", "MIT", "ace-step/acestep-v15-xl-sft/model-00002-of-00004.safetensors"),
    PinnedFile("xl", "ACE-Step/acestep-v15-xl-sft", "d06de46b4622f781cf07f4a013a67d591ca52819", "model-00003-of-00004.safetensors",
               4_986_942_808, "befe558365f53bc6c44f3f7e521cb2dd026927a817c36aa05ad94fcd80a533a9", "MIT", "ace-step/acestep-v15-xl-sft/model-00003-of-00004.safetensors"),
    PinnedFile("xl", "ACE-Step/acestep-v15-xl-sft", "d06de46b4622f781cf07f4a013a67d591ca52819", "model-00004-of-00004.safetensors",
               4_988_483_464, "dfbc77aa26e54d0127892d6e7f3c0868bfc4623aa067867f6595c3b2fb7d31a4", "MIT", "ace-step/acestep-v15-xl-sft/model-00004-of-00004.safetensors"),
    PinnedFile("xl", "ACE-Step/acestep-v15-xl-sft", "d06de46b4622f781cf07f4a013a67d591ca52819", "model.safetensors.index.json",
               71_471, "0a0c7e153a7bb6f6061b155739e9502cf83977bb959186d4df688595302a9474", "MIT", "ace-step/acestep-v15-xl-sft/model.safetensors.index.json"),
    PinnedFile("xl", "ACE-Step/acestep-v15-xl-sft", "d06de46b4622f781cf07f4a013a67d591ca52819", "modeling_acestep_v15_xl_base.py",
               105_981, "e367811c6d8cd9162e630da86622dd6edc9d5c2b7f605eadd68e69a227c35e88", "MIT", "ace-step/acestep-v15-xl-sft/modeling_acestep_v15_xl_base.py"),
    PinnedFile("xl", "ACE-Step/acestep-v15-xl-sft", "d06de46b4622f781cf07f4a013a67d591ca52819", "silence_latent.pt",
               3_841_215, "a778e9dd942f5e8b2c09c55370782d318834432b03dabbcdf70e6ed49ad6358b", "MIT", "ace-step/acestep-v15-xl-sft/silence_latent.pt"),
    PinnedFile("heartmula", "HeartMuLa/HeartMuLaGen", "2e18e01702011f4f7dc8642b6260967df62417ea", "config.json",
               101, "2fc578fc8d8445fa9d73d1925146d53d25b2e5f5d74b8a19aa5c000e37094f41", "Apache-2.0", "heartmula/ckpt/config.json"),
    PinnedFile("heartmula", "HeartMuLa/HeartMuLaGen", "2e18e01702011f4f7dc8642b6260967df62417ea", "gen_config.json",
               101, "2fc578fc8d8445fa9d73d1925146d53d25b2e5f5d74b8a19aa5c000e37094f41", "Apache-2.0", "heartmula/ckpt/gen_config.json"),
    PinnedFile("heartmula", "HeartMuLa/HeartMuLaGen", "2e18e01702011f4f7dc8642b6260967df62417ea", "tokenizer.json",
               9_085_657, "79e3e522635f3171300913bb421464a87de6222182a0570b9b2ccba2a964b2b4", "Apache-2.0", "heartmula/ckpt/tokenizer.json"),
    PinnedFile("heartmula", "HeartMuLa/HeartMuLa-oss-3B-happy-new-year", "41f6fc68490e11dc43fdabaa6b5767946408c903", "config.json",
               308, "a05bf0650f560d235fb3a0c109173d44eb3a9b6231fe599968e12706cd20edf4", "Apache-2.0", "heartmula/ckpt/HeartMuLa-oss-3B/config.json"),
    PinnedFile("heartmula", "HeartMuLa/HeartMuLa-oss-3B-happy-new-year", "41f6fc68490e11dc43fdabaa6b5767946408c903", "model-00001-of-00004.safetensors",
               4_933_188_000, "4815367cdbddc0192abe3dbe81e2ae6a6e8978a39ad4dd1d26a20e445b3aeea6", "Apache-2.0", "heartmula/ckpt/HeartMuLa-oss-3B/model-00001-of-00004.safetensors"),
    PinnedFile("heartmula", "HeartMuLa/HeartMuLa-oss-3B-happy-new-year", "41f6fc68490e11dc43fdabaa6b5767946408c903", "model-00002-of-00004.safetensors",
               4_932_808_592, "f079416ba4c0570231f7a2830a2e95033336a8b3528d1c033a07923a25f4b5d7", "Apache-2.0", "heartmula/ckpt/HeartMuLa-oss-3B/model-00002-of-00004.safetensors"),
    PinnedFile("heartmula", "HeartMuLa/HeartMuLa-oss-3B-happy-new-year", "41f6fc68490e11dc43fdabaa6b5767946408c903", "model-00003-of-00004.safetensors",
               4_935_901_576, "b43cd60ff50033fa9a5e08c4eb26edff55d5e95e0a313592804ab026ed3b895d", "Apache-2.0", "heartmula/ckpt/HeartMuLa-oss-3B/model-00003-of-00004.safetensors"),
    PinnedFile("heartmula", "HeartMuLa/HeartMuLa-oss-3B-happy-new-year", "41f6fc68490e11dc43fdabaa6b5767946408c903", "model-00004-of-00004.safetensors",
               950_588_000, "07c5f5820c0c00f0b8ad4544ed8a24ae1888f7d8c8a5d8bb3cdaba418a39cf2f", "Apache-2.0", "heartmula/ckpt/HeartMuLa-oss-3B/model-00004-of-00004.safetensors"),
    PinnedFile("heartmula", "HeartMuLa/HeartMuLa-oss-3B-happy-new-year", "41f6fc68490e11dc43fdabaa6b5767946408c903", "model.safetensors.index.json",
               22_579, "a96e28d71e3f767c94a7d25723f7ab77ce2abd06d5495315d9770be0e87e00ca", "Apache-2.0", "heartmula/ckpt/HeartMuLa-oss-3B/model.safetensors.index.json"),
    PinnedFile("heartmula", "HeartMuLa/HeartCodec-oss-20260123", "f889dab0532cfa4bf459f2a3367eb6d346b8eeda", "config.json",
               981, "b55a505b4cf5cd22bba40aac0d9d5082d560c1c942fe17583aaa95f28e3c08cf", "Apache-2.0", "heartmula/ckpt/HeartCodec-oss/config.json"),
    PinnedFile("heartmula", "HeartMuLa/HeartCodec-oss-20260123", "f889dab0532cfa4bf459f2a3367eb6d346b8eeda", "model-00001-of-00002.safetensors",
               4_930_472_472, "50d2c022468a0e127992d99e02117d2efbad2b10fddc8ceaf09550f154e185d4", "Apache-2.0", "heartmula/ckpt/HeartCodec-oss/model-00001-of-00002.safetensors"),
    PinnedFile("heartmula", "HeartMuLa/HeartCodec-oss-20260123", "f889dab0532cfa4bf459f2a3367eb6d346b8eeda", "model-00002-of-00002.safetensors",
               1_707_911_436, "6f319d0f5b31858dfe98bd8fc89d3083cbfff74ac75a0531f1a7b86949197917", "Apache-2.0", "heartmula/ckpt/HeartCodec-oss/model-00002-of-00002.safetensors"),
    PinnedFile("heartmula", "HeartMuLa/HeartCodec-oss-20260123", "f889dab0532cfa4bf459f2a3367eb6d346b8eeda", "model.safetensors.index.json",
               83_875, "d19153a142865e4a90508294f36c7ec5ab07d33bf0e8b01a0717c75b80ce6a63", "Apache-2.0", "heartmula/ckpt/HeartCodec-oss/model.safetensors.index.json"),
    PinnedFile("diffrhythm", "ASLP-lab/DiffRhythm-1_2-full", "2aea17a3cf7072e0933f15be3a01e762a5c7fbe9", "cfm_model.pt",
               2_219_500_168, "72a1215fda7b4117eb5314c6a4f00b1f9a9d0ca7c89e19bae10b73246a8f4993", "Apache-2.0", "diffrhythm/cfm_model.pt"),
    PinnedFile("diffrhythm", "ASLP-lab/DiffRhythm-vae", "74e2afacfd91dd1b96662c96dcef763c1258768b", "vae_model.pt",
               624_520_127, "712693f27299937c6ccf1a6d6f1d9b45c7c8c11210d3b0cbb0f36181465ba29f", "Stability-AI-Community", "diffrhythm/vae_model.pt"),
    PinnedFile("diffrhythm", "OpenMuQ/MuQ-MuLan-large", "2e01c796b71dca71b45251384c04cd7b237c9020", "config.json",
               847, "8fefc545ef87ecd9bcde7417dd03464370c48c321f36dcff20266a752079e468", "CC-BY-NC-4.0", "diffrhythm/hf/models--OpenMuQ--MuQ-MuLan-large/snapshots/2e01c796b71dca71b45251384c04cd7b237c9020/config.json"),
    PinnedFile("diffrhythm", "OpenMuQ/MuQ-MuLan-large", "2e01c796b71dca71b45251384c04cd7b237c9020", "pytorch_model.bin",
               2_653_954_401, "d42ae3f7cb9b66759ee0089ddc70e2f28b130c2d8ba621457358272d32dd0444", "CC-BY-NC-4.0", "diffrhythm/hf/models--OpenMuQ--MuQ-MuLan-large/snapshots/2e01c796b71dca71b45251384c04cd7b237c9020/pytorch_model.bin"),
    PinnedFile("diffrhythm", "OpenMuQ/MuQ-large-msd-iter", "0562a57814f6f8bbd9fdea0a25921a2fce1a841a", "config.json",
               3_133, "237335ee27d8fb951ce778701a12a79e06c51ae636dd786f97e45f51ce532543", "CC-BY-NC-4.0", "diffrhythm/hf/models--OpenMuQ--MuQ-large-msd-iter/snapshots/0562a57814f6f8bbd9fdea0a25921a2fce1a841a/config.json"),
    PinnedFile("diffrhythm", "OpenMuQ/MuQ-large-msd-iter", "0562a57814f6f8bbd9fdea0a25921a2fce1a841a", "model.safetensors",
               1_333_825_096, "273febab2be02872c37d2c37e48a9d6c52c1c9392f3eeeabd498efa281ccb7a6", "CC-BY-NC-4.0", "diffrhythm/hf/models--OpenMuQ--MuQ-large-msd-iter/snapshots/0562a57814f6f8bbd9fdea0a25921a2fce1a841a/model.safetensors"),
    PinnedFile("diffrhythm", "OpenMuQ/MuQ-large-msd-iter", "0562a57814f6f8bbd9fdea0a25921a2fce1a841a", "pytorch_model.bin",
               1_333_965_438, "334df3de2832ec1acfd8b6ce54e7de4073401fe821f7ec0ad0d954832be2d26a", "CC-BY-NC-4.0", "diffrhythm/hf/models--OpenMuQ--MuQ-large-msd-iter/snapshots/0562a57814f6f8bbd9fdea0a25921a2fce1a841a/pytorch_model.bin"),
    PinnedFile("diffrhythm", "FacebookAI/xlm-roberta-base", "e73636d4f797dec63c3081bb6ed5c7b0bb3f2089", "config.json",
               615, "d66ed8cd4f2a93b358c245e50736fa389ed4f35c0bae7aad0b32abb20c62b579", "MIT", "diffrhythm/hf/models--xlm-roberta-base/snapshots/e73636d4f797dec63c3081bb6ed5c7b0bb3f2089/config.json"),
    PinnedFile("diffrhythm", "FacebookAI/xlm-roberta-base", "e73636d4f797dec63c3081bb6ed5c7b0bb3f2089", "model.safetensors",
               1_115_567_652, "6fd4797bc397c3b8b55d6bb5740366b57e6a3ce91c04c77f22aafc0c128e6feb", "MIT", "diffrhythm/hf/models--xlm-roberta-base/snapshots/e73636d4f797dec63c3081bb6ed5c7b0bb3f2089/model.safetensors"),
    PinnedFile("diffrhythm", "FacebookAI/xlm-roberta-base", "e73636d4f797dec63c3081bb6ed5c7b0bb3f2089", "sentencepiece.bpe.model",
               5_069_051, "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865", "MIT", "diffrhythm/hf/models--xlm-roberta-base/snapshots/e73636d4f797dec63c3081bb6ed5c7b0bb3f2089/sentencepiece.bpe.model"),
    PinnedFile("diffrhythm", "FacebookAI/xlm-roberta-base", "e73636d4f797dec63c3081bb6ed5c7b0bb3f2089", "tokenizer.json",
               9_096_718, "a898ea75433890f6610f4e470b8ebeb0c21dce5c8dd61f892eb09eb5919d2e2c", "MIT", "diffrhythm/hf/models--xlm-roberta-base/snapshots/e73636d4f797dec63c3081bb6ed5c7b0bb3f2089/tokenizer.json"),
    PinnedFile("yue", "m-a-p/YuE2-3B", "8f24312f187763b9854adeec87e437e2b03bbff1", "LICENSE",
               20_309, "060985741d20e70613b4c189c7de106cabd3fb2109fbbfb6d705c9d619417dd0", "CC-BY-NC-4.0", "yue/YuE2-3B/LICENSE"),
    PinnedFile("yue", "m-a-p/YuE2-3B", "8f24312f187763b9854adeec87e437e2b03bbff1", "THIRD_PARTY_NOTICES.md",
               793, "14d3fd9f6fee86b4260b69b0979735b99ffec8c6b4db254567047a4215cd9af3", "CC-BY-NC-4.0", "yue/YuE2-3B/THIRD_PARTY_NOTICES.md"),
    PinnedFile("yue", "m-a-p/YuE2-3B", "8f24312f187763b9854adeec87e437e2b03bbff1", "config.json",
               959, "ad3477bbef890bf98ae196c1e4b44779494a6231c4ab66f32708eabadf265329", "CC-BY-NC-4.0", "yue/YuE2-3B/config.json"),
    PinnedFile("yue", "m-a-p/YuE2-3B", "8f24312f187763b9854adeec87e437e2b03bbff1", "generation_config.json",
               204, "ccc842772f029049fadf9b288393bcdd9f79271b361c0f1875ec1218e4ba5170", "CC-BY-NC-4.0", "yue/YuE2-3B/generation_config.json"),
    PinnedFile("yue", "m-a-p/YuE2-3B", "8f24312f187763b9854adeec87e437e2b03bbff1", "licenses/SnakeBeta-NVIDIA-MIT.txt",
               1_076, "da9858d516047d82096d01c112a61bd67f26d289039464d668a1d45f91738ecc", "CC-BY-NC-4.0", "yue/YuE2-3B/licenses/SnakeBeta-NVIDIA-MIT.txt"),
    PinnedFile("yue", "m-a-p/YuE2-3B", "8f24312f187763b9854adeec87e437e2b03bbff1", "licenses/stable-audio-tools-MIT.txt",
               1_069, "a1fac33b7bcd791b74fb33aeb439f825e7277e239fc119fb7d2ab6f084a0c101", "CC-BY-NC-4.0", "yue/YuE2-3B/licenses/stable-audio-tools-MIT.txt"),
    PinnedFile("yue", "m-a-p/YuE2-3B", "8f24312f187763b9854adeec87e437e2b03bbff1", "model.safetensors",
               7_261_441_640, "1d55c42c1a9875c34f5d736e15078449992b044e807ce2a138e6cf289a1e59e9", "CC-BY-NC-4.0", "yue/YuE2-3B/model.safetensors"),
    PinnedFile("yue", "m-a-p/YuE2-3B", "8f24312f187763b9854adeec87e437e2b03bbff1", "modeling_yue2.py",
               32_983, "62d2bd758c8fff1a0d64584a699343076fca9a7880db5d8e19263bc30ae79d7c", "CC-BY-NC-4.0", "yue/YuE2-3B/modeling_yue2.py"),
    PinnedFile("yue", "m-a-p/YuE2-3B", "8f24312f187763b9854adeec87e437e2b03bbff1", "qwen.tiktoken",
               2_561_218, "b2b1b8dfb5cc5f024bafc373121c6aba3f66f9a5a0269e243470a1de16a33186", "CC-BY-NC-4.0", "yue/YuE2-3B/qwen.tiktoken"),
    PinnedFile("yue", "m-a-p/YuE2-3B", "8f24312f187763b9854adeec87e437e2b03bbff1", "weights_manifest.json",
               179, "2296f82c29dcb11aeefde07e216403a32fc86c8f6da2ec15a70102abfd936a93", "CC-BY-NC-4.0", "yue/YuE2-3B/weights_manifest.json"),
    PinnedFile("yue", "m-a-p/YuE2-3B", "8f24312f187763b9854adeec87e437e2b03bbff1", "yue2_generation_config.json",
               466, "203830cebde6e3644eb291925362990d198e66c3b6006cd11f1aaa21904bcc61", "CC-BY-NC-4.0", "yue/YuE2-3B/yue2_generation_config.json"),
    PinnedFile("yue", "m-a-p/YuE2-Vae", "152733a19ad43aa67e367f9b5503ef8075bb5126", "LICENSE",
               20_309, "060985741d20e70613b4c189c7de106cabd3fb2109fbbfb6d705c9d619417dd0", "CC-BY-NC-4.0", "yue/YuE2-Vae/LICENSE"),
    PinnedFile("yue", "m-a-p/YuE2-Vae", "152733a19ad43aa67e367f9b5503ef8075bb5126", "THIRD_PARTY_NOTICES.md",
               793, "14d3fd9f6fee86b4260b69b0979735b99ffec8c6b4db254567047a4215cd9af3", "CC-BY-NC-4.0", "yue/YuE2-Vae/THIRD_PARTY_NOTICES.md"),
    PinnedFile("yue", "m-a-p/YuE2-Vae", "152733a19ad43aa67e367f9b5503ef8075bb5126", "config.json",
               1_378, "f0191bb9694009956de44e0c361a6f1334760be4c8f848e599bde242a54a0970", "CC-BY-NC-4.0", "yue/YuE2-Vae/config.json"),
    PinnedFile("yue", "m-a-p/YuE2-Vae", "152733a19ad43aa67e367f9b5503ef8075bb5126", "licenses/SnakeBeta-NVIDIA-MIT.txt",
               1_076, "da9858d516047d82096d01c112a61bd67f26d289039464d668a1d45f91738ecc", "CC-BY-NC-4.0", "yue/YuE2-Vae/licenses/SnakeBeta-NVIDIA-MIT.txt"),
    PinnedFile("yue", "m-a-p/YuE2-Vae", "152733a19ad43aa67e367f9b5503ef8075bb5126", "licenses/stable-audio-tools-MIT.txt",
               1_069, "a1fac33b7bcd791b74fb33aeb439f825e7277e239fc119fb7d2ab6f084a0c101", "CC-BY-NC-4.0", "yue/YuE2-Vae/licenses/stable-audio-tools-MIT.txt"),
    PinnedFile("yue", "m-a-p/YuE2-Vae", "152733a19ad43aa67e367f9b5503ef8075bb5126", "model.safetensors",
               530_512_720, "807ce9d5149fa27c5ad3e6582058469852e908f6c5acc8c8aa338e7ab7751346", "CC-BY-NC-4.0", "yue/YuE2-Vae/model.safetensors"),
    PinnedFile("yue", "m-a-p/YuE2-Vae", "152733a19ad43aa67e367f9b5503ef8075bb5126", "modeling_vae.py",
               25_265, "b6578ff461e6be9fd079ca5ddf12893940f3b3338ff3435fceae89c466486f5a", "CC-BY-NC-4.0", "yue/YuE2-Vae/modeling_vae.py"),
    PinnedFile("yue", "m-a-p/YuE2-Vae", "152733a19ad43aa67e367f9b5503ef8075bb5126", "weights_manifest.json",
               178, "017d64a4d288217a43fcac3866a0e1d55f796fa891d4dfb6de0bd170ab750026", "CC-BY-NC-4.0", "yue/YuE2-Vae/weights_manifest.json"),
    PinnedFile("vevo2", "RMSnow/Vevo2", "2674843cbaa50aa89ee7ccaf5bb15d6ccf46c6c8", "acoustic_modeling/fm_emilia101k_singnet7k_repa/config.json",
               2_290, "e0ce1432ac20903ec8c65d16794e93c278a11d0a472ab0032241c20b8d07a4db", "CC-BY-NC-ND-4.0", "vevo2/acoustic_modeling/fm_emilia101k_singnet7k_repa/config.json"),
    PinnedFile("vevo2", "RMSnow/Vevo2", "2674843cbaa50aa89ee7ccaf5bb15d6ccf46c6c8", "acoustic_modeling/fm_emilia101k_singnet7k_repa/model.safetensors",
               1_451_496_488, "ef3733b3f92cf8f38e32f6d161f58751247b199cffc15cc1562e76c1289a7186", "CC-BY-NC-ND-4.0", "vevo2/acoustic_modeling/fm_emilia101k_singnet7k_repa/model.safetensors"),
    PinnedFile("vevo2", "RMSnow/Vevo2", "2674843cbaa50aa89ee7ccaf5bb15d6ccf46c6c8", "tokenizer/contentstyle_fvq16384_12.5hz/model.safetensors",
               244_781_696, "bebdcea39e2d0134dbcce193aed0e7b0d393a866346317cd196389e40e9151b0", "CC-BY-NC-ND-4.0", "vevo2/tokenizer/contentstyle_fvq16384_12.5hz/model.safetensors"),
    PinnedFile("vevo2", "RMSnow/Vevo2", "2674843cbaa50aa89ee7ccaf5bb15d6ccf46c6c8", "vocoder/config.json",
               2_069, "a6445bcc2182c43d97491b38d79022901f02b0f9fb56aa7a51498d0da8b9767d", "CC-BY-NC-ND-4.0", "vevo2/vocoder/config.json"),
    PinnedFile("vevo2", "RMSnow/Vevo2", "2674843cbaa50aa89ee7ccaf5bb15d6ccf46c6c8", "vocoder/model.safetensors",
               1_020_206_416, "5b5d1a46b19351c9a71bd8a5a59dd16be0be2ddefe70d3a0b4915d9a425e56d3", "CC-BY-NC-ND-4.0", "vevo2/vocoder/model.safetensors"),
    PinnedFile("vevo2", "RMSnow/Vevo2", "2674843cbaa50aa89ee7ccaf5bb15d6ccf46c6c8", "vocoder/model_1.safetensors",
               69_768_280, "850799d78699134b969056183fc9d490c51f8d8154d0ed00fae3e738b6b30af6", "CC-BY-NC-ND-4.0", "vevo2/vocoder/model_1.safetensors"),
    PinnedFile("vevo2", "RMSnow/Vevo2", "2674843cbaa50aa89ee7ccaf5bb15d6ccf46c6c8", "vocoder/model_2.safetensors",
               180_693_296, "56130fd13d5fbe828e56d61edb0049d35700db0472a866b8167d1d217d2687f8", "CC-BY-NC-ND-4.0", "vevo2/vocoder/model_2.safetensors"),
    PinnedFile("seedvc", "Plachta/Seed-VC", "257283f9f41585055e8f858fba4fd044e5caed6e", "DiT_seed_v2_uvit_whisper_base_f0_44k_bigvgan_pruned_ft_ema_v2.pth",
               820_865_494, "42aef93ffe65857c840d270252fa040f7ba04514945ec460f3ac1ac2a96de684", "GPL-3.0", "seedvc/DiT_seed_v2_uvit_whisper_base_f0_44k_bigvgan_pruned_ft_ema_v2.pth"),
    PinnedFile("seedvc", "Plachta/Seed-VC", "257283f9f41585055e8f858fba4fd044e5caed6e", "config_dit_mel_seed_uvit_whisper_base_f0_44k.yml",
               2_408, "ff86f343b08c2def405f075022eeb10dafba23c4eda36d151010f4ca6d7a1a0a", "GPL-3.0", "seedvc/config_dit_mel_seed_uvit_whisper_base_f0_44k.yml"),
    PinnedFile("seedvc", "lj1995/VoiceConversionWebUI", "e6d0c1a17da07c33557852f9dfa2bd44cc75737d", "rmvpe.pt",
               181_184_272, "6d62215f4306e3ca278246188607209f09af3dc77ed4232efdd069798c4ec193", "MIT", "seedvc/rmvpe.pt"),
    PinnedFile("seedvc", "funasr/campplus", "e4b6ede7ce16997aff4ae69fbca1f0175e2afede", "campplus_cn_common.bin",
               28_036_335, "3388cf5fd3493c9ac9c69851d8e7a8badcfb4f3dc631020c4961371646d5ada8", "Apache-2.0", "seedvc/campplus_cn_common.bin"),
    PinnedFile("seedvc", "nvidia/bigvgan_v2_44khz_128band_512x", "95a9d1dcb12906c03edd938d77b9333d6ded7dfb", "bigvgan_generator.pt",
               489_041_291, "d9fe7ec6bd0b44ed9d66973d5012d8181c1570b01e5c72df51973e241dccd357", "MIT", "seedvc/bigvgan/bigvgan_generator.pt"),
    PinnedFile("seedvc", "nvidia/bigvgan_v2_44khz_128band_512x", "95a9d1dcb12906c03edd938d77b9333d6ded7dfb", "config.json",
               1_403, "65b7f487bfaf15256056a75f6667ef5f640214c981922a111927873705ea4ddb", "MIT", "seedvc/bigvgan/config.json"),
    PinnedFile("seedvc", "openai/whisper-small", "973afd24965f72e36ca33b3055d56a652f456b4d", "config.json",
               1_967, "e6a2b489da1b5aed65a8eb8d1e7466fa867ad5643a8bc138ba708bd56b2875c4", "Apache-2.0", "seedvc/whisper-small/config.json"),
    PinnedFile("seedvc", "openai/whisper-small", "973afd24965f72e36ca33b3055d56a652f456b4d", "model.safetensors",
               966_995_080, "1d7734884874f1a1513ed9aa760a4f8e97aaa02fd6d93a3a85d27b2ae9ca596b", "Apache-2.0", "seedvc/whisper-small/model.safetensors"),
    PinnedFile("seedvc", "openai/whisper-small", "973afd24965f72e36ca33b3055d56a652f456b4d", "preprocessor_config.json",
               184_990, "9b5cd03a36fbb8a627c64d98a5b5b126ead95a77720723944487311f0110b666", "Apache-2.0", "seedvc/whisper-small/preprocessor_config.json"),
)

# The Hugging Face cache folders (under the models directory) whose refs/main provisioning writes, so libraries that load a
# model by its repository name find the pinned files offline: (group, folder, revision).
HF_CACHE_REFS: tuple[tuple[str, str, str], ...] = (
    ("diffrhythm", "diffrhythm/hf/models--OpenMuQ--MuQ-MuLan-large", "2e01c796b71dca71b45251384c04cd7b237c9020"),
    ("diffrhythm", "diffrhythm/hf/models--OpenMuQ--MuQ-large-msd-iter", "0562a57814f6f8bbd9fdea0a25921a2fce1a841a"),
    ("diffrhythm", "diffrhythm/hf/models--xlm-roberta-base", "e73636d4f797dec63c3081bb6ed5c7b0bb3f2089"),
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
    if "vevosing" in groups or "vevo2" in groups:
        selected.append(("openai/whisper-medium.pt", WHISPER_MEDIUM_FILE[0], "20231117", WHISPER_MEDIUM_FILE[1],
                         WHISPER_MEDIUM_FILE[2], WHISPER_MEDIUM_FILE[3], WHISPER_MEDIUM_FILE[4]))
    return selected
