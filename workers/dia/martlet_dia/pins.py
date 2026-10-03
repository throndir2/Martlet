"""Pinned Dia source, weights and codec for the Martlet dia host role. Nothing is downloaded at run time."""

from __future__ import annotations

# nari-labs/dia has no tagged releases; this is main at the time of pinning (the last change under dia/ is 998fc25).
# Code: Apache-2.0 (LICENSE at that commit).
DIA_SOURCE_COMMIT = "876125e461a03b157ec905b0fe8b57a0f8b9e7a0"
DIA_SOURCE_LICENSE = "Apache-2.0"
DIA_SOURCE_URL = "https://raw.githubusercontent.com/nari-labs/dia/" + DIA_SOURCE_COMMIT + "/"

# path in the repository -> (bytes, SHA-256)
DIA_SOURCE_FILES: dict[str, tuple[int, str]] = {
    "dia/__init__.py": (50, "d6cc98eb95ccf0123697c036df7759db197d5424aa9eb52f75e8d4f88d736b6a"),
    "dia/audio.py": (6142, "bfad8e67beb97cc084289abc4327f6cdd4ec4dd81f8460a58be334c7b39a68ff"),
    "dia/config.py": (8746, "7dbc448c213551de67b9f339257691407b02859f1bb07046d962a25b98cac159"),
    "dia/layers.py": (31662, "a5a6eeef3853fa4d130f38fbfb92418acf332d897a6832311d88827dc0921f0d"),
    "dia/model.py": (33337, "8f50580518731f38fdc5195c38d1ff8933a27657089e0355a5b0f2f90ff0b8ca"),
    "dia/state.py": (7659, "90c20d96948dcf91daddb3fc3ebcb6e6f67805e365e06f651223483f896cfc27"),
    "LICENSE": (11338, "34c1659a3495220f85dffebd4d3a930bcbfd9f216f8a3c5f880a1d4a59a5358d"),
}

# Nonverbal cues Dia recognizes, verbatim from the README at DIA_SOURCE_COMMIT. Text passes to Dia unchanged, so these
# work when a reply contains them; Dia warns that overusing them or using unlisted ones can cause artifacts.
NONVERBAL_TAGS: tuple[str, ...] = (
    "(laughs)", "(clears throat)", "(sighs)", "(gasps)", "(coughs)", "(singing)", "(sings)", "(mumbles)", "(beep)",
    "(groans)", "(sniffs)", "(claps)", "(screams)", "(inhales)", "(exhales)", "(applause)", "(burps)", "(humming)",
    "(sneezes)", "(chuckle)", "(whistles)",
)

DEFAULT_MODEL = "dia-1.6b-0626"

# Pinned model files per selectable model: role -> (artifact ID, URL, revision, bytes, SHA-256, license, path under models/).
# Dia-1.6B-0626 at ef2795f: config.json is the format dia/config.py at the pinned commit reads, and pytorch_model.bin is
# the state dict Dia.from_local loads (byte-identical to dia-v1.pth). The DAC 44.1 kHz codec is the one dia/model.py uses
# (dac.utils.download() default: descript-audio-codec release 0.0.1 weights.pth); Martlet loads it from this path instead.
PINNED_MODELS: dict[str, dict[str, tuple[str, str, str, int, str, str, str]]] = {
    "dia-1.6b-0626": {
        "model_weights": (
            "dia-1.6b-0626",
            "https://huggingface.co/nari-labs/Dia-1.6B-0626/resolve/ef2795fcc29c5abe6ffc91fd33808588b49bbc66/pytorch_model.bin",
            "ef2795fcc29c5abe6ffc91fd33808588b49bbc66",
            6_444_787_333,
            "8a5106c06899aeea013a7f6ef32e84a15a30f965be676b97996b3ddaf1eb55b9",
            "Apache-2.0",
            "dia-1.6b-0626/pytorch_model.bin",
        ),
        "model_configuration": (
            "dia-1.6b-0626-config",
            "https://huggingface.co/nari-labs/Dia-1.6B-0626/resolve/ef2795fcc29c5abe6ffc91fd33808588b49bbc66/config.json",
            "ef2795fcc29c5abe6ffc91fd33808588b49bbc66",
            1_396,
            "4f4e9c50e6898fa79d6fe16e9991bebef07a4d3dd6926eee5eb7f7e39c88e04f",
            "Apache-2.0",
            "dia-1.6b-0626/config.json",
        ),
        "codec_weights": (
            "dac-44khz-8kbps",
            "https://github.com/descriptinc/descript-audio-codec/releases/download/0.0.1/weights.pth",
            "0.0.1",
            306_717_287,
            "a88eed82a7024ccc1facdb1e605c4c2f99281c8118c22c9895ffa846d8fb61aa",
            "MIT",
            "dac-44khz-8kbps/weights.pth",
        ),
    }
}
