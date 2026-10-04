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
        self.matcher: _Matcher | None = None
        # ACE-Step's 5 Hz planner rewrites the caption and plans tempo, key and audio codes before the music. Martlet's own
        # model already writes the lyrics, style, tempo and key, and the planner costs 35-320 s on the PyTorch backend, so it
        # runs only for a request with neither: "auto" (default), "none" or "lm" (always).
        self.planner = os.environ.get("MARTLET_SINGING_PLANNER", "auto")
        self.lm_backend = os.environ.get("MARTLET_SINGING_LM_BACKEND", "pt")  # "pt" or "vllm"
        self.quantized: str | None = None
        self.dit_resident = False
        # Keep models in system memory between songs (faster next song) unless memory is short; "release" frees each
        # stage's models as soon as it is done. MARTLET_SINGING_KEEP_MODELS: auto, keep or release.
        self.keep_models = os.environ.get("MARTLET_SINGING_KEEP_MODELS", "auto")

    # ---- lifecycle

    def release(self) -> None:
        self.ace.clear()
        self.demucs = None
        if self.matcher is not None:
            self.matcher.close()
            self.matcher = None
        self._free()

    def _plans(self, job: dict[str, Any]) -> bool:
        return self.planner == "lm" or self.planner == "auto" and not job.get("bpm") and not job.get("key")

    def _keep(self, minimum_gb: float = 16.0) -> bool:
        """Whether models may stay in system memory for the next song: MARTLET_SINGING_KEEP_MODELS keep or release, or (auto)
        while more than minimum_gb of system memory is available."""
        if self.keep_models in ("keep", "release"):
            return self.keep_models == "keep"
        try:
            import psutil

            return psutil.virtual_memory().available > minimum_gb * 2**30
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
        resident = self.dit_resident
        _check(canceled)

        # The music model stays warm on the card while separating and matching leave room; otherwise it waits in system
        # memory when there is plenty, or is released.
        if not (self.dit_resident and _card_free_mib() >= 3_072):
            if self._keep(8.0):
                self._park_ace()
            else:
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
        converted48, matched = self._match(vocals48, reference, reference_rate, job["voice_match"], directory, canceled)
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
        bpm_planned = _number(plan.get("bpm")) or _number(job.get("bpm"))
        beats_per_bar = int(_number(plan.get("timesignature")) or 4)
        if beats_per_bar not in (2, 3, 4, 6):
            beats_per_bar = 4
        grid = timing.beat_grid(backing48.mean(axis=0), audio.OUTPUT_RATE, bpm_planned, beats_per_bar)
        lines = timing.lyric_times(audio.lyric_lines(job["lyrics"]), lrc_raw, converted48, audio.OUTPUT_RATE, grid["downbeats"])
        words, word_source, word_metric = timing.word_times(audio.lyric_lines(job["lyrics"]), sentences, lines, converted48,
                                                            audio.OUTPUT_RATE)
        if lrc_raw and (word_metric.get("median_onset_offset_ms_raw") or 0) > 1_000:
            # ACE-Step's alignment missed the singing (its words sit over a second from any vocal onset, as when a short song
            # crams its lines): time the lines and words from the vocals' phrases instead.
            lrc_raw, sentences = "", []
            lines = timing.lyric_times(audio.lyric_lines(job["lyrics"]), "", converted48, audio.OUTPUT_RATE, grid["downbeats"])
            words, word_source, word_metric = timing.word_times(audio.lyric_lines(job["lyrics"]), [], lines, converted48,
                                                                audio.OUTPUT_RATE)
        timer.add("aligning", started)

        peak_vram = round(torch.cuda.max_memory_reserved() / 2**20) if torch.cuda.is_available() else 0
        self._free()  # what stays on the card between songs is the warm music model, not the allocator's cache
        peak_vram = max(peak_vram, int(matched.get("peak_vram_mib") or 0))
        return {
            "engine": {"generator": pins.GENERATOR_IDS[job["quality"]], "separator": pins.SEPARATOR_ID,
                       "converter": pins.CONVERTER_IDS[job["voice_match"]], "quality": job["quality"],
                       "voice_match": job["voice_match"], "fixture": False,
                       "planner": "lm" if self._plans(job) else "none", "lm_backend": self.lm_backend if self._plans(job) else None,
                       "dit_resident": resident, "dit_warm_after": bool(self.ace) and self.dit_resident,
                       "quantization": self.quantized, "match": {k: matched.get(k) for k in (
                           "shift_semitones", "singer_hz", "voice_hz", "load_seconds", "pitch_seconds", "convert_seconds",
                           "process_seconds", "peak_vram_mib", "transformers", "warm")}},
            "frames": frames, "seed": seed, "bpm": grid["bpm"] or bpm_planned, "planned_bpm": bpm_planned,
            "key": plan.get("keyscale") or job.get("key"), "beats_per_bar": beats_per_bar,
            "beats": grid["beats"], "downbeats": grid["downbeats"], "lyrics": lines,
            "words": words, "word_timing_source": word_source, "word_timing": word_metric,
            "lyric_timing_source": "ace-step-lrc" if lrc_raw else "vocal-phrases", "vocal_bleed_db": bleed_db,
            "timings": timer.as_list(), "peak_vram_mib": peak_vram,
        }

    # ---- stage 1: ACE-Step

    def _dit_fits(self, dit: Any) -> bool:
        """Whether the music model can stay on the card for this song: it already is, or the card (nvidia-smi: every
        process) has room for it plus about 4.5 GB for the text encoder, the decoder and their work (an int8 song peaked at
        6-7.2 GB with the model staying on the card)."""
        try:
            on_card = next(dit.model.parameters()).device.type == "cuda"
        except (AttributeError, StopIteration):
            on_card = False
        if on_card:
            self.dit_resident = True
            return True
        card = card_memory()
        needed_mib = (2_700 if self.quantized else 5_000) + 4_500
        self.dit_resident = card is not None and card["total_mib"] - card["used_mib"] >= needed_mib
        return self.dit_resident

    def _park_ace(self) -> None:
        """Moves the music model from the card to system memory."""
        for name, dit in self.ace.items():
            if name.startswith("dit:") and getattr(dit, "model", None) is not None:
                try:
                    if next(dit.model.parameters()).device.type == "cuda":
                        dit._recursive_to_device(dit.model, "cpu")
                        if hasattr(dit, "silence_latent"):
                            dit.silence_latent = dit.silence_latent.to("cpu")
                except StopIteration:
                    pass
        self.dit_resident = False

    def _quantization(self) -> str | None:
        """int8 weights for the music model (about half its 4.8 GB) when the graphics card has less than 11 GB free (in full
        precision a song peaked at 9.6-10.7 GB, int8 at about 5 GB), as on a card the speaking and listening roles share;
        full precision otherwise. MARTLET_SINGING_QUANTIZATION overrides
        (none, int8_weight_only)."""
        choice = os.environ.get("MARTLET_SINGING_QUANTIZATION", "auto")
        if choice != "auto":
            self.quantized = None if choice == "none" else choice
            return self.quantized
        import torch

        free = torch.cuda.mem_get_info()[0] / 2**30 if torch.cuda.is_available() else 99.0
        # On Windows (WDDM) CUDA's free figure ignores other processes and spills into shared system memory instead of
        # failing, so the card's own count (nvidia-smi) decides when it is lower.
        card = card_memory()
        if card:
            free = min(free, (card["total_mib"] - card["used_mib"]) / 1024)
        self.quantized = "int8_weight_only" if free < 11.0 else None
        return self.quantized

    def _ace_handlers(self, quality: str, plans: bool, timer: Timer, report: Report) -> tuple[Any, Any]:
        config = pins.ACE_SFT if quality == "high_quality" else pins.ACE_TURBO
        key = f"dit:{config}"
        if key not in self.ace or (plans and "lm" not in self.ace):
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
            if plans and "lm" not in self.ace:
                llm = LLMHandler()
                status, ok = llm.initialize(checkpoint_dir=str(self.models / "ace-step"), lm_model_path=pins.ACE_LM,
                                            backend=self.lm_backend, device=self.device, offload_to_cpu=True)
                if not ok:
                    raise SongError("song.failed", f"The music planner could not load: {status}"[:300])
                self.ace["lm"] = llm
            timer.add("loading", started)
        from acestep.llm_inference import LLMHandler

        return self.ace[key], (self.ace.get("lm") if plans else None) or LLMHandler()

    def _write_music(self, job: dict[str, Any], seed: int, timer: Timer, report: Report, canceled: Canceled):
        import numpy as np
        import soundfile
        import torch

        plans = self._plans(job)
        dit, llm = self._ace_handlers(job["quality"], plans, timer, report)
        from acestep.inference import GenerationConfig, GenerationParams, generate_music

        started = time.perf_counter()
        report("writing_music", 0.05)
        # The music model stays on the card from the music through the lyric timestamps when the card has room, instead of
        # moving there and back for each (which costs more than the work itself where system memory is short).
        dit.offload_dit_to_cpu = not self._dit_fits(dit)
        steps = 50 if job["quality"] == "high_quality" else 8
        params = GenerationParams(caption=job["style"], lyrics=job["lyrics"], duration=float(job["duration_seconds"]),
                                  vocal_language=job["language"], seed=seed, thinking=plans, use_cot_metas=plans,
                                  use_cot_caption=plans, use_cot_language=plans,
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
        timer.add("lyric_timestamps", started)
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

    def _match(self, vocals48, reference, reference_rate: int, voice_match: str, directory: Path,
               canceled: Canceled) -> tuple[Any, dict[str, Any]]:
        """Converts the vocals in martlet_singing.match, a child process with the converters' own Transformers. SoulX-Singer's
        process stays (model in system memory) for the next song while memory allows; VevoSing's ends after each song."""
        import json

        import numpy as np
        import soundfile

        if voice_match == "vevosing" and "vevosing" not in self.voice_matches:
            raise SongError("singing.voice_match_unavailable", "VevoSing is not set up on this computer.")
        engine = "vevosing" if voice_match == "vevosing" else "soulx"
        vocals_path, reference_path, out_path = (directory / "match-vocals.wav", directory / "match-reference.wav",
                                                 directory / "match-out.wav")
        soundfile.write(str(vocals_path), vocals48.mean(axis=0).astype(np.float32), audio.OUTPUT_RATE, subtype="FLOAT")
        soundfile.write(str(reference_path), reference.astype(np.float32), reference_rate, subtype="FLOAT")
        if self.matcher is not None and (self.matcher.engine != engine or self.matcher.process.poll() is not None):
            self.matcher.close()
            self.matcher = None
        warm = self.matcher is not None
        if self.matcher is None:
            self.matcher = _Matcher(engine, self.models, self.device, directory)
        try:
            summary = self.matcher.ask({"vocals": str(vocals_path), "reference": str(reference_path), "out": str(out_path)},
                                       canceled)
        except JobCanceled:
            self.matcher.close()
            self.matcher = None
            raise
        except SongError:
            self.matcher.close()
            self.matcher = None
            raise
        if engine != "soulx" or not self._keep(4.0):
            self.matcher.close()
            self.matcher = None
        if isinstance(summary.get("error"), dict):
            raise SongError(summary["error"].get("code", "song.failed"), summary["error"].get("summary", "Voice matching failed."))
        converted, _ = soundfile.read(str(out_path), dtype="float32")
        for path in (vocals_path, reference_path, out_path):
            path.unlink(missing_ok=True)
        summary["warm"] = warm
        return _fit(converted, vocals48.shape[1]), summary


class _Matcher:
    """A running martlet_singing.match process: one JSON line per song in, one out, read without blocking so a cancel or the
    process dying is noticed (and, on Windows, without a thread blocked on a pipe)."""

    def __init__(self, engine: str, models: Path, device: str, directory: Path) -> None:
        import subprocess

        self.engine = engine
        environment = dict(os.environ)
        paths = [str(Path(__file__).resolve().parent.parent)]
        overlay = os.environ.get("MARTLET_SINGING_MATCH_SITE")
        if overlay:
            paths.insert(0, overlay)
        environment["PYTHONPATH"] = os.pathsep.join(paths + [p for p in environment.get("PYTHONPATH", "").split(os.pathsep) if p])
        self.log = (Path(os.environ.get("MARTLET_SINGING_JOBS", str(directory.parent))) / f"match-{engine}.log")
        self.errors = open(self.log, "ab")
        self.process = subprocess.Popen(
            [os.environ.get("MARTLET_SINGING_MATCH_PYTHON") or sys.executable, "-m", "martlet_singing.match", "--engine", engine,
             "--models", str(models), "--device", device],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.errors, env=environment, cwd=str(directory.parent))
        self.pending = b""

    def ask(self, job: dict[str, Any], canceled: Canceled) -> dict[str, Any]:
        import json

        assert self.process.stdin is not None and self.process.stdout is not None
        try:
            self.process.stdin.write((json.dumps(job) + "\n").encode("utf-8"))
            self.process.stdin.flush()
        except OSError as error:
            raise SongError("song.failed", "Matching the voice failed.") from error
        descriptor = self.process.stdout.fileno()
        while b"\n" not in self.pending:
            if canceled():
                raise JobCanceled()
            ready = _readable(descriptor)
            if ready:
                chunk = os.read(descriptor, ready)
                if not chunk:
                    self._failed()
                self.pending += chunk
            elif self.process.poll() is not None:
                self._failed()
        line, self.pending = self.pending.split(b"\n", 1)
        return json.loads(line)

    def _failed(self) -> None:
        self.process.wait()
        tail = self.log.read_text(encoding="utf-8", errors="replace").strip().splitlines()[-1:] if self.log.exists() else []
        print(f"voice matching failed ({self.process.returncode}): {tail}", file=sys.stderr, flush=True)
        raise SongError("song.failed", "Matching the voice failed.")

    def close(self) -> None:
        if self.process.poll() is None:
            try:
                assert self.process.stdin is not None
                self.process.stdin.close()
                self.process.wait(timeout=10)
            except Exception:  # noqa: BLE001 - it is ended below
                pass
        if self.process.poll() is None:
            self.process.kill()
            self.process.wait()
        self.errors.close()


def _readable(descriptor: int) -> int:
    """How many bytes a pipe holds now (waiting up to 0.2 s for some)."""
    if os.name == "nt":
        import ctypes
        import msvcrt
        from ctypes import wintypes

        available = wintypes.DWORD()
        if not ctypes.windll.kernel32.PeekNamedPipe(wintypes.HANDLE(msvcrt.get_osfhandle(descriptor)), None, 0, None,
                                                    ctypes.byref(available), None):
            return 1  # broken pipe: let the read see the end
        if available.value == 0:
            time.sleep(0.2)
        return available.value
    import select

    return 65536 if select.select([descriptor], [], [], 0.2)[0] else 0


def _card_free_mib() -> int:
    card = card_memory()
    return card["total_mib"] - card["used_mib"] if card else 1 << 20


def card_memory() -> dict[str, int] | None:
    """The graphics card's used and total memory as the driver counts it (all processes), or None without nvidia-smi."""
    import subprocess

    try:
        output = subprocess.run(["nvidia-smi", "--query-gpu=memory.used,memory.total", "--format=csv,noheader,nounits"],
                                capture_output=True, text=True, timeout=3, check=True).stdout.split("\n")[0]
        used, total = (int(v.strip()) for v in output.split(","))
        return {"used_mib": used, "total_mib": total}
    except (OSError, subprocess.SubprocessError, ValueError):
        return None


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


def _soulx_build_model():
    """SoulX-Singer's cli/inference_svc.py build_model, loaded from its file: ACE-Step also has a top-level "cli" module, so
    SoulX's cli package cannot be imported by name next to it."""
    import importlib.util

    spec = importlib.util.spec_from_file_location("martlet_soulx_inference_svc", _soulx_root() / "cli" / "inference_svc.py")
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module.build_model


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
