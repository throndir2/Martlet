"""Clip sets: real read speech with references (LibriSpeech), the companion prompts spoken by a voice, or your own recordings."""

from __future__ import annotations

import json
import shutil
import subprocess
import tempfile
from pathlib import Path

from .common import CLIPS, DATA, HOME, SAMPLE_RATE, decode_audio, log, read_audio, write_wav

# Downloaded or generated audio before it becomes a clip set (vb clips import), kept with the bench, never in the repository.
SOURCES = HOME / "sources"


def prompts() -> list[str]:
    return json.loads((DATA / "prompts.json").read_text(encoding="utf-8"))["prompts"]


def fetch_librispeech(name: str = "librispeech") -> Path:
    """73 real read-speech utterances (LibriSpeech dev-clean, the Hugging Face test sample), 2-30 s, with references."""
    import pyarrow.parquet as pq
    from huggingface_hub import hf_hub_download

    folder = CLIPS / name
    parquet = hf_hub_download("hf-internal-testing/librispeech_asr_dummy", "clean/validation-00000-of-00001.parquet",
                              repo_type="dataset")
    table = pq.read_table(parquet).to_pylist()
    folder.mkdir(parents=True, exist_ok=True)
    for row in table:
        clip_id = str(row.get("id") or row["file"]).replace("/", "_")
        samples = decode_audio(row["audio"]["bytes"])
        write_wav(folder / f"{clip_id}.wav", samples)
        (folder / f"{clip_id}.txt").write_text(row["text"].strip().lower() + "\n", encoding="utf-8")
    log(f"Wrote {len(table)} LibriSpeech clips to {folder}")
    return folder


def synth_sapi(name: str = "prompts-sapi") -> Path:
    """The companion prompts spoken by Windows' own voices (clean and robotic: fine for timing, easy for accuracy)."""
    folder = CLIPS / name
    folder.mkdir(parents=True, exist_ok=True)
    items = prompts()
    with tempfile.TemporaryDirectory() as temp:
        listing = Path(temp) / "prompts.json"
        listing.write_text(json.dumps([{"path": str(folder / f"p{i:02d}.wav"), "text": t} for i, t in enumerate(items)]),
                           encoding="utf-8")
        script = (
            "Add-Type -AssemblyName System.Speech;"
            f"$items = Get-Content -Raw '{listing}' | ConvertFrom-Json;"
            "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer;"
            "$voices = @($s.GetInstalledVoices() | Where-Object Enabled | ForEach-Object { $_.VoiceInfo.Name });"
            f"$fmt = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo({SAMPLE_RATE}, "
            "[System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono);"
            "$i = 0; foreach ($item in $items) { $s.SelectVoice($voices[$i % $voices.Count]); $s.Rate = 1;"
            "$s.SetOutputToWaveFile($item.path, $fmt); $s.Speak($item.text); $s.SetOutputToNull(); $i++ }; $s.Dispose()"
        )
        subprocess.run(["powershell", "-NoProfile", "-Command", script], check=True)
    for i, text in enumerate(items):
        (folder / f"p{i:02d}.txt").write_text(text + "\n", encoding="utf-8")
    log(f"Wrote {len(items)} Windows-voice prompts to {folder}")
    return folder


def synth_voice(url: str, name: str = "prompts-voice") -> Path:
    """The companion prompts spoken by a Martlet voice worker (Chatterbox Turbo by default), rotating the starter voices.
    More natural than Windows' voices, but still generated speech."""
    from .tts import MartletWorker, starter_voices

    folder = CLIPS / name
    folder.mkdir(parents=True, exist_ok=True)
    worker = MartletWorker(url)
    voices = starter_voices()
    for i, text in enumerate(prompts()):
        voice = voices[i % len(voices)]
        result = worker.synthesize(text, voice)
        write_wav(folder / f"p{i:02d}.wav", result.samples_16k())
        (folder / f"p{i:02d}.txt").write_text(text + "\n", encoding="utf-8")
        log(f"  p{i:02d} {voice.name}: {result.audio_seconds:.1f} s")
    log(f"Wrote {len(prompts())} voice-worker prompts to {folder}")
    return folder


def record(name: str = "prompts-mine") -> Path:
    """Records you saying each prompt from the default microphone: Enter starts, Enter stops. The most realistic set."""
    import numpy as np
    import sounddevice

    folder = CLIPS / name
    folder.mkdir(parents=True, exist_ok=True)
    for i, text in enumerate(prompts()):
        input(f'\n[{i + 1}/{len(prompts())}] Press Enter, then say: "{text}"  (Enter again when done)')
        frames: list[np.ndarray] = []
        with sounddevice.InputStream(samplerate=SAMPLE_RATE, channels=1, dtype="float32",
                                     callback=lambda data, *_: frames.append(data.copy())):
            input("  recording... ")
        samples = np.concatenate(frames)[:, 0] if frames else np.zeros(0, dtype=np.float32)
        write_wav(folder / f"p{i:02d}.wav", samples[: 60 * SAMPLE_RATE])
        (folder / f"p{i:02d}.txt").write_text(text + "\n", encoding="utf-8")
    log(f"Saved your recordings to {folder}")
    return folder


def import_folder(source: Path, name: str) -> Path:
    """Copies WAV/FLAC/MP3 files (each with a same-name .txt reference) into a clip set as 16 kHz mono WAV."""
    folder = CLIPS / name
    folder.mkdir(parents=True, exist_ok=True)
    count = 0
    for path in sorted(source.iterdir()):
        if path.suffix.lower() not in {".wav", ".flac", ".mp3", ".ogg"}:
            continue
        write_wav(folder / f"{path.stem}.wav", read_audio(path))
        text = path.with_suffix(".txt")
        if text.is_file():
            shutil.copyfile(text, folder / f"{path.stem}.txt")
        count += 1
    # Where the clips came from and their licence travel with the set.
    for notice in ("SOURCE.txt", "LICENSE.txt"):
        if (source / notice).is_file():
            shutil.copyfile(source / notice, folder / notice)
    log(f"Imported {count} clips to {folder}")
    return folder


def fetch_ami(config: str = "ihm", count: int = 32, name: str | None = None) -> Path:
    """Real spontaneous meeting speech from the AMI corpus (CC BY 4.0, edinburghcstr/ami on Hugging Face): short turns of
    2-6.5 s with at least four words, at most three per speaker, spread over the test meetings (native and non-native
    English). config ihm is each speaker's headset microphone, sdm one distant microphone in the room. Fetched row by row
    through the Hugging Face datasets API, so only the chosen clips download."""
    import json as _json
    import time
    import urllib.error
    import urllib.request

    def get(url: str) -> bytes:
        # The datasets API rate-limits bursts (HTTP 429): wait and try again.
        for attempt in range(8):
            try:
                with urllib.request.urlopen(url, timeout=120) as response:
                    return response.read()
            except urllib.error.HTTPError as error:
                if error.code not in (429, 500, 502, 503) or attempt == 7:
                    raise
                time.sleep(min(60, 5 * 2 ** attempt))
        raise RuntimeError("unreachable")

    name = name or f"ami-{'headset' if config == 'ihm' else 'room'}"
    folder = SOURCES / name
    folder.mkdir(parents=True, exist_ok=True)
    api = "https://datasets-server.huggingface.co/rows?dataset=edinburghcstr/ami&config={}&split=test&offset={}&length=100"
    total = _json.loads(get(api.format(config, 0, 1)))["num_rows_total"]
    chosen: list[dict] = []
    speakers: dict[str, int] = {}
    # Pages spread over the whole test split, so every meeting (and its speakers) can contribute.
    for offset in range(0, total, max(100, total // 24)):
        rows = _json.loads(get(api.format(config, offset, 100)))["rows"]
        time.sleep(1)
        page = 0
        for item in rows:
            row = item["row"]
            seconds = row["end_time"] - row["begin_time"]
            words = row["text"].split()
            if not 2.0 <= seconds <= 6.5 or len(words) < 4 or speakers.get(row["speaker_id"], 0) >= 3 or page >= 2:
                continue
            speakers[row["speaker_id"]] = speakers.get(row["speaker_id"], 0) + 1
            chosen.append(row)
            page += 1
        if len(chosen) >= count:
            break
    for row in chosen[:count]:
        clip_id = row["audio_id"].replace("/", "_")
        samples = decode_audio(get(row["audio"][0]["src"]))
        write_wav(folder / f"{clip_id}.wav", samples)
        (folder / f"{clip_id}.txt").write_text(row["text"].strip().lower() + "\n", encoding="utf-8")
    (folder / "SOURCE.txt").write_text(
        "AMI Meeting Corpus (https://groups.inf.ed.ac.uk/ami/corpus/), test split, "
        f"{'individual headset microphones (ihm)' if config == 'ihm' else 'single distant microphone (sdm)'}, via "
        "https://huggingface.co/datasets/edinburghcstr/ami. Licence: CC BY 4.0. Speakers: "
        f"{len(speakers)}. Resampled to 16 kHz mono.\n", encoding="utf-8")
    log(f"Downloaded {min(count, len(chosen))} AMI {config} clips from {len(speakers)} speakers to {folder}")
    return import_folder(folder, name)


def desk_mic(source: str, name: str, seed: int = 7) -> Path:
    """A messier copy of a clip set, as if said into a desk microphone across a room: a synthetic room (reverb, RT60
    0.25-0.6 s), fan, hum and room noise at 10-20 dB SNR, a cheap-microphone band (120 Hz-7 kHz, so it stays a 16 kHz
    recording), a level 14 dB below to 2 dB above the original, and 0.3-0.8 s of room tone before and 0.4-1.0 s after the
    words. Deterministic for a seed."""
    import numpy as np

    from .common import load_clips

    rng = np.random.default_rng(seed)
    folder = SOURCES / name
    folder.mkdir(parents=True, exist_ok=True)
    clips = load_clips(source)

    def room(rt60: float) -> np.ndarray:
        length = int(rt60 * 1.2 * SAMPLE_RATE)
        t = np.arange(length) / SAMPLE_RATE
        tail = rng.standard_normal(length) * np.exp(-6.9 * t / rt60)
        tail[: int(0.004 * SAMPLE_RATE)] = 0
        response = np.zeros(length)
        response[0] = 1.0
        response += tail * 0.35
        return response / np.sqrt(np.sum(response ** 2))

    def band(signal: np.ndarray, low: float, high: float) -> np.ndarray:
        spectrum = np.fft.rfft(signal)
        frequencies = np.fft.rfftfreq(len(signal), 1 / SAMPLE_RATE)
        gain = 1 / np.sqrt(1 + (low / np.maximum(frequencies, 1)) ** 4) / np.sqrt(1 + (frequencies / high) ** 8)
        return np.fft.irfft(spectrum * gain, len(signal))

    def noise(length: int) -> np.ndarray:
        white = rng.standard_normal(length)
        pink = np.fft.irfft(np.fft.rfft(white) / np.sqrt(np.maximum(np.fft.rfftfreq(length, 1 / SAMPLE_RATE), 20)), length)
        t = np.arange(length) / SAMPLE_RATE
        hum = 0.3 * np.sin(2 * np.pi * rng.choice([50, 60]) * t) + 0.1 * np.sin(2 * np.pi * 180 * t)
        fan = band(rng.standard_normal(length), 200, 1200) * (1 + 0.2 * np.sin(2 * np.pi * 0.5 * t))
        mix = pink / np.std(pink) + 0.4 * fan / np.std(fan) + 0.15 * hum
        return mix / np.std(mix)

    notes = []
    for clip in clips:
        speech = clip.samples.astype(np.float64)
        rt60 = float(rng.uniform(0.25, 0.6))
        wet = np.convolve(speech, room(rt60))[: len(speech) + int(0.3 * SAMPLE_RATE)]
        lead, trail = int(rng.uniform(0.3, 0.8) * SAMPLE_RATE), int(rng.uniform(0.4, 1.0) * SAMPLE_RATE)
        signal = np.concatenate([np.zeros(lead), wet, np.zeros(trail)])
        snr = float(rng.uniform(10, 20))
        voiced = wet[np.abs(wet) > 0.02 * np.max(np.abs(wet))]
        floor = noise(len(signal)) * np.sqrt(np.mean(voiced ** 2)) / (10 ** (snr / 20))
        mixed = band(signal + floor, 120, 7000)
        level = float(rng.uniform(-14, 2))
        mixed *= 10 ** (level / 20) * 0.5 / max(np.sqrt(np.mean(voiced ** 2)) * 4, 1e-6)
        write_wav(folder / f"{clip.id}.wav", np.clip(mixed, -1, 1).astype(np.float32))
        (folder / f"{clip.id}.txt").write_text(clip.text + "\n", encoding="utf-8")
        notes.append(f"{clip.id}: RT60 {rt60:.2f} s, SNR {snr:.1f} dB, level {level:+.1f} dB, lead {lead / SAMPLE_RATE:.2f} s, "
                     f"trail {trail / SAMPLE_RATE:.2f} s")
    (folder / "SOURCE.txt").write_text(f"Made by voicebench from the clip set '{source}' (generated speech) with desk_mic(seed={seed}):\n"
                                       + "\n".join(notes) + "\n", encoding="utf-8")
    log(f"Made {len(clips)} desk-microphone clips in {folder}")
    return import_folder(folder, name)


def list_sets() -> None:
    if not CLIPS.is_dir():
        log(f"No clip sets yet in {CLIPS}")
        return
    from .common import load_clips

    for folder in sorted(p for p in CLIPS.iterdir() if p.is_dir()):
        clips = load_clips(str(folder))
        seconds = sum(c.seconds for c in clips)
        print(f"{folder.name:20} {len(clips):3} clips  {seconds:7.1f} s  median {sorted(c.seconds for c in clips)[len(clips) // 2]:.1f} s")
