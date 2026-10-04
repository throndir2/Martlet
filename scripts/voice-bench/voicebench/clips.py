"""Clip sets: real read speech with references (LibriSpeech), the companion prompts spoken by a voice, or your own recordings."""

from __future__ import annotations

import json
import shutil
import subprocess
import tempfile
from pathlib import Path

from .common import CLIPS, DATA, SAMPLE_RATE, decode_audio, log, read_audio, write_wav


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
    log(f"Imported {count} clips to {folder}")
    return folder


def list_sets() -> None:
    if not CLIPS.is_dir():
        log(f"No clip sets yet in {CLIPS}")
        return
    from .common import load_clips

    for folder in sorted(p for p in CLIPS.iterdir() if p.is_dir()):
        clips = load_clips(str(folder))
        seconds = sum(c.seconds for c in clips)
        print(f"{folder.name:20} {len(clips):3} clips  {seconds:7.1f} s  median {sorted(c.seconds for c in clips)[len(clips) // 2]:.1f} s")
