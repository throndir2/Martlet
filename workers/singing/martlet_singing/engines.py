"""The song engines the singing worker runs: the real pipeline (ACE-Step 1.5 -> Demucs vocals -> SoulX-Singer-SVC or
VevoSing -> mix -> lyric and beat timing) and the deterministic FIXTURE - NOT AI tone engine for plumbing checks.

An engine writes mix.pcm (48 kHz stereo), vocals.pcm (48 kHz mono) and backing.pcm (48 kHz stereo) as signed 16-bit
little-endian PCM into the job directory and returns the song's metadata. Stages run one at a time and each releases the
graphics card's memory it used before the next starts.
"""

from __future__ import annotations

import array
import hashlib
import math
import os
import random
import sys
import time
from pathlib import Path
from typing import Any, Callable

from martlet_singing import audio, pins

Report = Callable[[str, float], None]
Canceled = Callable[[], bool]

FIXTURE_STAGE_SECONDS = float(os.environ.get("MARTLET_SINGING_FIXTURE_STAGE_SECONDS", "0.05"))


class SongError(Exception):
    def __init__(self, code: str, summary: str) -> None:
        super().__init__(summary)
        self.code = code
        self.summary = summary


class JobCanceled(Exception):
    pass


def _check(canceled: Canceled) -> None:
    if canceled():
        raise JobCanceled()


class Timer:
    def __init__(self) -> None:
        self.seconds: dict[str, float] = {}

    def add(self, stage: str, started: float) -> None:
        self.seconds[stage] = round(self.seconds.get(stage, 0.0) + time.perf_counter() - started, 3)

    def as_list(self) -> list[dict[str, Any]]:
        return [{"stage": stage, "seconds": seconds} for stage, seconds in self.seconds.items()]


# ------------------------------------------------------------------ FIXTURE - NOT AI


class FixtureEngine:
    """FIXTURE - NOT AI: a sine melody (one note per word, pitched from the voice) over sine pads with a click on every
    beat; every line starts on a downbeat of a straight 4/4 grid. Mirrors Martlet.Core.Singing.FixtureSongMaker."""

    identity_generator = "FIXTURE - NOT AI tone song"

    def release(self) -> None:
        return

    def run(self, job: dict[str, Any], directory: Path, report: Report, canceled: Canceled) -> dict[str, Any]:
        timer = Timer()
        for index, stage in enumerate(("writing_music", "separating", "matching_voice", "mixing", "aligning")):
            _check(canceled)
            report(stage, index / 5)
            started = time.perf_counter()
            time.sleep(FIXTURE_STAGE_SECONDS)
            timer.add(stage, started)
        started = time.perf_counter()
        meta = self.compose(job, directory)
        timer.add("aligning", started)
        meta["timings"] = timer.as_list()
        return meta

    @staticmethod
    def compose(job: dict[str, Any], directory: Path) -> dict[str, Any]:
        rate = audio.OUTPUT_RATE
        duration = int(job["duration_seconds"])
        frames = duration * rate
        lines = audio.lyric_lines(job["lyrics"]) or [("", job["lyrics"].strip())]
        seed = job.get("seed")
        if seed is None:
            seed = int.from_bytes(hashlib.sha256((job["lyrics"] + "\n" + job["style"]).encode()).digest()[:4], "little")
        voice = hashlib.sha256(job["voice_id"].encode()).digest()
        root = 196.0 * 2 ** ((voice[0] % 12) / 12)
        bpm = int(job.get("bpm") or 96)
        beat = 60.0 / bpm
        bar = beat * 4
        bars_per_line = max(1, min(2, int((duration - bar) / bar / (len(lines) + 1))))
        scale = (0, 2, 4, 5, 7, 9, 11, 12)
        chords = ((0, 4, 7), (9, 12, 16), (5, 9, 12), (7, 11, 14))
        beats = [round(b * beat, 6) for b in range(int(duration / beat) + 1) if b * beat < duration]
        downbeats = beats[::4]
        lyric = []
        words = []
        for number, (section, text) in enumerate(lines):
            start = bar * (1 + number * bars_per_line)
            if start >= duration - 1:
                break
            end = min(start + bar * bars_per_line - beat / 2, duration)
            lyric.append({"start": round(start, 6), "end": round(end, 6), "text": text, "section": section})
            # One sung note per word, spread evenly over the line.
            parts = text.split()
            each = (end - start) / len(parts)
            for n, part in enumerate(parts):
                words.append({"start": round(start + n * each, 6), "end": round(start + (n + 1) * each - 0.02, 6),
                              "text": part, "line": len(lyric) - 1})

        vocals = array.array("h", bytes(frames * 2))
        backing = array.array("h", bytes(frames * 4))
        mix = array.array("h", bytes(frames * 4))
        two_pi = 2 * math.pi
        pad_freqs = [[root / 2 * 2 ** (step / 12) for step in chord] for chord in chords]
        for n in range(frames):
            t = n / rate
            chord = pad_freqs[int(t / bar) % 4]
            pad = 0.06 * (math.sin(two_pi * chord[0] * t) + math.sin(two_pi * chord[1] * t) + math.sin(two_pi * chord[2] * t))
            beat_index = int(t / beat)
            since = t - beat_index * beat
            if since < 0.03:
                pad += math.sin(two_pi * 1000 * t) * (1 - since / 0.03) * (0.25 if beat_index % 4 == 0 else 0.12)
            sung = 0.0
            for number, word in enumerate(words):
                if word["start"] <= t < word["end"]:
                    degree = scale[(seed + number * 3 + word["line"]) % len(scale)]
                    phase = t - word["start"]
                    envelope = min(1.0, phase / 0.03) * min(1.0, (word["end"] - t) / 0.05)
                    sung = 0.22 * envelope * math.sin(two_pi * root * 2 ** (degree / 12) * (1 + 0.004 * math.sin(two_pi * 5.5 * t)) * t)
                    break
            fade = min(1.0, (duration - t) / 1.0)
            pan = 0.2 * math.sin(two_pi * 0.1 * t)
            left, right, sung = pad * (1 + pan) * fade, pad * (1 - pan) * fade, sung * fade
            vocals[n] = _sample(sung)
            backing[2 * n], backing[2 * n + 1] = _sample(left), _sample(right)
            mix[2 * n], mix[2 * n + 1] = _sample(left + sung), _sample(right + sung)
        for name, samples in (("mix", mix), ("vocals", vocals), ("backing", backing)):
            if sys.byteorder != "little":
                samples.byteswap()
            (directory / f"{name}.pcm").write_bytes(samples.tobytes())
        return {
            "engine": {"generator": FixtureEngine.identity_generator, "separator": "FIXTURE - NOT AI",
                       "converter": "FIXTURE - NOT AI", "quality": job["quality"], "voice_match": job["voice_match"],
                       "fixture": True},
            "frames": frames, "seed": seed, "bpm": bpm, "key": job.get("key"), "beats_per_bar": 4,
            "beats": beats, "downbeats": downbeats, "lyrics": lyric,
            "words": words, "word_timing_source": "fixture", "lyric_timing_source": "fixture",
        }


def _sample(value: float) -> int:
    return int(round(max(-1.0, min(1.0, value)) * 32767))


# ------------------------------------------------------------------ the real pipeline


class SongEngine:
    """ACE-Step 1.5 writes the song; Demucs (htdemucs_ft's vocals model) separates the vocals; SoulX-Singer-SVC (or
    VevoSing) matches them to the voice's recording; the matched vocals go back over the backing. Models load on first
    use, stay in system memory between songs and use the graphics card only during their stage."""

    def __init__(self, models: Path, device: str, voice_matches: list[str]) -> None:
        self.models = models
        self.device = device
        self.voice_matches = voice_matches
        self.ace: dict[str, Any] = {}
        self.demucs: Any = None
        self.rmvpe: Any = None
        self.soulx: Any = None
        self.vevo: Any = None
        self.planner = os.environ.get("MARTLET_SINGING_PLANNER", "lm")  # "lm" or "none"
        self.lm_backend = os.environ.get("MARTLET_SINGING_LM_BACKEND", "pt")  # "pt" or "vllm"
        self.quantized: str | None = None
        # Keep models in system memory between songs (faster next song) unless memory is short; "release" frees each
        # stage's models as soon as it is done. MARTLET_SINGING_KEEP_MODELS: auto, keep or release.
        self.keep_models = os.environ.get("MARTLET_SINGING_KEEP_MODELS", "auto")

    # ---- lifecycle

    def release(self) -> None:
        self.ace.clear()
        self.demucs = self.rmvpe = self.soulx = self.vevo = None
        self._free()

    def _keep(self) -> bool:
        if self.keep_models in ("keep", "release"):
            return self.keep_models == "keep"
        try:
            import psutil

            return psutil.virtual_memory().available > 16 * 2**30
        except ImportError:
            return True

    def _free(self) -> None:
        import gc

        import torch

        gc.collect()
        if torch.cuda.is_available():
            torch.cuda.empty_cache()

    # ---- the job

    def run(self, job: dict[str, Any], directory: Path, report: Report, canceled: Canceled) -> dict[str, Any]:
        import numpy as np
        import torch

        timer = Timer()
        torch.cuda.reset_peak_memory_stats() if torch.cuda.is_available() else None
        seed = job.get("seed")
        if seed is None:
            seed = random.SystemRandom().randrange(0, 2**32)
        reference_pcm, reference_rate = audio.parse_reference(job["reference_audio"])
        reference = np.frombuffer(reference_pcm, dtype="<i2").astype(np.float32) / 32768.0

        report("writing_music", 0.0)
        mix48, plan, lrc_raw, sentences = self._write_music(job, seed, timer, report, canceled)
        _check(canceled)

        if not self._keep():
            self.ace.clear()
            self._free()
        report("separating", 0.45)
        started = time.perf_counter()
        vocals48, backing48 = self._separate(mix48)
        if not self._keep():
            self.demucs = None
            self._free()
        timer.add("separating", started)
        _check(canceled)

        report("matching_voice", 0.6)
        started = time.perf_counter()
        if job["voice_match"] == "vevosing":
            converted48 = self._match_vevo(vocals48, reference, reference_rate)
        else:
            converted48 = self._match_soulx(vocals48, reference, reference_rate, directory)
        if not self._keep():
            self.soulx = self.vevo = None
            self._free()
        timer.add("matching_voice", started)
        _check(canceled)

        report("mixing", 0.85)
        started = time.perf_counter()
        from martlet_singing import timing

        # The vocals track drives lip sync: only the sung phrases stay, so no backing bleed or conversion noise remains.
        converted48, bleed_db = timing.gate_vocals(converted48, vocals48.mean(axis=0), audio.OUTPUT_RATE)
        original_rms = float(np.sqrt(np.mean(vocals48.mean(axis=0) ** 2)) + 1e-9)
        converted48 = converted48 * (original_rms / (float(np.sqrt(np.mean(converted48 ** 2))) + 1e-9))
        mix = backing48 + converted48[None, :]
        peak = float(np.max(np.abs(mix))) if mix.size else 0.0
        if peak > 0.98:
            scale = 0.98 / peak
            mix, backing48, converted48 = mix * scale, backing48 * scale, converted48 * scale
        frames = mix.shape[1]
        _write_pcm(directory / "mix.pcm", mix)
        _write_pcm(directory / "backing.pcm", backing48)
        _write_pcm(directory / "vocals.pcm", converted48[None, :])
        timer.add("mixing", started)

        report("aligning", 0.92)
        started = time.perf_counter()
        bpm_planned = _number(plan.get("bpm"))
        beats_per_bar = int(_number(plan.get("timesignature")) or 4)
        if beats_per_bar not in (2, 3, 4, 6):
            beats_per_bar = 4
        grid = timing.beat_grid(backing48.mean(axis=0), audio.OUTPUT_RATE, bpm_planned, beats_per_bar)
        lines = timing.lyric_times(audio.lyric_lines(job["lyrics"]), lrc_raw, converted48, audio.OUTPUT_RATE, grid["downbeats"])
        words, word_source, word_metric = timing.word_times(audio.lyric_lines(job["lyrics"]), sentences, lines, converted48,
                                                            audio.OUTPUT_RATE)
        timer.add("aligning", started)

        peak_vram = round(torch.cuda.max_memory_reserved() / 2**20) if torch.cuda.is_available() else 0
        return {
            "engine": {"generator": pins.GENERATOR_IDS[job["quality"]], "separator": pins.SEPARATOR_ID,
                       "converter": pins.CONVERTER_IDS[job["voice_match"]], "quality": job["quality"],
                       "voice_match": job["voice_match"], "fixture": False,
                       "planner": self.planner, "lm_backend": self.lm_backend if self.planner == "lm" else None,
                       "quantization": self.quantized},
            "frames": frames, "seed": seed, "bpm": grid["bpm"] or bpm_planned, "planned_bpm": bpm_planned,
            "key": plan.get("keyscale") or job.get("key"), "beats_per_bar": beats_per_bar,
            "beats": grid["beats"], "downbeats": grid["downbeats"], "lyrics": lines,
            "words": words, "word_timing_source": word_source, "word_timing": word_metric,
            "lyric_timing_source": "ace-step-lrc" if lrc_raw else "vocal-phrases", "vocal_bleed_db": bleed_db,
            "timings": timer.as_list(), "peak_vram_mib": peak_vram,
        }

    # ---- stage 1: ACE-Step

    def _quantization(self) -> str | None:
        """int8 weights for the music model (about half its 4.8 GB) when the graphics card has less than 7 GB free, as on a
        card the speaking and listening roles share; full precision otherwise. MARTLET_SINGING_QUANTIZATION overrides
        (none, int8_weight_only)."""
        choice = os.environ.get("MARTLET_SINGING_QUANTIZATION", "auto")
        if choice != "auto":
            self.quantized = None if choice == "none" else choice
            return self.quantized
        import torch

        free = torch.cuda.mem_get_info()[0] / 2**30 if torch.cuda.is_available() else 99.0
        self.quantized = "int8_weight_only" if free < 7.0 else None
        return self.quantized

    def _ace_handlers(self, quality: str, timer: Timer, report: Report) -> tuple[Any, Any]:
        config = pins.ACE_SFT if quality == "high_quality" else pins.ACE_TURBO
        key = f"dit:{config}"
        if key not in self.ace or (self.planner == "lm" and "lm" not in self.ace):
            report("loading", 0.02)
            started = time.perf_counter()
            _prepare_ace(self.models)
            from acestep.handler import AceStepHandler
            from acestep.llm_inference import LLMHandler

            for other in [k for k in self.ace if k.startswith("dit:") and k != key]:
                del self.ace[other]  # one music model at a time
            self._free()
            if key not in self.ace:
                dit = AceStepHandler()
                status, ok = dit.initialize_service(project_root=str(self.models / "ace-step-project"), config_path=config,
                                                    device=self.device, offload_to_cpu=True, offload_dit_to_cpu=True,
                                                    quantization=self._quantization())
                if not ok:
                    raise SongError("song.failed", f"The music model could not load: {status}"[:300])
                self.ace[key] = dit
            if self.planner == "lm" and "lm" not in self.ace:
                llm = LLMHandler()
                status, ok = llm.initialize(checkpoint_dir=str(self.models / "ace-step"), lm_model_path=pins.ACE_LM,
                                            backend=self.lm_backend, device=self.device, offload_to_cpu=True)
                if not ok:
                    raise SongError("song.failed", f"The music planner could not load: {status}"[:300])
                self.ace["lm"] = llm
            timer.add("loading", started)
        from acestep.llm_inference import LLMHandler

        return self.ace[key], self.ace.get("lm") or LLMHandler()

    def _write_music(self, job: dict[str, Any], seed: int, timer: Timer, report: Report, canceled: Canceled):
        import numpy as np
        import soundfile
        import torch

        dit, llm = self._ace_handlers(job["quality"], timer, report)
        from acestep.inference import GenerationConfig, GenerationParams, generate_music

        started = time.perf_counter()
        report("writing_music", 0.05)
        steps = 50 if job["quality"] == "high_quality" else 8
        params = GenerationParams(caption=job["style"], lyrics=job["lyrics"], duration=float(job["duration_seconds"]),
                                  vocal_language=job["language"], seed=seed, thinking=self.planner == "lm",
                                  bpm=job.get("bpm"), keyscale=job.get("key") or "", inference_steps=steps)
        config = GenerationConfig(batch_size=1, use_random_seed=False, seeds=[seed], audio_format="wav")

        def progress(value: float, desc: str | None = None, *args: Any, **kwargs: Any) -> None:
            report("writing_music", 0.05 + 0.35 * max(0.0, min(1.0, float(value))))

        output = directory_for(job) / "ace"
        output.mkdir(parents=True, exist_ok=True)
        result = generate_music(dit, llm, params, config, save_dir=str(output), progress=progress)
        if not result.success or not result.audios:
            raise SongError("song.failed", f"The music model failed: {result.error or result.status_message}"[:300])
        path = result.audios[0].get("path")
        samples, rate = soundfile.read(path, dtype="float32", always_2d=True)
        mix = samples.T
        if mix.shape[0] == 1:
            mix = np.repeat(mix, 2, axis=0)
        if rate != audio.OUTPUT_RATE:
            mix = _resample(mix, rate, audio.OUTPUT_RATE)
        extra = result.extra_outputs or {}
        plan = extra.get("lm_metadata") if isinstance(extra.get("lm_metadata"), dict) else {}
        timer.add("writing_music", started)
        _check(canceled)

        # ACE-Step's own lyric timestamps (LRC) from the same generation, while its tensors are at hand.
        started = time.perf_counter()
        lrc = ""
        sentences: list[dict[str, Any]] = []
        try:
            needed = [extra.get(k) for k in ("pred_latents", "encoder_hidden_states", "encoder_attention_mask",
                                             "context_latents", "lyric_token_idss")]
            if all(item is not None for item in needed):
                timestamps = dit.get_lyric_timestamp(
                    pred_latent=needed[0][0:1], encoder_hidden_states=needed[1][0:1], encoder_attention_mask=needed[2][0:1],
                    context_latents=needed[3][0:1], lyric_token_ids=needed[4][0:1],
                    total_duration_seconds=float(mix.shape[1] / audio.OUTPUT_RATE), vocal_language=job["language"],
                    inference_steps=steps, seed=42)
                if timestamps.get("success"):
                    lrc = timestamps.get("lrc_text") or ""
                    sentences = [{"text": s.text, "start": float(s.start), "end": float(s.end),
                                  "tokens": [{"text": k.text, "start": float(k.start), "end": float(k.end)} for k in s.tokens]}
                                 for s in timestamps.get("sentence_timestamps") or []]
        except Exception as error:  # noqa: BLE001 - timing falls back to the vocals' phrases
            print(f"Lyric timestamps failed: {type(error).__name__}: {error}", file=sys.stderr, flush=True)
        timer.add("aligning", started)
        for item in output.glob("*"):
            item.unlink(missing_ok=True)
        del result, extra
        self._free()
        torch.cuda.synchronize() if torch.cuda.is_available() else None
        return mix.astype(np.float32), plan, lrc, sentences

    # ---- stage 2: Demucs vocals

    def _separate(self, mix48):
        import numpy as np
        import torch

        if self.demucs is None:
            import torch
            from demucs.states import load_model

            # The file is pinned by SHA-256; Demucs pickles its model class with the weights, which torch.load refuses by
            # default since PyTorch 2.6.
            package = torch.load(str(self.models / pins.DEMUCS_FILE[4]), map_location="cpu", weights_only=False)
            self.demucs = load_model(package)
            self.demucs.eval()
        model = self.demucs
        source = _resample(mix48, audio.OUTPUT_RATE, model.samplerate)
        from demucs.apply import apply_model

        with torch.no_grad():
            model.to(self.device)
            tensor = torch.from_numpy(np.ascontiguousarray(source)).float()
            reference = tensor.mean(0)
            mean, std = reference.mean(), reference.std() + 1e-8
            separated = apply_model(model, ((tensor - mean) / std)[None], device=self.device, split=True, overlap=0.25,
                                    progress=False)[0]
            separated = separated * std + mean
            model.to("cpu")
        vocals = separated[model.sources.index("vocals")].cpu().numpy()
        del separated, tensor
        self._free()
        vocals48 = _resample(vocals, model.samplerate, audio.OUTPUT_RATE)[:, : mix48.shape[1]]
        if vocals48.shape[1] < mix48.shape[1]:
            vocals48 = np.pad(vocals48, ((0, 0), (0, mix48.shape[1] - vocals48.shape[1])))
        backing48 = mix48 - vocals48
        return vocals48.astype(np.float32), backing48.astype(np.float32)

    # ---- stage 3: voice matching

    def _f0(self, extractor: Any, samples, rate: int):
        """RMVPE pitch (SoulX-Singer's extractor) at the 24 kHz / hop 480 frame rate SoulX expects."""
        import tempfile

        import numpy as np
        import soundfile

        with tempfile.TemporaryDirectory() as folder:
            wav = Path(folder) / "voice.wav"
            soundfile.write(str(wav), samples, rate)
            f0 = extractor.process(str(wav), f0_path=str(Path(folder) / "f0.npy"))
        return np.asarray(f0, dtype=np.float32)

    def _match_soulx(self, vocals48, reference, reference_rate: int, directory: Path):
        import numpy as np
        import torch

        _soulx_path()
        from soulxsinger.utils.file_utils import load_config

        root = _soulx_root()
        config = load_config(str(root / "soulxsinger/config/soulxsinger.yaml"))
        rate = config.audio.sample_rate  # 24 kHz
        target = _resample(vocals48.mean(axis=0), audio.OUTPUT_RATE, rate)
        prompt = _resample(reference, reference_rate, rate)
        # RMVPE is small: load it on the card for this song only.
        from preprocess.tools.f0_extraction import F0Extractor

        extractor = F0Extractor(model_path=str(self.models / "soulx/rmvpe/rmvpe.pt"), device=self.device, verbose=False)
        gt_f0 = self._f0(extractor, target, rate)
        pt_f0 = self._f0(extractor, prompt, rate)
        del extractor
        self._free()
        if self.soulx is None:
            from cli.inference_svc import build_model

            model = build_model(str(self.models / "soulx/model-svc.pt"), config, device="cpu", use_fp16=False)
            model.half()
            model.mel.float()
            model.whisper_encoder.model.to("cpu")
            self.soulx = model
            self._free()
        model = self.soulx
        voiced_gt, voiced_pt = gt_f0[gt_f0 > 0], pt_f0[pt_f0 > 0]
        shift = 0
        if voiced_gt.size and voiced_pt.size:
            # Singing sits about 7 semitones above the same person's speaking voice; shift whole octaves only, so the
            # backing never needs re-pitching.
            raw = 12 * math.log2(float(np.median(voiced_pt)) / float(np.median(voiced_gt)))
            shift = 12 * round((raw + 7) / 12)
        torch.manual_seed(42)
        device = self.device
        try:
            with torch.no_grad():
                model.to(device)
                pt = torch.from_numpy(prompt).float()[None].to(device)
                gt = torch.from_numpy(target).float()[None].to(device)
                generated, _ = model.infer(pt_wav=pt, gt_wav=gt, pt_f0=torch.from_numpy(pt_f0)[None].to(device),
                                           gt_f0=torch.from_numpy(gt_f0)[None].to(device), auto_shift=False,
                                           pitch_shift=shift, n_steps=32, cfg=3.0, use_fp16=device.startswith("cuda"))
                converted = generated.squeeze().float().cpu().numpy()
                del generated, pt, gt
        finally:
            model.to("cpu")
            model.whisper_encoder.model.to("cpu")
            self._free()
        converted48 = _resample(converted, rate, audio.OUTPUT_RATE)
        return _fit(converted48, vocals48.shape[1])

    def _match_vevo(self, vocals48, reference, reference_rate: int):
        import numpy as np
        import torch

        if "vevosing" not in self.voice_matches:
            raise SongError("singing.voice_match_unavailable", "VevoSing is not set up on this computer.")
        _amphion_path()
        if self.vevo is None:
            os.environ.setdefault("XDG_CACHE_HOME", str(self.models))
            from models.svc.vevosing.vevosing_utils import VevosingInferencePipeline

            amphion = _amphion_root()
            cwd = os.getcwd()
            os.chdir(amphion)
            try:
                self.vevo = VevosingInferencePipeline(
                    content_style_tokenizer_ckpt_path=str(self.models / "vevo/tokenizer/contentstyle_fvq16384_12.5hz"),
                    fmt_cfg_path=str(amphion / "models/svc/vevosing/config/fm_emilia101k_singnet7k.json"),
                    fmt_ckpt_path=str(self.models / "vevo/acoustic_modeling/fm_emilia101k_singnet7k"),
                    vocoder_cfg_path=str(amphion / "models/svc/vevosing/config/vocoder.json"),
                    vocoder_ckpt_path=str(self.models / "vevo/acoustic_modeling/Vocoder"),
                    device=torch.device(self.device))
            finally:
                os.chdir(cwd)
        pipeline = self.vevo
        import librosa
        import torchaudio
        from evaluation.metrics.f0.f0_corr import extract_f0_hz  # noqa: F401 - imported for parity with Amphion

        source = _resample(vocals48.mean(axis=0), audio.OUTPUT_RATE, 24_000)
        prompt = _resample(reference, reference_rate, 24_000)
        src_f0 = librosa.yin(source, fmin=70, fmax=1000, sr=24_000)
        ref_f0 = librosa.yin(prompt, fmin=70, fmax=1000, sr=24_000)
        raw = 12 * math.log2(float(np.median(ref_f0)) / float(np.median(src_f0)))
        octave = 12 * round((raw + 7) / 12)
        converted = np.zeros_like(source)
        with torch.no_grad():
            ref24 = torch.from_numpy(prompt).float()[None].to(self.device)
            ref16 = torchaudio.functional.resample(ref24, 24_000, 16_000)
            ref_codecs = pipeline.extract_coco_codec("content_style", ref16, prompt.astype(np.float32))
            ref_mels = pipeline.extract_mel_feature(ref24)
            for a, b in _split_points(source, 24_000):
                piece = source[a:b]
                if np.max(np.abs(piece)) < 1e-3:
                    continue
                if octave:
                    piece = librosa.effects.pitch_shift(piece, sr=24_000, n_steps=octave)
                src24 = torch.from_numpy(piece).float()[None].to(self.device)
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
        self._free()
        return _fit(_resample(converted, 24_000, audio.OUTPUT_RATE), vocals48.shape[1])


def directory_for(job: dict[str, Any]) -> Path:
    return Path(job["directory"])


def _number(value: Any) -> float | None:
    try:
        number = float(str(value).split("/")[0])
        return number if number > 0 else None
    except (TypeError, ValueError):
        return None


def _to(model: Any, device: str) -> None:
    if model is not None and hasattr(model, "to"):
        model.to(device)


def _fit(samples, frames: int):
    import numpy as np

    samples = samples[:frames]
    return np.pad(samples, (0, frames - samples.shape[-1])) if samples.shape[-1] < frames else samples


def _resample(samples, source: int, target: int):
    import numpy as np

    if source == target:
        return samples.astype(np.float32)
    import torch
    import torchaudio

    tensor = torch.from_numpy(np.ascontiguousarray(samples, dtype=np.float32))
    return torchaudio.functional.resample(tensor, source, target).numpy()


def _write_pcm(path: Path, samples) -> None:
    """Interleaved signed 16-bit little-endian PCM from a (channels, frames) float array."""
    import numpy as np

    data = np.clip(np.asarray(samples, dtype=np.float32).T, -1.0, 1.0)
    path.write_bytes((data * 32767.0).round().astype("<i2").tobytes())


def _split_points(samples, rate: int, maximum: float = 24.0, minimum: float = 12.0) -> list[tuple[int, int]]:
    import librosa
    import numpy as np

    hop = rate // 100
    rms = librosa.feature.rms(y=samples, frame_length=hop * 4, hop_length=hop)[0]
    pieces, start = [], 0
    while len(samples) - start > maximum * rate:
        lo, hi = (start + int(minimum * rate)) // hop, (start + int(maximum * rate)) // hop
        cut = (lo + int(np.argmin(rms[lo:hi]))) * hop
        pieces.append((start, cut))
        start = cut
    pieces.append((start, len(samples)))
    return pieces


def _source_root(name: str) -> Path:
    return Path(os.environ.get("MARTLET_SINGING_SOURCES", "/opt/martlet-singing/src")) / name


def _soulx_root() -> Path:
    return _source_root("soulx-singer")


def _amphion_root() -> Path:
    return _source_root("amphion")


def _soulx_path() -> None:
    root = str(_soulx_root())
    if root not in sys.path:
        sys.path.insert(0, root)


def _amphion_path() -> None:
    for root in (str(_amphion_root()), str(Path(__file__).resolve().parent / "shims")):
        if root not in sys.path:
            sys.path.insert(0, root)


def _prepare_ace(models: Path) -> None:
    """ACE-Step reads its checkpoints from ACESTEP_CHECKPOINTS_DIR; the role provisions the turbo and SFT models, the VAE,
    the text encoder and the 0.6B planner there. Its 1.7B planner is not used, and nothing may be downloaded at run time."""
    os.environ["ACESTEP_CHECKPOINTS_DIR"] = str(models / "ace-step")
    os.environ["ACESTEP_PROJECT_ROOT"] = str(models / "ace-step-project")
    (models / "ace-step-project").mkdir(parents=True, exist_ok=True)
    import acestep.model_downloader as downloader

    downloader.MAIN_MODEL_COMPONENTS = [c for c in downloader.MAIN_MODEL_COMPONENTS if c != "acestep-5Hz-lm-1.7B"]

    def refuse(*args: Any, **kwargs: Any) -> Any:
        raise SongError("song.failed", "A singing model file is missing; set the singing role up again.")

    for name in ("_download_from_huggingface_internal", "_download_from_modelscope_internal", "_smart_download"):
        if hasattr(downloader, name):
            setattr(downloader, name, refuse)

    # Other roles share the graphics card, so the device-wide free memory often looks low; ACE-Step would then decode on the
    # CPU, which takes minutes. Its tiled decode fits in what this process already holds, so it stays on the card.
    import acestep.core.generation.handler.generate_music_decode as decode

    if not getattr(decode, "_martlet_gpu_decode", False):
        measured = decode.get_effective_free_vram_gb
        decode.get_effective_free_vram_gb = lambda *args, **kwargs: max(2.0, measured(*args, **kwargs))
        decode._martlet_gpu_decode = True
