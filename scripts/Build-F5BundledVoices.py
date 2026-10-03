"""Builds the starter voices Martlet adds to every voice list (src/Martlet.F5/BundledVoices) from their pinned upstream recordings.

Every source is public domain or CC0 (the cute and anime voices' LibriVox readings of Anne of Green Gables, LJ Speech)
or CMU ARCTIC (free for any use; its notice is kept in
src/Martlet.F5/BundledVoices/NOTICES.txt). Download the sources into one directory with their upstream layout:

  librivox/anneofgreengables_02_montgomery.mp3 https://archive.org/download/anneofgreengables_sp_librivox/
  librivox/agg_02_montgomery.mp3               https://archive.org/download/anne_gables_0808/
  LJSpeech-1.1/wavs/LJ001-0009.wav             https://data.keithito.com/data/speech/LJSpeech-1.1.tar.bz2
  cmu_us_<speaker>_arctic/wav/arctic_a00NN.wav http://festvox.org/cmu_arctic/cmu_arctic/cmu_us_<speaker>_arctic/wav/

then run (needs numpy and miniaudio, and praat-parselmouth for the anime voices):

  python scripts/Build-F5BundledVoices.py <sources directory> [output directory] [--only key,key...]

--only rebuilds just the named voices (for example when only their sources are downloaded).

Modifications (marked here as the CMU ARCTIC terms require): the spoken passage is cut out at its silences (short pauses
kept, ARCTIC's two sentences joined by 0.3 s of silence), DC offset and rumble below 50 Hz are removed, the level is set
to -20 dBFS speech RMS (peaks at most -1 dBFS), 10 ms fades are applied at each cut, MP3s are decoded and resampled to
24 kHz, and the result is written as mono 16-bit PCM WAV. The anime voices' pitch is also raised 1.33x (range 1.15x) and
their formants 1.12x with Praat's PSOLA "Change gender", keeping the duration; no other clip's pitch, speed or timbre is
changed.
"""

import hashlib
import pathlib
import sys
import wave

import miniaudio
import numpy as np

ARCTIC_SENTENCES = [("arctic_a0058", "I came for information more out of curiosity than anything else."),
                    ("arctic_a0039", "The ship should be in within a week or ten days.")]
ARCTIC_SOURCES = {
    "slt": ("08f5ad6bfdbcc9327bc9b558b0dd753d110273ccb6d367499ef9ae24c2257498", "4d2df5a499114499467289b26898d8e7e6ea12f5d3acc99592e194afa27b80fa"),
    "bdl": ("cedcfe39c544f6facf42d791c0d5bc72fff6e5b912c9365a0fdd21f88c71b7d2", "6c2d5d014826053e4dfbfd3760725214809ab181245f1a8b2609f6326c4e857b"),
}

ANNE_V6 = ("librivox/anneofgreengables_02_montgomery.mp3", "39e640f3ca7a59bdfa5c528c4af63bc4233e62e85dc791d3cbdba28a7ef1b145")
ANNE_V4 = ("librivox/agg_02_montgomery.mp3", "4ef534ec93aa850571defca7c5d1dda245a0c3df210a3b43a32c9e24377f544d")
# The anime voices raise a cute voice's pitch and formants with Praat's PSOLA "Change gender" (needs praat-parselmouth):
# (formant shift ratio, pitch median factor, pitch range factor). Duration is unchanged.
ANIME_LIFT = (1.12, 1.33, 1.15)

# key: [(relative source path, SHA-256, start s, end s or None for the whole file)], output sample rate[, lift]
VOICES = {
    # The cute, high voices come first; the first is F5's default.
    # WoollyBee as Anne Shirley (Chapter II): "You do get so attached to things like that, don't you? Is there a brook
    # anywhere near Green Gables? I forgot to ask Mrs. Spencer that." Lifted to an anime pitch.
    "librivox-woollybee-anime": ([(*ANNE_V6, 852.50, 860.80)], 24_000, ANIME_LIFT),
    # WoollyBee as Anne (Chapter II): "It isn't heavy. I've got all my worldly goods in it, but it isn't heavy. And if it
    # isn't carried in just a certain way the handle pulls out, so I'd better keep it because I know the exact knack of it."
    "librivox-woollybee": ([(*ANNE_V6, 413.30, 423.72)], 24_000),
    # Annie Coleman Rothenberg as Anne (Chapter II): "But am I talking too much? People are always telling me I do. Would
    # you rather I didn't talk? If you say so, I'll stop." Lifted to an anime pitch, and as read.
    "librivox-annie-anime": ([(*ANNE_V4, 795.30, 802.65)], 24_000, ANIME_LIFT),
    "librivox-annie": ([(*ANNE_V4, 795.30, 802.65)], 24_000),
    "lj-speech": ([("LJSpeech-1.1/wavs/LJ001-0009.wav", "d003373f9c769b17995cfff2a99818e720a56f3376678b27fc6c9c27539c3f75", 0, None)], None),
}
for speaker, digests in ARCTIC_SOURCES.items():
    VOICES[f"arctic-{speaker}"] = ([(f"cmu_us_{speaker}_arctic/wav/{name}.wav", digest, 0, None)
                                    for (name, _), digest in zip(ARCTIC_SENTENCES, digests)], None)

FRAME_SECONDS = 0.01
PRE_PAD, POST_PAD, GAP, LEAD, TAIL = 0.06, 0.15, 0.30, 0.05, 0.25
TARGET_RMS_DB, PEAK_LIMIT_DB = -20.0, -1.0


def load(path: pathlib.Path, digest: str):
    data = path.read_bytes()
    actual = hashlib.sha256(data).hexdigest()
    if actual != digest:
        raise SystemExit(f"{path}: SHA-256 {actual} is not the pinned {digest}")
    if path.suffix == ".wav":
        with wave.open(str(path), "rb") as reader:
            if reader.getnchannels() != 1 or reader.getsampwidth() != 2:
                raise SystemExit(f"{path}: expected mono 16-bit PCM")
            return np.frombuffer(reader.readframes(reader.getnframes()), dtype="<i2").astype(np.float64) / 32768, reader.getframerate()
    decoded = miniaudio.decode(data, output_format=miniaudio.SampleFormat.FLOAT32, nchannels=1)
    return np.asarray(decoded.samples, dtype=np.float64), decoded.sample_rate


def resample(x, rate, target):
    if target is None or target == rate:
        return x, rate
    count = int(round(len(x) * target / rate))
    spectrum = np.fft.rfft(x)
    kept = np.zeros(count // 2 + 1, dtype=complex)
    bins = min(len(kept), len(spectrum))
    kept[:bins] = spectrum[:bins]
    return np.fft.irfft(kept, count) * (count / len(x)), target


def high_pass(x, rate, cutoff=50.0):
    x = x - x.mean()
    alpha = 1 / (1 + 2 * np.pi * cutoff / rate)
    y = np.empty_like(x)
    previous_in = previous_out = 0.0
    for index, value in enumerate(x):
        previous_out = alpha * (previous_out + value - previous_in)
        previous_in = value
        y[index] = previous_out
    return y


def levels(x, rate):
    frame = int(rate * FRAME_SECONDS)
    count = len(x) // frame
    rms = np.sqrt(np.mean(x[: count * frame].reshape(count, frame) ** 2, axis=1) + 1e-12)
    return 20 * np.log10(rms), frame


def speech(x, rate):
    """The spoken part with short natural pads, cut at its silences, and its active frames' mask."""
    db, frame = levels(x, rate)
    threshold = max(np.percentile(db, 10) + 12, db.max() - 40)
    active = np.flatnonzero(db > threshold)
    start = max(0, active[0] * frame - int(PRE_PAD * rate))
    end = min(len(x), (active[-1] + 1) * frame + int(POST_PAD * rate))
    piece = x[start:end].copy()
    fade = int(0.01 * rate)
    ramp = 0.5 - 0.5 * np.cos(np.linspace(0, np.pi, fade))
    piece[:fade] *= ramp
    piece[-fade:] *= ramp[::-1]
    return piece, threshold


def lift(x, rate, shift):
    """Raises pitch and formants (Praat's PSOLA "Change gender"), keeping the duration."""
    import parselmouth
    from parselmouth.praat import call, run

    # PSOLA fills unvoiced stretches with random pulses; a fixed seed keeps the clip reproducible.
    run("random_initializeWithSeedUnsafelyButPredictably (5)")
    formant_ratio, pitch_factor, range_factor = shift
    sound = parselmouth.Sound(x, rate)
    f0 = sound.to_pitch(pitch_floor=100, pitch_ceiling=800).selected_array["frequency"]
    median = float(np.median(f0[f0 > 0]))
    changed = call(sound, "Change gender", 100, 800, formant_ratio, median * pitch_factor, range_factor, 1.0)
    if changed.sampling_frequency != rate:
        raise SystemExit("Change gender altered the sample rate")
    return changed.values[0].astype(np.float64)


def build(sources: pathlib.Path, key: str):
    parts, target, *shift = VOICES[key]
    pieces, rate, thresholds = [], None, []
    for relative, digest, start, end in parts:
        x, source_rate = load(sources / relative, digest)
        x = x[int(start * source_rate): None if end is None else int(end * source_rate)]
        x, piece_rate = resample(high_pass(x, source_rate), source_rate, target)
        if rate not in (None, piece_rate):
            raise SystemExit(f"{key}: sources differ in sample rate")
        rate = piece_rate
        piece, threshold = speech(x, rate)
        pieces.append(piece)
        thresholds.append(threshold)
    silence = lambda seconds: np.zeros(int(seconds * rate))
    joined = [silence(LEAD)]
    for index, piece in enumerate(pieces):
        if index:
            joined.append(silence(GAP))
        joined.append(piece)
    joined.append(silence(TAIL))
    audio = np.concatenate(joined)
    if shift:
        audio = lift(audio, rate, shift[0])
    db, frame = levels(audio, rate)
    active = np.repeat(db > min(thresholds), frame)
    speech_rms = np.sqrt(np.mean(audio[: len(active)][active] ** 2))
    gain = min(10 ** (TARGET_RMS_DB / 20) / speech_rms, 10 ** (PEAK_LIMIT_DB / 20) / np.abs(audio).max())
    samples = np.clip(np.round(audio * gain * 32768), -32768, 32767).astype("<i2")
    return samples, rate


def main():
    arguments = sys.argv[1:]
    only = None
    if "--only" in arguments:
        index = arguments.index("--only")
        if index + 1 >= len(arguments):
            raise SystemExit(__doc__)
        only = arguments[index + 1].split(",")
        del arguments[index: index + 2]
        unknown = [key for key in only if key not in VOICES]
        if unknown:
            raise SystemExit(f"unknown voice(s): {', '.join(unknown)}")
    if len(arguments) not in (1, 2):
        raise SystemExit(__doc__)
    sources = pathlib.Path(arguments[0])
    output = pathlib.Path(arguments[1]) if len(arguments) == 2 else pathlib.Path(__file__).resolve().parents[1] / "src" / "Martlet.F5" / "BundledVoices"
    output.mkdir(parents=True, exist_ok=True)
    for key in only or VOICES:
        samples, rate = build(sources, key)
        path = output / f"{key}.wav"
        with wave.open(str(path), "wb") as writer:
            writer.setnchannels(1)
            writer.setsampwidth(2)
            writer.setframerate(rate)
            writer.writeframes(samples.tobytes())
        print(f"{key}: {rate} Hz, {len(samples) / rate:.2f} s, SHA-256 {hashlib.sha256(path.read_bytes()).hexdigest()}")


if __name__ == "__main__":
    main()
