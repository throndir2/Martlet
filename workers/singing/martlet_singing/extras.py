"""The singing models the owner may add, each in its own process: HeartMuLa, DiffRhythm and YuE write music; Vevo2 and
Seed-VC match the voice.

Each model has its own packages over the base image's (in $MARTLET_SINGING_EXTRAS/<name>, put first on the path by the
worker) and its own source checkout ($MARTLET_SINGING_SOURCES/<name>), so it never meets ACE-Step's or another model's
versions. The worker runs

    python -m martlet_singing.extras --engine heartmula --models DIR --device cuda:0

and writes one JSON job on stdin:
  music  {"lyrics", "style", "duration", "seed", "language", "out"}: writes the song to out (a WAV at the model's own rate,
         stereo or mono) and answers {"rate", "load_seconds", "generate_seconds", "peak_vram_mib"}.
  match  {"vocals", "reference", "out"}: mono 48 kHz vocals and the voice's recording at its own rate; writes mono 48 kHz
         vocals as long as the input and answers {"shift_semitones", "singer_hz", "voice_hz", "load_seconds",
         "convert_seconds", "peak_vram_mib"}.
or {"error": {code, summary}}. One job per process: every model loads, makes one song and ends, which frees its memory.
Everything the models print goes to stderr.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys
import tempfile
import time
import types
from pathlib import Path
from typing import Any

from martlet_singing import audio
from martlet_singing.engines import SongError, _fit, _resample, _source_root, _split_points

MUSIC = ("heartmula", "diffrhythm", "yue")
MATCHES = ("vevo2", "seedvc")


def octave_shift(speaking_hz: float, singing_hz: float) -> int:
    """Whole octaves that bring the singer's median pitch nearest to four semitones above the voice's speaking pitch (as
    martlet_singing.match does for SoulX-Singer and VevoSing)."""
    return 12 * round((12 * math.log2(speaking_hz / singing_hz) + 4) / 12)


def _on_path(*roots: Path) -> None:
    for root in reversed(roots):
        if str(root) not in sys.path:
            sys.path.insert(0, str(root))


def _free() -> None:
    import gc

    import torch

    gc.collect()
    if torch.cuda.is_available():
        torch.cuda.empty_cache()


def _card_gib(device: str) -> float:
    import torch

    if not torch.cuda.is_available():
        return 0.0
    return torch.cuda.get_device_properties(torch.device(device)).total_memory / 2**30


def _stub(name: str, **attributes: Any) -> None:
    """A training-only module a model's package imports at load time but inference never uses."""
    if name in sys.modules:
        return
    module = types.ModuleType(name)
    module.__dict__.update(attributes)
    sys.modules[name] = module


def _no_compile() -> None:
    # torch.compile needs a C compiler the image doesn't have; the models run eagerly.
    import torch

    torch.compile = lambda model=None, *args, **kwargs: model if model is not None else (lambda m: m)


# ------------------------------------------------------------------ music


def lrc(lyrics: str, seconds: float) -> str:
    """DiffRhythm's timed lyrics (LRC) from plain lyrics: the sung lines spread over the song after a short intro and
    before a short outro, with a little more room where a section starts."""
    lines = audio.lyric_lines(lyrics)
    if not lines:
        return ""
    start, end = min(8.0, seconds * 0.08), seconds * 0.92
    weights = [1.5 if i > 0 and lines[i][0] != lines[i - 1][0] else 1.0 for i in range(len(lines))]
    step = (end - start) / sum(weights)
    at, out = start, []
    for (_, text), weight in zip(lines, weights):
        if weight > 1.0:
            at += step * (weight - 1.0)
        out.append(f"[{int(at // 60):02d}:{at % 60:05.2f}]{text}")
        at += step
    return "\n".join(out)


def heartmula(models: Path, device: str, job: dict[str, Any]) -> tuple[Any, int, dict[str, Any]]:
    """HeartMuLa-oss-3B with HeartCodec: one model on the card at a time (lazy loading), bf16, 48 kHz."""
    import torch

    _no_compile()
    _on_path(_source_root("heartlib") / "src")
    from heartlib import HeartMuLaGenPipeline

    started = time.perf_counter()
    pipe = HeartMuLaGenPipeline.from_pretrained(str(models / "heartmula" / "ckpt"), device=torch.device(device),
                                                dtype={"mula": torch.bfloat16, "codec": torch.float32}, version="3B",
                                                lazy_load=True)
    loaded = time.perf_counter() - started
    torch.manual_seed(int(job["seed"]) % 2**63)
    started = time.perf_counter()
    with torch.no_grad():
        inputs = pipe.preprocess({"lyrics": job["lyrics"], "tags": job["style"]}, cfg_scale=1.5)
        frames = pipe._forward(inputs, max_audio_length_ms=int(job["duration"] * 1000), temperature=1.0, topk=50,
                               cfg_scale=1.5)["frames"]
        wav = pipe.codec.detokenize(frames.to(pipe.codec_device)).float().cpu().numpy()
    return wav, 48_000, {"load_seconds": round(loaded, 2), "generate_seconds": round(time.perf_counter() - started, 2)}


def diffrhythm(models: Path, device: str, job: dict[str, Any]) -> tuple[Any, int, dict[str, Any]]:
    """DiffRhythm 1.2 full (96-285 s; a shorter song is made at 96 s and cut), its style from the caption through
    MuQ-MuLan, 44.1 kHz stereo."""
    import json as _json

    import torch

    root = _source_root("diffrhythm")
    _on_path(root, root / "infer")
    os.chdir(root)  # its tokenizer, config and negative style prompt load from relative paths
    _stub("wandb")
    _stub("ema_pytorch", EMA=object)
    from infer_utils import (CNENTokenizer, decode_audio, get_lrc_token, get_negative_style_prompt, get_reference_latent,
                             get_style_prompt, load_checkpoint)
    from model.cfm import CFM
    from model.dit import DiT
    from muq import MuQMuLan

    seconds = float(job["duration"])
    length = int(min(285, max(96, math.ceil(seconds))))
    max_frames = 6144
    started = time.perf_counter()
    config = _json.loads((root / "config" / "diffrhythm-1b.json").read_text(encoding="utf-8"))
    cfm = CFM(transformer=DiT(**config["model"], max_frames=max_frames), num_channels=config["model"]["mel_dim"],
              max_frames=max_frames).to(device)
    cfm = load_checkpoint(cfm, str(models / "diffrhythm" / "cfm_model.pt"), device=device, use_ema=False)
    tokenizer = CNENTokenizer()
    muq = MuQMuLan.from_pretrained("OpenMuQ/MuQ-MuLan-large", cache_dir=str(models / "diffrhythm" / "hf")).to(device).eval()
    vae = torch.jit.load(str(models / "diffrhythm" / "vae_model.pt"), map_location="cpu").to(device)
    loaded = time.perf_counter() - started

    started = time.perf_counter()
    torch.manual_seed(int(job["seed"]) % 2**63)
    with torch.inference_mode():
        lrc_prompt, start_time, end_frame, song_duration = get_lrc_token(max_frames, lrc(job["lyrics"], length), tokenizer,
                                                                         length, device)
        style_prompt = get_style_prompt(muq, prompt=job["style"])
        del muq
        _free()
        negative = get_negative_style_prompt(device)
        latent_prompt, pred_frames = get_reference_latent(device, max_frames, False, None, None, vae)
        latents, _ = cfm.sample(cond=latent_prompt, text=lrc_prompt, duration=end_frame, style_prompt=style_prompt,
                                max_duration=end_frame, song_duration=song_duration, negative_style_prompt=negative,
                                steps=32, cfg_strength=4.0, start_time=start_time, latent_pred_segments=pred_frames,
                                batch_infer_num=1)
        del cfm
        _free()
        output = decode_audio(latents[0].to(torch.float32).transpose(1, 2), vae, chunked=True)
    wav = output[0].float().cpu().numpy()
    peak = float(abs(wav).max()) or 1.0
    wav = wav / peak * 0.95
    frames = min(wav.shape[-1], int(seconds * 44_100))
    wav = wav[:, :frames]
    fade = min(frames, 2 * 44_100)
    if length > seconds and fade:
        import numpy as np

        wav[:, frames - fade:] *= np.linspace(1.0, 0.0, fade, dtype=wav.dtype)
    return wav, 44_100, {"load_seconds": round(loaded, 2), "generate_seconds": round(time.perf_counter() - started, 2)}


def yue(models: Path, device: str, job: dict[str, Any]) -> tuple[Any, int, dict[str, Any]]:
    """YuE2-3B with its VAE, 48 kHz stereo. The lyrics set the song's length. On a card under 22 GB its language model
    uses fp8 weights and leaves the card while the song is decoded."""
    from yue2 import YuE2Pipeline

    card = _card_gib(device)
    small = card < 22
    started = time.perf_counter()
    pipe = YuE2Pipeline(models / "yue" / "YuE2-3B", models / "yue" / "YuE2-Vae", device=device,
                        memory_budget_gib=max(card, 6.0), quantization="fp8" if small else "none", offload_ar=small,
                        verify_hashes=False, progress=False)
    loaded = time.perf_counter() - started
    started = time.perf_counter()
    song = pipe(style=job["style"], lyrics=job["lyrics"], seed=int(job["seed"]) % 2**63, cot="full")
    wav = song.audio
    wav = wav.T if getattr(wav, "ndim", 1) == 2 else wav[None, :]
    return wav, int(song.sample_rate), {"load_seconds": round(loaded, 2),
                                        "generate_seconds": round(time.perf_counter() - started, 2)}


# ------------------------------------------------------------------ voice match


def vevo2(models: Path, device: str, vocals48, reference, reference_rate: int) -> tuple[Any, dict[str, Any]]:
    """Amphion's Vevo2 flow matching (timbre only: the tune and timing stay), at 24 kHz, piece by piece between the sung
    phrases, the singing moved by whole octaves towards the voice's range as VevoSing does."""
    import librosa
    import numpy as np
    import soundfile
    import torch

    amphion = _source_root("amphion")
    _on_path(amphion, Path(__file__).resolve().parent / "shims")
    os.environ.setdefault("XDG_CACHE_HOME", str(models))  # Whisper medium, as VevoSing loads it
    folder = models / "vevo2"
    cwd = os.getcwd()
    os.chdir(amphion)  # its config names whisper_stats.pt relative to the checkout
    started = time.perf_counter()
    from models.svc.vevo2.vevo2_utils import Vevo2InferencePipeline

    pipeline = Vevo2InferencePipeline(
        content_style_tokenizer_ckpt_path=str(folder / "tokenizer/contentstyle_fvq16384_12.5hz"),
        fmt_cfg_path=str(folder / "acoustic_modeling/fm_emilia101k_singnet7k_repa/config.json"),
        fmt_ckpt_path=str(folder / "acoustic_modeling/fm_emilia101k_singnet7k_repa"),
        vocoder_cfg_path=str(folder / "vocoder/config.json"), vocoder_ckpt_path=str(folder / "vocoder"),
        device=torch.device(device))
    loaded = time.perf_counter() - started
    source = _resample(vocals48, audio.OUTPUT_RATE, 24_000)
    prompt = _resample(reference, reference_rate, 24_000)
    src_f0 = librosa.yin(source, fmin=70, fmax=1000, sr=24_000)
    ref_f0 = librosa.yin(prompt, fmin=70, fmax=1000, sr=24_000)
    octave = octave_shift(float(np.median(ref_f0)), float(np.median(src_f0)))
    converted = np.zeros_like(source)
    started = time.perf_counter()
    try:
        with tempfile.TemporaryDirectory() as temporary, torch.no_grad():
            ref_path, piece_path = Path(temporary) / "ref.wav", Path(temporary) / "piece.wav"
            soundfile.write(str(ref_path), prompt.astype(np.float32), 24_000)
            for a, b in _split_points(source, 24_000):
                piece = source[a:b]
                if np.max(np.abs(piece)) < 1e-3:
                    continue
                if octave:
                    piece = librosa.effects.pitch_shift(piece, sr=24_000, n_steps=octave)
                soundfile.write(str(piece_path), piece.astype(np.float32), 24_000)
                out = pipeline.inference_fm(src_wav_path=str(piece_path), timbre_ref_wav_path=str(ref_path),
                                            use_pitch_shift=False, flow_matching_steps=32)
                out = (out.detach().float().cpu().numpy() if hasattr(out, "detach") else np.asarray(out)).reshape(-1)[: b - a]
                converted[a:a + len(out)] = out
    finally:
        os.chdir(cwd)
    return _fit(_resample(converted, 24_000, audio.OUTPUT_RATE), vocals48.shape[-1]), {
        "shift_semitones": octave, "load_seconds": round(loaded, 2), "convert_seconds": round(time.perf_counter() - started, 2),
        "singer_hz": round(float(np.median(src_f0)), 1), "voice_hz": round(float(np.median(ref_f0)), 1)}


def seedvc(models: Path, device: str, vocals48, reference, reference_rate: int) -> tuple[Any, dict[str, Any]]:
    """Seed-VC's singing model (44.1 kHz, with pitch: Whisper small content, CAMPPlus style, RMVPE pitch, BigVGAN), 30
    diffusion steps, the singing moved by whole octaves towards the voice's range, as Seed-VC's own inference does it."""
    import functools

    import numpy as np
    import torch
    import torchaudio
    import yaml

    root = _source_root("seed-vc")
    _on_path(root)
    os.chdir(root)
    # Only its discrete-content path uses DAC's quantizer; the singing model is continuous.
    _stub("dac")
    _stub("dac.nn")
    _stub("dac.nn.quantize", VectorQuantize=object)
    folder = models / "seedvc"
    # Its checkpoints are pickled dictionaries (pinned by SHA-256), which torch.load refuses by default since PyTorch 2.6.
    torch.load = functools.partial(torch.load, weights_only=False)
    from modules.audio import mel_spectrogram
    from modules.bigvgan import bigvgan
    from modules.campplus.DTDNN import CAMPPlus
    from modules.commons import build_model, load_checkpoint, recursive_munch
    from modules.rmvpe import RMVPE
    from transformers import AutoFeatureExtractor, WhisperModel

    target = torch.device(device)
    started = time.perf_counter()
    config = yaml.safe_load((folder / "config_dit_mel_seed_uvit_whisper_base_f0_44k.yml").read_text(encoding="utf-8"))
    params = recursive_munch(config["model_params"])
    params.dit_type = "DiT"
    model = build_model(params, stage="DiT")
    model, _, _, _ = load_checkpoint(model, None, str(folder / "DiT_seed_v2_uvit_whisper_base_f0_44k_bigvgan_pruned_ft_ema_v2.pth"),
                                     load_only_params=True, ignore_modules=[], is_distributed=False)
    for key in model:
        model[key].eval()
        model[key].to(target)
    model.cfm.estimator.setup_caches(max_batch_size=1, max_seq_length=8192)
    f0_extractor = RMVPE(str(folder / "rmvpe.pt"), is_half=False, device=target)
    campplus = CAMPPlus(feat_dim=80, embedding_size=192)
    campplus.load_state_dict(torch.load(str(folder / "campplus_cn_common.bin"), map_location="cpu"))
    campplus.eval().to(target)
    vocoder = bigvgan.BigVGAN.from_pretrained(str(folder / "bigvgan"), use_cuda_kernel=False)
    vocoder.remove_weight_norm()
    vocoder = vocoder.eval().to(target)
    whisper = WhisperModel.from_pretrained(str(folder / "whisper-small"), torch_dtype=torch.float16).to(target)
    del whisper.decoder
    extractor = AutoFeatureExtractor.from_pretrained(str(folder / "whisper-small"))
    loaded = time.perf_counter() - started

    def semantic(waves_16k):
        inputs = extractor([waves_16k.squeeze(0).cpu().numpy()], return_tensors="pt", return_attention_mask=True)
        features = whisper._mask_input_features(inputs.input_features, attention_mask=inputs.attention_mask).to(target)
        out = whisper.encoder(features.to(whisper.encoder.dtype), head_mask=None, output_attentions=False,
                              output_hidden_states=False, return_dict=True)
        return out.last_hidden_state.to(torch.float32)[:, :waves_16k.size(-1) // 320 + 1]

    spect = config["preprocess_params"]["spect_params"]
    sr, hop = 44_100, 512
    to_mel = functools.partial(mel_spectrogram, n_fft=spect["n_fft"], win_size=spect["win_length"], hop_size=hop,
                               num_mels=spect["n_mels"], sampling_rate=sr, fmin=0, fmax=None, center=False)
    started = time.perf_counter()
    with torch.no_grad():
        source = torch.from_numpy(_resample(vocals48, audio.OUTPUT_RATE, sr).astype(np.float32))[None].to(target)
        ref = torch.from_numpy(_resample(reference, reference_rate, sr)[: sr * 25].astype(np.float32))[None].to(target)
        source16 = torchaudio.functional.resample(source, sr, 16_000)
        ref16 = torchaudio.functional.resample(ref, sr, 16_000)
        # Whisper hears 30 s at a time: longer vocals go in 30 s windows that overlap by 5 s.
        if source16.size(-1) <= 16_000 * 30:
            content = semantic(source16)
        else:
            parts, buffer, done = [], None, 0
            while done < source16.size(-1):
                chunk = source16[:, done:done + 16_000 * 30] if buffer is None else torch.cat(
                    [buffer, source16[:, done:done + 16_000 * 25]], dim=-1)
                features = semantic(chunk)
                parts.append(features if done == 0 else features[:, 50 * 5:])
                buffer = chunk[:, -16_000 * 5:]
                done += 30 * 16_000 if done == 0 else chunk.size(-1) - 16_000 * 5
            content = torch.cat(parts, dim=1)
        prompt_content = semantic(ref16)
        mel, mel2 = to_mel(source.float()), to_mel(ref.float())
        fbank = torchaudio.compliance.kaldi.fbank(ref16, num_mel_bins=80, dither=0, sample_frequency=16_000)
        style = campplus((fbank - fbank.mean(dim=0, keepdim=True)).unsqueeze(0))
        f0_ref = torch.from_numpy(f0_extractor.infer_from_audio(ref16[0], thred=0.03)).to(target)[None]
        f0_src = torch.from_numpy(f0_extractor.infer_from_audio(source16[0], thred=0.03)).to(target)[None]
        voiced_ref, voiced_src = f0_ref[f0_ref > 1], f0_src[f0_src > 1]
        shift = 0
        if voiced_ref.numel() and voiced_src.numel():
            shift = octave_shift(float(voiced_ref.median()), float(voiced_src.median()))
        shifted = f0_src.clone()
        if shift:
            shifted[f0_src > 1] = f0_src[f0_src > 1] * 2 ** (shift / 12)
        cond = model.length_regulator(content, ylens=torch.LongTensor([mel.size(2)]).to(target), n_quantizers=3, f0=shifted)[0]
        prompt = model.length_regulator(prompt_content, ylens=torch.LongTensor([mel2.size(2)]).to(target), n_quantizers=3,
                                        f0=f0_ref)[0]
        window = sr // hop * 30 - mel2.size(2)
        overlap_frames, overlap = 16, 16 * hop
        pieces, previous, done = [], None, 0
        while done < cond.size(1):
            chunk = cond[:, done:done + window]
            last = done + window >= cond.size(1)
            joined = torch.cat([prompt, chunk], dim=1)
            with torch.autocast(device_type=target.type, dtype=torch.float16):
                mel_out = model.cfm.inference(joined, torch.LongTensor([joined.size(1)]).to(target), mel2, style, None, 30,
                                              inference_cfg_rate=0.7)[:, :, mel2.size(-1):]
            wave = vocoder(mel_out.float()).squeeze()[None, :][0].cpu().numpy()
            if previous is not None:
                fade_in = np.cos(np.linspace(np.pi / 2, 0, overlap)) ** 2
                head = min(overlap, len(wave))
                wave[:head] = wave[:head] * fade_in[:head] + (previous * np.cos(np.linspace(0, np.pi / 2, overlap)) ** 2)[:head]
            if last:
                pieces.append(wave)
                break
            pieces.append(wave[:-overlap])
            previous = wave[-overlap:]
            done += mel_out.size(2) - overlap_frames
        converted = np.concatenate(pieces).astype(np.float32)
    return _fit(_resample(converted, sr, audio.OUTPUT_RATE), vocals48.shape[-1]), {
        "shift_semitones": shift, "load_seconds": round(loaded, 2), "convert_seconds": round(time.perf_counter() - started, 2),
        "singer_hz": round(float(voiced_src.median()), 1) if voiced_src.numel() else None,
        "voice_hz": round(float(voiced_ref.median()), 1) if voiced_ref.numel() else None}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="martlet_singing.extras")
    parser.add_argument("--engine", choices=MUSIC + MATCHES, required=True)
    parser.add_argument("--models", type=Path, required=True)
    parser.add_argument("--device", default="cuda:0")
    arguments = parser.parse_args(argv)
    answers = os.fdopen(os.dup(sys.stdout.fileno()), "w", encoding="utf-8")
    os.dup2(sys.stderr.fileno(), sys.stdout.fileno())

    import numpy as np
    import soundfile
    import torch

    line = sys.stdin.readline()
    started = time.perf_counter()
    try:
        job = json.loads(line)
        if torch.cuda.is_available():
            torch.cuda.reset_peak_memory_stats()
        if arguments.engine in MUSIC:
            make = {"heartmula": heartmula, "diffrhythm": diffrhythm, "yue": yue}[arguments.engine]
            wav, rate, summary = make(arguments.models, arguments.device, job)
            wav = np.asarray(wav, dtype=np.float32)
            wav = wav[None, :] if wav.ndim == 1 else wav.reshape(wav.shape[-2], wav.shape[-1])
            soundfile.write(job["out"], wav.T, rate, subtype="FLOAT")
            summary["rate"] = rate
        else:
            vocals, rate = soundfile.read(job["vocals"], dtype="float32", always_2d=True)
            if rate != audio.OUTPUT_RATE:
                raise SongError("song.failed", "The vocals must be 48 kHz.")
            reference, reference_rate = soundfile.read(job["reference"], dtype="float32", always_2d=True)
            convert = vevo2 if arguments.engine == "vevo2" else seedvc
            converted, summary = convert(arguments.models, arguments.device, vocals.mean(axis=1), reference.mean(axis=1),
                                         reference_rate)
            soundfile.write(job["out"], np.asarray(converted, dtype=np.float32), audio.OUTPUT_RATE, subtype="FLOAT")
            summary["transformers"] = __import__("transformers").__version__
        summary["peak_vram_mib"] = round(torch.cuda.max_memory_reserved() / 2**20) if torch.cuda.is_available() else 0
        summary["process_seconds"] = round(time.perf_counter() - started, 2)
        answers.write(json.dumps(summary) + "\n")
    except SongError as error:
        answers.write(json.dumps({"error": {"code": error.code, "summary": error.summary}}) + "\n")
    except Exception as error:  # noqa: BLE001 - the worker reports the failure; the details go to the log
        import traceback

        traceback.print_exc()
        answers.write(json.dumps({"error": {"code": "song.failed",
                                            "summary": f"{arguments.engine} failed: {type(error).__name__}"[:200]}}) + "\n")
    answers.flush()
    # As martlet_singing.match: end without the interpreter's teardown, which can abort with CUDA loaded.
    os._exit(0)


if __name__ == "__main__":
    sys.exit(main())
