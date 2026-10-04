"""Voice matching in its own process: SoulX-Singer-SVC or VevoSing turns the separated vocals into the chosen voice.

Both converters were written against Transformers 4.4x (SoulX-Singer's Llama decoder predates the rotary
"position_embeddings" argument that ACE-Step's Transformers 4.57 requires), so the worker runs this stage with its own
Transformers overlay (MARTLET_SINGING_MATCH_SITE, Transformers 4.46.3) or interpreter (MARTLET_SINGING_MATCH_PYTHON). The
process exits after the song, which also returns all of its memory.

    python -m martlet_singing.match --engine soulx --models DIR --device cuda:0 --vocals in.wav --reference ref.wav --out out.wav

The vocals are mono 48 kHz; the reference is the voice's recording at its own rate; the result is mono 48 kHz, as long as the
vocals. The last stdout line is a JSON summary.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys
import time
from pathlib import Path
from typing import Any

from martlet_singing import audio
from martlet_singing.engines import (SongError, _amphion_path, _amphion_root, _fit, _resample, _soulx_build_model, _soulx_path,
                                     _soulx_root, _split_points)


def _free() -> None:
    import gc

    import torch

    gc.collect()
    if torch.cuda.is_available():
        torch.cuda.empty_cache()


def octave_shift(speaking_hz: float, singing_hz: float) -> int:
    """Whole octaves (so the backing never needs re-pitching) that bring the singer's median pitch nearest to about four
    semitones above the voice's speaking pitch, where people's singing usually sits: a male singer for a higher voice goes
    up an octave, a singer already in the voice's range stays."""
    raw = 12 * math.log2(speaking_hz / singing_hz)
    return 12 * round((raw + 4) / 12)


def _f0(extractor: Any, samples, rate: int):
    """RMVPE pitch (SoulX-Singer's extractor) at the 24 kHz / hop 480 frame rate SoulX expects."""
    import tempfile

    import numpy as np
    import soundfile

    with tempfile.TemporaryDirectory() as folder:
        wav = Path(folder) / "voice.wav"
        soundfile.write(str(wav), samples, rate)
        f0 = extractor.process(str(wav), f0_path=str(Path(folder) / "f0.npy"))
    return np.asarray(f0, dtype=np.float32)


def soulx(models: Path, device: str, vocals48, reference, reference_rate: int) -> tuple[Any, dict[str, Any]]:
    import numpy as np
    import torch

    _soulx_path()
    from preprocess.tools.f0_extraction import F0Extractor
    from soulxsinger.utils.file_utils import load_config

    root = _soulx_root()
    config = load_config(str(root / "soulxsinger/config/soulxsinger.yaml"))
    rate = config.audio.sample_rate  # 24 kHz
    target = _resample(vocals48, audio.OUTPUT_RATE, rate)
    prompt = _resample(reference, reference_rate, rate)
    extractor = F0Extractor(model_path=str(models / "soulx/rmvpe/rmvpe.pt"), device=device, verbose=False)
    gt_f0 = _f0(extractor, target, rate)
    pt_f0 = _f0(extractor, prompt, rate)
    del extractor
    _free()
    started = time.perf_counter()
    model = _soulx_build_model()(str(models / "soulx/model-svc.pt"), config, device="cpu", use_fp16=False)
    model.half()
    model.mel.float()
    loaded = time.perf_counter() - started
    voiced_gt, voiced_pt = gt_f0[gt_f0 > 0], pt_f0[pt_f0 > 0]
    shift = 0
    if voiced_gt.size and voiced_pt.size:
        shift = octave_shift(float(np.median(voiced_pt)), float(np.median(voiced_gt)))
    torch.manual_seed(42)
    started = time.perf_counter()
    with torch.no_grad():
        model.to(device)
        pt = torch.from_numpy(prompt).float()[None].to(device)
        gt = torch.from_numpy(target).float()[None].to(device)
        generated, _ = model.infer(pt_wav=pt, gt_wav=gt, pt_f0=torch.from_numpy(pt_f0)[None].to(device),
                                   gt_f0=torch.from_numpy(gt_f0)[None].to(device), auto_shift=False, pitch_shift=shift,
                                   n_steps=32, cfg=3.0, use_fp16=device.startswith("cuda"))
        converted = generated.squeeze().float().cpu().numpy()
    converting = time.perf_counter() - started
    return _fit(_resample(converted, rate, audio.OUTPUT_RATE), vocals48.shape[-1]), {
        "shift_semitones": shift, "load_seconds": round(loaded, 2), "convert_seconds": round(converting, 2),
        "singer_hz": round(float(np.median(voiced_gt)), 1) if voiced_gt.size else None,
        "voice_hz": round(float(np.median(voiced_pt)), 1) if voiced_pt.size else None}


def vevosing(models: Path, device: str, vocals48, reference, reference_rate: int) -> tuple[Any, dict[str, Any]]:
    import numpy as np
    import torch

    _amphion_path()
    os.environ.setdefault("XDG_CACHE_HOME", str(models))
    import librosa
    import torchaudio
    from models.svc.vevosing.vevosing_utils import VevosingInferencePipeline

    amphion = _amphion_root()
    started = time.perf_counter()
    cwd = os.getcwd()
    os.chdir(amphion)
    try:
        pipeline = VevosingInferencePipeline(
            content_style_tokenizer_ckpt_path=str(models / "vevo/tokenizer/contentstyle_fvq16384_12.5hz"),
            fmt_cfg_path=str(amphion / "models/svc/vevosing/config/fm_emilia101k_singnet7k.json"),
            fmt_ckpt_path=str(models / "vevo/acoustic_modeling/fm_emilia101k_singnet7k"),
            vocoder_cfg_path=str(amphion / "models/svc/vevosing/config/vocoder.json"),
            vocoder_ckpt_path=str(models / "vevo/acoustic_modeling/Vocoder"),
            device=torch.device(device))
    finally:
        os.chdir(cwd)
    loaded = time.perf_counter() - started
    source = _resample(vocals48, audio.OUTPUT_RATE, 24_000)
    prompt = _resample(reference, reference_rate, 24_000)
    src_f0 = librosa.yin(source, fmin=70, fmax=1000, sr=24_000)
    ref_f0 = librosa.yin(prompt, fmin=70, fmax=1000, sr=24_000)
    octave = octave_shift(float(np.median(ref_f0)), float(np.median(src_f0)))
    converted = np.zeros_like(source)
    started = time.perf_counter()
    with torch.no_grad():
        ref24 = torch.from_numpy(prompt).float()[None].to(device)
        ref16 = torchaudio.functional.resample(ref24, 24_000, 16_000)
        ref_codecs = pipeline.extract_coco_codec("content_style", ref16, prompt.astype(np.float32))
        ref_mels = pipeline.extract_mel_feature(ref24)
        for a, b in _split_points(source, 24_000):
            piece = source[a:b]
            if np.max(np.abs(piece)) < 1e-3:
                continue
            if octave:
                piece = librosa.effects.pitch_shift(piece, sr=24_000, n_steps=octave)
            src24 = torch.from_numpy(piece).float()[None].to(device)
            src16 = torchaudio.functional.resample(src24, 24_000, 16_000)
            src_codecs = pipeline.extract_coco_codec("content_style", src16, piece.astype(np.float32))
            cond = pipeline.fmt_model.cond_emb(torch.cat([ref_codecs, src_codecs], dim=1))
            if pipeline.fmt_model.do_resampling:
                cond = pipeline.fmt_model.resampling_layers(cond.transpose(1, 2)).transpose(1, 2)
            total = ref_mels.shape[1] + pipeline.extract_mel_feature(src24).shape[1]
            cond = cond[:, :total, :] if cond.shape[1] >= total else torch.cat(
                [cond, cond[:, -1:, :].repeat(1, total - cond.shape[1], 1)], dim=1)
            mel = pipeline.fmt_model.reverse_diffusion(cond=cond, prompt=ref_mels, n_timesteps=32)
            out = pipeline.vocoder_model(mel.transpose(1, 2)).detach().float().cpu().numpy()[0, 0][: b - a]
            converted[a:a + len(out)] = out
    converting = time.perf_counter() - started
    return _fit(_resample(converted, 24_000, audio.OUTPUT_RATE), vocals48.shape[-1]), {
        "shift_semitones": octave, "load_seconds": round(loaded, 2), "convert_seconds": round(converting, 2),
        "singer_hz": round(float(np.median(src_f0)), 1), "voice_hz": round(float(np.median(ref_f0)), 1)}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="martlet_singing.match")
    parser.add_argument("--engine", choices=("soulx", "vevosing"), required=True)
    parser.add_argument("--models", type=Path, required=True)
    parser.add_argument("--device", default="cuda:0")
    parser.add_argument("--vocals", type=Path, required=True)
    parser.add_argument("--reference", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    arguments = parser.parse_args(argv)

    import numpy as np
    import soundfile
    import torch

    vocals, rate = soundfile.read(str(arguments.vocals), dtype="float32", always_2d=True)
    if rate != audio.OUTPUT_RATE:
        raise SystemExit("vocals must be 48 kHz")
    reference, reference_rate = soundfile.read(str(arguments.reference), dtype="float32", always_2d=True)
    convert = soulx if arguments.engine == "soulx" else vevosing
    try:
        converted, summary = convert(arguments.models, arguments.device, vocals.mean(axis=1), reference.mean(axis=1),
                                     reference_rate)
    except SongError as error:
        print(json.dumps({"error": {"code": error.code, "summary": error.summary}}), flush=True)
        return 2
    soundfile.write(str(arguments.out), np.asarray(converted, dtype=np.float32), audio.OUTPUT_RATE, subtype="FLOAT")
    summary["peak_vram_mib"] = round(torch.cuda.max_memory_reserved() / 2**20) if torch.cuda.is_available() else 0
    summary["transformers"] = __import__("transformers").__version__
    print(json.dumps(summary), flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
